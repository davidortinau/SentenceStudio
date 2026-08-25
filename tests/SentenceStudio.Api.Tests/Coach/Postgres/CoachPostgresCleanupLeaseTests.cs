using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;

namespace SentenceStudio.Api.Tests.Coach.Postgres;

/// <summary>
/// The advisory lock that keeps scheduled coach cleanup to one runner across replicas.
/// </summary>
/// <remarks>
/// This is the one guarantee in the coach persistence layer that has no in-memory equivalent at
/// all. <c>pg_try_advisory_xact_lock</c> is a server-side primitive scoped to a transaction on a
/// specific connection, so proving "exactly one runner" requires two genuinely independent
/// connections contending for it. A single-connection test would take the lock twice and prove
/// nothing, because PostgreSQL grants a session its own advisory lock re-entrantly.
/// </remarks>
public sealed class CoachPostgresCleanupLeaseTests : IAsyncLifetime
{
    private CoachPostgresHarness _harness = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await CoachPostgresHarness.CreateAsync("cleanup");
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task Only_one_of_two_replicas_takes_the_cleanup_lease()
    {
        // The lease opens and owns its own transaction, so each contender only needs its own
        // context -- and therefore its own connection, which is what makes the contention real.
        await using var firstDb = _harness.NewContext();
        await using var secondDb = _harness.NewContext();

        var first = await _harness.NewCleanupLease(firstDb).TryAcquireAsync();
        first.Should().NotBeNull("the first replica to ask must be allowed to run the sweep");

        var second = await _harness.NewCleanupLease(secondDb).TryAcquireAsync();
        second.Should().BeNull(
            "a second replica must be turned away rather than run a concurrent sweep; two sweeps "
            + "deleting the same expired rows is how a cleanup job turns into a deadlock or a "
            + "double-delete");

        await first!.DisposeAsync();
    }

    [PostgresFact]
    public async Task The_lease_is_released_when_the_holders_transaction_ends()
    {
        await using var firstDb = _harness.NewContext();

        var held = await _harness.NewCleanupLease(firstDb).TryAcquireAsync();
        held.Should().NotBeNull();

        // Disposing the handle ends the transaction the lease opened, which is what releases a
        // transaction-scoped advisory lock. The lock therefore cannot leak: even a replica that
        // died mid-sweep would drop it when its connection closed.
        await held!.DisposeAsync();

        await using var secondDb = _harness.NewContext();
        var next = await _harness.NewCleanupLease(secondDb).TryAcquireAsync();

        next.Should().NotBeNull(
            "cleanup must not be wedged forever by a replica that died holding the lease");

        await next!.DisposeAsync();
    }

    [PostgresFact]
    public async Task Many_replicas_asking_at_once_still_yield_exactly_one_runner()
    {
        const int Replicas = 8;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var peak = 0;
        var granted = 0;

        async Task ContendAsync()
        {
            await using var db = _harness.NewContext();
            var lease = _harness.NewCleanupLease(db);

            await gate.Task;

            var handle = await lease.TryAcquireAsync();
            if (handle is null)
            {
                return;
            }

            Interlocked.Increment(ref granted);
            var current = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, current);

            // Hold it long enough that any other winner would overlap observably.
            await Task.Delay(60);

            Interlocked.Decrement(ref running);
            await handle.DisposeAsync();
        }

        var contenders = Enumerable.Range(0, Replicas).Select(_ => ContendAsync()).ToArray();
        gate.SetResult();
        await Task.WhenAll(contenders);

        granted.Should().Be(1, "exactly one replica may sweep at a time");
        peak.Should().Be(1, "and no two holders may ever overlap");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}
