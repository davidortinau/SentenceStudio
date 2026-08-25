using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.Observation;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Evidence;

/// <summary>
/// The evidence a turn actually earned, projected from the reads it actually made.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaces.</b> Evidence used to be built from <c>intent.EvidenceReferences</c> — the
/// model's own claim about what it had consulted. The server took a kind and a window-day count
/// from that claim, invented a label from the enum name, invented a summary sentence, and attached
/// an empty value list. Nothing in it came from a read. A turn that consulted nothing and asserted
/// "PracticeBalance, 7 days" produced an evidence card indistinguishable from one backed by a real
/// query, and the card is the thing a learner opens precisely to check whether the claim is real.
/// </para>
/// <para>
/// <b>What replaces it.</b> The turn's observation buffer holds one record per completed tool call,
/// each carrying the <c>CoachResultScope</c> that read stated. Those scopes are facts the server
/// produced. Evidence is projected from them and from nothing else, so the panel can only ever
/// describe reads that happened.
/// </para>
/// <para>
/// <b>Aggregate only, still.</b> A scope is structurally incapable of carrying a term, a gloss, an
/// example, or the model's query text — the <c>ResultScope</c> embargo rules guarantee it — and the
/// values below are counts drawn from the scope plus fixed server copy. Every one of those three
/// strings is fallback prose for an old client; a current client localizes from the codes beside
/// them and never shows them. No learner content enters
/// through this path, including for a withheld due word: its count crosses and it does not.
/// </para>
/// </remarks>
public static class CoachTurnEvidenceProjection
{
    /// <summary>
    /// True when at least one read this turn stated a scope, which is what makes a grounding claim
    /// checkable.
    /// </summary>
    /// <remarks>
    /// Deliberately independent of whether <see cref="Project"/> produced anything. Some reads —
    /// skills, today — map to no member of the wire's <see cref="CoachEvidenceKind"/>, so a turn
    /// can be genuinely grounded and still yield a shorter list. Tying the two together would
    /// reject a truthful turn for a gap in an enum.
    /// </remarks>
    public static bool AnyGroundedRead(IReadOnlyList<CoachToolCallObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        return observations.Any(o => o.Scope is not null && o.Outcome == CoachToolCallOutcome.Succeeded);
    }

    /// <summary>
    /// Projects one evidence item per population the turn consulted, in the order first consulted.
    /// </summary>
    /// <param name="observations">The turn's completed tool calls.</param>
    /// <param name="today">The learner's local date, used only when a read states no window.</param>
    /// <remarks>
    /// <para>
    /// <b>One item per population, last read wins.</b> A turn may read the same definition twice —
    /// two vocabulary searches with different queries, say. Both are true, and the later one is
    /// what the coach's closing message rests on, so it is the one shown. The complete
    /// call-by-call record is the turn trace's job, not this panel's; a disclosure that grew to
    /// twenty entries would stop being read at all.
    /// </para>
    /// <para>
    /// <b>Failed and refused calls contribute nothing.</b> A read that threw or was refused
    /// produced no scope and grounds no claim.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<CoachEvidenceDto> Project(
        IReadOnlyList<CoachToolCallObservation> observations,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(observations);

        // Insertion-ordered, so the panel reads in the order the coach consulted things, while a
        // repeated definition is replaced in place rather than appended.
        var byDefinition = new Dictionary<CoachScopeDefinition, CoachEvidenceDto>();
        var order = new List<CoachScopeDefinition>();

        foreach (var observation in observations)
        {
            if (observation.Outcome != CoachToolCallOutcome.Succeeded || observation.Scope is not { } scope)
            {
                continue;
            }

            if (ToEvidenceKind(scope.DefinitionCode) is not { } kind)
            {
                continue;
            }

            if (!byDefinition.ContainsKey(scope.DefinitionCode))
            {
                order.Add(scope.DefinitionCode);
            }

            byDefinition[scope.DefinitionCode] = Describe(kind, scope, today);
        }

