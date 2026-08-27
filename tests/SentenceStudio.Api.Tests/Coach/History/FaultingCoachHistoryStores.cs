using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// A message store that can be made to die at a chosen append, so a test can put the process
/// failure exactly where it hurts: between the plan write and the record of it.
/// </summary>
/// <remarks>
/// A decorator rather than a fake. Every call that is not deliberately failed goes to the real
/// store over the real database, so a crash-window test still proves what the production code
/// path actually wrote.
/// </remarks>
internal sealed class FaultingCoachMessageStore(ICoachMessageStore inner) : ICoachMessageStore
{
    private int _appends;

    /// <summary>The 1-based append that throws. Null never throws.</summary>
    public int? FailOnAppendNumber { get; set; }

    /// <summary>How many appends were attempted, including the one that threw.</summary>
    public int AppendAttempts => _appends;

    /// <summary>
    /// Arms a fault at the nth append from now, forgetting appends already made.
    /// </summary>
    /// <remarks>
    /// The raw counter is cumulative across the whole harness, so "fail on the second append"
    /// means something different in a test that has already run a turn. Tests that crash two
    /// conversations in sequence want the fault positioned within each turn, not within the
    /// harness's lifetime.
    /// </remarks>
    public void FailOnNextAppend(int offset)
    {
        _appends = 0;
        FailOnAppendNumber = offset;
    }

    public Task<CoachMessageAppendResult> AppendAsync(
        CoachOwner owner,
        AppendCoachMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        _appends++;
        if (FailOnAppendNumber == _appends)
        {
            throw new InvalidOperationException("Simulated crash during message append.");
        }

        return inner.AppendAsync(owner, request, cancellationToken);
    }

    public Task<CoachMessagePage> GetLatestAsync(
        CoachOwner owner, string conversationId, int? pageSize = null, CancellationToken cancellationToken = default)
        => inner.GetLatestAsync(owner, conversationId, pageSize, cancellationToken);

    public Task<CoachMessagePage> GetBeforeAsync(
        CoachOwner owner, string conversationId, string cursor, int? pageSize = null, CancellationToken cancellationToken = default)
        => inner.GetBeforeAsync(owner, conversationId, cursor, pageSize, cancellationToken);

    public Task<CoachMessagePage> GetBeforeSequenceAsync(
        CoachOwner owner, string conversationId, long upperExclusiveSequence, int? pageSize = null, CancellationToken cancellationToken = default)
        => inner.GetBeforeSequenceAsync(owner, conversationId, upperExclusiveSequence, pageSize, cancellationToken);

    public Task<CoachMessagePage> GetRangeAsync(
        CoachOwner owner, string conversationId, long fromSequence, long toSequence, CancellationToken cancellationToken = default)
        => inner.GetRangeAsync(owner, conversationId, fromSequence, toSequence, cancellationToken);
}

/// <summary>
/// A turn-operation store that can die at the moment a turn is being finalized — the worst
/// possible window, because everything the turn did is already committed and only the record
/// saying so is missing.
/// </summary>
internal sealed class FaultingCoachTurnOperationStore(ICoachTurnOperationStore inner) : ICoachTurnOperationStore
{
    /// <summary>When true, <see cref="CompleteAsync"/> throws instead of finalizing.</summary>
    public bool FailOnComplete { get; set; }

    /// <summary>
    /// Runs immediately before a finalizing call reaches the real store, so a test can act in the
    /// moment the turn is being closed out.
    /// </summary>
    /// <remarks>
    /// The window this opens is the one the heartbeat used to write into: everything the turn did
    /// is committed, the operation row is still Running, and the finalizing write has not started.
    /// </remarks>
    public Action? BeforeComplete { get; set; }

    /// <summary>The same window for the paths that end a turn by failing or cancelling it.</summary>
    public Action? BeforeFail { get; set; }

    /// <summary>
    /// When set, <see cref="CompleteAsync"/> reports this instead of writing.
    /// </summary>
    /// <remarks>
    /// Stands in for a refusal the store could not resolve by re-reading — the state a genuinely
    /// contended operation row ends in once its retries are spent.
    /// </remarks>
    public CoachTurnFinalizeResult? CompleteOutcome { get; set; }

    public Task<CoachTurnClaimResult> ClaimAsync(
        CoachOwner owner, ClaimCoachTurnRequest request, CancellationToken cancellationToken = default)
        => inner.ClaimAsync(owner, request, cancellationToken);

    public Task<CoachTurnFinalizeResult> RenewLeaseAsync(
        CoachOwner owner, string operationId, string leaseOwner, long fencingVersion, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => inner.RenewLeaseAsync(owner, operationId, leaseOwner, fencingVersion, leaseDuration, cancellationToken);

    public Task<CoachTurnFinalizeResult> RequestCancelAsync(
        CoachOwner owner, string operationId, CancellationToken cancellationToken = default)
        => inner.RequestCancelAsync(owner, operationId, cancellationToken);

    public Task<CoachTurnFinalizeResult> CompleteAsync(
        CoachOwner owner, string operationId, string leaseOwner, long fencingVersion, string outcomePayload,
        int outcomeSchemaVersion, long? firstResponseSequence, long? lastResponseSequence,
        CancellationToken cancellationToken = default)
    {
        BeforeComplete?.Invoke();

        if (FailOnComplete)
        {
            throw new InvalidOperationException("Simulated crash before the operation was completed.");
        }

        if (CompleteOutcome is { } forced)
        {
            return Task.FromResult(forced);
        }

        return inner.CompleteAsync(
            owner, operationId, leaseOwner, fencingVersion, outcomePayload, outcomeSchemaVersion,
            firstResponseSequence, lastResponseSequence, cancellationToken);
    }

    public Task<CoachTurnFinalizeResult> FailAsync(
        CoachOwner owner, string operationId, string leaseOwner, long fencingVersion, string errorCode,
        CancellationToken cancellationToken = default)
    {
        BeforeFail?.Invoke();

        return inner.FailAsync(owner, operationId, leaseOwner, fencingVersion, errorCode, cancellationToken);
    }

    public Task<CoachTurnOutcome?> GetOutcomeAsync(
        CoachOwner owner, string operationId, CancellationToken cancellationToken = default)
        => inner.GetOutcomeAsync(owner, operationId, cancellationToken);

    public Task<IReadOnlyList<CoachTurnOutcome>> GetRecentOutcomesAsync(
        CoachOwner owner, string conversationId, int limit, CancellationToken cancellationToken = default)
        => inner.GetRecentOutcomesAsync(owner, conversationId, limit, cancellationToken);

    public Task<CoachTurnOperationRecord?> GetAsync(
        CoachOwner owner, string operationId, CancellationToken cancellationToken = default)
        => inner.GetAsync(owner, operationId, cancellationToken);

    public Task<CoachTurnOperationRecord?> FindActiveAsync(
        CoachOwner owner, string conversationId, CancellationToken cancellationToken = default)
        => inner.FindActiveAsync(owner, conversationId, cancellationToken);

    public Task<IReadOnlyList<CoachTurnOperationRecord>> ListExpiredAsync(
        CoachOwner owner, int limit = 50, CancellationToken cancellationToken = default)
        => inner.ListExpiredAsync(owner, limit, cancellationToken);
}
