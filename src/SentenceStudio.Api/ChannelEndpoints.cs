using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api;

public static class ChannelEndpoints
{
    public static WebApplication MapChannelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/channels").RequireAuthorization();

        group.MapGet("/", GetChannels);
        group.MapPost("/", AddChannel);
        group.MapPut("/{id}", UpdateChannel);
        group.MapDelete("/{id}", DeleteChannel);
        group.MapPost("/{id}/check", TriggerCheck);

        return app;
    }

    private static async Task<IResult> GetChannels(
        ClaimsPrincipal user,
        [FromServices] ChannelMonitorService channelService)
    {
        var userProfileId = user.FindFirstValue("user_profile_id");
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        var channels = await channelService.GetAllAsync(userProfileId);
        return Results.Ok(channels);
    }

    private static async Task<IResult> AddChannel(
        [FromBody] MonitoredChannel request,
        ClaimsPrincipal user,
        [FromServices] ChannelMonitorService channelService)
    {
        var userProfileId = user.FindFirstValue("user_profile_id");
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        request.UserProfileId = userProfileId;
        var channel = await channelService.AddAsync(request);
        return Results.Created($"/api/channels/{channel.Id}", channel);
    }

    private static async Task<IResult> UpdateChannel(
        string id,
        [FromBody] MonitoredChannel request,
        ClaimsPrincipal user,
        [FromServices] ChannelMonitorService channelService)
    {
        var userProfileId = user.FindFirstValue("user_profile_id");
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        // Verify ownership
        var existing = await channelService.GetByIdAsync(id);
        if (existing == null)
            return Results.NotFound();
        if (existing.UserProfileId != userProfileId)
            return Results.Forbid();

        request.Id = id;
        request.UserProfileId = userProfileId;
        await channelService.UpdateAsync(request);
        return Results.Ok(request);
    }

    private static async Task<IResult> DeleteChannel(
        string id,
        ClaimsPrincipal user,
        [FromServices] ChannelMonitorService channelService)
    {
        var userProfileId = user.FindFirstValue("user_profile_id");
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        // Verify ownership
        var existing = await channelService.GetByIdAsync(id);
        if (existing == null)
            return Results.NotFound();
        if (existing.UserProfileId != userProfileId)
            return Results.Forbid();

        await channelService.DeleteAsync(id);
        return Results.NoContent();
    }

    private static async Task<IResult> TriggerCheck(
        string id,
        ClaimsPrincipal user,
        [FromServices] ChannelMonitorService channelService,
        [FromServices] VideoImportPipelineService pipelineService)
    {
        var userProfileId = user.FindFirstValue("user_profile_id");
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        // Verify ownership
        var channel = await channelService.GetByIdAsync(id);
        if (channel == null)
            return Results.NotFound();
        if (channel.UserProfileId != userProfileId)
            return Results.Forbid();

        // Get recent videos and check for new ones
        var videos = await channelService.GetRecentVideosAsync(channel, maxResults: 10);
        var newImports = new List<VideoImport>();

        foreach (var (videoId, title, url) in videos)
        {
            // Check if already imported
            if (await channelService.IsVideoAlreadyImportedAsync(videoId, userProfileId))
                continue;

            // Create VideoImport and run pipeline in background
            var import = new VideoImport
            {
                UserProfileId = userProfileId,
                MonitoredChannelId = channel.Id,
                VideoId = videoId,
                VideoTitle = title,
                VideoUrl = url,
                Language = channel.Language,
                CreatedAt = DateTime.UtcNow
            };

            // Run pipeline in background (non-blocking)
            _ = Task.Run(async () =>
            {
                try
                {
                    await pipelineService.RunPipelineAsync(import);
                }
                catch (Exception ex)
                {
                    // Logging happens inside the pipeline service
                }
            });

            newImports.Add(import);

            // Rate limiting
            await Task.Delay(500);
        }

        // Mark channel as checked
        await channelService.MarkCheckedAsync(id);

        return Results.Ok(new
        {
            CheckedAt = DateTime.UtcNow,
            NewVideosFound = newImports.Count,
            Imports = newImports.Select(i => new { i.Id, i.VideoTitle, i.Status })
        });
    }
}
