using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Coach.Application.History;

/// <summary>
/// Extends the lease on a turn operation from outside the request's own unit of work.
/// </summary>
/// <remarks>
/// <para>
/// A separate abstraction rather than a direct call to <see cref="ICoachTurnOperationStore"/>
/// because renewal has to happen <em>while</em> the turn is running, and the turn is already
/// using the request-scoped database context. Two concurrent operations on one
/// <c>DbContext</c> is not a race the store can win; it is an invalid use of the context. The
/// renewer therefore owns its own scope, and the seam is what lets a test supply one.
/// </para>
/// </remarks>
public interface ICoachTurnLeaseRenewer
{
    /// <summary>
    /// Extends the lease named by <paramref name="fence"/>, or reports why it could not be.
    /// </summary>
    Task<CoachTurnFinalizeOutcome> RenewAsync(
        CoachOwner owner,
        CoachTurnFence fence,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The production renewer. Resolves a fresh operation store in its own scope for each renewal.
/// </summary>
public sealed class ScopedCoachTurnLeaseRenewer : ICoachTurnLeaseRenewer
{
    private readonly IServiceScopeFactory _scopes;

    public ScopedCoachTurnLeaseRenewer(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<CoachTurnFinalizeOutcome> RenewAsync(
        CoachOwner owner,
        CoachTurnFence fence,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fence);

        await using var scope = _scopes.CreateAsyncScope();
        var operations = scope.ServiceProvider.GetRequiredService<ICoachTurnOperationStore>();

        var result = await operations.RenewLeaseAsync(
            owner,
            fence.OperationId,
            fence.LeaseOwner,
            fence.FencingVersion,
            leaseDuration,
            cancellationToken).ConfigureAwait(false);

        return result.Outcome;
    }
}

/// <summary>
/// Keeps one worker's lease alive for as long as it is actually working, and cancels its work
/// the moment the lease is gone.
/// </summary>
/// <remarks>
/// <para>
/// The lease is the whole of the exactly-once contract: a worker may write while it holds one,
/// and another worker may take the conversation over once it lapses. Granting a lease and never
/// extending it makes those two facts contradict each other for any turn that outlives the
/// grant — the first worker is still running, still about to append its answer, and the
/// conversation is already available to be claimed by the retry the learner just sent.
/// </para>
/// <para>
/// Renewal runs on <see cref="TimeProvider"/> rather than a wall clock so the interval is
/// testable, and the interval is a fraction of the lease so a single missed or slow renewal
/// still leaves time for the next one before the lease lapses.
/// </para>
/// <para>
/// Losing the lease is not an error to retry. It means another worker owns the conversation and
/// will produce the answer the learner sees, so this one stops: <see cref="Token"/> cancels, the
/// model run unwinds, and the caller reports a conflict instead of racing to append a second
/// reply. That is the difference between a duplicate transcript and a retry that behaves.
/// </para>
/// <para>
/// Renewal has to stop before the turn is finalized, not merely before the heartbeat is disposed.
/// A renewal and a completion are two writes to one row from two different database contexts, and
/// the completion reads the row before it writes it; a renewal that commits inside that window
/// moves the row's concurrency token and the completion is rejected. <see cref="QuiesceAsync"/> is
/// what closes that window.
/// </para>
/// </remarks>
public sealed class CoachTurnLeaseHeartbeat : IAsyncDisposable
{
    /// <summary>
    /// The smallest lease this will renew against. Below it the renewal interval collapses to
    /// something that renews more often than the database can answer.
    /// </summary>
    private static readonly TimeSpan MinimumLease = TimeSpan.FromSeconds(15);

    private readonly ICoachTurnLeaseRenewer _renewer;
    private readonly CoachOwner _owner;
    private readonly CoachTurnFence _fence;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _lost = new();
    private readonly CancellationTokenSource _linked;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ITimer _timer;
    private readonly object _quiesceGate = new();

    private DateTimeOffset _holdsUntil;
    private Task? _quiescing;
    private volatile bool _stopped;
    private int _renewals;
    private bool _disposed;

    private CoachTurnLeaseHeartbeat(
        ICoachTurnLeaseRenewer renewer,
        CoachOwner owner,
        CoachTurnFence fence,
        TimeSpan leaseDuration,
        TimeProvider clock,
        ILogger logger,
        CancellationToken requestToken)
    {
        _renewer = renewer;
        _owner = owner;
        _fence = fence;
        _leaseDuration = leaseDuration < MinimumLease ? MinimumLease : leaseDuration;
        _clock = clock;
        _logger = logger;

        _linked = CancellationTokenSource.CreateLinkedTokenSource(requestToken, _lost.Token);
        _holdsUntil = clock.GetUtcNow() + _leaseDuration;

        var interval = RenewalInterval(_leaseDuration);
        _timer = clock.CreateTimer(_ => Tick(), state: null, dueTime: interval, period: interval);
    }

    /// <summary>
    /// Starts renewing the lease behind <paramref name="fence"/> until the returned heartbeat is
    /// disposed.
    /// </summary>
    public static CoachTurnLeaseHeartbeat Start(
        ICoachTurnLeaseRenewer renewer,
        CoachOwner owner,
        CoachTurnFence fence,
        TimeSpan leaseDuration,
        TimeProvider clock,
        ILogger logger,
        CancellationToken requestToken) =>
        new(renewer, owner, fence, leaseDuration, clock, logger, requestToken);

    /// <summary>
    /// How often a lease of <paramref name="leaseDuration"/> is renewed.
    /// </summary>
    /// <remarks>
    /// A third of the lease, so two consecutive renewals can fail outright and the lease still
    /// has a third of its life left when the third one runs. Halving would leave no margin at all
    /// once a single renewal is slow, which is precisely when renewal matters.
    /// </remarks>
    public static TimeSpan RenewalInterval(TimeSpan leaseDuration)
    {
        var lease = leaseDuration < MinimumLease ? MinimumLease : leaseDuration;
        return TimeSpan.FromTicks(lease.Ticks / 3);
    }

    /// <summary>
    /// The token the owned work must observe. Cancels when the request is abandoned or when the
    /// lease is lost.
    /// </summary>
    public CancellationToken Token => _linked.Token;

    /// <summary>True once this worker has been superseded and must not write again.</summary>
    public bool IsLeaseLost => _lost.IsCancellationRequested;

    /// <summary>How many renewals have been written. Zero on a turn that finished inside one lease.</summary>
    public int RenewalCount => Volatile.Read(ref _renewals);

    /// <summary>
    /// Stops renewing for good, and completes only once no renewal can still be in flight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this immediately before finalizing the operation — completing it, failing it, or
    /// cancelling it. Finalizing reads the operation row and then writes it, and a renewal that
    /// commits between those two steps moves the row's concurrency token, so the finalizing write
    /// is rejected for a reason that has nothing to do with ownership: this worker still holds the
    /// lease and simply lost a race with its own heartbeat. The observed cost of that race is a
    /// turn that answered correctly, was reported as still running, and left the client polling a
    /// row nobody would ever move.
    /// </para>
    /// <para>
    /// Stopping renewal here does not shorten the lease meaningfully. Renewal runs at a third of
    /// the lease, so a heartbeat that has been renewing successfully leaves at least two thirds of
    /// the grant standing — far longer than a finalizing write takes.
    /// </para>
    /// <para>
    /// One-way and idempotent. The permit is taken and never returned, so a tick that arrives
    /// afterwards finds the gate closed and does nothing; the tick that may already be past that
    /// check when this is called is precisely what taking the permit waits for. The awaited task
    /// is memoized, so a second call — disposal, most often — observes the first rather than
    /// waiting forever on a permit that is never coming back.
    /// </para>
    /// </remarks>
    public Task QuiesceAsync()
    {
        lock (_quiesceGate)
        {
            return _quiescing ??= QuiesceCoreAsync();
        }
    }

    private async Task QuiesceCoreAsync()
    {
        // Ordered: the flag stops a tick that has not started, disposing the timer stops one from
        // being scheduled, and taking the permit waits out the one already running. Any other
        // order leaves a tick that can still start a renewal after this returns.
        _stopped = true;

        try
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already gone. Nothing is being scheduled either way.
        }

        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposal beat this to the gate, which means renewal is over — all this promises.
        }
    }