        return order.Select(d => byDefinition[d]).ToList();
    }

    /// <summary>
    /// Builds one item from one scope: fixed server copy for the words, the scope's own counts for
    /// the numbers, and the scope itself for the terms it was read under.
    /// </summary>
    private static CoachEvidenceDto Describe(
        CoachEvidenceKind kind,
        CoachResultScope scope,
        DateOnly today)
    {
        var values = new List<CoachEvidenceValueDto>(3)
        {
            new()
            {
                Code = CoachEvidenceValueCode.RowsRead,
                Label = "Rows read",
                Value = scope.ReturnedCount,
                Unit = CoachEvidenceUnit.Items
            }
        };

        if (scope.MatchedCount is { } matched && matched != scope.ReturnedCount)
        {
            values.Add(new CoachEvidenceValueDto
            {
                Code = CoachEvidenceValueCode.RowsMatched,
                Label = "Rows matched",
                Value = matched,
                Unit = CoachEvidenceUnit.Items
            });
        }

        if (scope.WithheldCount > 0)
        {
            values.Add(new CoachEvidenceValueDto
            {
                Code = CoachEvidenceValueCode.RowsWithheld,
                Label = "Rows withheld",
                Value = scope.WithheldCount,
                Unit = CoachEvidenceUnit.Items
            });
        }

        // A window when the read had one; otherwise the single day it was made on. The DTO's
        // contract is that evidence always states a date range, and a read taken as of an instant
        // is honestly described by the calendar day containing it — the exact instant rides on
        // AsOfUtc, which the scope supplies.
        var start = scope.WindowStartDate ?? today;
        var end = scope.WindowEndDate ?? today;

        var evidence = new CoachEvidenceDto
        {
            Kind = kind,
            Label = LabelFor(kind),
            Summary = SummaryFor(scope.DefinitionCode),
            WindowStartDate = start,
            WindowEndDate = end,
            Values = values
        };

        return CoachEvidenceScopeProjection.WithScope(evidence, scope);
    }

    /// <summary>
    /// Which evidence bucket a population belongs to, or null when the wire has no member for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No discard arm, so a definition added later stops being covered and the compiler says so,
    /// with a census test behind it.
    /// </para>
    /// <para>
    /// <b>Skills return null on purpose.</b> <see cref="CoachEvidenceKind"/> has no member for
    /// them, and the two candidates are both wrong: <c>LearnerProfile</c> would file a practice
    /// shelf under settings, and adding a member is a wire change that needs the client adoption
    /// gate. Returning null costs a row on the panel; guessing would put a false label on the
    /// learner's own data, and <see cref="AnyGroundedRead"/> already keeps a skills-only turn from
    /// being called ungrounded.
    /// </para>
    /// </remarks>
    internal static CoachEvidenceKind? ToEvidenceKind(CoachScopeDefinition definition) => definition switch
    {
        CoachScopeDefinition.Unspecified => null,

        CoachScopeDefinition.OwnedResourceCatalog => CoachEvidenceKind.ResourceCatalog,
        CoachScopeDefinition.OwnedResourceList => CoachEvidenceKind.ResourceCatalog,
        CoachScopeDefinition.OwnedResourceDetail => CoachEvidenceKind.ResourceCatalog,

        CoachScopeDefinition.ActiveSkillList => null,
        CoachScopeDefinition.ActiveSkillDetail => null,

        CoachScopeDefinition.TrackedVocabularyDueSummary => CoachEvidenceKind.VocabularyDue,
        CoachScopeDefinition.UndueVocabularySearch => CoachEvidenceKind.VocabularyDue,
        CoachScopeDefinition.TrackedVocabularyDetail => CoachEvidenceKind.VocabularyDue,

        CoachScopeDefinition.LearnerSettingsSnapshot => CoachEvidenceKind.LearnerProfile,
        CoachScopeDefinition.LearnerOverviewSummary => CoachEvidenceKind.LearnerProfile,

        CoachScopeDefinition.PlanDaySummary => CoachEvidenceKind.PlanPreview,
        CoachScopeDefinition.DeterministicPlanPreview => CoachEvidenceKind.PlanPreview,

        CoachScopeDefinition.PracticeWindowBalance => CoachEvidenceKind.PracticeBalance
    };

    /// <summary>
    /// The heading for one evidence bucket. <b>Fallback prose only.</b>
    /// </summary>
    /// <remarks>
    /// English, and unavoidably so: the server does not know what the learner reads. A client that
    /// can name the <see cref="CoachEvidenceKind"/> localizes the heading itself and never renders
    /// this. It stays populated for clients built before that change.
    /// </remarks>
    internal static string LabelFor(CoachEvidenceKind kind) => kind switch
    {
        CoachEvidenceKind.PracticeBalance => "Practice balance",
        CoachEvidenceKind.VocabularyDue => "Vocabulary",
        CoachEvidenceKind.ResourceCatalog => "Resources",
        CoachEvidenceKind.LearnerProfile => "Settings",
        CoachEvidenceKind.PlanPreview => "Plan",

        // A client-side sentinel: the tolerant converter produces it when a NEWER server names a
        // kind an OLDER client cannot read. This server is the one doing the naming, so reaching
        // here means a bucket was added without a heading. Throwing says which; returning empty
        // prose would ship a headless card and call it fallback.
        CoachEvidenceKind.Unrecognized => throw new ArgumentOutOfRangeException(
            nameof(kind), kind,
            "Unrecognized is produced by the client's tolerant converter, never by the server. "
            + "A server-side evidence bucket must name itself.")
    };

    /// <summary>
    /// What was consulted, in one deterministic sentence. Names a population, never its contents.
    /// <b>Fallback prose only</b>, on the same terms as <see cref="LabelFor"/>: a client that can
    /// name the definition code localizes from it instead.
    /// </summary>
    internal static string SummaryFor(CoachScopeDefinition definition) => definition switch
    {
        CoachScopeDefinition.Unspecified => string.Empty,

        CoachScopeDefinition.OwnedResourceCatalog => "Your resources, ranked by how recently you used them.",
        CoachScopeDefinition.OwnedResourceList => "Your resources, ranked by when you last changed them.",
        CoachScopeDefinition.OwnedResourceDetail => "One of your resources.",

        CoachScopeDefinition.ActiveSkillList => string.Empty,
        CoachScopeDefinition.ActiveSkillDetail => string.Empty,

        CoachScopeDefinition.TrackedVocabularyDueSummary => "Every word you are tracking, counted by review schedule.",
        CoachScopeDefinition.UndueVocabularySearch => "Your words that are not currently due for review.",
        CoachScopeDefinition.TrackedVocabularyDetail => "One word you are tracking.",

        CoachScopeDefinition.LearnerSettingsSnapshot => "Your study settings.",
        CoachScopeDefinition.LearnerOverviewSummary => "Your study settings, and how much you have saved.",

        CoachScopeDefinition.PlanDaySummary => "Today's plan and what you have logged against it.",
        CoachScopeDefinition.DeterministicPlanPreview => "A plan worked out from your data. Nothing was saved.",

        CoachScopeDefinition.PracticeWindowBalance => "Practice you logged over the window."
    };
}
