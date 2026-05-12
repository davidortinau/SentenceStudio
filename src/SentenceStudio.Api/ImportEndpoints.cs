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

    private static async Task<IResult> GetImports(
        ClaimsPrincipal user,
        [FromServices] VideoImportPipelineService pipelineService,
        [FromQuery] int limit = 50)
    {
        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        var clampedLimit = Math.Clamp(limit, 1, MaxImportHistoryLimit);
        var imports = await pipelineService.GetImportHistoryAsync(userProfileId, clampedLimit);
        return Results.Ok(imports);
    }

    private static async Task<IResult> GetImport(
        string id,
        ClaimsPrincipal user,
        [FromServices] VideoImportPipelineService pipelineService)
    {
        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        var import = await pipelineService.GetImportByIdAsync(id);
        if (import == null)
            return Results.NotFound();

        // Verify ownership
        if (import.UserProfileId != userProfileId)
            return Results.Forbid();

        return Results.Ok(import);
    }

    private static async Task<IResult> StartImport(
        [FromBody] StartImportRequest request,
        ClaimsPrincipal user,
        [FromServices] VideoImportPipelineService pipelineService,
        [FromServices] ILogger<ImportEndpointsLog> logger)
    {
        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.VideoUrl))
            return Results.BadRequest("VideoUrl is required");

        // Create the import record and return immediately
        var import = new VideoImport
        {
            UserProfileId = userProfileId,
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

        var import = await pipelineService.GetImportByIdAsync(id);
        if (import == null)
            return Results.NotFound();

        // Verify ownership
        if (import.UserProfileId != userProfileId)
            return Results.Forbid();

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

public record StartImportRequest(string VideoUrl, string? Language);
