using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Telemetry;

namespace SentenceStudio.Api.Coach.Persistence.Cleanup;

/// <summary>
/// A mutual-exclusion lease that stops several API replicas from running cleanup at once.
/// </summary>
/// <remarks>
/// Without this, every replica wakes on the same interval and runs the same delete against the
/// same rows. That is not merely wasteful: concurrent batched deletes on overlapping row sets
/// deadlock each other, and the resulting retry storm is worst exactly when the table is largest.
/// </remarks>
public interface ICoachCleanupLease
{
    /// <summary>
    /// Tries to take the lease. Returns null when another replica holds it — that is the normal,
    /// expected outcome for every replica but one, and is not an error.
    /// </summary>
    Task<ICoachCleanupLeaseHandle?> TryAcquireAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A held cleanup lease. Call <see cref="CompleteAsync"/> on success; disposing without
/// completing abandons the work.
/// </summary>
public interface ICoachCleanupLeaseHandle : IAsyncDisposable
{
    /// <summary>Commits the work done under the lease and releases it.</summary>
    Task CompleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// PostgreSQL advisory-lock lease, scoped to a transaction.
/// </summary>
/// <remarks>
/// <para>
/// <c>pg_try_advisory_xact_lock</c> is used rather than the session-scoped
/// <c>pg_try_advisory_lock</c> because a transaction-scoped lock is released by the database when
/// the transaction ends — including when the process is killed mid-pass. A session-scoped lock
/// survives on a pooled connection that is never explicitly unlocked, and the failure mode is a
/// cleanup job that stops running forever with nothing in the logs to say why.
/// </para>
/// <para>
/// The cleanup pass runs inside the same transaction, so the lock covers the deletes it protects
/// and no separate connection is involved.
/// </para>
/// </remarks>
public sealed class PostgresCoachCleanupLease : ICoachCleanupLease
{
    /// <summary>
    /// An arbitrary but permanently fixed key. A runtime-computed hash must not be used: the key
    /// has to be identical across every replica and every deployment, and hash algorithms change.
    /// </summary>
    internal const long AdvisoryLockKey = 8_314_770_115_001_001L;

    private readonly CoachDbContext _db;
    private readonly ILogger<PostgresCoachCleanupLease> _logger;

    public PostgresCoachCleanupLease(CoachDbContext db, ILogger<PostgresCoachCleanupLease> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ICoachCleanupLeaseHandle?> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var acquired = await TryLockAsync(cancellationToken);

            if (!acquired)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
                return null;
            }

            return new TransactionLeaseHandle(transaction);
        }
        catch (Exception ex)
        {
            await transaction.DisposeAsync();

            // Losing the lease attempt must never take the host down; the next tick retries.
            var facts = CoachExceptionSanitizer.Describe(ex);
            _logger.LogWarning(
                "[Coach] Cleanup lease could not be acquired. Category={FailureCategory}",
                facts.Category);

            return null;
        }
    }

    private async Task<bool> TryLockAsync(CancellationToken cancellationToken)
    {
        // Issued through EF so the ambient transaction and connection are used automatically —
        // the lock must be taken on the same transaction the deletes run in, or it protects
        // nothing.
        var acquired = await _db.Database
            .SqlQueryRaw<bool>($"SELECT pg_try_advisory_xact_lock({AdvisoryLockKey}) AS \"Value\"")
            .SingleAsync(cancellationToken);

        return acquired;
    }

    private sealed class TransactionLeaseHandle : ICoachCleanupLeaseHandle
    {
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction _transaction;
        private bool _completed;

        public TransactionLeaseHandle(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction) =>
            _transaction = transaction;

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            await _transaction.CommitAsync(cancellationToken);
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                // Abandoning is the safe default: the lock releases with the transaction and the
                // next pass reconsiders the same rows.
                try
                {
                    await _transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                    // The transaction is already dead; disposal below is all that is left to do.
                }
            }

            await _transaction.DisposeAsync();
        }
    }
}

/// <summary>
/// Single-process lease for providers without advisory locks — the SQLite test provider, and any
/// single-instance local run.
/// </summary>
/// <remarks>
/// This guarantees mutual exclusion <b>within one process only</b>. It is selected only when the
/// provider is not PostgreSQL, so a deployed multi-replica host always gets the database lease
/// instead. The static semaphore is deliberate: a per-instance one would guard nothing, since
/// each scope resolves its own lease.
/// </remarks>
public sealed class InProcessCoachCleanupLease : ICoachCleanupLease
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <inheritdoc />
    public async Task<ICoachCleanupLeaseHandle?> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        var acquired = await Gate.WaitAsync(TimeSpan.Zero, cancellationToken);
        return acquired ? new SemaphoreLeaseHandle() : null;
    }

    private sealed class SemaphoreLeaseHandle : ICoachCleanupLeaseHandle
    {
        private bool _released;

        public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            if (!_released)
            {
                _released = true;
                Gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
