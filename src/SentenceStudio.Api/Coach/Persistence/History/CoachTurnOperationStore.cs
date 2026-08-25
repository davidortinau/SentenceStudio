using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// EF Core implementation of <see cref="ICoachTurnOperationStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// The single-writer rule is enforced inside the claim transaction: a conversation may have at
/// most one non-terminal operation holding an unexpired lease. A second claim for the same
/// conversation is told the conversation is busy rather than being allowed to interleave writes
/// into the same transcript.
/// </para>
/// <para>
/// Takeover is safe because every claim increments <see cref="CoachTurnOperation.FencingVersion"/>
/// and every finalization must present the token it was given. A worker that stalled past its
/// lease therefore cannot complete an operation another worker has already taken over — it is
/// told the lease was lost and discards its work.
/// </para>
/// </remarks>
public sealed class CoachTurnOperationStore : ICoachTurnOperationStore
{
    /// <summary>
    /// How many times a finalizing write re-reads and retries after losing a concurrency race.
    /// </summary>
    /// <remarks>
    /// Three, because the writers it contends with are bounded: a heartbeat renewing at a third of
    /// the lease, and at most one cancel request per turn. A row that has moved three times inside
    /// one finalizing write is not contention, it is a fault, and spinning on it would turn a bug
    /// into a hang.
    /// </remarks>
    private const int MaxFinalizeAttempts = 3;

    private readonly CoachDbContext _db;
    private readonly ICoachContentProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CoachTurnOperationStore> _logger;

