using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Services;

namespace SentenceStudio.Api;

public static class MaintenanceEndpoints
{
    public static WebApplication MapMaintenanceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/vocabulary/progress").RequireAuthorization();

        group.MapPost("/migrate-streak", MigrateStreak)
            .WithMetadata(new ApiExplorerSettingsAttribute { IgnoreApi = true });

        return app;
    }

    /// <summary>
    /// One-shot maintenance endpoint that re-runs the streak-based scoring
    /// migration. The Flutter client gates this behind kDebugMode, so it is
    /// hidden from the public OpenAPI surface via [ApiExplorerSettings].
    /// </summary>
    private static async Task<IResult> MigrateStreak(
        ClaimsPrincipal user,
        [FromServices] IVocabularyProgressService progressService)
    {
        var userProfileId = user.FindFirstValue("user_profile_id");
        if (string.IsNullOrEmpty(userProfileId))
            return Results.Unauthorized();

        var migrated = await progressService.MigrateToStreakBasedScoringAsync();
        return Results.Ok(new { migrated });
    }
}
