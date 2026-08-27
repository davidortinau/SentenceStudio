using SentenceStudio.Application.Practice;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// Reads the balance of input work and output work over a stated window.
/// The window is seven, fourteen, or thirty days. No other window is allowed.
/// Every value is an aggregate.
/// </summary>
public sealed class PracticeBalanceTool : CoachToolBase
{
    private readonly IPracticeHistoryQueries _history;
    private readonly IPlanDateContext _dates;

    public PracticeBalanceTool(
        IUserScopeProvider userScope,
        IPracticeHistoryQueries history,
        IPlanDateContext dates)
        : base(userScope)
    {
        _history = history;
        _dates = dates;
    }

    public override string ToolName => CoachToolNames.GetPracticeBalance;

    /// <summary>Returns the minutes, the counts, and the attempts in the window.</summary>
    public async Task<PracticeBalanceSummary> GetAsync(
        CoachPracticeWindow window,
        CancellationToken ct = default)
    {
        var userProfileId = RequireUserProfileId();

        if (!Enum.IsDefined(window))
        {
            throw InvalidArgument("The window must be seven, fourteen, or thirty days.");
        }

        var days = window.ToDays();
        var endDate = _dates.UserLocalDate;
        var startDate = endDate.AddDays(-(days - 1));
        var startUtc = _dates.ToUtcMidnight(startDate);
        var endUtc = _dates.ToUtcMidnight(endDate.AddDays(1));

        IReadOnlyList<PracticeCompletionFacts> completions;
        int attemptCount;
        try
        {
            completions = await _history.GetCompletionsInRangeAsync(userProfileId, startUtc, endUtc, ct);
            attemptCount = await _history.CountActivityAttemptsAsync(userProfileId, startUtc, endUtc, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw DataAccessFailure(ex);
        }

        var byActivity = completions
            .GroupBy(c => c.ActivityType ?? string.Empty)
            .Select(g => new PracticeActivityTotal(
                ActivityType: SanitizeMetadata(g.Key, 40),
                Channel: ClassifyChannel(g.Key),
                Minutes: g.Sum(x => Math.Max(0, x.MinutesSpent)),
                CompletedCount: g.Count(x => x.IsCompleted)))
            .ToList();

        // Before the "some work happened" filter below, so the scope can report how many activity
        // types appeared in the window against how many survived into the answer.
        var distinctActivityTypes = byActivity.Count;

        byActivity = byActivity
            .Where(t => t.Minutes > 0 || t.CompletedCount > 0)
            .OrderByDescending(t => t.Minutes)
            .ThenBy(t => t.ActivityType, StringComparer.Ordinal)
            .ToList();

        // The gap the evidence bar opened. It has to be reported, not merely computable: a model
        // shown five matched and three returned with nothing paged fills the silence with a paging
        // boundary and offers to fetch the rest, which would return nothing forever.
        var withheldActivityTypes = distinctActivityTypes - byActivity.Count;

        var activeDays = completions
            .Where(c => c.MinutesSpent > 0)
            .Select(c => _dates.ToUserLocal(c.Date))
            .Distinct()
            .Count();

        return new PracticeBalanceSummary(
            WindowDays: days,
            WindowStartDate: startDate,
            WindowEndDate: endDate,
            InputMinutes: byActivity.Where(t => t.Channel == CoachPracticeChannel.Input).Sum(t => t.Minutes),
            OutputMinutes: byActivity.Where(t => t.Channel == CoachPracticeChannel.Output).Sum(t => t.Minutes),
            MixedMinutes: byActivity.Where(t => t.Channel == CoachPracticeChannel.Mixed).Sum(t => t.Minutes),
            TotalMinutes: byActivity.Sum(t => t.Minutes),
            ActiveDayCount: activeDays,
            AttemptCount: attemptCount,
            ByActivityType: byActivity,
            Scope: new CoachResultScope
            {
                Coverage = CoachScopeCoverage.WindowBounded,
                Order = CoachScopeOrder.MinutesDescending,
                OrderHonored = true,

                // MinimumEvidence is carried whether or not the bar dropped anything, the same way
                // every other filter is. Filter present with no count means the bar was applied
                // and nothing failed it; filter absent would mean the read has no bar at all.
                Filters = CoachScopeFilters.OwnerScoped
                    | CoachScopeFilters.DateWindow
                    | CoachScopeFilters.MinimumEvidence,
                AsOfUtc = _dates.UtcNow,
                WindowStartDate = startDate,
                WindowEndDate = endDate,

                // One population throughout: activity types that appeared in the window. Matched
                // counts them all, withheld counts the ones with nothing logged, eligible and
                // returned count the survivors. Nothing is paged, so matched == returned +
                // withheld and the model can see the arithmetic close.
                ReturnedCount = byActivity.Count,
                MatchedCount = distinctActivityTypes,
                WithheldCount = withheldActivityTypes,
                WithheldReason = withheldActivityTypes > 0
                    ? CoachScopeWithheldReason.BelowMinimumEvidence
                    : CoachScopeWithheldReason.None,
                Truncated = false,
                DefinitionCode = CoachScopeDefinition.PracticeWindowBalance,

                // The types that cleared the bar, not the completion rows behind them. Counting
                // rows here reported more eligible than matched for any learner who logged the
                // same activity twice, which is most of them.
                EligiblePopulationCount = byActivity.Count,
                MinimumEvidence = CoachScopeMinimumEvidence.LoggedWorkRequired,
                TieBreak = CoachScopeTieBreak.ActivityTypeOrdinal,
                // The window is the learner's own run of calendar days, converted to UTC bounds
                // only to query. Reporting it as a UTC window would move the boundary for anyone
                // who is not on UTC.
                ClockBasis = CoachScopeClockBasis.LearnerLocalDay,
                ReferenceMode = CoachScopeReferenceMode.DateWindow
            });
    }

    /// <summary>
    /// Maps an activity type to a channel.
    /// Input covers comprehension. Output covers production.
    /// Mixed covers recognition and retrieval work that uses both channels.
    /// An unknown name maps to Mixed, so a new activity never inflates one side.
    /// </summary>
    internal static CoachPracticeChannel ClassifyChannel(string? activityType)
    {
        if (!Enum.TryParse<PlanActivityType>(activityType, ignoreCase: false, out var parsed))
        {
            return CoachPracticeChannel.Mixed;
        }

        return parsed switch
        {
            PlanActivityType.Reading or PlanActivityType.Listening or PlanActivityType.VideoWatching
                => CoachPracticeChannel.Input,
            PlanActivityType.Writing or PlanActivityType.SceneDescription or PlanActivityType.Conversation
                or PlanActivityType.Shadowing or PlanActivityType.Translation
                => CoachPracticeChannel.Output,
            _ => CoachPracticeChannel.Mixed
        };
    }
}
