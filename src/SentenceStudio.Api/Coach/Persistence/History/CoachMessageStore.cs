using Microsoft.EntityFrameworkCore;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// EF Core implementation of <see cref="ICoachMessageStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Sequence allocation is the delicate part. The conversation row carries the allocator
/// (<see cref="CoachConversation.LastSequence"/>) guarded by its concurrency token, and the
/// unique index on owner + conversation + sequence is the backstop. Two writers racing produce
/// exactly one winner: the loser sees a concurrency or unique-violation failure, re-reads the
/// true maximum, and retries. The index means a bug in the retry path fails loudly instead of
/// silently duplicating a position in the transcript.
/// </para>
/// </remarks>
public sealed class CoachMessageStore : ICoachMessageStore
{
    /// <summary>
    /// How many times an append re-reads and retries before giving up. Each retry only happens
    /// when another writer won, so the bound is contention tolerance, not error masking.
    /// </summary>
    private const int MaxAppendAttempts = 5;

    private readonly CoachDbContext _db;
    private readonly ICoachContentProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CoachMessageStore> _logger;

    public CoachMessageStore(
        CoachDbContext db,
        ICoachContentProtector protector,
        TimeProvider timeProvider,
        ILogger<CoachMessageStore> logger)
    {
        _db = db;
        _protector = protector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private IQueryable<CoachMessage> Owned(CoachOwner owner, string conversationId) =>
        _db.CoachMessages.Where(m => m.UserProfileId == owner.UserProfileId && m.ConversationId == conversationId);

    private bool HasOwner(CoachOwner owner, string operation)
    {
        if (!owner.IsEmpty)
        {
            return true;
        }

        _logger.LogWarning("[Coach] {Operation} called with no active user id — returning no data.", operation);
        return false;
    }

    public async Task<CoachMessageAppendResult> AppendAsync(
        CoachOwner owner,
        AppendCoachMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (owner.IsEmpty)
        {
            _logger.LogWarning("[Coach] {Operation} refused: no owning user profile in scope.", nameof(AppendAsync));
            return CoachMessageAppendResult.Failed(CoachHistoryStatus.NoOwner);
        }

        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return CoachMessageAppendResult.Failed(CoachHistoryStatus.NotFound);
        }

        var payload = request.Payload ?? throw new ArgumentException("A coach message requires a payload.", nameof(request));
        payload.SchemaVersion = CoachHistorySchema.MessagePayloadVersion;

        var validation = CoachMessagePayloadSerializer.Validate(payload);
        if (!validation.IsValid)
        {
            // The bound is checked on plaintext, before protection: encryption hides content but
            // not length, so validating afterwards would leave the limit to the database.
            _logger.LogWarning(
                "[Coach] {Operation} rejected a payload: {Reason} on {Field}.",
                nameof(AppendAsync),
                validation.Error,
                validation.Field ?? "payload");
            return CoachMessageAppendResult.Failed(CoachHistoryStatus.InvalidRequest);
        }

        var messageId = string.IsNullOrWhiteSpace(request.MessageId)
            ? Guid.NewGuid().ToString("n")
            : request.MessageId!;

        if (messageId.Length > CoachHistoryLimits.IdMaxLength)
        {
            return CoachMessageAppendResult.Failed(CoachHistoryStatus.InvalidRequest);
        }

        for (var attempt = 1; attempt <= MaxAppendAttempts; attempt++)
        {
            var result = await TryAppendOnceAsync(owner, request, payload, messageId, attempt, cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }

        _logger.LogWarning(
            "[Coach] {Operation} exhausted {Attempts} sequence allocation attempts under contention.",
            nameof(AppendAsync),
            MaxAppendAttempts);
        return CoachMessageAppendResult.Failed(CoachHistoryStatus.Conflict);
    }

    /// <summary>
    /// One allocation attempt. Returns null when a concurrent writer won and the caller should
    /// re-read and retry.
    /// </summary>
    private async Task<CoachMessageAppendResult?> TryAppendOnceAsync(
        CoachOwner owner,
        AppendCoachMessageRequest request,
        CoachMessagePayload payload,
        string messageId,
        int attempt,
        CancellationToken cancellationToken)
    {
        // A caller that already opened a transaction owns the boundary; nesting one here would
        // commit its work early.
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        CoachConversation? conversation = null;
        CoachMessage? message = null;

        try
        {
            conversation = await _db.CoachConversations
                .FirstOrDefaultAsync(
                    c => c.UserProfileId == owner.UserProfileId
                         && c.Id == request.ConversationId
                         && c.Status != CoachConversationStatus.Deleting
                         && c.DeletedAt == null,
                    cancellationToken);

            if (conversation is null)
            {
                return CoachMessageAppendResult.Failed(CoachHistoryStatus.NotFound);
            }

            // The caller may supply its own message id for idempotent retries. Detecting the
            // duplicate before Add keeps a re-send from colliding with an instance the context
            // already tracks, which surfaces as an identity conflict rather than a store error.
            var duplicateId = await _db.CoachMessages
                .AsNoTracking()
                .AnyAsync(m => m.UserProfileId == owner.UserProfileId && m.Id == messageId, cancellationToken);

            if (duplicateId)
            {
                _logger.LogWarning(
                    "[Coach] {Operation} rejected an append because the message id already exists.",
                    nameof(AppendAsync));
                return CoachMessageAppendResult.Failed(CoachHistoryStatus.Conflict);
            }

            // The fence is taken inside this transaction, immediately before the insert, because
            // the question it answers ("am I still the writer?") can only be answered together
            // with the write it authorizes. The conditional update is what makes it atomic: it
            // both verifies the operation is still held at this fencing version and takes the
            // row's exclusive lock, so a takeover racing this append must either commit first —
            // in which case the predicate no longer matches and nothing is appended — or wait
            // until this append has committed and then supersede it. There is no interleaving in
            // which both workers believe they hold the conversation.
            if (request.Fence is { } fence)
            {
                var stillHeld = await _db.CoachTurnOperations
                    .Where(o => o.UserProfileId == owner.UserProfileId
                                && o.Id == fence.OperationId
                                && o.FencingVersion == fence.FencingVersion
                                && o.LeaseOwner == fence.LeaseOwner
                                && o.Status != CoachTurnOperationStatus.Completed
                                && o.Status != CoachTurnOperationStatus.Failed
                                && o.Status != CoachTurnOperationStatus.Cancelled)
                    // UpdatedAt only. The row's concurrency token is deliberately left alone so
                    // this lock does not turn the holder's own later completion into a spurious
                    // conflict — the fencing version is the authority here, not the token.
                    .ExecuteUpdateAsync(o => o.SetProperty(x => x.UpdatedAt, UtcNow), cancellationToken);

                if (stillHeld != 1)
                {
                    _logger.LogWarning(
                        "[Coach] {Operation} refused an append from a superseded lease holder at fencing version {FencingVersion}.",
                        nameof(AppendAsync),
                        fence.FencingVersion);

                    await RollbackAsync(transaction, cancellationToken);
                    return CoachMessageAppendResult.Failed(CoachHistoryStatus.LeaseLost);
                }
            }

            // Trust the ledger, not the counter. LastSequence is a cache that can fall behind if a
            // writer committed a row this context never saw, so the true tail is always the max
            // stored sequence. Reading it on every attempt is what makes "no gaps, no duplicates"
            // a property of the data rather than of the counter's bookkeeping.
            var highest = await Owned(owner, request.ConversationId)
                .MaxAsync(m => (long?)m.Sequence, cancellationToken) ?? 0;

            var nextSequence = Math.Max(conversation.LastSequence, highest) + 1;

            var now = UtcNow;
            payload.CreatedAtUtc = now;

            var version = _protector.CurrentVersion;
            var json = CoachMessagePayloadSerializer.Serialize(payload);

            message = new CoachMessage
            {
                Id = messageId,
                UserProfileId = owner.UserProfileId,
                TenantId = owner.TenantId,
                ConversationId = request.ConversationId,
                Sequence = nextSequence,
                Role = request.Role,
                Kind = request.Kind,
                ProtectedPayload = _protector.Protect(PayloadContext(owner, messageId, version), json),
                ContentSchemaVersion = payload.SchemaVersion,
                ContentProtectionVersion = version,
                OperationId = request.OperationId,
                CreatedAt = now
            };

            _db.CoachMessages.Add(message);

            conversation.LastSequence = nextSequence;
            conversation.UpdatedAt = now;
            conversation.Version++;

            await _db.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new CoachMessageAppendResult(CoachHistoryStatus.Success, Project(owner, message, payload));
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer changed the conversation between our read and our write.
            await RollbackAsync(transaction, cancellationToken);
            Detach(conversation, message);
            return null;
        }
        catch (DbUpdateException)
        {
            // Either the unique sequence index rejected our position, or the caller supplied a
            // message id that already exists. Both surface identically through EF, so the cause
            // is resolved by asking the database rather than by matching a provider error code.
            await RollbackAsync(transaction, cancellationToken);
            Detach(conversation, message);

            var duplicateId = await _db.CoachMessages
                .AnyAsync(m => m.UserProfileId == owner.UserProfileId && m.Id == messageId, cancellationToken);

            if (duplicateId)
            {
                _logger.LogWarning(
                    "[Coach] {Operation} rejected an append because the message id already exists.",
                    nameof(AppendAsync));
                return CoachMessageAppendResult.Failed(CoachHistoryStatus.Conflict);
            }

            return null;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static async Task RollbackAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Drops only the entities this attempt touched, so the retry re-reads the true rows and a
    /// caller sharing the context keeps its own tracked state.
    /// </summary>
    private void Detach(CoachConversation? conversation, CoachMessage? message)
    {
        if (message is not null)
        {
            _db.Entry(message).State = EntityState.Detached;
        }

        if (conversation is not null)
        {
            _db.Entry(conversation).State = EntityState.Detached;
        }
    }

    public async Task<CoachMessagePage> GetLatestAsync(
        CoachOwner owner,
        string conversationId,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(GetLatestAsync)))
        {
            return CoachMessagePage.Empty(CoachHistoryStatus.NoOwner);
        }

        return await ReadBackwardAsync(owner, conversationId, upperExclusive: null, pageSize, cancellationToken);
    }

    public async Task<CoachMessagePage> GetBeforeAsync(
        CoachOwner owner,
        string conversationId,
        string cursor,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(GetBeforeAsync)))
        {
            return CoachMessagePage.Empty(CoachHistoryStatus.NoOwner);
        }

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return CoachMessagePage.Empty(CoachHistoryStatus.NotFound);
        }

        if (!CoachHistoryCursor.TryDecodeMessage(_protector, owner, conversationId, cursor, out var before))
        {
            _logger.LogWarning("[Coach] {Operation} received an unreadable cursor — refusing the read.", nameof(GetBeforeAsync));
            return CoachMessagePage.Empty(CoachHistoryStatus.InvalidCursor);
        }

        return await ReadBackwardAsync(owner, conversationId, before, pageSize, cancellationToken);
    }

    public async Task<CoachMessagePage> GetBeforeSequenceAsync(
        CoachOwner owner,
        string conversationId,
        long upperExclusiveSequence,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(GetBeforeSequenceAsync)))
        {
            return CoachMessagePage.Empty(CoachHistoryStatus.NoOwner);
        }

        // Sequences start at one, so a bound of one or less selects nothing. That is a real answer
        // — the first message in a conversation has no history before it — and the ordinary read
        // path produces it, so ownership and visibility are still checked the same way.
        return await ReadBackwardAsync(owner, conversationId, upperExclusiveSequence, pageSize, cancellationToken);
    }

    public async Task<CoachMessagePage> GetRangeAsync(
        CoachOwner owner,
        string conversationId,
        long fromSequence,
        long toSequence,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(GetRangeAsync)))
        {
            return CoachMessagePage.Empty(CoachHistoryStatus.NoOwner);
        }

        if (string.IsNullOrWhiteSpace(conversationId) || toSequence < fromSequence)
        {
            return CoachMessagePage.Empty(CoachHistoryStatus.InvalidRequest);
        }

        if (!await ConversationIsVisibleAsync(owner, conversationId, cancellationToken))
        {
            return CoachMessagePage.Empty(CoachHistoryStatus.NotFound);
        }

        // A caller cannot widen a range read into a full transcript dump.
        var capped = Math.Min(toSequence, fromSequence + CoachHistoryLimits.MessagePageMax - 1);

        var rows = await Owned(owner, conversationId)
            .Where(m => m.Sequence >= fromSequence && m.Sequence <= capped)
            .OrderBy(m => m.Sequence)
            .ToListAsync(cancellationToken);

        return BuildPage(owner, rows, includePreviousCursor: false, conversationId);
    }

    private async Task<CoachMessagePage> ReadBackwardAsync(
        CoachOwner owner,
        string conversationId,
        long? upperExclusive,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return CoachMessagePage.Empty(CoachHistoryStatus.NotFound);
        }

        if (!await ConversationIsVisibleAsync(owner, conversationId, cancellationToken))
        {
            return CoachMessagePage.Empty(CoachHistoryStatus.NotFound);
        }

        var size = Clamp(pageSize, CoachHistoryLimits.MessagePageDefault, CoachHistoryLimits.MessagePageMax);
        var query = Owned(owner, conversationId);

        if (upperExclusive is { } bound)
        {
            query = query.Where(m => m.Sequence < bound);
        }

        // Read newest-first so paging is anchored to the end of the transcript, then reverse:
        // the client always receives history in the order it happened.
        var rows = await query
            .OrderByDescending(m => m.Sequence)
            .Take(size)
            .ToListAsync(cancellationToken);

        rows.Reverse();
        return BuildPage(owner, rows, includePreviousCursor: rows.Count == size, conversationId);
    }

    private async Task<bool> ConversationIsVisibleAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken) =>
        await _db.CoachConversations.AnyAsync(
            c => c.UserProfileId == owner.UserProfileId
                 && c.Id == conversationId
                 && c.Status != CoachConversationStatus.Deleting
                 && c.DeletedAt == null,
            cancellationToken);

    private CoachMessagePage BuildPage(
        CoachOwner owner,
        List<CoachMessage> rows,
        bool includePreviousCursor,
        string conversationId)
    {
        var items = new List<CoachMessageRecord>(rows.Count);
        var unreadable = 0;

        foreach (var row in rows)
        {
            var record = Project(owner, row, payload: null);
            if (!record.IsReadable)
            {
                unreadable++;
            }

            items.Add(record);
        }

        var previousCursor = includePreviousCursor && rows.Count > 0
            ? CoachHistoryCursor.EncodeMessage(_protector, owner, conversationId, rows[0].Sequence)
            : null;

        return new CoachMessagePage(CoachHistoryStatus.Success, items, previousCursor, unreadable);
    }

    private CoachMessageRecord Project(CoachOwner owner, CoachMessage message, CoachMessagePayload? payload)
    {
        if (payload is null)
        {
            var context = PayloadContext(owner, message.Id, message.ContentProtectionVersion);
            if (_protector.TryUnprotect(context, message.ProtectedPayload, out var json))
            {
                // An unreadable row is still returned, with a null payload, so the ledger keeps
                // its shape and the client can show a recoverable placeholder instead of
                // silently losing a turn.
                CoachMessagePayloadSerializer.TryDeserialize(json, out payload);
            }
        }

        return new CoachMessageRecord(
            message.Id,
            message.ConversationId,
            message.Sequence,
            message.Role,
            message.Kind,
            payload,
            message.ContentSchemaVersion,
            message.OperationId,
            message.CreatedAt);
    }

    private static CoachProtectionContext PayloadContext(CoachOwner owner, string messageId, int version) =>
        new(owner, CoachProtectedContentKind.MessagePayload, messageId, version);

    private static int Clamp(int? requested, int fallback, int max)
    {
        if (requested is null or <= 0)
        {
            return fallback;
        }

        return requested.Value > max ? max : requested.Value;
    }
}
