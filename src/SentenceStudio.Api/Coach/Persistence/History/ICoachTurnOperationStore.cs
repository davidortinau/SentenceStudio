namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// Owner-scoped storage for durable turn operations: idempotency, single-writer leasing,
/// cancellation, and replayable outcomes.
/// </summary>
/// <remarks>
/// <para>
/// This is the primitive that makes a coach turn survive a restart. A retry finds the same row
/// instead of running the turn twice; a crashed worker's lease expires and a replacement claims
/// the row with a higher fencing version, which makes the dead worker's finalization fail closed
/// rather than append a second copy of its output.
/// </para>
/// <para>
/// The store owns durability only. It does not run turns, call models, or append messages —
/// composing those into a turn is the application layer's job.
/// </para>
/// </remarks>
public interface ICoachTurnOperationStore
{
    /// <summary>
    /// Atomically claims the single-writer slot for a conversation, or reports why the caller
    /// may not proceed: a completed replay, an in-flight duplicate, a same-key/different-payload
    /// conflict, or another operation holding the slot.
    /// </summary>
    Task<CoachTurnClaimResult> ClaimAsync(
        CoachOwner owner,
        ClaimCoachTurnRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends a lease the caller still holds. Fails with
    /// <see cref="CoachTurnFinalizeOutcome.LeaseLost"/> once another worker has taken over.
    /// </summary>
    Task<CoachTurnFinalizeResult> RenewLeaseAsync(
        CoachOwner owner,
        string operationId,
        string leaseOwner,
        long fencingVersion,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Durably records a cancellation request. A pending operation ends immediately; a running
    /// one is flagged so its worker can stop at the next checkpoint.
    /// </summary>
    Task<CoachTurnFinalizeResult> RequestCancelAsync(
        CoachOwner owner,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes an operation with a replayable outcome. Requires the caller's fencing token, so
    /// a superseded worker cannot overwrite the winner's result.
    /// </summary>
    Task<CoachTurnFinalizeResult> CompleteAsync(
        CoachOwner owner,
        string operationId,
        string leaseOwner,
        long fencingVersion,
        string outcomePayload,
        int outcomeSchemaVersion,
        long? firstResponseSequence,
        long? lastResponseSequence,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fails an operation with a content-free error code. Requires the caller's fencing token.
    /// </summary>
    Task<CoachTurnFinalizeResult> FailAsync(
        CoachOwner owner,
        string operationId,
        string leaseOwner,
        long fencingVersion,
        string errorCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one operation's durable outcome, so a poll can reconstruct the exact response the
    /// winning worker produced.
    /// </summary>
    /// <remarks>
    /// Claiming already returns the outcome on a same-key replay, but a client that lost its
    /// response and polls by operation id has no key to claim with. Without this, a dropped
    /// response degrades into "read the ledger and hope it matches", which is exactly the
    /// success-shaped gap durable operations exist to close.
    /// </remarks>
    Task<CoachTurnOutcome?> GetOutcomeAsync(
        CoachOwner owner,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent completed outcomes for one conversation, newest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounded by <paramref name="limit"/> and scoped to the owner and the conversation, because the
    /// only caller is the correction-state load and an unbounded scan of a long conversation would
    /// put an arbitrary amount of decryption on the front of every turn.
    /// </para>
    /// <para>
    /// Owner scoping is not a filter the caller can forget: the query runs through the same owned
    /// set every other read uses, so an empty owner yields nothing rather than everything. A dispute
    /// carried across owners or conversations would constrain one learner's answer with another
    /// learner's disagreement.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<CoachTurnOutcome>> GetRecentOutcomesAsync(
        CoachOwner owner,
        string conversationId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one operation without disturbing its lease.</summary>
    Task<CoachTurnOperationRecord?> GetAsync(
        CoachOwner owner,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the operation currently running for a conversation, if any.
    /// </summary>
    /// <remarks>
    /// The compatibility <c>/sessions</c> cancel route carries a session id and no operation id,
    /// because it predates durable operations entirely. Without this lookup that cancel can only
    /// signal the in-process run registry, which stops the local model call but leaves the durable
    /// operation to finish and write its result on the next stage — a cancel button that appears to
    /// work and does not.
    /// </remarks>
    Task<CoachTurnOperationRecord?> FindActiveAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists operations whose lease has expired while still non-terminal — the crash-recovery
    /// input. Owner-scoped, so a recovery pass is always run for a known learner.
    /// </summary>
    Task<IReadOnlyList<CoachTurnOperationRecord>> ListExpiredAsync(
        CoachOwner owner,
        int limit = 50,
        CancellationToken cancellationToken = default);
}
