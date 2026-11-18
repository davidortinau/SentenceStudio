using Microsoft.Extensions.Logging;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools.SamTools;

/// <summary>
/// Reads today's plan as the dashboard shows it: its items with their resource, skill and word
/// counts, and the plan's mastery figure. Never exposes plan rationale text.
/// </summary>
/// <remarks>
/// Reads through <see cref="IPlanService.GetTodayAsync"/>, the same reconstruction the dashboard
/// uses, so archived items from a regenerated plan and "Choose My Own" sessions never count.
/// </remarks>
public sealed class CurrentPlanSummaryTool : CoachToolBase
{
    private readonly CoachTodayPlanReader _plans;
    private readonly IPlanDateContext _dates;
    private readonly ILogger<CurrentPlanSummaryTool> _logger;

    public CurrentPlanSummaryTool(
        IUserScopeProvider userScope,
        CoachTodayPlanReader plans,
        IPlanDateContext dates,
        ILogger<CurrentPlanSummaryTool> logger)
        : base(userScope)
    {
        _plans = plans;
        _dates = dates;
        _logger = logger;
    }

    public override string ToolName => CoachToolNames.GetCurrentPlanSummary;

    public async Task<CurrentPlanSummary> GetAsync(CancellationToken ct = default)
    {
        RequireUserProfileId();

        try
        {
            var plan = await _plans.GetTodayAsync(ct);
            var day = _dates.UserLocalDate.ToString("yyyy-MM-dd");

            if (plan is null)
            {
                return new CurrentPlanSummary(
                    HasPlan: false, PlanDate: day, Strategy: null, Items: [], OverallCompletionPct: 0,
                    MinutesPlanned: 0, FocusWordCount: 0, AverageMasteryPct: null,
                    Scope: PlanScope(itemCount: 0));
            }

            var titles = (plan.Narrative?.Resources ?? [])
                .GroupBy(r => r.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);

            var items = plan.Items
                .OrderBy(i => i.Priority)
                .Select(i => new PlanItemSummary(
                    PlanItemId: SanitizeMetadata(i.Id, 80),
                    ActivityType: i.ActivityType,
                    IsCompleted: i.IsCompleted,
                    MinutesPlanned: i.EstimatedMinutes,
                    MinutesSpent: i.MinutesSpent,
                    ResourceId: i.ResourceId,
                    ResourceTitle: i.ResourceId is { } id && titles.TryGetValue(id, out var title)
                        ? SanitizeMetadata(title, 120)
                        : null,
                    SkillId: i.SkillId,
                    FocusWordCount: i.FocusVocabularyIds.Count))
                .ToList();

            var completedCount = items.Count(i => i.IsCompleted);
            var pct = items.Count > 0 ? Math.Round(100.0 * completedCount / items.Count, 1) : 0;
            var mastery = plan.Narrative?.VocabInsight?.AverageMastery is > 0 and var m
                ? Math.Round(m * 100.0, 0)
                : (double?)null;

            return new CurrentPlanSummary(
                HasPlan: true,
                PlanDate: day,
                Strategy: string.IsNullOrWhiteSpace(plan.Strategy) ? null : SanitizeMetadata(plan.Strategy, 80),
                Items: items,
                OverallCompletionPct: pct,
                MinutesPlanned: items.Sum(i => i.MinutesPlanned),
                FocusWordCount: PlanWordIds(plan).Count,
                AverageMasteryPct: mastery,
                Scope: PlanScope(items.Count));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw DataAccessFailure(ex); }
    }

    /// <summary>Every word the plan works on, plan-level first, in plan order, once each.</summary>
    internal static IReadOnlyList<string> PlanWordIds(SentenceStudio.Contracts.Plans.TodaysPlanDto plan) =>
        plan.FocusVocabularyIds
            .Concat(plan.Items.OrderBy(i => i.Priority).SelectMany(i => i.FocusVocabularyIds))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The terms this read answers under. One calendar day of the learner's own week, every item
    /// of today's plan, in the plan's own order (its priority), which is the order the dashboard
    /// lists them.
    /// </summary>
    private CoachResultScope PlanScope(int itemCount)
    {
        var day = _dates.UserLocalDate;

        return new CoachResultScope
        {
            Coverage = CoachScopeCoverage.SingleDay,
            Order = CoachScopeOrder.PriorityAscending,
            OrderHonored = true,
            Filters = CoachScopeFilters.OwnerScoped | CoachScopeFilters.CalendarDay,
            AsOfUtc = _dates.UtcNow,
            WindowStartDate = day,
            WindowEndDate = day,
            ReturnedCount = itemCount,
            MatchedCount = itemCount,
            DefinitionCode = CoachScopeDefinition.PlanDaySummary,
            EligiblePopulationCount = itemCount,
            MinimumEvidence = CoachScopeMinimumEvidence.None,
            TieBreak = CoachScopeTieBreak.None,
            ClockBasis = CoachScopeClockBasis.LearnerLocalDay,
            ReferenceMode = CoachScopeReferenceMode.CalendarDay
        };
    }
}
