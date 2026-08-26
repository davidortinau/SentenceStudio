using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Evidence;

/// <summary>
/// The one place the server's scope vocabulary and the wire's scope vocabulary are reconciled.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a mapper exists at all.</b> <c>CoachResultScope</c> and its enums live in this assembly;
/// <see cref="CoachEvidenceDto"/> lives in Contracts, which cannot reference this assembly. Moving
/// the enums would have touched every coach tool and every scope test at once, in files three
/// workstreams are currently inside. So the wire carries mirrors, and this file is the cost of
/// that choice — paid once, in one place, with census tests that fail the moment the two
/// vocabularies drift apart.
/// </para>
/// <para>
/// <b>Why the switches have no discard arm.</b> A <c>_ =&gt;</c> arm would map a member added next
/// year to <c>Unknown</c> and ship it, and <c>Unknown</c> renders as no claim at all — so a new
/// coverage kind would quietly become silence. Without the arm, adding a server member makes this
/// file stop covering its input and the compiler says so; if that warning is ever missed, the
/// census tests fail outright. An undefined value — one cast in from an integer rather than named
/// — throws rather than degrades, because a scope value that is not a member of its own enum is a
/// defect upstream, and reporting it as anything at all would launder it.
/// </para>
/// <para>
/// <b>What the mapper may not do.</b> It moves closed codes, bounded counts, and one timestamp. It
/// never reads a term, a gloss, an example, an expected answer, or the model's query text, because
/// <c>CoachResultScope</c> is structurally incapable of carrying one — that is what the
/// <c>ResultScope</c> embargo rules guarantee. The projection inherits the embargo rather than
/// restating it.
/// </para>
/// </remarks>
public static class CoachEvidenceScopeProjection
{
    // CS8524 only — the "an integer could be cast to an unnamed value" case, which is exactly the
    // case these switches are meant to throw on. CS8509, raised when a NAMED member stops being
    // covered, is deliberately left enabled: that is the drift signal, and silencing both would
    // turn a missing map into a silent runtime throw on a value the server routinely produces.
#pragma warning disable CS8524

    /// <summary>Projects a scope's coverage onto the wire vocabulary.</summary>
    public static CoachEvidenceCoverage ToWire(CoachScopeCoverage value) => value switch
    {
        CoachScopeCoverage.Unspecified => CoachEvidenceCoverage.Unknown,
        CoachScopeCoverage.CompleteOwnedSet => CoachEvidenceCoverage.CompleteOwnedSet,
        CoachScopeCoverage.PageOfOwnedSet => CoachEvidenceCoverage.PageOfOwnedSet,
        CoachScopeCoverage.WindowBounded => CoachEvidenceCoverage.WindowBounded,
        CoachScopeCoverage.SingleItem => CoachEvidenceCoverage.SingleItem,
        CoachScopeCoverage.SingleDay => CoachEvidenceCoverage.SingleDay,
        CoachScopeCoverage.SettingsSnapshot => CoachEvidenceCoverage.SettingsSnapshot,
        CoachScopeCoverage.DerivedProjection => CoachEvidenceCoverage.DerivedProjection,
        CoachScopeCoverage.CompleteAggregateWithBreakdown => CoachEvidenceCoverage.CompleteAggregateWithBreakdown
    };

    /// <summary>Projects a scope's order onto the wire vocabulary.</summary>
    public static CoachEvidenceOrder ToWire(CoachScopeOrder value) => value switch
    {
        CoachScopeOrder.Unspecified => CoachEvidenceOrder.Unknown,
        CoachScopeOrder.NotApplicable => CoachEvidenceOrder.NotApplicable,
        CoachScopeOrder.Unordered => CoachEvidenceOrder.Unordered,
        CoachScopeOrder.LastUsedAscending => CoachEvidenceOrder.LastUsedAscending,
        CoachScopeOrder.UpdatedDescending => CoachEvidenceOrder.UpdatedDescending,
        CoachScopeOrder.MasteryDescending => CoachEvidenceOrder.MasteryDescending,
        CoachScopeOrder.MinutesDescending => CoachEvidenceOrder.MinutesDescending,
        CoachScopeOrder.PriorityAscending => CoachEvidenceOrder.PriorityAscending,
        CoachScopeOrder.FrequencyDescending => CoachEvidenceOrder.FrequencyDescending,
        CoachScopeOrder.BandLabelAscending => CoachEvidenceOrder.BandLabelAscending
    };

