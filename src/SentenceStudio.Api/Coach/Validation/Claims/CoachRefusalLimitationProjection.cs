using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>
/// Turns a grounding refusal into the typed limitation the learner's client renders.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this replaces a sentence.</b> The refusal shipped as
/// <c>"I could not answer that one. Nothing changed."</c> — English, server-authored, and delivered
/// straight to a learner who may be reading the app in Korean. Learner-visible copy is the client's
/// resource file by design, and a server string in a notice bypasses that entirely. What the server
/// owns is the <em>reason</em>, as a closed code, and the destination, as a typed route.
/// </para>
/// <para>
/// <b>The evidence does the explaining.</b> A refusal carrying nothing tells the learner only that
/// something went wrong. The same refusal beside the turn's real coverage, counts and withheld
/// reason tells them what Sam actually looked at — so this projection deliberately produces a small
/// code and leaves the substance to <c>BuildEvidence()</c>, which is preserved rather than emptied.
/// </para>
/// <para>
/// <b>A destination only when one truly follows.</b> The route is derived from the definition the
/// turn's own reads reported, and only for the four families where a real screen exists. Anything
/// else is null: no <c>/profile</c>, no composed path, no query value, no screen invented to fill
/// the slot. A destination that cannot do the thing sends the learner hunting for a control nobody
/// wrote, which is the W7 failure this must not repeat.
/// </para>
/// </remarks>
public static class CoachRefusalLimitationProjection
{
    /// <summary>
    /// The limitation for an answer-shape projection failure. Grounding did not run, so every
    /// evidence-derived field is null/empty and coverage is Unknown.
    /// </summary>
    /// <param name="asOfUtc">When the shape failure was decided.</param>
    public static CoachLimitationDto ProjectShape(DateTime asOfUtc) => new()
    {
        Code = CoachLimitationCode.AnswerShapeInvalid,
        Coverage = CoachEvidenceCoverage.Unknown,
        AsOfUtc = Normalize(asOfUtc),
        WindowStartDate = null,
        WindowEndDate = null,
        AffectedCount = null,
        Destination = null,
        WithheldCount = null,
        WithheldReason = null,
        Alternatives = [],
        HintLadder = [],
        ShorterSession = null,
        FullScopeSurface = null,
        ExportSurface = null
    };

    /// <summary>
    /// The limitation for a grounding refusal, built from the turn's own evidence.
    /// </summary>
    /// <param name="evidence">The content-free evidence the turn produced. May be empty.</param>
    /// <param name="asOfUtc">When the refusal was decided.</param>
    public static CoachLimitationDto Project(
        IReadOnlyList<CoachEvidenceDto> evidence,
        DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var leading = LeadingEvidence(evidence);
        var withheld = SingleWithheldFact(evidence);

        return new CoachLimitationDto
        {
            Code = CoachLimitationCode.UnverifiedClaimWithheld,

            // The coverage of the read the refusal is about, so the learner can tell "I looked at
            // all of it and still could not say" from "I only looked at part of it". Unknown when
            // the turn read nothing, which is itself the honest answer.
            Coverage = leading?.Coverage ?? CoachEvidenceCoverage.Unknown,

            AsOfUtc = Normalize(asOfUtc),
            WindowStartDate = leading?.WindowStartDate,
            WindowEndDate = leading?.WindowEndDate,

            // Null rather than zero, matching W7: a rendered "0" is a fact the server checked.
            AffectedCount = leading?.MatchedCount is > 0 ? leading.MatchedCount : null,

            Destination = DestinationFor(leading?.DefinitionCode),

            // All-or-nothing, and only when one read held rows back. See SingleWithheldFact.
            WithheldCount = withheld?.Count,
            WithheldReason = withheld?.Reason,

            // No alternatives, no hint ladder, no shorter-session offer. Those are W7's boundary
            // answers for requests Sam declines by design; this is a turn Sam tried to answer and
            // could not stand behind, and offering a ladder here would imply the learner asked for
            // something they did not.
            Alternatives = [],
            HintLadder = [],
            ShorterSession = null,
            FullScopeSurface = null,
            ExportSurface = null
        };
    }

