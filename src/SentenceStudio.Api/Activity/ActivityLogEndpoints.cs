using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Activity;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Api.Activity;

/// <summary>
/// <c>GET /api/v1/activity-log</c> — read-only Activity Log endpoint serving
/// the Flutter client. Wraps <see cref="IActivityLogService"/> and maps the
/// rich internal week/day/plan/entry records to the wire DTOs documented in
/// <c>specs/activity-log-api-spec.md</c>. No new business logic — clustering,
/// week rollup, and category mapping all happen in the shared service.
/// </summary>
public static class ActivityLogEndpoints
{
    /// <summary>Maximum days the server will service in a single call. The Flutter
    /// client requests at most ~56 days (8 weeks) up front, 4-week pages thereafter,
    /// so 90 days is a comfortable ceiling.</summary>
    public const int MaxRangeDays = 90;

    public static IEndpointRouteBuilder MapActivityLog(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/activity-log", GetActivityLogAsync)
            .WithName("GetActivityLog")
            .RequireAuthorization();
        return app;
    }

    private static async Task<IResult> GetActivityLogAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? filter,
        IActivityLogService activityLogService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("ActivityLog");

        if (fromUtc is null || toUtc is null)
        {
            return Results.Problem(
                title: "Missing date range",
                detail: "Both 'fromUtc' and 'toUtc' query parameters are required (ISO 8601 UTC).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (fromUtc > toUtc)
        {
            return Results.Problem(
                title: "Invalid date range",
                detail: "'fromUtc' must be on or before 'toUtc'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if ((toUtc.Value.Date - fromUtc.Value.Date).TotalDays > MaxRangeDays)
        {
            return Results.Problem(
                title: "Date range too large",
                detail: $"Range cannot exceed {MaxRangeDays} days. Paginate by passing narrower fromUtc/toUtc windows.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        ActivityCategory? categoryFilter = null;
        if (!string.IsNullOrEmpty(filter))
        {
            if (!Enum.TryParse<ActivityCategory>(filter, ignoreCase: true, out var parsed))
            {
                return Results.Problem(
                    title: "Invalid filter",
                    detail: "'filter' must be 'Input', 'Output', or omitted.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            categoryFilter = parsed;
        }

        try
        {
            var weeks = await activityLogService.GetActivityLogAsync(
                fromUtc.Value, toUtc.Value, categoryFilter, ct);
            var wire = weeks.Select(MapWeek).ToList();
            return Results.Ok(wire);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GET /api/v1/activity-log failed");
            return Results.Problem(
                detail: "Failed to load activity log.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    internal static ActivityLogWeekDto MapWeek(ActivityLogWeek week)
    {
        // Weeks: WeekEnd in the service model is the Monday-anchored start + 6
        // days (i.e. Sunday at 00:00). The spec asks for Sunday 23:59:59 UTC so
        // the half-open interval reads naturally on the client.
        var weekEnd = week.WeekEnd.Date.AddDays(1).AddSeconds(-1);

        return new ActivityLogWeekDto
        {
            WeekStart = DateTime.SpecifyKind(week.WeekStart.Date, DateTimeKind.Utc),
            WeekEnd = DateTime.SpecifyKind(weekEnd, DateTimeKind.Utc),
            TotalMinutes = week.TotalMinutes,
            InputMinutes = week.InputMinutes,
            OutputMinutes = week.OutputMinutes,
            ActivityCount = week.ActivityCount,
            PlansCompleted = week.PlansCompleted,
            PlansTotal = week.PlansTotal,
            Days = week.Days.Select(MapDay).ToList(),
        };
    }

    internal static ActivityLogDayDto MapDay(ActivityLogDay day)
    {
        // Number generated plans within the day for display labels. Ad-hoc
        // plans are labeled separately.
        var generatedIdx = 0;
        var plans = new List<ActivityLogPlanDto>(day.Plans.Count);
        foreach (var plan in day.Plans)
        {
            var isAdhoc = plan.Items.Count > 0 && IsAdhocId(plan.Items[0].PlanItemId);
            var displayName = isAdhoc
                ? "Ad-hoc"
                : $"Plan {++generatedIdx}";

            plans.Add(new ActivityLogPlanDto
            {
                IsAdhoc = isAdhoc,
                PlanItemId = plan.Items.FirstOrDefault()?.PlanItemId ?? string.Empty,
                DisplayName = displayName,
                TotalMinutes = plan.TotalMinutesSpent,
                Completed = plan.IsFullyCompleted,
                Entries = plan.Items
                    .OrderBy(e => e.CompletedAt ?? DateTime.MaxValue)
                    .Select(MapEntry)
                    .ToList(),
            });
        }

        // Per-day input/output minute totals (the service computes these at the
        // week level but not the day level, so derive from entries).
        var inputMinutes = day.Plans.Sum(p => p.Items
            .Where(i => i.Category == ActivityCategory.Input)
            .Sum(i => i.MinutesSpent));
        var outputMinutes = day.Plans.Sum(p => p.Items
            .Where(i => i.Category == ActivityCategory.Output)
            .Sum(i => i.MinutesSpent));

        return new ActivityLogDayDto
        {
            Date = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            HasActivity = day.Plans.Count > 0,
            InputMinutes = inputMinutes,
            OutputMinutes = outputMinutes,
            TotalMinutes = day.TotalMinutes,
            AllPlansCompleted = day.AllPlansCompleted,
            Plans = plans,
        };
    }

    internal static ActivityLogEntryDto MapEntry(ActivityLogEntry entry)
    {
        var completedAt = entry.CompletedAt is { } ts
            ? DateTime.SpecifyKind(ts, DateTimeKind.Utc)
            : (DateTime?)null;
        // TitleKey/DescriptionKey are resource keys for now (e.g.
        // "PlanItemReadingTitle"). Spec activity-log-api-spec.md explicitly
        // permits this passthrough until server-side localization lands.
        var title = !string.IsNullOrEmpty(entry.TitleKey)
            ? entry.TitleKey
            : entry.ActivityType.ToString();
        var description = entry.DescriptionKey ?? string.Empty;
        return new ActivityLogEntryDto
        {
            PlanItemId = entry.PlanItemId ?? string.Empty,
            ActivityType = entry.ActivityType.ToString(),
            Category = entry.Category.ToString(),
            MinutesSpent = entry.MinutesSpent,
            EstimatedMinutes = entry.EstimatedMinutes,
            IsCompleted = entry.IsCompleted,
            CompletedAtUtc = completedAt,
            ResourceTitle = entry.ResourceTitle,
            SkillName = entry.SkillName,
            Title = title,
            Description = description,
        };
    }

    private static bool IsAdhocId(string planItemId)
        => !string.IsNullOrEmpty(planItemId)
           && planItemId.StartsWith("adhoc-", StringComparison.OrdinalIgnoreCase);
}
