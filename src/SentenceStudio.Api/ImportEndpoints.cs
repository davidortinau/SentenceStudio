using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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

    private static async Task<IResult> GetImports(
        ClaimsPrincipal user,
        [FromServices] VideoImportPipelineService pipelineService,
        [FromQuery] int limit = 50)
    {
        var userProfileId = user.FindFirstValue("user_profile_id");
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        var imports = await pipelineService.GetImportHistoryAsync(userProfileId, limit);
        return Results.Ok(imports);
    }

    private static async Task<IResult> GetImport(
        string id,
        ClaimsPrincipal user,
        [FromServices] VideoImportPipelineService pipelineService)
    {
        var userProfileId = user.FindFirstValue("user_profile_id");
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
        [FromServices] ILoggerFactory loggerFactory)
    {
        var userProfileId = user.FindFirstValue("user_profile_id");
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

        // Run pipeline in background (non-blocking)
        _ = Task.Run(async () =>
        {
            try
            {
                await pipelineService.RunPipelineAsync(import);
            }
            catch (Exception ex)
            {
                var logger = loggerFactory.CreateLogger(nameof(ImportEndpoints));
                logger.LogError(ex, "Unhandled exception in import pipeline for import {ImportId}", import.Id);
                await pipelineService.FailImportAsync(import, ex.Message);
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
        [FromServices] ILoggerFactory loggerFactory)
    {
        var userProfileId = user.FindFirstValue("user_profile_id");
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
        _ = Task.Run(async () =>
        {
            try
            {
                await pipelineService.RunPipelineAsync(import);
            }
            catch (Exception ex)
            {
                var logger = loggerFactory.CreateLogger(nameof(ImportEndpoints));
                logger.LogError(ex, "Unhandled exception in import pipeline for import {ImportId}", import.Id);
                await pipelineService.FailImportAsync(import, ex.Message);
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

public record StartImportRequest(string VideoUrl, string? Language);