    /// <summary>
    /// The evidence item the refusal is about: the first one that states a definition.
    /// </summary>
    /// <remarks>
    /// First rather than broadest, because the evidence list is already in the order the turn
    /// produced it and the first scoped read is the one the answer was built on. Picking by some
    /// other measure would attribute the refusal to whichever read happened to be largest.
    /// </remarks>
    private static CoachEvidenceDto? LeadingEvidence(IReadOnlyList<CoachEvidenceDto> evidence) =>
        evidence.FirstOrDefault(item =>
            item.DefinitionCode is not null and not CoachDefinitionCode.Unknown)
        ?? evidence.FirstOrDefault();

    /// <summary>
    /// The one screen a definition genuinely leads to, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An explicit arm per definition rather than a default that guesses. A fourteenth definition
    /// falls into the null arm and the learner gets no link, which is correct: a route this build
    /// cannot derive is a route it must not state.
    /// </para>
    /// <para>
    /// The resource family maps to null deliberately. Resources are read and edited on
    /// <c>/resources</c>, which is not one of the six routes the plan binds, and there is no
    /// vocabulary or activity screen that shows a resource catalogue. Pointing at
    /// <c>Vocabulary</c> because it is nearby would be the fake screen this exists to prevent.
    /// </para>
    /// </remarks>
    public static CoachDestinationDto? DestinationFor(CoachDefinitionCode? definition) =>
        definition switch
        {
            // Vocabulary the learner owns.
            CoachDefinitionCode.TrackedVocabularyDueSummary
                or CoachDefinitionCode.UndueVocabularySearch
                or CoachDefinitionCode.TrackedVocabularyDetail
                => CoachRouteCatalog.Build(CoachRouteName.Vocabulary),

            // Practice, history and the day's plan all live on the activity log.
            CoachDefinitionCode.PracticeWindowBalance
                or CoachDefinitionCode.PlanDaySummary
                or CoachDefinitionCode.DeterministicPlanPreview
                or CoachDefinitionCode.LatestPracticeSummary
                => CoachRouteCatalog.Build(CoachRouteName.ActivityLog),

            CoachDefinitionCode.ActiveSkillList or CoachDefinitionCode.ActiveSkillDetail
                => CoachRouteCatalog.Build(CoachRouteName.Skills),

            CoachDefinitionCode.LearnerSettingsSnapshot or CoachDefinitionCode.LearnerOverviewSummary
                => CoachRouteCatalog.Build(CoachRouteName.Settings),

            // Resources have no screen among the six the plan binds. Null, not the nearest thing.
            CoachDefinitionCode.OwnedResourceCatalog
                or CoachDefinitionCode.OwnedResourceList
                or CoachDefinitionCode.OwnedResourceDetail
                => null,

            // Unknown, absent, and any member added later.
            _ => null
        };

    /// <summary>One read's withheld count and reason, or nothing.</summary>
    private readonly record struct WithheldFact(int Count, CoachWithheldReason Reason);

    /// <summary>
    /// The withheld pair, when exactly one read produced a coherent one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Coherent means both halves.</b> A count with no reason cannot be rendered — "4 held back"
    /// with no because — and a reason with no count states no scale. <c>None</c> and
    /// <c>Unknown</c> are not reasons: the first says nothing was withheld, which contradicts a
    /// positive count, and the second says this build cannot name why.
    /// </para>
    /// <para>
    /// <b>Two reads are two populations, so the answer is null rather than a sum.</b> This holds
    /// even when both reads name the same reason. A vocabulary search that held back four due terms
    /// and a due summary that held back two are not six of anything: no read computed a union, the
    /// two sets may overlap, and a learner shown "6 held back" would be reading a number the server
    /// invented. Collapsing them is precisely the fluent arithmetic the grounding layer exists to
    /// prevent, so the limitation states nothing and the evidence rows — which are per-read and
    /// still truthful individually — carry the detail.
    /// </para>
    /// <para>
    /// A single coherent pair among several reads is unambiguous and is used: only one read held
    /// anything back, so there is one population and one number.
    /// </para>
    /// </remarks>
    private static WithheldFact? SingleWithheldFact(IReadOnlyList<CoachEvidenceDto> evidence)
    {
        WithheldFact? found = null;

        foreach (var item in evidence)
        {
            if (item is null || item.WithheldCount is not > 0)
            {
                continue;
            }

            if (item.WithheldReason is not { } reason
                || reason == CoachWithheldReason.None
                || reason == CoachWithheldReason.Unknown
                || !Enum.IsDefined(reason))
            {
                // An incoherent pair on any read makes the whole turn's withheld picture
                // unstatable: the server cannot say how much was held back overall when one of the
                // holdings has no explanation.
                return null;
            }

            if (found is not null)
            {
                // A second population. Null rather than a sum nobody computed.
                return null;
            }

            found = new WithheldFact(item.WithheldCount.Value, reason);
        }

        return found;
    }

