using SentenceStudio.Application.Learners;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// Reads the learner settings the coach needs to plan a session.
/// The answer holds no name, no email, no key, and no identifier.
/// </summary>
public sealed class LearnerProfileSummaryTool : CoachToolBase
{
    private readonly ILearnerProfileQueries _profiles;
    private readonly IPlanDateContext _dates;

    public LearnerProfileSummaryTool(
        IUserScopeProvider userScope,
        ILearnerProfileQueries profiles,
        IPlanDateContext dates)
        : base(userScope)
    {
        _profiles = profiles;
        _dates = dates;
    }

    public override string ToolName => CoachToolNames.GetLearnerProfileSummary;

    /// <summary>
    /// Returns the languages, the display language, the preferred session length,
    /// and the level of the learner.
    /// </summary>
    public async Task<LearnerProfileSummary> GetAsync(CancellationToken ct = default)
    {
        var userProfileId = RequireUserProfileId();

        var row = await ReadAsync(userProfileId, ct);
        if (row is null)
        {
            throw new CoachToolException(
                CoachToolFailureKind.ProfileMissing, ToolName, "The learner has no settings record.");
        }

        var targetLanguages = SplitList(row.TargetLanguages);
        if (targetLanguages.Count == 0 && !string.IsNullOrWhiteSpace(row.TargetLanguage))
        {
            targetLanguages = [row.TargetLanguage];
        }

        var daysSinceStart = row.CreatedAt == default
            ? 0
            : Math.Max(0, (int)(_dates.UtcNow.Date - row.CreatedAt.ToUniversalTime().Date).TotalDays);

        return new LearnerProfileSummary(
            TargetLanguage: SanitizeMetadata(row.TargetLanguage, 40),
            TargetLanguages: targetLanguages,
            NativeLanguage: SanitizeMetadata(row.NativeLanguage, 40),
            DisplayLanguage: string.IsNullOrWhiteSpace(row.DisplayLanguage)
                ? null
                : SanitizeMetadata(row.DisplayLanguage, 40),
            PreferredSessionMinutes: row.PreferredSessionMinutes,
            TargetLevel: string.IsNullOrWhiteSpace(row.TargetCefrLevel)
                ? null
                : SanitizeMetadata(row.TargetCefrLevel, 20),
            DaysSinceStart: daysSinceStart,
            Scope: new CoachResultScope
            {
                Coverage = CoachScopeCoverage.SettingsSnapshot,
                Order = CoachScopeOrder.NotApplicable,
                OrderHonored = true,
                Filters = CoachScopeFilters.OwnerScoped,
                AsOfUtc = _dates.UtcNow,
                ReturnedCount = 1,
                DefinitionCode = CoachScopeDefinition.LearnerSettingsSnapshot,
                MinimumEvidence = CoachScopeMinimumEvidence.None,
                TieBreak = CoachScopeTieBreak.NotApplicable,
                ClockBasis = CoachScopeClockBasis.ServerUtcInstant,
                ReferenceMode = CoachScopeReferenceMode.AsOfInstant
            });
    }

    private async Task<LearnerProfileFacts?> ReadAsync(string userProfileId, CancellationToken ct)
    {
        try
        {
            return await _profiles.GetProfileFactsAsync(userProfileId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw DataAccessFailure(ex);
        }
    }

    private static List<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(10)
                .Select(v => SanitizeMetadata(v, 40))
                .Where(v => v.Length > 0)
                .ToList();
}
