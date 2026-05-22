using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Plans;

/// <summary>
/// HTTP surface for the daily-plan server contract. Five routes, all
/// authenticated, all per-user (via <see cref="IUserScopeProvider"/>) and
/// timezone-aware (via <see cref="IPlanDateContext"/>). RFC 7807
/// problem+json bodies on error.
/// </summary>
public static class PlanEndpoints
{
    private const int DefaultAdHocEstimatedMinutes = 10;
    private const int MaxAdHocEstimatedMinutes = PlanService.MaxMinutesSpent;

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

        group.MapPost("/adhoc/start", StartAdHocAsync)
            .WithName("StartAdHocPlanSession");

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

    private static async Task<IResult> StartAdHocAsync(
        StartAdHocPlanSessionRequest request,
        IUserScopeProvider userScope,
        IPlanDateContext dateContext,
        ApplicationDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!TryValidateStartAdHocRequest(request, out var normalizedClientSessionId, out var activityType, out var estimatedMinutes, out var validationProblem))
        {
            return validationProblem;
        }

        try
        {
            var userId = userScope.UserProfileId;
            var date = dateContext.UserLocalDate;
            var dateKey = ToDateKey(date);
            var planItemId = $"adhoc-{normalizedClientSessionId}";

            var existing = await FindAdHocCompletionAsync(db, userId, dateKey, planItemId, ct);
            if (existing is not null)
            {
                return Results.Ok(MapAdHocResponse(existing));
            }

            var nowUtc = dateContext.UtcNow;
            var completion = new DailyPlanCompletion
            {
                Id = Guid.NewGuid().ToString(),
                UserProfileId = userId,
                Date = dateKey,
                PlanItemId = planItemId,
                ActivityType = activityType.ToString(),
                ResourceId = string.IsNullOrWhiteSpace(request.ResourceId) ? null : request.ResourceId.Trim(),
                SkillId = string.IsNullOrWhiteSpace(request.SkillId) ? null : request.SkillId.Trim(),
                IsCompleted = false,
                CompletedAt = null,
                MinutesSpent = 0,
                EstimatedMinutes = estimatedMinutes,
                Priority = 999,
                TitleKey = $"Activity_{activityType}",
                DescriptionKey = string.Empty,
                Rationale = string.Empty,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc
            };

            db.DailyPlanCompletions.Add(completion);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                var replayed = await FindAdHocCompletionAsync(db, userId, dateKey, planItemId, ct);
                if (replayed is not null)
                {
                    return Results.Ok(MapAdHocResponse(replayed));
                }

                throw;
            }

            return Results.Created(
                $"/api/v1/plans/{date:yyyy-MM-dd}/items/{Uri.EscapeDataString(planItemId)}/progress",
                MapAdHocResponse(completion));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Plans")
                .LogError(ex, "POST /api/v1/plans/adhoc/start failed");
            return Results.Problem(
                detail: "Failed to start ad-hoc plan session.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<DailyPlanCompletion?> FindAdHocCompletionAsync(
        ApplicationDbContext db,
        string userId,
        DateTime dateKey,
        string planItemId,
        CancellationToken ct)
    {
        return await db.DailyPlanCompletions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserProfileId == userId
                && c.Date == dateKey
                && c.PlanItemId == planItemId, ct);
    }

    private static bool TryValidateStartAdHocRequest(
        StartAdHocPlanSessionRequest? request,
        out string normalizedClientSessionId,
        out PlanActivityType activityType,
        out int estimatedMinutes,
        out IResult problem)
    {
        normalizedClientSessionId = string.Empty;
        activityType = default;
        estimatedMinutes = DefaultAdHocEstimatedMinutes;
        problem = Results.Empty;

        if (request is null)
        {
            problem = Results.Problem(
                title: "Invalid request",
                detail: "Request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        if (!Guid.TryParse(request.ClientSessionId, out var clientSessionGuid))
        {
            problem = Results.Problem(
                title: "Invalid clientSessionId",
                detail: "clientSessionId must be a valid UUID.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }
        normalizedClientSessionId = clientSessionGuid.ToString("D");

        if (!Enum.TryParse<PlanActivityType>(request.ActivityType, ignoreCase: false, out activityType)
            || !Enum.IsDefined(activityType))
        {
            problem = Results.Problem(
                title: "Invalid activityType",
                detail: "activityType must match a server PlanActivityType value.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        estimatedMinutes = request.EstimatedMinutes ?? DefaultAdHocEstimatedMinutes;
        if (estimatedMinutes <= 0 || estimatedMinutes > MaxAdHocEstimatedMinutes)
        {
            problem = Results.Problem(
                title: "Invalid estimatedMinutes",
                detail: $"estimatedMinutes must be between 1 and {MaxAdHocEstimatedMinutes}.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        return true;
    }

    private static StartAdHocPlanSessionResponse MapAdHocResponse(DailyPlanCompletion completion)
    {
        return new StartAdHocPlanSessionResponse(
            PlanItemId: completion.PlanItemId,
            ActivityType: completion.ActivityType,
            Date: DateOnly.FromDateTime(completion.Date),
            EstimatedMinutes: completion.EstimatedMinutes);
    }

    private static DateTime ToDateKey(DateOnly localDate) =>
        localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}

public sealed record StartAdHocPlanSessionRequest(
    string ClientSessionId,
    string ActivityType,
    string? ResourceId,
    string? SkillId,
    int? EstimatedMinutes);

public sealed record StartAdHocPlanSessionResponse(
    string PlanItemId,
    string ActivityType,
    DateOnly Date,
    int EstimatedMinutes);
