using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// The result of one learner turn.
/// The response holds a receipt only when the server applied a change.
/// </summary>
public sealed class CoachTurnResponse
{
    /// <summary>The session identifier.</summary>
    public required string SessionId { get; init; }

    /// <summary>The turn identifier.</summary>
    public required string TurnId { get; init; }

    /// <summary>The result of the turn.</summary>
    public required CoachTurnStatus Status { get; init; }

    /// <summary>The reason the coach stopped work on this turn.</summary>
    public required CoachStopReason StopReason { get; init; }

    /// <summary>The status of the session after this turn.</summary>
    public required CoachSessionStatus SessionStatus { get; init; }

    /// <summary>The new messages from this turn, in time order.</summary>
    public IReadOnlyList<CoachMessageDto> Messages { get; init; } = Array.Empty<CoachMessageDto>();

    /// <summary>The active constraints after this turn.</summary>
    public required CoachConstraintSetDto ActiveConstraints { get; init; }

    /// <summary>The plan canvas state after this turn.</summary>
    public required CoachPlanStateDto PlanState { get; init; }

    /// <summary>The suggestion that waits for a decision. Null if there is no suggestion.</summary>
    public PendingCoachSuggestionDto? PendingSuggestion { get; init; }

    /// <summary>The receipt for the applied change. Null if the server applied no change.</summary>
    public CoachChangeReceiptDto? ChangeReceipt { get; init; }

    /// <summary>
    /// The answer to a language-learning question, when this turn answered one.
    /// </summary>
    /// <remarks>
    /// An answer and a <see cref="ChangeReceipt"/> never appear together: answering a question
    /// writes nothing, and a turn that wrote the plan did not answer a question.
    /// </remarks>
    public CoachAnswerDto? Answer { get; init; }

    /// <summary>The evidence behind the coach answer.</summary>
    public IReadOnlyList<CoachEvidenceDto> Evidence { get; init; } = Array.Empty<CoachEvidenceDto>();

    /// <summary>
    /// The learner's open correction of an earlier answer, when one is in force.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive and nullable, so a client built before W8 reads the same response it always did.
    /// Content-free by construction: a closed signal, a closed status, and a bounded ledger
    /// identifier. The learner's own words stay in the encrypted message they typed them into.
    /// </para>
    /// <para>
    /// Present on the turn the dispute opens as well as on the turns it constrains, because the
    /// correcting turn is itself the first answer the dispute applies to — a learner who says "that
    /// is wrong" is owed a different answer immediately, not one turn later.
    /// </para>
    /// </remarks>
    public CoachDisputeDto? Dispute { get; init; }

    /// <summary>
    /// The typed boundary behind a refusal, when the turn produced one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive and nullable, so a client built before W9 reads the same response it always did.
    /// This replaces a hardcoded English notice: the server states a closed code and a typed
    /// destination, and the client renders the sentence in the learner's own language. Deterministic
    /// server copy is for logs and operator surfaces, never for a learner.
    /// </para>
    /// <para>
    /// Travels beside <see cref="Evidence"/>, which is preserved on a refusal rather than emptied.
    /// A refusal with no evidence tells the learner only that something went wrong; a refusal with
    /// the turn's real coverage, counts and withheld reason tells them what Sam did look at, which
    /// is the difference between an apology and an answer.
    /// </para>
    /// </remarks>
    public CoachLimitationDto? Limitation { get; init; }

    /// <summary>
    /// What the grounding layer did to this answer, when it ran and the answer shipped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive and nullable. Null means the layer did not run — Off, Observe, or a host with no
    /// grounding — which is a different thing from <see cref="CoachRepairDisclosure.None"/>, and a
    /// client can tell "not checked" from "checked and clean".
    /// </para>
    /// <para>
    /// Always null on a refused turn: the learner received no answer, so there is nothing to
    /// disclose about, and <see cref="Limitation"/> is the shape that speaks for that case.
    /// </para>
    /// </remarks>
    public CoachRepairDisclosure? RepairDisclosure { get; init; }

    /// <summary>The question the coach asks. Null if the coach asks no question.</summary>
    public string? ClarifyingQuestion { get; init; }

    /// <summary>The clarification questions the coach has left in this session.</summary>
    public int ClarificationsRemaining { get; init; }

    /// <summary>The runs the learner has left today. Null if the server sets no daily limit.</summary>
    public int? RunsRemainingToday { get; init; }

    /// <summary>The time the session expires.</summary>
    public required DateTime ExpiresAtUtc { get; init; }

    /// <summary>
    /// A preference the learner asked to have remembered, waiting for their explicit decision.
    /// Null on every turn that proposed nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A candidate is inert. It is not active, it does not enter any prompt, and it changes no
    /// plan, setting, or review schedule. It becomes real only when the learner approves it
    /// through the memory routes, which is a separate action from accepting a plan suggestion —
    /// deliberately so, because "yes" to a plan change must never also mean "yes, remember this
    /// about me".
    /// </para>
    /// <para>
    /// Presentation order for a turn that carries several things to say is answer, then the write
    /// proposal, then the plan suggestion, then this — the order <c>CoachChatPane</c> renders. The
    /// pedagogical answer is what the learner asked for; the memory prompt is the server asking
    /// them for something, so it comes last.
    /// </para>
    /// <para>
    /// Carries no storage identifier beyond the opaque fact id and version the client must echo
    /// back to approve, edit, or decline it.
    /// </para>
    /// </remarks>
    public CoachMemoryFactDto? MemoryCandidate { get; init; }

