using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Tests.Coach.History;

/// <summary>
/// The production renewal path over its own database context, with a signal a test can wait on
/// and a switch a test can use to take the lease away.
/// </summary>
/// <remarks>
/// <para>
/// A decorator, not a fake: every renewal that is not deliberately overridden goes to the real
/// <see cref="CoachTurnOperationStore"/> over the real database, on a context of its own, exactly
/// as <see cref="ScopedCoachTurnLeaseRenewer"/> does in production. Sharing the turn's context
/// would be the one thing production is careful not to do.
/// </para>
/// <para>
/// <see cref="WaitForNextAsync"/> exists because the heartbeat's timer callback starts a renewal
/// and returns; advancing a virtual clock therefore proves the renewal was <em>due</em>, not that
/// it landed. Waiting on the completion signal is what makes the assertion about durable state
/// rather than about scheduling.
/// </para>
/// </remarks>
internal sealed class RecordingCoachTurnLeaseRenewer : ICoachTurnLeaseRenewer
{
    private readonly CoachPersistenceHarness _persistence;
    private readonly object _gate = new();
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RecordingCoachTurnLeaseRenewer(CoachPersistenceHarness persistence) => _persistence = persistence;

    /// <summary>How many renewals have been attempted.</summary>
    public int Attempts { get; private set; }

    /// <summary>The outcomes handed back, in order.</summary>
    public List<CoachTurnFinalizeOutcome> Outcomes { get; } = new();

    /// <summary>
    /// When set, every renewal reports this instead of touching the database. Used to take the
    /// lease away from a worker without having to win a real race first.
    /// </summary>
    public CoachTurnFinalizeOutcome? ForcedOutcome { get; set; }

    /// <summary>When set, every renewal throws this, standing in for an unreachable database.</summary>
    public Func<Exception>? ForcedFault { get; set; }

    /// <summary>
    /// Completes when the next renewal finishes. Capture it before advancing the clock.
    /// </summary>
    public Task WaitForNextAsync()
    {
        lock (_gate)
        {
            return _next.Task;
        }
    }

    public async Task<CoachTurnFinalizeOutcome> RenewAsync(
        CoachOwner owner,
        CoachTurnFence fence,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        Attempts++;

        try
        {
            if (ForcedFault is { } fault)
            {
                throw fault();
            }

            if (ForcedOutcome is { } forced)
            {
                Outcomes.Add(forced);
                return forced;
            }

            await using var db = _persistence.NewContext();
            var operations = _persistence.NewTurnOperationStore(db);

            var result = await operations.RenewLeaseAsync(
                owner,
                fence.OperationId,
                fence.LeaseOwner,
                fence.FencingVersion,
                leaseDuration,
                cancellationToken).ConfigureAwait(false);

            Outcomes.Add(result.Outcome);
            return result.Outcome;
        }
        finally
        {
            Signal();
        }
    }

    private void Signal()
    {
        TaskCompletionSource completed;
        lock (_gate)
        {
            completed = _next;
            _next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        completed.TrySetResult();
    }
}