    public CoachTurnOperationStore(
        CoachDbContext db,
        ICoachContentProtector protector,
        TimeProvider timeProvider,
        ILogger<CoachTurnOperationStore> logger)
    {
        _db = db;
        _protector = protector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private IQueryable<CoachTurnOperation> Owned(CoachOwner owner) =>
        _db.CoachTurnOperations.Where(o => o.UserProfileId == owner.UserProfileId);

    private bool HasOwner(CoachOwner owner, string operation)
    {
        if (!owner.IsEmpty)
        {
            return true;
        }

        _logger.LogWarning("[Coach] {Operation} called with no active user id — returning no data.", operation);
        return false;
    }

    public async Task<CoachTurnClaimResult> ClaimAsync(
        CoachOwner owner,
        ClaimCoachTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!HasOwner(owner, nameof(ClaimAsync)))
        {
            return CoachTurnClaimResult.Failed(CoachTurnClaimOutcome.NoOwner);
        }

        if (string.IsNullOrWhiteSpace(request.ConversationId)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.IdempotencyKey.Length > CoachHistoryLimits.IdempotencyKeyMaxLength
            || string.IsNullOrWhiteSpace(request.LeaseOwner)
            || request.LeaseOwner.Length > CoachHistoryLimits.LeaseOwnerMaxLength
            || (request.OperationId is { } id && !IsWellFormedOperationId(id)))
        {
            return CoachTurnClaimResult.Failed(CoachTurnClaimOutcome.ConversationNotFound);
        }

        var keyDigest = ComputeKeyDigest(owner, request.ConversationId, request.IdempotencyKey);
        var requestDigest = ComputeRequestDigest(request.RequestPayload ?? string.Empty);

        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            var conversationExists = await _db.CoachConversations.AnyAsync(
                c => c.UserProfileId == owner.UserProfileId
                     && c.Id == request.ConversationId
                     && c.Status == CoachConversationStatus.Active
                     && c.DeletedAt == null,
                cancellationToken);

            if (!conversationExists)
            {
                return CoachTurnClaimResult.Failed(CoachTurnClaimOutcome.ConversationNotFound);
            }

            var now = UtcNow;

            var existing = await Owned(owner).FirstOrDefaultAsync(
                o => o.ConversationId == request.ConversationId && o.IdempotencyKeyDigest == keyDigest,
                cancellationToken);

            if (existing is not null)
            {
                var replay = EvaluateReplay(owner, existing, requestDigest, now);
                if (replay is not null)
                {
                    return replay;
                }

                // The lease expired on a non-terminal operation: take it over with a higher
                // fencing version so the previous worker can no longer finalize.
                return await TakeOverAsync(owner, existing, request, now, transaction, cancellationToken);
            }

            // The client names its own operations so it can poll after a lost response, which
            // means it can also reuse a name by mistake. Reaching here with an id that is already
            // taken means this key has never been seen but the id has, so the two disagree about
            // which turn they refer to. That is the same ambiguity as a reused idempotency key and
            // gets the same answer: refuse, rather than insert and let the primary key decide.
            if (!string.IsNullOrWhiteSpace(request.OperationId))
            {
                var idTaken = await Owned(owner).AsNoTracking().AnyAsync(
                    o => o.Id == request.OperationId,
                    cancellationToken);

                if (idTaken)
                {
                    _logger.LogWarning(
                        "[Coach] {Operation} rejected a claim reusing an operation id under a different idempotency key.",
                        nameof(ClaimAsync));
                    return CoachTurnClaimResult.Failed(CoachTurnClaimOutcome.PayloadConflict);
                }
            }

            var busy = await Owned(owner).AnyAsync(
                o => o.ConversationId == request.ConversationId
                     && (o.Status == CoachTurnOperationStatus.Pending || o.Status == CoachTurnOperationStatus.Running)
                     && o.LeaseExpiresAt != null
                     && o.LeaseExpiresAt > now,
                cancellationToken);

            if (busy)
            {
                return CoachTurnClaimResult.Failed(CoachTurnClaimOutcome.ConversationBusy);
            }

            var conversationVersion = await _db.CoachConversations
                .Where(c => c.UserProfileId == owner.UserProfileId && c.Id == request.ConversationId)
                .Select(c => c.Version)
                .FirstAsync(cancellationToken);

            var operationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? Guid.NewGuid().ToString("n")
                : request.OperationId!;

            var version = _protector.CurrentVersion;

            var operation = new CoachTurnOperation
            {
                Id = operationId,
                UserProfileId = owner.UserProfileId,
                TenantId = owner.TenantId,
                ConversationId = request.ConversationId,
                IdempotencyKeyDigest = keyDigest,
                ProtectedRequestDigest = _protector.Protect(DigestContext(owner, operationId, version), requestDigest),
                ContentProtectionVersion = version,
                BaseConversationVersion = conversationVersion,
                Status = CoachTurnOperationStatus.Running,
                LeaseOwner = request.LeaseOwner,
                LeaseExpiresAt = now + NormalizeLease(request.LeaseDuration),
                FencingVersion = 1,
                AttemptCount = 1,
                CancelRequested = false,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
                StartedAt = now
            };

            _db.CoachTurnOperations.Add(operation);
            await _db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new CoachTurnClaimResult(
                CoachTurnClaimOutcome.Claimed,
                Project(operation),
                operation.FencingVersion,
                null,
                null);
        }
        catch (DbUpdateException)
        {
            // Two workers raced the same idempotency key; the unique index picked the winner.
            await RollbackAsync(transaction, cancellationToken);
            _db.ChangeTracker.Clear();

            var winner = await Owned(owner).AsNoTracking().FirstOrDefaultAsync(
                o => o.ConversationId == request.ConversationId && o.IdempotencyKeyDigest == keyDigest,
                cancellationToken);

            if (winner is null)
            {
                // No row for this key, so the constraint that rejected the insert was the primary
                // key: a client-supplied operation id that another turn already owns. The check
                // above catches this without an exception in the ordinary case; this covers the
                // race where the id was taken between that read and this write.
                if (!string.IsNullOrWhiteSpace(request.OperationId)
                    && await Owned(owner).AsNoTracking().AnyAsync(o => o.Id == request.OperationId, cancellationToken))
                {
                    return CoachTurnClaimResult.Failed(CoachTurnClaimOutcome.PayloadConflict);
                }

                return CoachTurnClaimResult.Failed(CoachTurnClaimOutcome.ConversationBusy);
            }

            return EvaluateReplay(owner, winner, requestDigest, UtcNow)
                   ?? CoachTurnClaimResult.Failed(CoachTurnClaimOutcome.InProgress);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// True when a client-supplied operation id is a safe opaque handle.
    /// </summary>
    /// <remarks>
    /// The id is chosen by the client and stored as a primary key, so it is bounded and limited
    /// to characters that cannot be mistaken for structure. It is a name and never a claim of
    /// ownership: the owner filter on every query is what actually decides who may read it.
    /// </remarks>
    private static bool IsWellFormedOperationId(string id) =>
        id.Length is > 0 and <= CoachHistoryLimits.IdMaxLength
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Decides what an existing operation means for this claim. Returns null only when the row
    /// is non-terminal with an expired lease, which is the one case that may be taken over.
    /// </summary>
    private CoachTurnClaimResult? EvaluateReplay(
        CoachOwner owner,
        CoachTurnOperation existing,
        string requestDigest,
        DateTime now)
    {
        if (!DigestMatches(owner, existing, requestDigest))
        {
            // Same key, different request. Serving the stored outcome would answer a question
            // the caller did not ask; running it would break the idempotency contract.
            _logger.LogWarning(
                "[Coach] {Operation} rejected a retry whose request does not match the stored request for the same key.",
                nameof(ClaimAsync));
            return CoachTurnClaimResult.Failed(CoachTurnClaimOutcome.PayloadConflict);
        }

        switch (existing.Status)
        {
            case CoachTurnOperationStatus.Completed:
                var context = OutcomeContext(owner, existing.Id, existing.ContentProtectionVersion);
                var readable = _protector.TryUnprotect(context, existing.ProtectedOutcome, out var outcome);
                return new CoachTurnClaimResult(
                    CoachTurnClaimOutcome.ReplayCompleted,
                    Project(existing),
                    existing.FencingVersion,
                    readable ? outcome : null,
                    existing.OutcomeSchemaVersion);

            case CoachTurnOperationStatus.Failed:
            case CoachTurnOperationStatus.Cancelled:
                return new CoachTurnClaimResult(
                    CoachTurnClaimOutcome.ReplayTerminal,
                    Project(existing),
                    existing.FencingVersion,
                    null,
                    null);

            default:
                return existing.LeaseExpiresAt is { } expiry && expiry > now
                    ? new CoachTurnClaimResult(
                        CoachTurnClaimOutcome.InProgress,
                        Project(existing),
                        existing.FencingVersion,
                        null,
                        null)
                    : null;
        }
    }

    private async Task<CoachTurnClaimResult> TakeOverAsync(
        CoachOwner owner,
        CoachTurnOperation existing,
        ClaimCoachTurnRequest request,
        DateTime now,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        existing.Status = CoachTurnOperationStatus.Running;
        existing.LeaseOwner = request.LeaseOwner;
        existing.LeaseExpiresAt = now + NormalizeLease(request.LeaseDuration);
        existing.FencingVersion++;
        existing.AttemptCount++;
        existing.UpdatedAt = now;
        existing.StartedAt ??= now;
        existing.Version++;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction, cancellationToken);
            return CoachTurnClaimResult.Failed(CoachTurnClaimOutcome.InProgress);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        _logger.LogInformation(
            "[Coach] Turn operation lease taken over at fencing version {FencingVersion} after the previous lease expired.",
            existing.FencingVersion);

        return new CoachTurnClaimResult(
            CoachTurnClaimOutcome.Claimed,
            Project(existing),
            existing.FencingVersion,
            null,
            null);
    }