    private void Tick()
    {
        if (_stopped || _disposed || IsLeaseLost)
        {
            return;
        }

        try
        {
            // Single-flight: a renewal that is slower than the interval must not stack up behind
            // itself, and the next tick has nothing to add while one is already in flight. It is
            // also how quiescence is enforced — a permit that is taken and never returned closes
            // this door for good.
            if (!_gate.Wait(0))
            {
                return;
            }

            // Re-checked with the permit in hand: quiescence may have set the flag between the
            // check above and this line, and a renewal started now would land after the caller
            // believes the heartbeat is quiet.
            if (_stopped)
            {
                _gate.Release();
                return;
            }

            _ = RenewOnceAsync();
        }
        catch (ObjectDisposedException)
        {
            // Disposed underneath a tick already in flight. There is nothing left to renew.
        }
    }

    private async Task RenewOnceAsync()
    {
        try
        {
            var outcome = await _renewer
                .RenewAsync(_owner, _fence, _leaseDuration, _linked.Token)
                .ConfigureAwait(false);

            switch (outcome)
            {
                case CoachTurnFinalizeOutcome.Success:
                    _holdsUntil = _clock.GetUtcNow() + _leaseDuration;
                    Interlocked.Increment(ref _renewals);
                    return;

                case CoachTurnFinalizeOutcome.LeaseLost:
                case CoachTurnFinalizeOutcome.NotFound:
                case CoachTurnFinalizeOutcome.AlreadyTerminal:
                case CoachTurnFinalizeOutcome.NoOwner:
                    // Settled: somebody else owns this operation, or it is already over.
                    Surrender(outcome.ToString());
                    return;

                default:
                    // A concurrent write won the row. That is recoverable on the next tick, so
                    // long as one lands before the lease this worker still holds runs out.
                    SurrenderIfExpired();
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            // The turn ended, or the lease was already surrendered. Either way there is nothing
            // left to renew and nothing to report.
        }
        catch (Exception ex)
        {
            // A renewal that cannot reach the database is not a lost lease on its own — the lease
            // this worker already holds is still valid until it expires. It is only fatal once
            // that grant runs out, which SurrenderIfExpired is what decides. Swallowing the
            // failure without that check is what would turn an outage into a duplicate turn.
            _logger.LogWarning(
                "[Coach] Turn lease renewal failed: {Reason}. The lease stands until it expires.",
                ex.GetType().Name);
            SurrenderIfExpired();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SurrenderIfExpired()
    {
        if (_clock.GetUtcNow() >= _holdsUntil)
        {
            Surrender("Expired");
        }
    }

    private void Surrender(string reason)
    {
        if (_lost.IsCancellationRequested)
        {
            return;
        }

        _logger.LogWarning(
            "[Coach] Turn lease surrendered at fencing version {FencingVersion}: {Reason}. The run is being stopped.",
            _fence.FencingVersion,
            reason);

        try
        {
            _lost.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed while surrendering; the run is already unwinding.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            // Quiescing already stops the timer and waits out any renewal in flight, so disposal
            // reuses it rather than repeating a weaker version of it. On a turn that finalized
            // normally this is already complete and costs nothing.
            await QuiesceAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // RenewOnceAsync never faults its task: it reports through Surrender instead. This
            // guard exists so a future change cannot turn a renewal fault into a failed turn.
        }

        _linked.Dispose();
        _lost.Dispose();
        _gate.Dispose();
    }
}
