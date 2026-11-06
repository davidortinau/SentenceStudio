using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Contracts;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api;

public static class ImportEndpoints
{
    public static WebApplication MapImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/imports").RequireAuthorization();

        group.MapGet("/", GetImports);
        group.MapGet("/{id}", GetImport);
        group.MapPost("/", StartImport);
        group.MapPost("/{id}/retry", RetryImport);

        return app;
    }

    private const int MaxImportHistoryLimit = 200;

    /// <summary>The most videos one status lookup takes (the channel page asks for its 20).</summary>
    internal const int MaxVideoLookup = 50;

    /// <summary>
    /// The owner's import history, newest first (<paramref name="limit"/> rows). With one or more
    /// <paramref name="videoIds"/> (<c>?videoId=a&amp;videoId=b</c>, additive) it is instead the
    /// owner's newest import of each of those videos, however old, as the source channel page
    /// looked each video up (ChannelMonitorService.GetVideoImportStatusAsync).
    /// </summary>
    private static async Task<IResult> GetImports(
        ClaimsPrincipal user,
        [FromServices] VideoImportPipelineService pipelineService,
        [FromQuery] int limit = 50,
        [FromQuery(Name = "videoId")] string[]? videoIds = null)
    {
        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        if (videoIds is { Length: > 0 })
        {
            var ids = videoIds.Where(videoId => !string.IsNullOrWhiteSpace(videoId))
                .Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count > MaxVideoLookup)
                return Results.BadRequest($"At most {MaxVideoLookup} videoId values can be looked up at once.");
            var latest = await pipelineService.GetLatestImportsForVideosAsync(ids, userProfileId);
            return Results.Ok(latest.Select(VideoImportResponse.From));
        }

        var clampedLimit = Math.Clamp(limit, 1, MaxImportHistoryLimit);
        var imports = await pipelineService.GetImportHistoryAsync(userProfileId, clampedLimit);
        return Results.Ok(imports.Select(VideoImportResponse.From));
    }

    private static async Task<IResult> GetImport(
        string id,
        ClaimsPrincipal user,
        [FromServices] VideoImportPipelineService pipelineService)
    {
        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        var import = await pipelineService.GetImportByIdAsync(id, userProfileId);
        if (import == null)
            return Results.NotFound();

        return Results.Ok(VideoImportResponse.From(import));
    }

    private static async Task<IResult> StartImport(
        [FromBody] StartImportRequest request,
        ClaimsPrincipal user,
        [FromServices] VideoImportPipelineService pipelineService,
        [FromServices] ChannelMonitorService channelService,
        [FromServices] ILogger<ImportEndpointsLog> logger)
    {
        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.VideoUrl))
            return Results.BadRequest("VideoUrl is required");

        // A channel's import names its video up front (source ChannelDetail ImportSelectedVideos),
        // so the channel page still finds it when the metadata step fails. It must be the link's.
        if (request.VideoId is not null
            && YoutubeExplode.Videos.VideoId.TryParse(request.VideoUrl)?.Value != request.VideoId)
            return Results.BadRequest("VideoId does not match VideoUrl");

        // A channel's import is linked to it (source ChannelDetail ImportSelectedVideos), but
        // only to the owner's own channel: a missing or another owner's channel refuses the
        // request, so nothing is written and no import is created unlinked.
        if (request.MonitoredChannelId is not null)
        {
            var channel = await channelService.GetByIdAsync(request.MonitoredChannelId);
            if (channel == null)
                return Results.NotFound();
            if (channel.UserProfileId != userProfileId)
                return Results.Forbid();
        }

        // Create the import record and return immediately
        var import = new VideoImport
        {
            UserProfileId = userProfileId,
            MonitoredChannelId = request.MonitoredChannelId,
            VideoId = request.VideoId,
            VideoTitle = request.VideoTitle,
            VideoUrl = request.VideoUrl,
            Language = request.Language ?? "Korean",
            CreatedAt = DateTime.UtcNow
        };

        // Run pipeline in background (non-blocking). Capture the import id so
        // the catch block can correlate the failure even if 'import' has been
        // mutated by the time the exception bubbles.
        var importIdForLog = import.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                await pipelineService.RunPipelineAsync(import);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Background import pipeline crashed for VideoImport {ImportId} (user {UserProfileId}, url {VideoUrl})",
                    importIdForLog, userProfileId, request.VideoUrl);
            }
        });

        return Results.Accepted($"/api/imports/{import.Id}", new
        {
            import.Id,
            import.Status,
            Message = "Import started. Poll this endpoint for progress."
        });
    }

    private static async Task<IResult> RetryImport(
        string id,
        ClaimsPrincipal user,
        [FromServices] VideoImportPipelineService pipelineService,
        [FromServices] ILogger<ImportEndpointsLog> logger)
    {
        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        var import = await pipelineService.GetImportByIdAsync(id, userProfileId);
        if (import == null)
            return Results.NotFound();

        // Only retry failed or stuck imports
        var isStuck = import.Status != VideoImportStatus.Failed
                   && import.Status != VideoImportStatus.Completed
                   && import.CreatedAt < DateTime.UtcNow.AddMinutes(-10);

        if (import.Status != VideoImportStatus.Failed && !isStuck)
            return Results.BadRequest("Only failed or stuck imports can be retried. In-progress imports must be older than 10 minutes.");

        // Reset status and retry
        import.Status = VideoImportStatus.Pending;
        import.ErrorMessage = null;

        // Run pipeline in background
        var importIdForLog = import.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                await pipelineService.RunPipelineAsync(import);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Background retry pipeline crashed for VideoImport {ImportId} (user {UserProfileId})",
                    importIdForLog, userProfileId);
            }
        });

        return Results.Accepted($"/api/imports/{import.Id}", new
        {
            import.Id,
            import.Status,
            Message = "Import retry started."
        });
    }
}

