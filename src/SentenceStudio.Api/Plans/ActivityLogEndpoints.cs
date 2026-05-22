using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Api.Plans;

public static class ActivityLogEndpoints
{
    private const string AdHocPrefix = "adhoc-";
    private const int PlanClusterSeconds = 60;

    public static IEndpointRouteBuilder MapActivityLog(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/activity-log", GetActivityLogAsync)
            .RequireAuthorization()
            .WithName("GetActivityLog");

        return app;
    }

    private static async Task<IResult> GetActivityLogAsync(
        [FromQuery] string? fromUtc,
        [FromQuery] string? toUtc,
        [FromQuery] string? filter,
        IUserScopeProvider userScope,
        ApplicationDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!TryParseUtcInstant(fromUtc, out var from))
        {
            return Results.Problem(
                title: "Invalid fromUtc",
                detail: "fromUtc is required and must be a valid ISO UTC instant.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!TryParseUtcInstant(toUtc, out var to))
        {
            return Results.Problem(
                title: "Invalid toUtc",
                detail: "toUtc is required and must be a valid ISO UTC instant.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (from > to)
        {
            return Results.Problem(
                title: "Invalid date range",
                detail: "fromUtc must be earlier than or equal to toUtc.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!TryParseActivityFilter(filter, out var categoryFilter))
        {
            return Results.Problem(
                title: "Invalid filter",
                detail: "filter must be either Input or Output.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var userId = userScope.UserProfileId;
            var fromDate = from.Date;
            var toDate = to.Date;

            var completions = await db.DailyPlanCompletions.AsNoTracking()
                .Where(c => c.UserProfileId == userId
                    && c.Date >= fromDate
                    && c.Date <= toDate)
                .OrderBy(c => c.Date)
                .ThenBy(c => c.CreatedAt)
                .ThenBy(c => c.PlanItemId)
                .ToListAsync(ct);

            if (completions.Count == 0)
            {
                return Results.Ok(Array.Empty<ActivityLogWeekResponse>());
            }

            var resourceIds = completions
                .Select(c => c.ResourceId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var skillIds = completions
                .Select(c => c.SkillId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var resourceLookup = await db.LearningResources.AsNoTracking()
                .Where(r => r.UserProfileId == userId && resourceIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.Title, ct);

            var skillLookup = await db.SkillProfiles.AsNoTracking()
                .Where(s => s.UserProfileId == userId && skillIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Title, ct);

            var activeDays = BuildDays(completions, resourceLookup, skillLookup, categoryFilter);
            if (activeDays.Count == 0)
            {
                return Results.Ok(Array.Empty<ActivityLogWeekResponse>());
            }

            var weeks = BuildWeeks(fromDate, toDate, activeDays);
            return Results.Ok(weeks);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("ActivityLog")
                .LogError(ex, "GET /api/v1/activity-log failed");
            return Results.Problem(
                detail: "Failed to load activity log.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Dictionary<DateTime, ActivityLogDayResponse> BuildDays(
        List<SentenceStudio.Shared.Models.DailyPlanCompletion> completions,
        Dictionary<string, string?> resourceLookup,
        Dictionary<string, string?> skillLookup,
        ActivityCategory? filter)
    {
        var days = new Dictionary<DateTime, ActivityLogDayResponse>();

        foreach (var dayGroup in completions.GroupBy(c => c.Date.Date).OrderBy(g => g.Key))
        {
            var plans = BuildPlanGroups(dayGroup.OrderBy(c => c.CreatedAt).ThenBy(c => c.PlanItemId).ToList(), resourceLookup, skillLookup, filter);
            if (plans.Count == 0)
            {
                continue;
            }

            var inputMinutes = plans.Sum(p => p.Entries.Where(e => e.Category == nameof(ActivityCategory.Input)).Sum(e => e.MinutesSpent));
            var outputMinutes = plans.Sum(p => p.Entries.Where(e => e.Category == nameof(ActivityCategory.Output)).Sum(e => e.MinutesSpent));

            days[dayGroup.Key] = new ActivityLogDayResponse(
                Date: DateOnly.FromDateTime(dayGroup.Key),
                HasActivity: true,
                InputMinutes: inputMinutes,
                OutputMinutes: outputMinutes,
                TotalMinutes: inputMinutes + outputMinutes,
                AllPlansCompleted: plans.Count > 0 && plans.All(p => p.Completed),
                Plans: plans);
        }

        return days;
    }

    private static List<ActivityLogPlanResponse> BuildPlanGroups(
        List<SentenceStudio.Shared.Models.DailyPlanCompletion> dayCompletions,
        Dictionary<string, string?> resourceLookup,
        Dictionary<string, string?> skillLookup,
        ActivityCategory? filter)
    {
        var groups = new List<(bool IsAdHoc, DateTime GeneratedAt, string PlanItemId, List<ActivityLogEntryResponse> Entries)>();

        foreach (var plannedCluster in BuildPlannedClusters(dayCompletions.Where(c => !IsAdHoc(c.PlanItemId)).ToList()))
        {
            var entries = plannedCluster
                .Select(c => MapEntry(c, resourceLookup, skillLookup, filter))
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();

            if (entries.Count > 0)
            {
                groups.Add((false, plannedCluster[0].CreatedAt, plannedCluster.Count == 1 ? plannedCluster[0].PlanItemId : string.Empty, entries));
            }
        }

        foreach (var completion in dayCompletions.Where(c => IsAdHoc(c.PlanItemId)).OrderBy(c => c.CreatedAt).ThenBy(c => c.PlanItemId))
        {
            var entry = MapEntry(completion, resourceLookup, skillLookup, filter);
            if (entry is not null)
            {
                groups.Add((true, completion.CreatedAt, completion.PlanItemId, new List<ActivityLogEntryResponse> { entry }));
            }
        }

        var planNumber = 0;
        return groups
            .OrderBy(g => g.GeneratedAt)
            .ThenBy(g => g.PlanItemId, StringComparer.Ordinal)
            .Select(g =>
            {
                var displayName = g.IsAdHoc ? "Ad-hoc" : $"Plan {++planNumber}";
                return new ActivityLogPlanResponse(
                    IsAdhoc: g.IsAdHoc,
                    PlanItemId: g.PlanItemId,
                    DisplayName: displayName,
                    TotalMinutes: g.Entries.Sum(e => e.MinutesSpent),
                    Completed: g.Entries.Count > 0 && g.Entries.All(e => e.IsCompleted),
                    Entries: g.Entries);
            })
            .ToList();
    }

    private static List<List<SentenceStudio.Shared.Models.DailyPlanCompletion>> BuildPlannedClusters(
        List<SentenceStudio.Shared.Models.DailyPlanCompletion> planned)
    {
        var clusters = new List<List<SentenceStudio.Shared.Models.DailyPlanCompletion>>();
        if (planned.Count == 0)
        {
            return clusters;
        }

        var current = new List<SentenceStudio.Shared.Models.DailyPlanCompletion> { planned[0] };
        for (var i = 1; i < planned.Count; i++)
        {
            if ((planned[i].CreatedAt - planned[i - 1].CreatedAt).TotalSeconds <= PlanClusterSeconds)
            {
                current.Add(planned[i]);
            }
            else
            {
                clusters.Add(current);
                current = new List<SentenceStudio.Shared.Models.DailyPlanCompletion> { planned[i] };
            }
        }
        clusters.Add(current);
        return clusters;
    }

    private static ActivityLogEntryResponse? MapEntry(
        SentenceStudio.Shared.Models.DailyPlanCompletion completion,
        Dictionary<string, string?> resourceLookup,
        Dictionary<string, string?> skillLookup,
        ActivityCategory? filter)
    {
        if (!Enum.TryParse<PlanActivityType>(completion.ActivityType, ignoreCase: false, out var activityType))
        {
            return null;
        }

        var category = ActivityCategoryMapper.Categorize(activityType);
        if (filter.HasValue && category != filter.Value)
        {
            return null;
        }

        var resourceTitle = !string.IsNullOrWhiteSpace(completion.ResourceId)
            && resourceLookup.TryGetValue(completion.ResourceId, out var title)
            ? title
            : null;
        var skillName = !string.IsNullOrWhiteSpace(completion.SkillId)
            && skillLookup.TryGetValue(completion.SkillId, out var skill)
            ? skill
            : null;

        return new ActivityLogEntryResponse(
            PlanItemId: completion.PlanItemId,
            ActivityType: activityType.ToString(),
            Category: category.ToString(),
            MinutesSpent: completion.MinutesSpent,
            EstimatedMinutes: completion.EstimatedMinutes,
            IsCompleted: completion.IsCompleted,
            CompletedAtUtc: completion.CompletedAt is null
                ? null
                : DateTime.SpecifyKind(completion.CompletedAt.Value, DateTimeKind.Utc),
            ResourceTitle: resourceTitle,
            SkillName: skillName,
            Title: completion.TitleKey,
            Description: completion.DescriptionKey);
    }

    private static List<ActivityLogWeekResponse> BuildWeeks(
        DateTime fromDate,
        DateTime toDate,
        Dictionary<DateTime, ActivityLogDayResponse> activeDays)
    {
        var firstMonday = StartOfWeek(fromDate);
        var weeks = new List<ActivityLogWeekResponse>();

        for (var weekStart = firstMonday; weekStart <= toDate; weekStart = weekStart.AddDays(7))
        {
            var weekEndDate = weekStart.AddDays(6);
            var days = Enumerable.Range(0, 7)
                .Select(offset =>
                {
                    var date = weekStart.AddDays(offset);
                    return activeDays.TryGetValue(date, out var active)
                        ? active
                        : EmptyDay(DateOnly.FromDateTime(date));
                })
                .ToList();

            weeks.Add(new ActivityLogWeekResponse(
                WeekStart: DateTime.SpecifyKind(weekStart, DateTimeKind.Utc),
                WeekEnd: DateTime.SpecifyKind(weekEndDate.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Utc),
                TotalMinutes: days.Sum(d => d.TotalMinutes),
                InputMinutes: days.Sum(d => d.InputMinutes),
                OutputMinutes: days.Sum(d => d.OutputMinutes),
                ActivityCount: days.Sum(d => d.Plans.Sum(p => p.Entries.Count)),
                PlansCompleted: days.Sum(d => d.Plans.Count(p => p.Completed)),
                PlansTotal: days.Sum(d => d.Plans.Count),
                Days: days));
        }

        weeks.Reverse();
        return weeks;
    }

    private static ActivityLogDayResponse EmptyDay(DateOnly date) =>
        new(
            Date: date,
            HasActivity: false,
            InputMinutes: 0,
            OutputMinutes: 0,
            TotalMinutes: 0,
            AllPlansCompleted: false,
            Plans: new List<ActivityLogPlanResponse>());

    private static DateTime StartOfWeek(DateTime date)
    {
        var current = date.Date;
        while (current.DayOfWeek != DayOfWeek.Monday)
        {
            current = current.AddDays(-1);
        }

        return current;
    }

    private static bool IsAdHoc(string planItemId) =>
        planItemId.StartsWith(AdHocPrefix, StringComparison.Ordinal);

    private static bool TryParseUtcInstant(string? value, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(value)
            || !DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        utc = parsed.UtcDateTime;
        return true;
    }

    private static bool TryParseActivityFilter(string? value, out ActivityCategory? filter)
    {
        filter = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse<ActivityCategory>(value, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            return false;
        }

        filter = parsed;
        return true;
    }
}

public sealed record ActivityLogWeekResponse(
    DateTime WeekStart,
    DateTime WeekEnd,
    int TotalMinutes,
    int InputMinutes,
    int OutputMinutes,
    int ActivityCount,
    int PlansCompleted,
    int PlansTotal,
    List<ActivityLogDayResponse> Days);

public sealed record ActivityLogDayResponse(
    DateOnly Date,
    bool HasActivity,
    int InputMinutes,
    int OutputMinutes,
    int TotalMinutes,
    bool AllPlansCompleted,
    List<ActivityLogPlanResponse> Plans);

public sealed record ActivityLogPlanResponse(
    bool IsAdhoc,
    string PlanItemId,
    string DisplayName,
    int TotalMinutes,
    bool Completed,
    List<ActivityLogEntryResponse> Entries);

public sealed record ActivityLogEntryResponse(
    string PlanItemId,
    string ActivityType,
    string Category,
    int MinutesSpent,
    int EstimatedMinutes,
    bool IsCompleted,
    DateTime? CompletedAtUtc,
    string? ResourceTitle,
    string? SkillName,
    string Title,
    string Description);