    /// <summary>Projects a scope's definition onto the wire vocabulary.</summary>
    public static CoachDefinitionCode ToWire(CoachScopeDefinition value) => value switch
    {
        CoachScopeDefinition.Unspecified => CoachDefinitionCode.Unknown,
        CoachScopeDefinition.OwnedResourceCatalog => CoachDefinitionCode.OwnedResourceCatalog,
        CoachScopeDefinition.OwnedResourceList => CoachDefinitionCode.OwnedResourceList,
        CoachScopeDefinition.OwnedResourceDetail => CoachDefinitionCode.OwnedResourceDetail,
        CoachScopeDefinition.ActiveSkillList => CoachDefinitionCode.ActiveSkillList,
        CoachScopeDefinition.ActiveSkillDetail => CoachDefinitionCode.ActiveSkillDetail,
        CoachScopeDefinition.TrackedVocabularyDueSummary => CoachDefinitionCode.TrackedVocabularyDueSummary,
        CoachScopeDefinition.UndueVocabularySearch => CoachDefinitionCode.UndueVocabularySearch,
        CoachScopeDefinition.TrackedVocabularyDetail => CoachDefinitionCode.TrackedVocabularyDetail,
        CoachScopeDefinition.LearnerSettingsSnapshot => CoachDefinitionCode.LearnerSettingsSnapshot,
        CoachScopeDefinition.LearnerOverviewSummary => CoachDefinitionCode.LearnerOverviewSummary,
        CoachScopeDefinition.PlanDaySummary => CoachDefinitionCode.PlanDaySummary,
        CoachScopeDefinition.PracticeWindowBalance => CoachDefinitionCode.PracticeWindowBalance,
        CoachScopeDefinition.DeterministicPlanPreview => CoachDefinitionCode.DeterministicPlanPreview,
        CoachScopeDefinition.LatestPracticeSummary => CoachDefinitionCode.LatestPracticeSummary
    };

    /// <summary>Projects a scope's withheld reason onto the wire vocabulary.</summary>
    /// <remarks>
    /// The one place the two numberings genuinely differ. The server's zero is <c>None</c> — a real
    /// claim that nothing was held back — and the wire's zero has to mean "no claim readable", so
    /// every member shifts up one. Reading this switch is how a reviewer checks that shift, which
    /// is why it is written out rather than computed.
    /// </remarks>
    public static CoachWithheldReason ToWire(CoachScopeWithheldReason value) => value switch
    {
        CoachScopeWithheldReason.None => CoachWithheldReason.None,
        CoachScopeWithheldReason.DueReviewEmbargo => CoachWithheldReason.DueReviewEmbargo,
        CoachScopeWithheldReason.ResultLimit => CoachWithheldReason.ResultLimit,
        CoachScopeWithheldReason.ArchivedExcluded => CoachWithheldReason.ArchivedExcluded,
        CoachScopeWithheldReason.BelowMinimumEvidence => CoachWithheldReason.BelowMinimumEvidence
    };

    /// <summary>
    /// Returns <paramref name="evidence"/> with the terms <paramref name="scope"/> was produced
    /// under attached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A copy rather than a mutation, because <see cref="CoachEvidenceDto"/> is init-only and the
    /// caller that wrote the label and the summary is not the caller that knows the scope.
    /// </para>
    /// <para>
    /// <c>ClockBasis</c> is deliberately absent; see
    /// <c>.squad/decisions/inbox/wash-w3a-clockbasis-defer.md</c>. <c>MinimumEvidence</c>,
    /// <c>TieBreak</c>, <c>ReferenceMode</c>, <c>Filters</c> and the scope's own window dates are
    /// absent too — the window is already on the evidence item, and the rest are server-side
    /// foundation members no client renders yet. Projecting them now would spend wire and screen on
    /// vocabulary nothing reads.
    /// </para>
    /// </remarks>
    public static CoachEvidenceDto WithScope(CoachEvidenceDto evidence, CoachResultScope scope)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(scope);

        return new CoachEvidenceDto
        {
            Kind = evidence.Kind,
            Label = evidence.Label,
            Summary = evidence.Summary,
            WindowStartDate = evidence.WindowStartDate,
            WindowEndDate = evidence.WindowEndDate,
            Values = evidence.Values,

            Coverage = ToWire(scope.Coverage),
            Order = ToWire(scope.Order),
            DefinitionCode = ToWire(scope.DefinitionCode),
            WithheldReason = ToWire(scope.WithheldReason),
            AsOfUtc = scope.AsOfUtc,
            MatchedCount = scope.MatchedCount,
            ReturnedCount = scope.ReturnedCount,
            WithheldCount = scope.WithheldCount
        };
    }
}

#pragma warning restore CS8524
