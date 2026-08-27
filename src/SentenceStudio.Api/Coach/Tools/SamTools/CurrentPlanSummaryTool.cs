using Microsoft.Extensions.Logging;
using SentenceStudio.Application.Practice;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools.SamTools;

/// <summary>
/// Reads today's daily plan and completion status. Never exposes plan rationale text.
/// </summary>
public sealed class CurrentPlanSummaryTool : CoachToolBase
{
    private readonly IPracticeHistoryQueries _history;
    private readonly IPlanDateContext _dates;
    private readonly ILogger<CurrentPlanSummaryTool> _logger;

    public CurrentPlanSummaryTool(
        IUserScopeProvider userScope,
        IPracticeHistoryQueries history,
        IPlanDateContext dates,
        ILogger<CurrentPlanSummaryTool> logger)
        : base(userScope)
    {
        _history = history;
        _dates = dates;
        _logger = logger;
    }

    public override string ToolName => CoachToolNames.GetCurrentPlanSummary;

    public async Task<CurrentPlanSummary> GetAsync(CancellationToken ct = default)
    {
        var userId = RequireUserProfileId();

        try
        {
            var today = _dates.UserLocalDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

            var plan = await _history.GetPlanForDateAsync(userId, today, ct);

            if (plan is null)
            {
                return new CurrentPlanSummary(
                    HasPlan: false,
                    PlanDate: _dates.UserLocalDate.ToString("yyyy-MM-dd"),
                    Strategy: null,
                    Items: [],
                    OverallCompletionPct: 0,
                    Scope: PlanScope(itemCount: 0));
            }

            var completions = await _history.GetPlanItemsForDateAsync(userId, today, ct);

            var items = completions.Select(c => new PlanItemSummary(
                ActivityType: c.ActivityType,
                IsCompleted: c.IsCompleted,
                MinutesPlanned: c.EstimatedMinutes,
                MinutesSpent: c.MinutesSpent
            )).ToList();

            var completedCount = items.Count(i => i.IsCompleted);
            var pct = items.Count > 0 ? Math.Round(100.0 * completedCount / items.Count, 1) : 0;

            return new CurrentPlanSummary(
                HasPlan: true,
                PlanDate: _dates.UserLocalDate.ToString("yyyy-MM-dd"),
                Strategy: plan.Strategy is null ? null : SanitizeMetadata(plan.Strategy, 80),
                Items: items,
                OverallCompletionPct: pct,
                Scope: PlanScope(items.Count));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw DataAccessFailure(ex); }
    }

    /// <summary>
    /// The terms this read answers under. One calendar day of the learner's own week, every
    /// logged item on it, and no order the caller may rely on — the items come back in whatever
    /// order the store held them, and claiming a plan order the answer does not have would invite
    /// the model to narrate "start with" from an accident.
    /// </summary>
    private CoachResultScope PlanScope(int itemCount)
    {
        var day = _dates.UserLocalDate;

        return new CoachResultScope
        {
            Coverage = CoachScopeCoverage.SingleDay,
            Order = CoachScopeOrder.Unordered,
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
