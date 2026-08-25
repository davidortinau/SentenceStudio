namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// The state of one coach session.
/// The server sends this shape when it starts a session, and when a client reads a session.
/// </summary>
public sealed class CoachSessionResponse
{
    /// <summary>The session identifier.</summary>
    public required string SessionId { get; init; }

    /// <summary>The status of the session.</summary>
    public required CoachSessionStatus Status { get; init; }

    /// <summary>The messages in this session, in time order.</summary>
    public IReadOnlyList<CoachMessageDto> Messages { get; init; } = Array.Empty<CoachMessageDto>();

    /// <summary>The active constraints for this session.</summary>
    public required CoachConstraintSetDto ActiveConstraints { get; init; }

    /// <summary>The plan canvas state.</summary>
    public required CoachPlanStateDto PlanState { get; init; }

    /// <summary>The suggestion that waits for a decision. Null if there is no suggestion.</summary>
    public PendingCoachSuggestionDto? PendingSuggestion { get; init; }

    /// <summary>The evidence the coach used.</summary>
    public IReadOnlyList<CoachEvidenceDto> Evidence { get; init; } = Array.Empty<CoachEvidenceDto>();

    /// <summary>
    /// The learner's open correction, when one is in force for this session.
    /// </summary>
    /// <remarks>
    /// Additive and nullable, so a client built before W8 reads the same response it always did.
    /// On a cold session read the durable answer carries its own copy, so this is the live view
    /// rather than the only one — a resumed conversation recovers the correction from the stored
    /// turn even on a host that answers this as null.
    /// </remarks>
    public CoachDisputeDto? Dispute { get; init; }

    /// <summary>
    /// The limitation still in force for this session, when the last answered turn was withheld.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive and nullable, so a client built before W9 reads the same response it always did,
    /// and a host that does not populate it answers null rather than an empty object.
    /// </para>
    /// <para>
    /// <b>Why this member has to exist.</b> A grounding refusal reaches the client on
    /// <see cref="CoachTurnResponse.Limitation"/> and nowhere else. After a reload the client
    /// rebuilds the conversation from the ledger's <c>CoachHistoryMessageDto</c> rows, and
    /// <see cref="CoachTurnOperationDto.Result"/> — the only stored copy of a turn response — is
    /// returned on the submit path only; a poll reads messages instead. So without this member a
    /// learner who reloads mid-refusal comes back to an answer that was withheld with nothing on
    /// screen saying so, which is the reverse of what the refusal is for.
    /// </para>
    /// <para>
    /// <b>Rows do not come with it.</b> The session projection restores the limitation from the
    /// stored outcome, but not the evidence the refusal was judged against — that lived on the
    /// turn. So a restored refusal carries its own <c>WithheldCount</c> and <c>WithheldReason</c>,
    /// and the client states them from the limitation rather than from an evidence panel that is
    /// no longer there.
    /// </para>
    /// </remarks>
    public CoachLimitationDto? Limitation { get; init; }

    /// <summary>
    /// The repair disclosure the last completed turn ended on, restored on reload.
    /// </summary>
    /// <remarks>
    /// Latest-only, exactly like <see cref="Limitation"/>: a later ordinary turn clears it, an
    /// unreadable latest outcome yields null, and no older disclosure is resurrected. A learner
    /// whose newest answer was clean must not be told an older one was rewritten.
    /// </remarks>
    public CoachRepairDisclosure? RepairDisclosure { get; init; }

    /// <summary>The applied revisions in this session, in time order.</summary>
    public IReadOnlyList<CoachRevisionDto> Revisions { get; init; } = Array.Empty<CoachRevisionDto>();

    /// <summary>The clarification questions the coach has left in this session.</summary>
    public int ClarificationsRemaining { get; init; }

    /// <summary>The runs the learner has left today. Null if the server sets no daily limit.</summary>
    public int? RunsRemainingToday { get; init; }

    /// <summary>The time the server created the session.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>The time the session expires.</summary>
    public required DateTime ExpiresAtUtc { get; init; }
}