    /// <summary>
    /// The change Sam proposed on this turn, waiting for the learner's answer. Null on every turn
    /// that proposed nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A proposal is inert. Nothing in learner data has changed and nothing will until the learner
    /// accepts it — or, for a protected change, confirms it through a separate authenticated
    /// request. What Sam wrote in its reply is prose; this field is the fact, and a client renders
    /// the card from this rather than from anything the model said.
    /// </para>
    /// <para>
    /// Presentation order for a turn carrying several things is answer, then this, then the plan
    /// suggestion, then the memory candidate — the order <c>CoachChatPane</c> renders. It runs
    /// from what the learner asked for, through what Sam wants to change in the material they
    /// already own, to what it wants to do to today's plan, and only last to what it wants to
    /// remember.
    /// </para>
    /// <para>
    /// Carries no arguments, no confirmation value, and no protected payload — only the opaque
    /// operation id the learner's own approval request will name.
    /// </para>
    /// </remarks>
    public CoachWriteOperationDto? WriteOperation { get; init; }

    /// <summary>
    /// Returns a copy carrying the given write proposal.
    /// </summary>
    /// <remarks>
    /// Attached at the single turn exit point for the same reason the memory candidate is: a
    /// proposal is orthogonal to every reducer and can ride along with an answer, a plan
    /// suggestion, or nothing at all.
    /// <b>A new member on this type must be added here too</b>, or it will silently disappear from
    /// any turn that proposed a change. <c>CoachTurnResponseCopyContractTests</c> fails if that
    /// happens.
    /// </remarks>
    public CoachTurnResponse WithWriteOperation(CoachWriteOperationDto? writeOperation) => new()
    {
        SessionId = SessionId,
        TurnId = TurnId,
        Status = Status,
        StopReason = StopReason,
        SessionStatus = SessionStatus,
        Messages = Messages,
        ActiveConstraints = ActiveConstraints,
        PlanState = PlanState,
        PendingSuggestion = PendingSuggestion,
        ChangeReceipt = ChangeReceipt,
        Answer = Answer,
        Evidence = Evidence,
        ClarifyingQuestion = ClarifyingQuestion,
        ClarificationsRemaining = ClarificationsRemaining,
        RunsRemainingToday = RunsRemainingToday,
        ExpiresAtUtc = ExpiresAtUtc,
        MemoryCandidate = MemoryCandidate,
        WriteOperation = writeOperation
    };

    /// <summary>
    /// Returns a copy carrying the given memory candidate.
    /// </summary>
    /// <remarks>
    /// Hand-written because this is a class, not a record, and a record would change the equality
    /// contract that existing callers rely on. The turn reducers each build a response for their
    /// own branch; a candidate is orthogonal to all of them, so it is attached once at the single
    /// exit point rather than threaded through seven signatures that have no interest in it.
    /// <b>A new member on this type must be added here too</b>, or it will silently disappear from
    /// any turn that proposed a memory.
    /// </remarks>
    public CoachTurnResponse WithMemoryCandidate(CoachMemoryFactDto? candidate) => new()
    {
        SessionId = SessionId,
        TurnId = TurnId,
        Status = Status,
        StopReason = StopReason,
        SessionStatus = SessionStatus,
        Messages = Messages,
        ActiveConstraints = ActiveConstraints,
        PlanState = PlanState,
        PendingSuggestion = PendingSuggestion,
        ChangeReceipt = ChangeReceipt,
        Answer = Answer,
        Evidence = Evidence,
        ClarifyingQuestion = ClarifyingQuestion,
        ClarificationsRemaining = ClarificationsRemaining,
        RunsRemainingToday = RunsRemainingToday,
        ExpiresAtUtc = ExpiresAtUtc,
        MemoryCandidate = candidate,
        WriteOperation = WriteOperation
    };

    /// <summary>
    /// Returns a copy whose message list is the given one.
    /// </summary>
    /// <remarks>
    /// Used to replace the messages a reducer minted in memory with the rows the ledger actually
    /// committed, so the identifiers and timestamps in a live turn response are the same ones a
    /// later reload of the conversation returns. Without it the same message answers to a fresh
    /// per-response GUID here and to its durable id on every history surface, and a client that
    /// merges the two sees one message as two.
    /// <b>A new member on this type must be added here too</b>, or it will silently disappear from
    /// every durable turn. <c>CoachTurnResponseCopyContractTests</c> fails if that happens.
    /// </remarks>
    public CoachTurnResponse WithMessages(IReadOnlyList<CoachMessageDto> messages) => new()
    {
        SessionId = SessionId,
        TurnId = TurnId,
        Status = Status,
        StopReason = StopReason,
        SessionStatus = SessionStatus,
        Messages = messages,
        ActiveConstraints = ActiveConstraints,
        PlanState = PlanState,
        PendingSuggestion = PendingSuggestion,
        ChangeReceipt = ChangeReceipt,
        Answer = Answer,
        Evidence = Evidence,
        ClarifyingQuestion = ClarifyingQuestion,
        ClarificationsRemaining = ClarificationsRemaining,
        RunsRemainingToday = RunsRemainingToday,
        ExpiresAtUtc = ExpiresAtUtc,
        MemoryCandidate = MemoryCandidate,
        WriteOperation = WriteOperation
    };
}