    public async Task<CoachTurnFinalizeResult> RenewLeaseAsync(
        CoachOwner owner,
        string operationId,
        string leaseOwner,
        long fencingVersion,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        await FencedWriteAsync(
            owner,
            operationId,
            leaseOwner,
            fencingVersion,
            nameof(RenewLeaseAsync),
            (operation, now) => operation.LeaseExpiresAt = now + NormalizeLease(leaseDuration),
            cancellationToken);

    public async Task<CoachTurnFinalizeResult> RequestCancelAsync(
        CoachOwner owner,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(RequestCancelAsync)))
        {
            return CoachTurnFinalizeResult.Failed(CoachTurnFinalizeOutcome.NoOwner);
        }

        for (var attempt = 1; ; attempt++)
        {
            // Read fresh for the same reason a fenced write does: the running worker's heartbeat
            // is moving this row from another context, and a cancel decided against a stale copy
            // would be rejected by its own concurrency token.
            DetachStale(owner, operationId);

            var operation = await Owned(owner).FirstOrDefaultAsync(o => o.Id == operationId, cancellationToken);
            if (operation is null)
            {
                return CoachTurnFinalizeResult.Failed(CoachTurnFinalizeOutcome.NotFound);
            }

            if (IsTerminal(operation.Status))
            {
                return new CoachTurnFinalizeResult(CoachTurnFinalizeOutcome.AlreadyTerminal, Project(operation));
            }

            var now = UtcNow;
            operation.CancelRequested = true;
            operation.UpdatedAt = now;

            if (operation.Status == CoachTurnOperationStatus.Pending)
            {
                // Nothing has started, so the request can end the operation immediately rather than
                // leaving a row nobody will ever pick up.
                operation.Status = CoachTurnOperationStatus.Cancelled;
                operation.CompletedAt = now;
                operation.LeaseOwner = null;
                operation.LeaseExpiresAt = null;
            }

            operation.Version++;

            if (await TrySaveFinalizeAsync(operation, attempt, nameof(RequestCancelAsync), cancellationToken)
                is { } settled)
            {
                return settled;
            }
        }
    }

    public async Task<CoachTurnFinalizeResult> CompleteAsync(
        CoachOwner owner,
        string operationId,
        string leaseOwner,
        long fencingVersion,
        string outcomePayload,
        int outcomeSchemaVersion,
        long? firstResponseSequence,
        long? lastResponseSequence,
        CancellationToken cancellationToken = default) =>
        await FencedWriteAsync(
            owner,
            operationId,
            leaseOwner,
            fencingVersion,
            nameof(CompleteAsync),
            (operation, now) =>
            {
                var version = _protector.CurrentVersion;

                operation.Status = CoachTurnOperationStatus.Completed;
                operation.ProtectedOutcome = _protector.Protect(
                    OutcomeContext(owner, operation.Id, version),
                    outcomePayload ?? string.Empty);
                operation.OutcomeSchemaVersion = outcomeSchemaVersion;
                operation.ContentProtectionVersion = version;
                operation.FirstResponseSequence = firstResponseSequence;
                operation.LastResponseSequence = lastResponseSequence;
                operation.LeaseOwner = null;
                operation.LeaseExpiresAt = null;
                operation.CompletedAt = now;
            },
            cancellationToken);

    public async Task<CoachTurnFinalizeResult> FailAsync(
        CoachOwner owner,
        string operationId,
        string leaseOwner,
        long fencingVersion,
        string errorCode,
        CancellationToken cancellationToken = default) =>
        await FencedWriteAsync(
            owner,
            operationId,
            leaseOwner,
            fencingVersion,
            nameof(FailAsync),
            (operation, now) =>
            {
                operation.Status = operation.CancelRequested
                    ? CoachTurnOperationStatus.Cancelled
                    : CoachTurnOperationStatus.Failed;

                // A content-free code only. A message here would put learner or provider text into a
                // column that ends up in logs and support tooling.
                operation.ErrorCode = Truncate(errorCode, CoachHistoryLimits.ErrorCodeMaxLength);
                operation.LeaseOwner = null;
                operation.LeaseExpiresAt = null;
                operation.CompletedAt = now;
            },
            cancellationToken);

    public async Task<CoachTurnOperationRecord?> GetAsync(
        CoachOwner owner,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(GetAsync)) || string.IsNullOrWhiteSpace(operationId))
        {
            return null;
        }

        var operation = await Owned(owner).AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == operationId, cancellationToken);

        return operation is null ? null : Project(operation);
    }

    public async Task<CoachTurnOperationRecord?> FindActiveAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(FindActiveAsync)) || string.IsNullOrWhiteSpace(conversationId))
        {
            return null;
        }

        // Newest first, because a conversation holds one writer at a time and any older
        // non-terminal row is a crashed attempt the recovery pass owns, not something to cancel.
        var operation = await Owned(owner).AsNoTracking()
            .Where(o => o.ConversationId == conversationId
                     && (o.Status == CoachTurnOperationStatus.Pending
                      || o.Status == CoachTurnOperationStatus.Running))
            .OrderByDescending(o => o.FencingVersion)
            .FirstOrDefaultAsync(cancellationToken);

        return operation is null ? null : Project(operation);
    }

    /// <summary>The bound this read will never exceed, whatever a caller asks for.</summary>
    /// <remarks>
    /// A clamp rather than a validation error: a caller that asks for too much has a bug, and
    /// failing their turn over it would be a worse outcome than answering with the recent history
    /// that actually matters. Ten turns is far more than a live dispute survives in practice.
    /// </remarks>
    public const int MaxRecentOutcomes = 10;

    public async Task<IReadOnlyList<CoachTurnOutcome>> GetRecentOutcomesAsync(
        CoachOwner owner,
        string conversationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(GetRecentOutcomesAsync))
            || string.IsNullOrWhiteSpace(conversationId)
            || limit <= 0)
        {
            // Fail closed. No owner, no conversation, or a nonsensical bound yields no history
            // rather than the unscoped history the caller did not ask for.
            return Array.Empty<CoachTurnOutcome>();
        }

        var take = Math.Min(limit, MaxRecentOutcomes);

        // Ordered by when the turn completed, not by FencingVersion. Fencing starts at 1 on every
        // claim and only increments on a lease takeover *within* one operation, so ordering by it
        // across operations is not an ordering at all — every row ties at 1 and the database
        // returns whichever it likes. Both callers depend on "most recent" meaning it: the
        // correction load would have restored an older turn's dispute, and the refusal resume
        // would have surfaced an arbitrary refusal after the learner had moved past it.
        //
        // CreatedAt breaks a tie between two rows completed inside the same clock tick, and Id
        // makes the result deterministic when even that ties.
        var operations = await Owned(owner).AsNoTracking()
            .Where(o => o.ConversationId == conversationId
                     && o.Status == CoachTurnOperationStatus.Completed
                     && o.ProtectedOutcome != null)
            .OrderByDescending(o => o.CompletedAt)
            .ThenByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        var outcomes = new List<CoachTurnOutcome>(operations.Count);

        foreach (var operation in operations)
        {
            var context = OutcomeContext(owner, operation.Id, operation.ContentProtectionVersion);
            var readable = _protector.TryUnprotect(context, operation.ProtectedOutcome, out var outcome);

            outcomes.Add(new CoachTurnOutcome(
                readable ? outcome : null,
                operation.OutcomeSchemaVersion,
                operation.FirstResponseSequence,
                operation.LastResponseSequence));
        }

        return outcomes;
    }

    public async Task<CoachTurnOutcome?> GetOutcomeAsync(
        CoachOwner owner,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(GetOutcomeAsync)) || string.IsNullOrWhiteSpace(operationId))
        {
            return null;
        }

        var operation = await Owned(owner).AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == operationId, cancellationToken);

        if (operation is null || operation.Status != CoachTurnOperationStatus.Completed)
        {
            return null;
        }

        var context = OutcomeContext(owner, operation.Id, operation.ContentProtectionVersion);
        var readable = _protector.TryUnprotect(context, operation.ProtectedOutcome, out var outcome);

        return new CoachTurnOutcome(
            readable ? outcome : null,
            operation.OutcomeSchemaVersion,
            operation.FirstResponseSequence,
            operation.LastResponseSequence);
    }

    public async Task<IReadOnlyList<CoachTurnOperationRecord>> ListExpiredAsync(
        CoachOwner owner,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(ListExpiredAsync)))
        {
            return Array.Empty<CoachTurnOperationRecord>();
        }

        var now = UtcNow;
        var capped = limit is <= 0 or > 200 ? 50 : limit;

        var rows = await Owned(owner).AsNoTracking()
            .Where(o => (o.Status == CoachTurnOperationStatus.Pending || o.Status == CoachTurnOperationStatus.Running)
                        && (o.LeaseExpiresAt == null || o.LeaseExpiresAt <= now))
            .OrderBy(o => o.CreatedAt)
            .Take(capped)
            .ToListAsync(cancellationToken);

        return rows.Select(Project).ToList();
    }

    /// <summary>
    /// Loads an operation the caller is entitled to finalize. Returns a non-null result only when
    /// the caller may not proceed.
    /// </summary>
    /// <remarks>
    /// The row is re-read rather than taken from the change tracker. A lease that is being renewed
    /// is written by a different context on a different connection, so a copy this one tracked at
    /// claim time is a snapshot of a row that has since moved on: its fencing check would pass
    /// against stale values and its concurrency token would then lose the write it just authorized.
    /// Reading fresh makes both decisions about the row as it is now.
    /// </remarks>
    private async Task<(CoachTurnFinalizeResult? Result, CoachTurnOperation? Operation)> LoadForFinalizeAsync(
        CoachOwner owner,
        string operationId,
        string leaseOwner,
        long fencingVersion,
        string operationName,
        CancellationToken cancellationToken)
    {
        if (!HasOwner(owner, operationName))
        {
            return (CoachTurnFinalizeResult.Failed(CoachTurnFinalizeOutcome.NoOwner), null);
        }

        if (string.IsNullOrWhiteSpace(operationId))
        {
            return (CoachTurnFinalizeResult.Failed(CoachTurnFinalizeOutcome.NotFound), null);
        }

        DetachStale(owner, operationId);

        var operation = await Owned(owner).FirstOrDefaultAsync(o => o.Id == operationId, cancellationToken);
        if (operation is null)
        {
            return (CoachTurnFinalizeResult.Failed(CoachTurnFinalizeOutcome.NotFound), null);
        }

        if (IsTerminal(operation.Status))
        {
            return (new CoachTurnFinalizeResult(CoachTurnFinalizeOutcome.AlreadyTerminal, Project(operation)), null);
        }

        if (operation.FencingVersion != fencingVersion
            || !string.Equals(operation.LeaseOwner, leaseOwner, StringComparison.Ordinal))
        {
            // A superseded worker. Letting it write would duplicate the output of the worker
            // that legitimately took over.
            _logger.LogWarning(
                "[Coach] {Operation} refused a write from a superseded lease holder at fencing version {FencingVersion}.",
                operationName,
                fencingVersion);
            return (new CoachTurnFinalizeResult(CoachTurnFinalizeOutcome.LeaseLost, Project(operation)), null);
        }

        return (null, operation);
    }

    /// <summary>
    /// Drops any tracked copy of one operation, so the next read comes from the database.
    /// </summary>
    /// <remarks>
    /// Scoped to the single row rather than clearing the tracker, because the same context is
    /// holding the conversation and the messages this turn is writing, and discarding those would
    /// turn a stale read into a lost write.
    /// </remarks>
    private void DetachStale(CoachOwner owner, string operationId)
    {
        foreach (var entry in _db.ChangeTracker.Entries<CoachTurnOperation>().ToList())
        {
            if (string.Equals(entry.Entity.Id, operationId, StringComparison.Ordinal)
                && string.Equals(entry.Entity.UserProfileId, owner.UserProfileId, StringComparison.Ordinal))
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Loads, mutates, and writes one operation under the caller's fencing token, re-reading and
    /// re-deciding if a concurrent write moved the row underneath.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row's <c>Version</c> is a concurrency token, and it is moved by writers this one does
    /// not coordinate with: the lease heartbeat renewing from its own context, and a learner's
    /// cancel arriving on another request. Either can commit between this load and this write, and
    /// the write is then rejected — not because the caller lost the lease, but because it lost a
    /// race with a write that has nothing to say about ownership.
    /// </para>
    /// <para>
    /// Retrying is safe because the retry is not a blind re-issue: it re-reads the row and re-runs
    /// the terminal and fencing checks, so a caller that has genuinely been superseded is still
    /// told so. What the retry removes is the spurious refusal, whose observed cost was a turn
    /// that answered correctly and was left recorded as still running.
    /// </para>
    /// </remarks>
    private async Task<CoachTurnFinalizeResult> FencedWriteAsync(
        CoachOwner owner,
        string operationId,
        string leaseOwner,
        long fencingVersion,
        string operationName,
        Action<CoachTurnOperation, DateTime> apply,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (result, operation) = await LoadForFinalizeAsync(
                owner, operationId, leaseOwner, fencingVersion, operationName, cancellationToken);

            if (operation is null)
            {
                return result!;
            }

            var now = UtcNow;
            apply(operation, now);
            operation.UpdatedAt = now;
            operation.Version++;

            if (await TrySaveFinalizeAsync(operation, attempt, operationName, cancellationToken) is { } settled)
            {
                return settled;
            }
        }
    }

    /// <summary>
    /// Commits one finalizing write, or returns null to ask the caller to re-read and try again.
    /// </summary>
    private async Task<CoachTurnFinalizeResult?> TrySaveFinalizeAsync(
        CoachTurnOperation operation,
        int attempt,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (attempt >= MaxFinalizeAttempts)
            {
                // Bounded on purpose. A row this contended is not something to spin on, and the
                // caller is told the write did not land rather than being handed a success it
                // cannot verify.
                _logger.LogWarning(
                    "[Coach] {Operation} lost {Attempts} concurrency races on one operation row and stopped retrying.",
                    operationName,
                    attempt);

                return CoachTurnFinalizeResult.Failed(CoachTurnFinalizeOutcome.Conflict);
            }

            _logger.LogDebug(
                "[Coach] {Operation} lost a concurrency race on attempt {Attempt}; re-reading the operation row.",
                operationName,
                attempt);

            return null;
        }

        return new CoachTurnFinalizeResult(CoachTurnFinalizeOutcome.Success, Project(operation));
    }

    private bool DigestMatches(CoachOwner owner, CoachTurnOperation operation, string requestDigest)
    {
        var context = DigestContext(owner, operation.Id, operation.ContentProtectionVersion);
        if (!_protector.TryUnprotect(context, operation.ProtectedRequestDigest, out var stored) || stored is null)
        {
            // An unreadable digest cannot be proven equal, so the claim is treated as a conflict
            // rather than being waved through.
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(stored),
            Encoding.UTF8.GetBytes(requestDigest));
    }

    /// <summary>
    /// Owner- and conversation-bound digest of the client's idempotency key. The key is never
    /// stored, and the binding means the same key reused in another conversation produces an
    /// unrelated value rather than colliding.
    /// </summary>
    private static string ComputeKeyDigest(CoachOwner owner, string conversationId, string idempotencyKey)
    {
        var material = $"{owner.UserProfileId}\u001f{conversationId}\u001f{idempotencyKey}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string ComputeRequestDigest(string requestPayload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestPayload)));

    private static CoachProtectionContext DigestContext(CoachOwner owner, string operationId, int version) =>
        new(owner, CoachProtectedContentKind.TurnRequestDigest, operationId, version);

    private static CoachProtectionContext OutcomeContext(CoachOwner owner, string operationId, int version) =>
        new(owner, CoachProtectedContentKind.TurnOutcome, operationId, version);

    private static bool IsTerminal(CoachTurnOperationStatus status) =>
        status is CoachTurnOperationStatus.Completed
            or CoachTurnOperationStatus.Failed
            or CoachTurnOperationStatus.Cancelled;

    private static TimeSpan NormalizeLease(TimeSpan requested) =>
        requested <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : requested;

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];

    private static async Task RollbackAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    private static CoachTurnOperationRecord Project(CoachTurnOperation operation) =>
        new(
            operation.Id,
            operation.ConversationId,
            operation.Status,
            operation.LeaseOwner,
            operation.LeaseExpiresAt,
            operation.FencingVersion,
            operation.AttemptCount,
            operation.CancelRequested,
            operation.BaseConversationVersion,
            operation.LearnerMessageSequence,
            operation.FirstResponseSequence,
            operation.LastResponseSequence,
            operation.ErrorCode,
            operation.CreatedAt,
            operation.UpdatedAt);
}