    /// <summary>
    /// What a shipped answer must disclose about the grounding layer's handling of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only for an answer that shipped.</b> A refused turn discloses nothing here — the learner
    /// received no answer, so there is nothing to disclose about, and the limitation is the shape
    /// that speaks for that case. The caller enforces it and this asserts it, because two places
    /// agreeing is what stops a future edit from surfacing both.
    /// </para>
    /// <para>
    /// <b>Null is "the layer did not run".</b> Off, Observe, or a host with no grounding. That is
    /// different from <see cref="CoachRepairDisclosure.None"/>, which means it ran and found
    /// nothing worth changing, and a client can tell the two apart.
    /// </para>
    /// <para>
    /// <b>Suppression needs a finding.</b> <c>RepairSuppressedForLanguage</c> is a property of the
    /// answer's language, decided before any rule runs, so it is set on every non-English answer at
    /// Repair and above. On its own it says only "substitution was unavailable", not "a repair was
    /// withheld". Without a finding there was nothing to withhold, and the honest value is
    /// <see cref="CoachRepairDisclosure.None"/>.
    /// </para>
    /// <para>
    /// <b>What the client may say.</b> The disclosure asserts that a finding stood and the answer
    /// was left unchanged with substitution unavailable in this language. It does not assert that
    /// an English learner would have got a rewrite: a finding with no substitute in any language
    /// reaches this branch too. Client copy should say the coach could not adjust the wording, not
    /// that it would have.
    /// </para>
    /// <para>
    /// <b>Precedence, if both flags are ever set.</b> They are mutually exclusive by construction —
    /// suppression downgrades the rung to Observe, which cannot alter anything — but if both ever
    /// arrive, <see cref="CoachRepairDisclosure.AnswerAltered"/> wins. An altered answer is a fact
    /// the learner can verify by reading it, and it is the more consequential of the two; claiming
    /// suppression instead would tell them the text is untouched when it is not. Failing to a
    /// neutral value would be safer only if both statements were equally unverifiable, and they are
    /// not.
    /// </para>
    /// </remarks>
    /// <param name="summary">The judged summary, or null when the layer did not run.</param>
    /// <param name="refused">Whether the turn withheld its answer.</param>
    public static CoachRepairDisclosure? ProjectDisclosure(
        CoachGroundingTurnSummary? summary,
        bool refused)
    {
        if (summary is null || refused)
        {
            return null;
        }

        if (summary.Altered)
        {
            return CoachRepairDisclosure.AnswerAltered;
        }

        // The flag alone is not enough. SuppressRepairForLanguage is decided from the answer's
        // display tag before any rule runs, so it is true for every non-English answer at Repair
        // and above — including one the layer had nothing to say about. Reporting suppression
        // there tells a learner something was found and withheld when nothing was found at all,
        // which is the exact shape of lie this disclosure exists to prevent. A finding has to
        // have stood for there to be anything to disclose.
        if (summary.RepairSuppressedForLanguage && summary.FindingCount > 0)
        {
            return CoachRepairDisclosure.RepairSuppressedForLanguage;
        }

        // The layer ran and left the answer alone. Distinct from null.
        return CoachRepairDisclosure.None;
    }

    /// <summary>Whole-second UTC, matching the scope and limitation timestamps.</summary>
    private static DateTime Normalize(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }
}
