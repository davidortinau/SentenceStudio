using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>
/// Everything a rule may read. Nothing else is reachable from a rule.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three inputs, and a router label is not one of them.</b> Plan B5: honesty rules trigger on
/// claim shape and on the trace, never on a router label. The reason is that a router label is the
/// model's own assertion about what it was doing, and a rule that reads it is asking the thing
/// under audit to describe itself. There is no property on this type a router could set, which is
/// how that rule is enforced rather than remembered.
/// </para>
/// <para>
/// The three real inputs are: the answer's <b>claim shape</b> (W3 evidence beside it), the
/// <b>trace</b> of what was actually called (W4), and the <b>manifest</b> of what this build can do
/// (W5). Each rule uses a different pair, and the ones that need the trace say so by returning no
/// finding when it is absent — an unrecorded turn is unproven, not guilty.
/// </para>
/// </remarks>
public sealed class CoachClaimRuleContext
{
    /// <summary>The answer under audit. Null when the turn produced none.</summary>
    public CoachAnswerDto? Answer { get; init; }

    /// <summary>The evidence the answer was built from. W3.</summary>
    public IReadOnlyList<CoachEvidenceDto> Evidence { get; init; } = [];

    /// <summary>What the turn actually called. W4. Null when this turn recorded no trace.</summary>
    public CoachTurnTraceSummary? Trace { get; init; }

    /// <summary>Capabilities the answer proposes to use, by manifest name.</summary>
    /// <remarks>
    /// Supplied by the caller from the turn's own intent, never parsed out of prose. A capability
    /// name recovered by matching words against the manifest would make the rules depend on how the
    /// model phrased itself, which is the failure mode B5 exists to close.
    /// </remarks>
    public IReadOnlyList<string> ProposedCapabilities { get; init; } = [];

    /// <summary>The promoted capability stage for this turn.</summary>
    public CoachCapabilityStage Stage { get; init; } = CoachCapabilityStage.Off;

    /// <summary>The client's advertised capabilities, merged for this turn only.</summary>
    public CoachClientCapabilityHandshake? Handshake { get; init; }

    /// <summary>
    /// The learner's open correction of an earlier claim, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null is how <c>Coach:CorrectionState:Enabled=false</c> reaches the rules: an off deployment
    /// supplies no dispute, so <see cref="CoachRepeatedDisputedClaimRule"/> has nothing to fire on.
    /// The bypass is total because there is no flag inside the rule to get out of step with the
    /// one outside it.
    /// </para>
    /// <para>
    /// Content-free, like everything else here. A signal code, a bounded message identifier, and
    /// the definition codes the disputed answer read — never the learner's words.
    /// </para>
    /// </remarks>
    public Persistence.History.CoachTurnDisputeState? Dispute { get; init; }

    /// <summary>
    /// The bounded inability this turn declared, or null when it declared none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The typed form of "I can't do that", projected from this turn's own findings by
    /// <see cref="CoachClaimLimitationProjection"/>. It reaches the rules so the dispute exit can be
    /// decided on a code rather than on a sentence: an answer claiming a boundary is exactly the
    /// unverified assertion the grounding layer exists to catch, and the sentence that sounds most
    /// like an honest limitation was the easiest way out of a standing dispute.
    /// </para>
    /// <para>
    /// Null on the overwhelming majority of turns, and null is not a limitation. Nothing here
    /// infers one from absence.
    /// </para>
    /// </remarks>
    public CoachLimitationDto? Limitation { get; init; }