// Marker type for ILogger<T> category name on background import tasks.
internal sealed class ImportEndpointsLog { }

/// <summary>
/// The optional fields are additive. <see cref="MonitoredChannelId"/>: an import started from one
/// of the owner's channels names it and is linked to it; without it the import is a manual one.
/// <see cref="VideoId"/> (the link's own id) and <see cref="VideoTitle"/> are stored before the
/// pipeline runs, as the source channel page's import set them.
/// </summary>
public record StartImportRequest(string VideoUrl, string? Language, string? MonitoredChannelId = null,
    string? VideoId = null, string? VideoTitle = null);

/// <summary>
/// An import history row: the fields the model serializes (same names and types as before) plus
/// <see cref="CreatedAt"/> and <see cref="CompletedAt"/>, which the model keeps out of JSON. A
/// client needs them for the history's date and its "stuck for more than 10 minutes" test.
/// </summary>
public sealed record VideoImportResponse(
    string Id,
    string? UserProfileId,
    string? VideoId,
    string? VideoTitle,
    string? VideoUrl,
    string? MonitoredChannelId,
    VideoImportStatus Status,
    string? ErrorMessage,
    string? LearningResourceId,
    string? RawTranscript,
    string? CleanedTranscript,
    string? Language,
    DateTime CreatedAt,
    DateTime? CompletedAt)
{
    public static VideoImportResponse From(VideoImport import) => new(
        import.Id, import.UserProfileId, import.VideoId, import.VideoTitle, import.VideoUrl,
        import.MonitoredChannelId, import.Status, import.ErrorMessage, import.LearningResourceId,
        import.RawTranscript, import.CleanedTranscript, import.Language, Utc(import.CreatedAt),
        import.CompletedAt is { } completed ? Utc(completed) : null);

    /// <summary>
    /// The instant in UTC (sent with a Z). The server writes these times in UTC, but Npgsql's
    /// legacy timestamp mode (Program.cs) reads timestamptz back as host-local time: a Local value
    /// is converted; a provider that drops the kind (Unspecified) stored UTC, so it is relabelled.
    /// </summary>
    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value
    };
}
