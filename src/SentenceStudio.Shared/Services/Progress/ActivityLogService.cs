using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services.Progress;

/// <summary>
/// Computes the per-week Activity Log rollup from <c>DailyPlanCompletions</c>.
/// Extracted from <see cref="ProgressService"/> so the API can serve it
/// without depending on the full progress graph (LLM plan generator,
/// vocabulary services, sync, etc.). MAUI's <see cref="IProgressService"/>
/// delegates here to keep a single source of truth for the rollup logic.
/// </summary>
public interface IActivityLogService
{
    Task<List<ActivityLogWeek>> GetActivityLogAsync(
        DateTime fromUtc,
        DateTime toUtc,
        ActivityCategory? filter = null,
        CancellationToken ct = default);
}

public sealed class ActivityLogService : IActivityLogService
{
    private readonly LearningResourceRepository _resourceRepo;
    private readonly SkillProfileRepository _skillRepo;
    private readonly IUserScopeProvider _userScope;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ActivityLogService> _logger;

    public ActivityLogService(
        LearningResourceRepository resourceRepo,
        SkillProfileRepository skillRepo,
        IUserScopeProvider userScope,
        IServiceProvider serviceProvider,
        ILogger<ActivityLogService> logger)
    {
        _resourceRepo = resourceRepo;
        _skillRepo = skillRepo;
        _userScope = userScope;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<List<ActivityLogWeek>> GetActivityLogAsync(
        DateTime fromUtc,
        DateTime toUtc,
        ActivityCategory? filter = null,
        CancellationToken ct = default)
    {
        _logger.LogDebug("📅 GetActivityLogAsync — from={From:yyyy-MM-dd}, to={To:yyyy-MM-dd}, filter={Filter}", fromUtc, toUtc, filter);

        if (!_userScope.TryGetUserProfileId(out var userProfileId) || string.IsNullOrEmpty(userProfileId))
        {
            _logger.LogWarning("❌ No active user profile — cannot get activity log");
            return new List<ActivityLogWeek>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var completions = await db.DailyPlanCompletions
            .Where(c => c.UserProfileId == userProfileId && c.Date >= fromUtc.Date && c.Date <= toUtc.Date)
            .OrderBy(c => c.Date)
            .ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);

        if (!completions.Any())
        {
            _logger.LogDebug("ℹ️ No completions found in date range");
            return new List<ActivityLogWeek>();
        }

        var resources = await _resourceRepo.GetAllResourcesLightweightAsync();
        var resourceLookup = resources
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.First().Title ?? string.Empty);

        var skills = await _skillRepo.ListAsync();
        var skillLookup = skills
            .GroupBy(s => s.Id)
            .ToDictionary(g => g.Key, g => g.First().Title ?? string.Empty);

        var dayGroups = completions
            .GroupBy(c => c.Date.Date)
            .OrderBy(g => g.Key)
            .ToList();

        var activityLogDays = new List<ActivityLogDay>();

        foreach (var dayGroup in dayGroups)
        {
            var date = dayGroup.Key;
            var dayCompletions = dayGroup.OrderBy(c => c.CreatedAt).ToList();

            // Sub-group by plan generation (cluster items within 60 seconds of each other)
            var plans = new List<ActivityLogPlan>();
            if (dayCompletions.Any())
            {
                var currentPlan = new List<DailyPlanCompletion> { dayCompletions[0] };
                for (int i = 1; i < dayCompletions.Count; i++)
                {
                    if ((dayCompletions[i].CreatedAt - dayCompletions[i - 1].CreatedAt).TotalSeconds <= 60)
                        currentPlan.Add(dayCompletions[i]);
                    else
                    {
                        plans.Add(BuildActivityLogPlan(currentPlan, resourceLookup, skillLookup, filter));
                        currentPlan = new List<DailyPlanCompletion> { dayCompletions[i] };
                    }
                }
                plans.Add(BuildActivityLogPlan(currentPlan, resourceLookup, skillLookup, filter));
            }

            // Drop plans whose entries were entirely filtered out
            plans = plans.Where(p => p.Items.Any()).ToList();
            if (!plans.Any())
                continue;

            var totalMinutes = plans.Sum(p => p.TotalMinutesSpent);
            var hasInput = plans.Any(p => p.Items.Any(i => i.Category == ActivityCategory.Input));
            var hasOutput = plans.Any(p => p.Items.Any(i => i.Category == ActivityCategory.Output));
            var allPlansCompleted = plans.All(p => p.IsFullyCompleted);

            activityLogDays.Add(new ActivityLogDay(
                Date: date,
                Plans: plans,
                TotalMinutes: totalMinutes,
                HasInput: hasInput,
                HasOutput: hasOutput,
                AllPlansCompleted: allPlansCompleted));
        }

        var weeks = BuildWeeks(fromUtc.Date, toUtc.Date, activityLogDays);

        _logger.LogDebug("✅ Built {WeekCount} weeks with {DayCount} active days", weeks.Count, activityLogDays.Count);

        return weeks;
    }

