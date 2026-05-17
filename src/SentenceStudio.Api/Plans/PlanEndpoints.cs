using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Plans;

/// <summary>
/// HTTP surface for the daily-plan server contract. Five routes, all
/// authenticated, all per-user (via <see cref="IUserScopeProvider"/>) and
/// timezone-aware (via <see cref="IPlanDateContext"/>). RFC 7807
/// problem+json bodies on error.
/// </summary>
public static class PlanEndpoints
{
    public static IEndpointRouteBuilder MapPlans(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/plans").RequireAuthorization();

        group.MapGet("/today", GetTodayAsync)
            .WithName("GetTodaysPlan");

        group.MapPost("/today/generate", GenerateTodayAsync)
            .WithName("GenerateTodaysPlan");

        group.MapPost("/{date}/items/{id}/progress", UpdateProgressAsync)
            .WithName("UpdatePlanItemProgress");

        group.MapPost("/{date}/items/{id}/complete", MarkCompleteAsync)
            .WithName("MarkPlanItemComplete");

        group.MapDelete("/today", ResetTodayAsync)
            .WithName("ResetTodaysPlan");

        return app;
    }

    private static async Task<IResult> GetTodayAsync(
        IPlanService planService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        try
        {
            var plan = await planService.GetTodayAsync(ct);
            return plan is null ? Results.NoContent() : Results.Ok(plan);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Plans").LogError(ex, "GET /api/v1/plans/today failed");
            return Results.Problem(detail: "Failed to load today's plan.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> GenerateTodayAsync(
        GenerateTodaysPlanRequest request,
        IPlanService planService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        try
        {
            var plan = await planService.GenerateTodayAsync(request ?? new GenerateTodaysPlanRequest(), ct);
            return Results.Ok(plan);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (InvalidOperationException ex)
        {
            // No resources, no skill profile, no vocabulary, etc. → 422.
            return Results.Problem(
                type: "https://sentencestudio.dev/problems/plan-generation-no-data",
                title: "Cannot generate plan",
                detail: ex.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Plans").LogError(ex, "POST /api/v1/plans/today/generate failed");
            return Results.Problem(detail: "Failed to generate plan.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> UpdateProgressAsync(
        string date,
        string id,
        PlanItemProgressRequest request,
        IPlanService planService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var parsedDate))
        {
            return Results.Problem(
                title: "Invalid date",
                detail: $"Date '{date}' is not a valid yyyy-MM-dd value.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var ok = await planService.UpdateProgressAsync(parsedDate, id, request?.MinutesSpent ?? 0, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Plans").LogError(ex, "POST progress for plan item {Id} failed", id);
            return Results.Problem(detail: "Failed to update progress.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> MarkCompleteAsync(
        string date,
        string id,
        PlanItemProgressRequest request,
        IPlanService planService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var parsedDate))
        {
            return Results.Problem(
                title: "Invalid date",
                detail: $"Date '{date}' is not a valid yyyy-MM-dd value.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var item = await planService.MarkCompleteAsync(parsedDate, id, request?.MinutesSpent ?? 0, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Plans").LogError(ex, "POST complete for plan item {Id} failed", id);
            return Results.Problem(detail: "Failed to mark plan item complete.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ResetTodayAsync(
        IPlanService planService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        try
        {
            await planService.ResetTodayAsync(ct);
            return Results.NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Plans").LogError(ex, "DELETE /api/v1/plans/today failed");
            return Results.Problem(detail: "Failed to reset today's plan.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
