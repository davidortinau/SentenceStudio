using System.Text.Json.Serialization;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>
/// How far the grounding pipeline is permitted to act. Plan B9.
/// </summary>
/// <remarks>
/// <para>
/// One ordered enum rather than three booleans, because the three questions a reviewer asks — does
/// it look, does it fix, does it block — are the same question at three depths. Booleans let a
/// deployment answer them inconsistently ("repair on, observe off") and there is no coherent
/// meaning for that combination.
/// </para>
/// <para>
/// Read with <c>&gt;=</c>. W6 ships the ladder and ships production at <see cref="Observe"/>; W9
/// promotes to <see cref="Enforce"/> after the foundation gate. Nothing here promotes itself.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachGroundingStage
{
    /// <summary>No scan. The answer passes through untouched.</summary>
    Off = 0,

    /// <summary>Scan and record. The answer is never altered.</summary>
    Observe = 1,

    /// <summary>Scan, record, and substitute. A violation that cannot be repaired is still shipped.</summary>
    Repair = 2,

    /// <summary>Scan, record, substitute, and refuse what substitution cannot make honest.</summary>
    Enforce = 3
}

/// <summary>
/// The nine honesty rules. Closed, so a violation is a code a metric counts and a test names.
/// </summary>
/// <remarks>
/// <para>
/// Six of these are foundation rules about whether a claim was checked. The last three are
/// capability rules about whether the app can do the thing the answer says it can — and plan §5.6
/// is explicit that none of the three existed before W6. <c>CoachOpportunityKind.UnsupportedCapability</c>
/// is a telemetry mapper and does not satisfy AC-F2; it counts what happened, it does not repair it.
/// </para>
/// <para>
/// The two capability rules point in opposite directions on purpose. Over-claiming ("I'll switch
/// your theme") and under-claiming ("I can't change themes") are the same defect measured from
/// different sides, and a codebase that only had the first would ship a coach that refuses things
/// the app plainly does.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachClaimRuleCode
{
    /// <summary>Not a rule. Present so a default-constructed value is never a real finding.</summary>
    Unknown = 0,

    /// <summary>
    /// The answer asserts something about the learner's own state without a read that establishes it.
    /// </summary>
    UnverifiedLearnerStateClaim = 1,

    /// <summary>
    /// The answer says the learner has none of something, while the evidence covers only part of
    /// their data or states no coverage at all.
    /// </summary>
    NegativeClaimWithoutCoverage = 2,

    /// <summary>The answer says it looked, and the trace shows it did not.</summary>
    FabricatedCheck = 3,

    /// <summary>The answer names a ranking the evidence did not produce.</summary>
    OrderClaimMismatch = 4,

    /// <summary>The answer states a number the evidence does not support.</summary>
    CountClaimMismatch = 5,

    /// <summary>Rows were deliberately held back and the answer does not say so.</summary>
    WithheldNotDisclosed = 6,

    /// <summary>The answer proposes a capability the manifest does not resolve to Present.</summary>
    CapabilityAbsent = 7,

    /// <summary>
    /// The answer claims inability while the manifest resolves Present or PresentOnAnotherSurface.
    /// </summary>
    FalseLimitation = 8,

    /// <summary>A proposed capability has a declared side effect the answer does not state.</summary>
    SideEffectNotDisclosed = 9,

    /// <summary>
    /// A dispute is open and the answer repeats the disputed claim without re-reading, correcting,
    /// or stating a limitation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Case D in the plan's own evidence: the coach repeated a disputed list <em>with more
    /// confidence</em>. A learner who corrects the coach and is answered more firmly with the same
    /// thing has been told their correction did not register, which is worse than the original
    /// error — the first was a mistake, the second is a system that cannot be corrected.
    /// </para>
    /// <para>
    /// The three exits are narrow and all three require the answer to have <em>done</em> something:
    /// re-read with materially different typed parameters, name and correct the prior claim, or
    /// state an honest limitation. Answering again is not one of them.
    /// </para>
    /// </remarks>
    RepeatedDisputedClaim = 10
}

/// <summary>
/// What the engine did about a finding.
/// </summary>
/// <remarks>
/// Ordered by escalation, and recorded so the ratio of substitutions to refusals is a number an
/// operator can watch. Plan: repair by substitution first, refuse last. A build whose refusal rate
/// climbs is a build whose substitutions stopped fitting, and that is visible here before it is
/// visible in a support ticket.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachClaimRepairAction
{
    /// <summary>No repair attempted. The stage did not permit one.</summary>
    None = 0,

    /// <summary>Recorded only. <see cref="CoachGroundingStage.Observe"/> never alters an answer.</summary>
    ObservedOnly = 1,

    /// <summary>A deterministic sentence replaced the offending span.</summary>
    Substituted = 2,

    /// <summary>The offending span was removed and nothing replaced it.</summary>
    Removed = 3,

    /// <summary>The answer was refused. The last resort, and only at Enforce.</summary>
    Refused = 4
}

/// <summary>
/// One finding. Content-free by construction.
/// </summary>
/// <remarks>
/// <para>
/// There is no text member on this record, and that is the design rather than an omission. A
/// finding travels into logs, into the protected turn outcome, and eventually into operator
/// reports; the moment it can carry the offending sentence, every one of those surfaces inherits
/// the answer's embargo. <see cref="BlockIndex"/> and <see cref="SpanIndex"/> locate the span for a
/// developer holding the answer, and mean nothing to anyone who is not.
/// </para>
/// <para>
/// The counts are the exception, and a deliberate one: <see cref="ClaimedCount"/> against
/// <see cref="EvidenceCount"/> is the whole content of a count mismatch, and both are numbers the
/// server already computed.
/// </para>
/// </remarks>
/// <param name="Rule">Which rule fired.</param>
/// <param name="Action">What was done about it.</param>
/// <param name="BlockIndex">Zero-based index of the block the finding sits in, or null.</param>
/// <param name="SpanIndex">Zero-based index of the span within that block, or null.</param>
/// <param name="ClaimedCount">The number the answer stated, for a count mismatch.</param>
/// <param name="EvidenceCount">The number the evidence supports, for a count mismatch.</param>
public sealed record CoachClaimFinding(
    CoachClaimRuleCode Rule,
    CoachClaimRepairAction Action,
    int? BlockIndex = null,
    int? SpanIndex = null,
    int? ClaimedCount = null,
    int? EvidenceCount = null);