    private ActivityLogPlan BuildActivityLogPlan(
        List<DailyPlanCompletion> completions,
        Dictionary<string, string> resourceLookup,
        Dictionary<string, string> skillLookup,
        ActivityCategory? filter)
    {
        var entries = new List<ActivityLogEntry>();

        foreach (var completion in completions)
        {
            if (!Enum.TryParse<PlanActivityType>(completion.ActivityType, out var actType))
            {
                _logger.LogWarning("⚠️ Failed to parse ActivityType '{ActivityType}' for completion {Id}", completion.ActivityType, completion.Id);
                continue;
            }

            var category = ActivityCategoryMapper.Categorize(actType);
            if (filter.HasValue && category != filter.Value)
                continue;

            var resourceTitle = !string.IsNullOrEmpty(completion.ResourceId) && resourceLookup.TryGetValue(completion.ResourceId, out var title)
                ? title : null;
            var skillName = !string.IsNullOrEmpty(completion.SkillId) && skillLookup.TryGetValue(completion.SkillId, out var skill)
                ? skill : null;

            entries.Add(new ActivityLogEntry(
                PlanItemId: completion.PlanItemId,
                ActivityType: actType,
                Category: category,
                MinutesSpent: completion.MinutesSpent,
                EstimatedMinutes: completion.EstimatedMinutes,
                IsCompleted: completion.IsCompleted,
                CompletedAt: completion.CompletedAt,
                ResourceTitle: resourceTitle,
                SkillName: skillName,
                TitleKey: completion.TitleKey,
                DescriptionKey: completion.DescriptionKey));
        }

        var generatedAt = completions.FirstOrDefault()?.CreatedAt ?? DateTime.UtcNow;
#pragma warning disable CS0618 // Rationale/NarrativeJson on completion are deprecated but still readable.
        var rationale = completions.FirstOrDefault()?.Rationale;
        var narrativeJson = completions.FirstOrDefault()?.NarrativeJson;
#pragma warning restore CS0618

        return new ActivityLogPlan(
            GeneratedAt: generatedAt,
            Items: entries,
            CompletedCount: entries.Count(e => e.IsCompleted),
            TotalCount: entries.Count,
            IsFullyCompleted: entries.Any() && entries.All(e => e.IsCompleted),
            TotalMinutesSpent: entries.Sum(e => e.MinutesSpent),
            TotalEstimatedMinutes: entries.Sum(e => e.EstimatedMinutes),
            Rationale: rationale,
            NarrativeJson: narrativeJson);
    }

    private static List<ActivityLogWeek> BuildWeeks(DateTime fromDate, DateTime toDate, List<ActivityLogDay> activityLogDays)
    {
        var dayLookup = activityLogDays.ToDictionary(d => d.Date.Date, d => d);
        var weeks = new List<ActivityLogWeek>();
        var current = fromDate.Date;

        // Anchor to Monday
        while (current.DayOfWeek != DayOfWeek.Monday && current <= toDate)
            current = current.AddDays(-1);

        while (current <= toDate)
        {
            var weekStart = current;
            var weekEnd = weekStart.AddDays(6);

            var days = new ActivityLogDay[7];
            for (int i = 0; i < 7; i++)
            {
                var date = weekStart.AddDays(i);
                days[i] = dayLookup.TryGetValue(date, out var day)
                    ? day
                    : new ActivityLogDay(
                        Date: date,
                        Plans: new List<ActivityLogPlan>(),
                        TotalMinutes: 0,
                        HasInput: false,
                        HasOutput: false,
                        AllPlansCompleted: false);
            }

            var totalMinutes = days.Sum(d => d.TotalMinutes);
            var inputMinutes = days.Sum(d => d.Plans.Sum(p => p.Items.Where(i => i.Category == ActivityCategory.Input).Sum(i => i.MinutesSpent)));
            var outputMinutes = days.Sum(d => d.Plans.Sum(p => p.Items.Where(i => i.Category == ActivityCategory.Output).Sum(i => i.MinutesSpent)));
            var activityCount = days.Sum(d => d.Plans.Sum(p => p.Items.Count));
            var plansCompleted = days.Sum(d => d.Plans.Count(p => p.IsFullyCompleted));
            var plansTotal = days.Sum(d => d.Plans.Count);

            weeks.Add(new ActivityLogWeek(
                WeekStart: weekStart,
                WeekEnd: weekEnd,
                Days: days,
                TotalMinutes: totalMinutes,
                InputMinutes: inputMinutes,
                OutputMinutes: outputMinutes,
                ActivityCount: activityCount,
                PlansCompleted: plansCompleted,
                PlansTotal: plansTotal));

            current = weekEnd.AddDays(1);
        }

        weeks.Reverse(); // newest first
        return weeks;
    }
}
