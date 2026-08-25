using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Turns the closed codes on an evidence item into words in the learner's own language.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the client does this at all.</b> <c>CoachEvidenceDto.Label</c> and <c>.Summary</c> were
/// documented as localized and never were: the server writes them in English from a fixed switch
/// and has no idea what the learner reads. The result was one card with an English heading over
/// Korean coverage and withheld lines. The server keeps sending the prose so an older client is
/// unaffected; a current client localizes from the codes instead and never shows it.
/// </para>
/// <para>
/// <b>Why it is shared.</b> Two components render evidence — the transcript's per-message panel and
/// the plan canvas list — and they must agree. Two copies of a fallback ladder is two chances to
/// get the unknown case wrong in only one of them.
/// </para>
/// <para>
/// <b>The fallback ladder, in order.</b> A known code wins. Otherwise the server's prose, which is
/// the old behaviour and is at least true. Otherwise nothing at all — never another code's words.
/// A wrong heading over real numbers is worse than a missing one, because the reader cannot see
/// that anything is missing.
/// </para>
/// </remarks>
public sealed class CoachEvidenceLocalizer
{
    private readonly BlazorLocalizationService _localize;

    public CoachEvidenceLocalizer(BlazorLocalizationService localize)
    {
        _localize = localize ?? throw new ArgumentNullException(nameof(localize));
    }

    /// <summary>
    /// The card heading. Localized from <see cref="CoachEvidenceDto.Kind"/>; falls back to the
    /// server's prose for a kind this build cannot name; empty when there is neither.
    /// </summary>
    public string Heading(CoachEvidenceDto item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var key = item.Kind switch
        {
            CoachEvidenceKind.PracticeBalance => "Coach_EvidenceKind_PracticeBalance",
            CoachEvidenceKind.VocabularyDue => "Coach_EvidenceKind_VocabularyDue",
            CoachEvidenceKind.ResourceCatalog => "Coach_EvidenceKind_ResourceCatalog",
            CoachEvidenceKind.LearnerProfile => "Coach_EvidenceKind_LearnerProfile",
            CoachEvidenceKind.PlanPreview => "Coach_EvidenceKind_PlanPreview",
            // Unrecognized: a newer server named a kind this build has no heading for.
            _ => null
        };

        return key is not null ? _localize[key] : item.Label ?? string.Empty;
    }

    /// <summary>
    /// The one-line summary. Localized from <see cref="CoachEvidenceDto.DefinitionCode"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Unknown and null are different, and the difference matters.</b> <c>Unknown</c> means the
    /// server named a definition this build cannot read — we know a code exists, so printing the
    /// English prose beside it would put an untranslated sentence in a Korean card for a fact the
    /// client could not name anyway. That summary is omitted.
    /// </para>
    /// <para>
    /// <c>null</c> means the server said nothing at all, which is the old-payload case. The prose
    /// is the only description there is, and it is true; dropping it would lose information rather
    /// than protect anyone. That is what keeping <c>Summary</c> required and populated is for.
    /// Every current server payload carries a code, so this path is reached only by an older
    /// server or a synthetic fixture.
    /// </para>
    /// </remarks>
    public string Summary(CoachEvidenceDto item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var key = item.DefinitionCode switch
        {
            CoachDefinitionCode.OwnedResourceCatalog => "Coach_EvidenceDefinition_OwnedResourceCatalog",
            CoachDefinitionCode.OwnedResourceList => "Coach_EvidenceDefinition_OwnedResourceList",
            CoachDefinitionCode.OwnedResourceDetail => "Coach_EvidenceDefinition_OwnedResourceDetail",
            CoachDefinitionCode.ActiveSkillList => "Coach_EvidenceDefinition_ActiveSkillList",
            CoachDefinitionCode.ActiveSkillDetail => "Coach_EvidenceDefinition_ActiveSkillDetail",
            CoachDefinitionCode.TrackedVocabularyDueSummary => "Coach_EvidenceDefinition_TrackedVocabularyDueSummary",
            CoachDefinitionCode.UndueVocabularySearch => "Coach_EvidenceDefinition_UndueVocabularySearch",
            CoachDefinitionCode.TrackedVocabularyDetail => "Coach_EvidenceDefinition_TrackedVocabularyDetail",
            CoachDefinitionCode.LearnerSettingsSnapshot => "Coach_EvidenceDefinition_LearnerSettingsSnapshot",
            CoachDefinitionCode.LearnerOverviewSummary => "Coach_EvidenceDefinition_LearnerOverviewSummary",
            CoachDefinitionCode.PlanDaySummary => "Coach_EvidenceDefinition_PlanDaySummary",
            CoachDefinitionCode.PracticeWindowBalance => "Coach_EvidenceDefinition_PracticeWindowBalance",
            CoachDefinitionCode.DeterministicPlanPreview => "Coach_EvidenceDefinition_DeterministicPlanPreview",
            _ => null
        };

        if (key is not null)
        {
            return _localize[key];
        }

        // null means the server named nothing — the old-payload case — so its prose is the only
        // description there is. Unknown means it named something unreadable, and printing English
        // beside a code we cannot name is the leak this revision exists to close.
        return item.DefinitionCode is null ? item.Summary ?? string.Empty : string.Empty;
    }

    /// <summary>Whether a summary line should render at all.</summary>
    public bool HasSummary(CoachEvidenceDto item) => Summary(item).Length > 0;

    /// <summary>
    /// One value's label. Localized from <see cref="CoachEvidenceValueDto.Code"/>; falls back to
    /// the server's prose when the server named no code this build can read.
    /// </summary>
    public string ValueLabel(CoachEvidenceValueDto value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var key = value.Code switch
        {
            CoachEvidenceValueCode.RowsRead => "Coach_EvidenceValue_RowsRead",
            CoachEvidenceValueCode.RowsMatched => "Coach_EvidenceValue_RowsMatched",
            CoachEvidenceValueCode.RowsWithheld => "Coach_EvidenceValue_RowsWithheld",
            _ => null
        };

        return key is not null ? _localize[key] : value.Label ?? string.Empty;
    }
}
