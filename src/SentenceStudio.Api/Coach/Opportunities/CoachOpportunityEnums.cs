using System.Text.Json.Serialization;

namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// What kind of gap the learner ran into.
/// </summary>
/// <remarks>
/// <para>
/// Stored as an <b>ordinal</b> by <c>CoachDbContext</c>, so member order is a persistence
/// contract: inserting a value into the middle silently re-labels every row already written.
/// Members may only be appended. <c>CoachOpportunityStoredEnumContractTests</c> pins every value.
/// </para>
/// <para>
/// Deliberately answers "what was the learner reaching for", not "what did the server return".
/// The second question is answered by <c>FailureCode</c>, which reuses the existing closed
/// vocabularies verbatim. Splitting them is what makes this table a product artifact rather than
/// a second error log: <c>preference_setting_session_minutes</c> + <c>invalid_arguments</c> is a
/// policy decision waiting to be made, and the same capability code with
/// <c>execution_failed</c> is a bug. One code cannot say both.
/// </para>
/// </remarks>
public enum CoachOpportunityKind
{
    /// <summary>The learner asked for something the server has no approved capability for.</summary>
    UnsupportedCapability = 0,

    /// <summary>A registered tool exists but is switched off for this deployment or learner.</summary>
    ToolUnavailable = 1,

    /// <summary>A capability exists and is enabled, but policy refuses this particular use.</summary>
    ProposalRefusedByPolicy = 2,

    /// <summary>
    /// A short decisive answer arrived with nothing structured to bind it to — the referent-loss
    /// case. See <c>CoachUnboundAnswerDetector</c>.
    /// </summary>
    AmbiguousFollowUp = 3,

    /// <summary>A request or a model answer failed a shape or argument check.</summary>
    ValidationFailure = 4,

    /// <summary>A tool ran and failed for an operational reason.</summary>
    ToolExecutionFailure = 5,

    /// <summary>An approval, confirmation, or undo arrived outside the state that accepts it.</summary>
    ConfirmationLifecycleFailure = 6,

    /// <summary>The learner asked for something outside what the coach covers.</summary>
    OutOfScopeRequest = 7,

    /// <summary>The learner asked for something the coach must refuse on safety grounds.</summary>
    HarmfulOrUnsafeRequest = 8,

    /// <summary>A bound was reached: run limits, token limits, per-turn proposal budget.</summary>
    CapacityOrBudgetRefusal = 9,

    /// <summary>
    /// The learner said, in as many words, that a response did not serve them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only kind on this enum that no server-side heuristic can produce. Every other member
    /// is the server observing itself refuse; this one is the learner disagreeing with a turn the
    /// server considered a success, which is precisely the gap automatic capture is blind to — a
    /// fluent, well-formed, <c>Completed</c> answer to the wrong question leaves no trace anywhere
    /// else in this ledger.
    /// </para>
    /// <para>
    /// Always <see cref="CoachOpportunityDisposition.Product"/>. A learner spent an action to say
    /// this, so it is always worth a person reading it; collapsing it into a counter would throw
    /// away the one signal that arrived with intent behind it.
    /// </para>
    /// </remarks>
    UserReportedResponse = 10
}

/// <summary>
/// Whether a row is individually reviewable or only ever a number.
/// </summary>
/// <remarks>
/// <para>
/// This is the anti-noise control. A normal, correct, safe refusal — an off-topic request, a
/// destructive-request refusal, a protocol-state error — must not become a backlog entry with a
/// conversation attached. It becomes a counter.
/// </para>
/// <para>
/// Stored as an ordinal; append only.
/// </para>
/// </remarks>
public enum CoachOpportunityDisposition
{
    /// <summary>
    /// Individually reviewable. May carry conversation, turn, and evidence pointers.
    /// </summary>
    Product = 0,

    /// <summary>
    /// Counted only. <b>Never</b> carries a conversation id, a turn id, or evidence pointers —
    /// the recorder strips them, and <c>CoachOpportunityShapeTests</c> proves it.
    /// </summary>
    AggregateOnly = 1
}