    /// <summary>
    /// A copy of this context carrying the given dispute.
    /// </summary>
    /// <remarks>
    /// An explicit clone rather than making the context a record. <c>with</c> would be shorter, but
    /// a record brings value equality to a type that caches computed spans and counts, and two
    /// contexts comparing equal because their inputs match — while holding different memoised
    /// state — is a subtlety nobody should have to know about.
    /// </remarks>
    public CoachClaimRuleContext WithDispute(Persistence.History.CoachTurnDisputeState? dispute) =>
        new()
        {
            Answer = Answer,
            Evidence = Evidence,
            Trace = Trace,
            ProposedCapabilities = ProposedCapabilities,
            Stage = Stage,
            Handshake = Handshake,
            Limitation = Limitation,
            Dispute = dispute
        };

    /// <summary>A copy of this context carrying the given projected limitation.</summary>
    /// <remarks>Same hand-written clone, for the same reason <see cref="WithDispute"/> is one.</remarks>
    public CoachClaimRuleContext WithLimitation(CoachLimitationDto? limitation) =>
        new()
        {
            Answer = Answer,
            Evidence = Evidence,
            Trace = Trace,
            ProposedCapabilities = ProposedCapabilities,
            Stage = Stage,
            Handshake = Handshake,
            Dispute = Dispute,
            Limitation = limitation
        };

    /// <summary>The scannable display spans, computed once.</summary>
    public IReadOnlyList<CoachClaimSpan> Spans => _spans ??= CoachClaimScope.Scannable(Answer);

    private IReadOnlyList<CoachClaimSpan>? _spans;

    /// <summary>True when at least one read in the trace succeeded.</summary>
    /// <remarks>
    /// The bar for "it looked" is a successful call, not an attempted one. A failed read produces
    /// no rows, and an answer built on a failure is exactly as unsupported as one built on nothing.
    /// </remarks>
    public bool TraceShowsASuccessfulRead =>
        Trace is not null
        && Trace.Calls.Any(call =>
            call.Outcome == Tools.Observation.CoachToolCallOutcome.Succeeded);

    /// <summary>The broadest coverage any evidence item claims.</summary>
    public bool EvidenceCoversCompleteSet =>
        Evidence.Any(item => item.Coverage is CoachEvidenceCoverage.CompleteOwnedSet
            or CoachEvidenceCoverage.CompleteAggregateWithBreakdown);

    /// <summary>True when any evidence item states a real ranking.</summary>
    public bool EvidenceStatesAnOrder =>
        Evidence.Any(item => item.Order is not null
            and not CoachEvidenceOrder.Unknown
            and not CoachEvidenceOrder.Unordered
            and not CoachEvidenceOrder.NotApplicable);

    /// <summary>Every count the evidence supports: matched, returned, and withheld.</summary>
    /// <remarks>
    /// A permissive set on purpose. The count rule's job is to catch a number that came from
    /// nowhere, not to insist the answer picked the denominator a reviewer would have picked.
    /// Treating "20 of 84" as two supported numbers is correct; treating it as one is how a true
    /// sentence gets rewritten.
    /// </remarks>
    public IReadOnlySet<int> SupportedCounts
    {
        get
        {
            if (_supportedCounts is not null)
            {
                return _supportedCounts;
            }

            var counts = new HashSet<int>();

            foreach (var item in Evidence)
            {
                AddIfPresent(counts, item.MatchedCount);
                AddIfPresent(counts, item.ReturnedCount);
                AddIfPresent(counts, item.WithheldCount);
                counts.Add(item.Values.Count);
            }

            if (Trace is not null)
            {
                foreach (var call in Trace.Calls)
                {
                    AddIfPresent(counts, call.MatchedCount);
                    AddIfPresent(counts, call.ReturnedCount);
                    AddIfPresent(counts, call.WithheldCount);
                }
            }

            return _supportedCounts = counts;
        }
    }

    private IReadOnlySet<int>? _supportedCounts;

    /// <summary>Rows deliberately held back across every evidence item.</summary>
    public int WithheldTotal => Evidence.Sum(item => item.WithheldCount ?? 0);

    private static void AddIfPresent(HashSet<int> counts, int? value)
    {
        if (value is { } present)
        {
            counts.Add(present);
        }
    }
}
