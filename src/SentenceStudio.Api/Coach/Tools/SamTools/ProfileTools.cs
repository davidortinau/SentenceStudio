using Microsoft.Extensions.Logging;
using SentenceStudio.Application.Learners;
using SentenceStudio.Application.Resources;
using SentenceStudio.Application.Skills;
using SentenceStudio.Application.Vocabulary;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools.SamTools;

/// <summary>
/// Reads the learner's extended profile summary: languages, level, streak, and
/// aggregate counts. Never exposes name, email, API key, or credentials.
/// </summary>
public sealed class CurrentProfileSummaryTool : CoachToolBase
{
    private readonly ILearnerProfileQueries _profiles;
    private readonly IVocabularyQueries _vocabulary;
    private readonly ISkillProfileQueries _skills;
    private readonly ILearningResourceQueries _resources;
    private readonly IPlanDateContext _dates;
    private readonly ILogger<CurrentProfileSummaryTool> _logger;

    public CurrentProfileSummaryTool(
        IUserScopeProvider userScope,
        ILearnerProfileQueries profiles,
        IVocabularyQueries vocabulary,
        ISkillProfileQueries skills,
        ILearningResourceQueries resources,
        IPlanDateContext dates,
        ILogger<CurrentProfileSummaryTool> logger)
        : base(userScope)
    {
        _profiles = profiles;
        _vocabulary = vocabulary;
        _skills = skills;
        _resources = resources;
        _dates = dates;
        _logger = logger;
    }

    public override string ToolName => CoachToolNames.GetCurrentProfileSummary;

    public async Task<CurrentProfileSummary> GetAsync(CancellationToken ct = default)
    {
        var userId = RequireUserProfileId();

        try
        {
            var profile = await _profiles.GetProfileFactsAsync(userId, ct);

            if (profile is null)
                throw new CoachToolException(CoachToolFailureKind.ProfileMissing, ToolName, "No profile found.");

            var targetLanguages = SplitList(profile.TargetLanguages);
            if (targetLanguages.Count == 0 && !string.IsNullOrWhiteSpace(profile.TargetLanguage))
                targetLanguages = [profile.TargetLanguage];

            var daysSince = profile.CreatedAt == default ? 0
                : Math.Max(0, (int)(_dates.UtcNow.Date - profile.CreatedAt.ToUniversalTime().Date).TotalDays);

            var wordCount = await _vocabulary.CountTrackedWordsAsync(userId, ct);

            // Archived skills are excluded here for the same reason they are excluded from every
            // list the learner practises from: a count that includes them describes a shelf the
            // learner cannot see. The model reads this number and says it out loud, so a count
            // that disagreed with the skills screen would make Sam wrong about the learner's own
            // account. The skills query owns that rule, which is why this asks it rather than
            // restating the predicate here.
            var skillCount = await _skills.CountActiveSkillsAsync(userId, ct);
            var resourceCount = await _resources.CountResourcesAsync(userId, ct);

            return new CurrentProfileSummary(
                TargetLanguage: SanitizeMetadata(profile.TargetLanguage, 40),
                TargetLanguages: targetLanguages,
                NativeLanguage: SanitizeMetadata(profile.NativeLanguage, 40),
                DisplayLanguage: string.IsNullOrWhiteSpace(profile.DisplayLanguage)
                    ? null : SanitizeMetadata(profile.DisplayLanguage, 40),
                PreferredSessionMinutes: profile.PreferredSessionMinutes,
                TargetLevel: string.IsNullOrWhiteSpace(profile.TargetCefrLevel)
                    ? null : SanitizeMetadata(profile.TargetCefrLevel, 20),
                DaysSinceStart: daysSince,
                TrackedWordCount: wordCount,
                SkillCount: skillCount,
                ResourceCount: resourceCount,
                Scope: new CoachResultScope
                {
                    Coverage = CoachScopeCoverage.SettingsSnapshot,
                    Order = CoachScopeOrder.NotApplicable,
                    OrderHonored = true,
                    // Three populations are counted here, and the flags name every predicate that
                    // shaped any of them: the skill count leaves out the archive, and the word
                    // count only sees words the learner has a progress record for. A caller told
                    // only about ownership would read three plain totals.
                    Filters = CoachScopeFilters.OwnerScoped
                        | CoachScopeFilters.ExcludeArchived
                        | CoachScopeFilters.ProgressRowExists,
                    AsOfUtc = _dates.UtcNow,
                    ReturnedCount = 1,
                    DefinitionCode = CoachScopeDefinition.LearnerOverviewSummary,
                    MinimumEvidence = CoachScopeMinimumEvidence.ProgressRowRequired,
                    TieBreak = CoachScopeTieBreak.NotApplicable,
                    ClockBasis = CoachScopeClockBasis.ServerUtcInstant,
                    ReferenceMode = CoachScopeReferenceMode.AsOfInstant
                });
        }
        catch (CoachToolException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw DataAccessFailure(ex); }
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

/// <summary>
/// Reads the learner's app settings and preferences. Never exposes credentials.
/// </summary>
public sealed class LearnerSettingsSummaryTool : CoachToolBase
{
    private readonly ILearnerProfileQueries _profiles;
    private readonly IPlanDateContext _dates;
    private readonly ILogger<LearnerSettingsSummaryTool> _logger;

    public LearnerSettingsSummaryTool(
        IUserScopeProvider userScope,
        ILearnerProfileQueries profiles,
        IPlanDateContext dates,
        ILogger<LearnerSettingsSummaryTool> logger)
        : base(userScope)
    {
        _profiles = profiles;
        _dates = dates;
        _logger = logger;
    }

    public override string ToolName => CoachToolNames.GetLearnerSettingsSummary;

    public async Task<LearnerSettingsSummary> GetAsync(CancellationToken ct = default)
    {
        var userId = RequireUserProfileId();

        try
        {
            var row = await _profiles.GetProfileFactsAsync(userId, ct);

            if (row is null)
                throw new CoachToolException(CoachToolFailureKind.ProfileMissing, ToolName, "No profile found.");

            return new LearnerSettingsSummary(
                TargetLanguage: SanitizeMetadata(row.TargetLanguage, 40),
                NativeLanguage: SanitizeMetadata(row.NativeLanguage, 40),
                DisplayLanguage: string.IsNullOrWhiteSpace(row.DisplayLanguage)
                    ? null : SanitizeMetadata(row.DisplayLanguage, 40),
                PreferredSessionMinutes: row.PreferredSessionMinutes,
                TargetLevel: string.IsNullOrWhiteSpace(row.TargetCefrLevel)
                    ? null : SanitizeMetadata(row.TargetCefrLevel, 20),
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
        catch (CoachToolException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw DataAccessFailure(ex); }
    }
}
