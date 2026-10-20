using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Features.ActivityLog;

internal sealed record ActivityLogRead(
    IReadOnlyList<ActivityLogWeek> Weeks, bool HasMore);

internal sealed class ActivityLogReader(
    IServiceScopeFactory scopes, ILogger<ActivityLogReader> logger)
{
    private static readonly string[] KnownActivityTypes =
        Enum.GetNames<PlanActivityType>();

    internal async Task<ActivityLogRead> ReadAsync(
        string? userId, DateTime toUtc, int weeksLoaded,
        ActivityCategory? filter, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            logger.LogWarning("Activity Log read refused without an active owner.");
            return new([], false);
        }
        if (toUtc.Kind != DateTimeKind.Utc || weeksLoaded < 1)
            throw new ArgumentException("Activity Log requires a UTC instant and a positive week count.");

        // The source requests eight weeks initially, then four more per tap.
        var fromDate = toUtc.AddDays(-weeksLoaded * 7).Date;
        var toDate = toUtc.Date;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var completions = await db.DailyPlanCompletions.AsNoTracking()
            .Where(row => row.UserProfileId == userId
                && row.Date >= fromDate && row.Date <= toDate)
            .OrderBy(row => row.Date)
            .ThenBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken);

        var eligibleTypes = filter is { } category
            ? KnownActivityTypes.Where(name =>
                ActivityCategoryMapper.Categorize(
                    Enum.Parse<PlanActivityType>(name)) == category).ToArray()
            : KnownActivityTypes;
        var hasMore = await db.DailyPlanCompletions.AsNoTracking()
            .AnyAsync(row => row.UserProfileId == userId
                && row.Date < fromDate
                && eligibleTypes.Contains(row.ActivityType), cancellationToken);
        if (completions.Count == 0)
            return new([], hasMore);

        var resourceIds = completions
            .Where(row => !string.IsNullOrWhiteSpace(row.ResourceId))
            .Select(row => row.ResourceId!).Distinct(StringComparer.Ordinal)
            .ToArray();
        var skillIds = completions
            .Where(row => !string.IsNullOrWhiteSpace(row.SkillId))
            .Select(row => row.SkillId!).Distinct(StringComparer.Ordinal)
            .ToArray();
        var resourceNames = await db.LearningResources.AsNoTracking()
            .Where(resource => resource.UserProfileId == userId
                && resourceIds.Contains(resource.Id))
            .ToDictionaryAsync(resource => resource.Id,
                resource => resource.Title, cancellationToken);
        // The source uses SkillProfileRepository.ListAsync's default, which
        // excludes archived skills even when an older completion is retained.
        var skillNames = await db.SkillProfiles.AsNoTracking()
            .Where(skill => skill.UserProfileId == userId && !skill.IsArchived
                && skillIds.Contains(skill.Id))
            .ToDictionaryAsync(skill => skill.Id,
                skill => skill.Title, cancellationToken);

        var days = new Dictionary<DateTime, ActivityLogDay>();
        foreach (var dayRows in completions.GroupBy(row => row.Date.Date))
        {
            var plans = new List<ActivityLogPlan>();
            var cluster = new List<DailyPlanCompletion>();
            foreach (var row in dayRows.OrderBy(row => row.CreatedAt)
                .ThenBy(row => row.Id))
            {
                if (cluster.Count > 0
                    && (row.CreatedAt - cluster[^1].CreatedAt).TotalSeconds > 60)
                {
                    AddPlan(cluster);
                    cluster.Clear();
                }
                cluster.Add(row);
            }
            AddPlan(cluster);
            if (plans.Count == 0)
                continue;
            days.Add(dayRows.Key, new ActivityLogDay(
                dayRows.Key, plans, plans.Sum(plan => plan.TotalMinutesSpent),
                plans.Any(plan => plan.Items.Any(entry =>
                    entry.Category == ActivityCategory.Input)),
                plans.Any(plan => plan.Items.Any(entry =>
                    entry.Category == ActivityCategory.Output)),
                plans.All(plan => plan.IsFullyCompleted)));

            void AddPlan(List<DailyPlanCompletion> rows)
            {
                if (rows.Count == 0)
                    return;
                var entries = new List<ActivityLogEntry>();
                foreach (var completion in rows)
                {
                    if (!Enum.TryParse<PlanActivityType>(
                            completion.ActivityType, out var type)
                        || !Enum.IsDefined(type))
                    {
                        logger.LogWarning(
                            "Activity Log omitted an unknown activity type on completion {CompletionId}.",
                            completion.Id);
                        continue;
                    }
                    var activityCategory = ActivityCategoryMapper.Categorize(type);
                    if (filter is { } selected && selected != activityCategory)
                        continue;
                    resourceNames.TryGetValue(completion.ResourceId ?? "", out var resource);
                    skillNames.TryGetValue(completion.SkillId ?? "", out var skill);
                    entries.Add(new ActivityLogEntry(
                        completion.PlanItemId, type, activityCategory,
                        completion.MinutesSpent, completion.EstimatedMinutes,
                        completion.IsCompleted, completion.CompletedAt,
                        resource, skill, completion.TitleKey,
                        completion.DescriptionKey));
                }
                if (entries.Count == 0)
                    return;
                plans.Add(new ActivityLogPlan(
                    rows[0].CreatedAt, entries,
                    entries.Count(entry => entry.IsCompleted), entries.Count,
                    entries.All(entry => entry.IsCompleted),
                    entries.Sum(entry => entry.MinutesSpent),
                    entries.Sum(entry => entry.EstimatedMinutes),
#pragma warning disable CS0618 // Source history still projects legacy plan copy.
                    rows[0].Rationale, rows[0].NarrativeJson));
#pragma warning restore CS0618
            }
        }

        // Owned rows anchor the week grid even when this category has no entries.
        var weeks = new List<ActivityLogWeek>();
        var start = fromDate;
        while (start.DayOfWeek != DayOfWeek.Monday)
            start = start.AddDays(-1);
        for (; start <= toDate; start = start.AddDays(7))
        {
            var weekDays = Enumerable.Range(0, 7)
                .Select(offset =>
                {
                    var date = start.AddDays(offset);
                    return days.TryGetValue(date, out var day)
                        ? day : new ActivityLogDay(date, [], 0, false, false, false);
                })
                .ToArray();
            weeks.Add(new ActivityLogWeek(
                start, start.AddDays(6), weekDays,
                weekDays.Sum(day => day.TotalMinutes),
                weekDays.Sum(day => day.Plans.Sum(plan => plan.Items
                    .Where(entry => entry.Category == ActivityCategory.Input)
                    .Sum(entry => entry.MinutesSpent))),
                weekDays.Sum(day => day.Plans.Sum(plan => plan.Items
                    .Where(entry => entry.Category == ActivityCategory.Output)
                    .Sum(entry => entry.MinutesSpent))),
                weekDays.Sum(day => day.Plans.Sum(plan => plan.Items.Count)),
                weekDays.Sum(day => day.Plans.Count(plan => plan.IsFullyCompleted)),
                weekDays.Sum(day => day.Plans.Count)));
        }
        weeks.Reverse();
        return new(weeks, hasMore);
    }
}
