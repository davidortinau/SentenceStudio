using SentenceStudio.Api.Coach.Agents;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// What the durable-history layer hands the session reducer for one turn.
/// </summary>
/// <remarks>
/// <para>
/// The reducer is the only writer of plan state, so durable history does not replace it — it
/// wraps it. This is the whole of the extra context that wrapping needs, and it is deliberately
/// small: everything else the turn requires is already reachable from the owned session.
/// </para>
/// <para>
/// <see cref="Default"/> is what the compatibility <c>/sessions</c> routes pass, so the
/// pre-history behaviour is a value rather than a branch.
/// </para>
/// </remarks>
public sealed record CoachTurnExecutionContext
{
    /// <summary>The context for a turn with no durable history involved.</summary>
    public static CoachTurnExecutionContext Default { get; } = new();

    /// <summary>
    /// Bounded, role-tagged conversation history to seed a rebuilt agent session with.
    /// </summary>
    /// <remarks>
    /// Populated only when the 24-hour checkpoint was missing, expired, or written under an
    /// incompatible configuration, so the turn has no serialized agent session to resume. The
    /// agent renders these as fenced conversation data; they are never replayed as instructions,
    /// and receipts, notices, and suggestion snapshots are excluded before they get here.
    /// </remarks>
    public IReadOnlyList<CoachPriorMessage> PriorMessages { get; init; } = Array.Empty<CoachPriorMessage>();

    /// <summary>
    /// True when the caller owns idempotency durably and the process-local store must not also
    /// answer or record.
    /// </summary>
    /// <remarks>
    /// Two idempotency stores answering the same retry is worse than one: the in-memory store
    /// forgets on restart, so it would return a hit for a turn the durable operation had already
    /// recorded differently. The durable operation is the single authority when history is on.
    /// </remarks>
    public bool BypassProcessIdempotency { get; init; }

    /// <summary>
    /// Asks whether a cancel has been recorded durably. Checked between the model call and the
    /// reducer; null means there is nothing durable to consult.
    /// </summary>
    /// <remarks>
    /// The in-process run registry only reaches the replica that owns the run, so a cancel that
    /// arrives at a different replica has to be seen in the database. Checking at the stage
    /// boundary — after the model has answered but before anything is applied — is what makes a
    /// cancel free of side effects rather than merely fast.
    /// </remarks>
    public Func<CancellationToken, ValueTask<bool>>? IsCancelRequested { get; init; }

    /// <summary>
    /// The learner's open correction of an earlier answer, when the durable layer found one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supplied by the caller rather than loaded here, because the dispute lives in the protected
    /// turn outcomes and the conversation service is the only layer that decrypts those. A session
    /// service that went looking for it would need the decoder, the owner scoping, and the bounded
    /// scan — three things that already exist one layer up, and would then exist twice.
    /// </para>
    /// <para>
    /// Null on a host without durable history, which is the same as no dispute: the correction
    /// state is a property of a conversation, and a conversation that is not stored has no earlier
    /// answer to correct.
    /// </para>
    /// </remarks>
    public Persistence.History.CoachTurnDisputeState? ActiveDispute { get; init; }

    /// <summary>
    /// The ledger identifier of the most recent durable coach message in this conversation.
    /// </summary>
    /// <remarks>
    /// The anchor a new dispute is keyed to. It has to be the exact prior coach message: a dispute
    /// keyed to the turn in flight would constrain the answer the learner is disputing rather than
    /// the one that comes after it, and a dispute keyed to nothing would constrain any answer at
    /// all. Null when the conversation has no coach message yet, in which case there is nothing to
    /// correct and no dispute can open.
    /// </remarks>
    public string? PriorCoachMessageId { get; init; }

    /// <summary>
    /// The prior turn's content-free trace, for the definitions it read.
    /// </summary>
    /// <remarks>
    /// Stored with the dispute when one opens, so the repeat check survives a reload: the next turn
    /// can tell "the coach looked somewhere new" from "the coach ran the same read again" without
    /// holding the previous turn's trace in memory.
    /// </remarks>
    public Persistence.History.CoachTurnTraceSummary? PriorTrace { get; init; }
}