/// <summary>
/// Which authoritative boundary observed the refusal.
/// </summary>
/// <remarks>Stored as an ordinal; append only.</remarks>
public enum CoachOpportunitySurface
{
    /// <summary>Observed at the turn outcome, after the response was computed.</summary>
    TurnOutcome = 0,

    /// <summary>Observed at the tool invocation boundary.</summary>
    ToolInvocation = 1,

    /// <summary>Observed when the write ledger recorded a refusal.</summary>
    WriteLedger = 2
}

/// <summary>
/// What the learner's message was answering, when anything.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="PriorClarification"/> and <see cref="PriorCoachQuestion"/> are produced by the
/// referent-loss detector, and both are graded from the <b>server's own prior message</b> rather
/// than from learner text. <see cref="None"/> means "record nothing" on that path: an
/// out-of-the-blue "yes" is noise, not an opportunity.
/// </para>
/// <para>Stored as an ordinal; append only.</para>
/// </remarks>
public enum CoachOpportunityOfferLink
{
    /// <summary>Nothing preceded this that an answer could bind to.</summary>
    None = 0,

    /// <summary>The prior coach message was a structural clarification.</summary>
    PriorClarification = 1,

    /// <summary>The prior coach message ended in a question, by deterministic predicate.</summary>
    PriorCoachQuestion = 2,

    /// <summary>A plan suggestion was open and awaiting a decision.</summary>
    OpenPlanSuggestion = 3,

    /// <summary>A write proposal was open and awaiting a decision.</summary>
    OpenWriteProposal = 4
}

/// <summary>
/// The review lifecycle of one ledger row.
/// </summary>
/// <remarks>
/// <para>
/// Rows are <b>never deleted on review</b> — the markdown log's own "update in place, never
/// delete" rule. Only account erasure and the retention sweep remove a row, and the sweep spares
/// <see cref="Accepted"/> and <see cref="Deferred"/> because those are decisions.
/// </para>
/// <para>Stored as an ordinal; append only.</para>
/// <para>
/// Serialized <b>by name</b> on the wire. This host has no global string-enum converter — every
/// coach enum that crosses an HTTP boundary opts in individually — and this one is bound from an
/// operator's review request body. Without the attribute the request fails to bind at the JSON
/// layer, before any handler runs, so every server-side test still passes while no review can
/// ever be recorded.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachOpportunityStatus
{
    /// <summary>Recorded, not yet triaged.</summary>
    New = 0,

    /// <summary>A reviewer read it and has not decided yet.</summary>
    Reviewed = 1,

    /// <summary>Accepted as real product work.</summary>
    Accepted = 2,

    /// <summary>Real, but not now.</summary>
    Deferred = 3,

    /// <summary>Not a problem worth carrying. Recurrence still bumps the counters.</summary>
    Dismissed = 4
}

/// <summary>
/// The closed vocabulary a reviewer may attach to a decision.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is deliberately no free-text reviewer note.</b> A prose field is precisely where a
/// reviewer would paste a learner's phrase to explain the entry, defeating every other control in
/// this design. Prose belongs in <c>docs/sam-future-opportunities.md</c>, which humans review
/// under that log's own evidence rule.
/// </para>
/// <para>Stored as an ordinal; append only.</para>
/// <para>
/// Serialized <b>by name</b> on the wire, for the same reason
/// <see cref="CoachOpportunityStatus"/> is: it arrives in an operator's review request body.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachOpportunityReviewerNoteCode
{
    /// <summary>Needs Captain's decision before anything can move.</summary>
    NeedsCaptainDecision = 0,

    /// <summary>Already tracked as an ordinary bug with an owner.</summary>
    DuplicateOfKnownBug = 1,

    /// <summary>Fixable by prompt or copy tuning; no new capability needed.</summary>
    PromptTuningOnly = 2,

    /// <summary>Needs a capability that does not exist yet.</summary>
    NeedsNewTool = 3,

    /// <summary>Needs an allowlist or policy change, not code.</summary>
    NeedsPolicyChange = 4,

    /// <summary>Working as intended.</summary>
    NotAProblem = 5,

    /// <summary>A spec has been written; see the linked path.</summary>
    SpecWritten = 6
}
