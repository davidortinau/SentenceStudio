using SentenceStudio.Application.Practice;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// Reads the date of the learner's most recent recorded practice and the number of whole
/// days since then. The read crosses the learner's full history — it is not windowed.
/// </summary>
public sealed class PracticeHistorySummaryTool : CoachToolBase
{
    private readonly IPracticeHistoryQueries _history;
    private readonly IPlanDateContext _dates;

    public PracticeHistorySummaryTool(
        IUserScopeProvider userScope,
        IPracticeHistoryQueries history,
        IPlanDateContext dates)
        : base(userScope)
    {
        _history = history;
        _dates = dates;
    }

    public override string ToolName => CoachToolNames.GetPracticeHistorySummary;

    /// <summary>Returns the last practice date and days since, or nulls when no practice exists.</summary>
    public async Task<PracticeHistorySummary> GetAsync(CancellationToken ct = default)
    {
        var userProfileId = RequireUserProfileId();

        DateTime? lastUtc;
        try
        {
            lastUtc = await _history.GetLastPracticeUtcAsync(userProfileId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw DataAccessFailure(ex);
        }

        DateOnly? lastLocal = lastUtc.HasValue ? _dates.ToUserLocal(lastUtc.Value) : null;
        int? daysSince = lastLocal.HasValue
            ? _dates.UserLocalDate.DayNumber - lastLocal.Value.DayNumber
            : null;

        return new PracticeHistorySummary(
            LastPracticeDate: lastLocal,
            DaysSincePractice: daysSince,
            Scope: new CoachResultScope
            {
                Coverage = CoachScopeCoverage.DerivedProjection,
                Order = CoachScopeOrder.NotApplicable,
                OrderHonored = true,
                TieBreak = CoachScopeTieBreak.NotApplicable,
                Filters = CoachScopeFilters.OwnerScoped,
                MinimumEvidence = CoachScopeMinimumEvidence.None,
                AsOfUtc = _dates.UtcNow,
                ReturnedCount = lastLocal.HasValue ? 1 : 0,
                MatchedCount = lastLocal.HasValue ? 1 : 0,
                EligiblePopulationCount = lastLocal.HasValue ? 1 : 0,
                WithheldCount = 0,
                WithheldReason = CoachScopeWithheldReason.None,
                Truncated = false,
                DefinitionCode = CoachScopeDefinition.LatestPracticeSummary,
                ClockBasis = CoachScopeClockBasis.LearnerLocalDay,
                ReferenceMode = CoachScopeReferenceMode.AsOfInstant
            });
    }
}
