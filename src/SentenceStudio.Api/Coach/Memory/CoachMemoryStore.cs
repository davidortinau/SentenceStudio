using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Api.Coach.Memory;

/// <summary>
/// EF Core implementation of <see cref="ICoachMemoryStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every query starts from <see cref="Owned"/>. There is no read or write in this file that
/// touches the memory table without an owner filter.
/// </para>
/// <para>
/// Values are encrypted with a purpose bound to the owner, the row id, and the protection version.
/// That binding is what makes a ciphertext lifted from another row or another account unreadable
/// rather than merely misplaced.
/// </para>
/// </remarks>
public sealed class CoachMemoryStore : ICoachMemoryStore
{
    private const string CursorPrefix = "mem1";
    private const string CursorScope = "memories";

    private readonly CoachDbContext _db;
    private readonly ICoachContentProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<CoachMemoryOptions> _options;
    private readonly ICoachMemoryChangedNotifier _notifier;
    private readonly ILogger<CoachMemoryStore> _logger;

    /// <summary>Creates the store.</summary>
    public CoachMemoryStore(
        CoachDbContext db,
        ICoachContentProtector protector,
        TimeProvider timeProvider,
        IOptions<CoachMemoryOptions> options,
        ICoachMemoryChangedNotifier notifier,
        ILogger<CoachMemoryStore> logger)
    {
        _db = db;
        _protector = protector;
        _timeProvider = timeProvider;
        _options = options;
        _notifier = notifier;
        _logger = logger;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private CoachMemoryOptions Options => _options.Value;

    private IQueryable<CoachMemoryFact> Owned(CoachOwner owner) =>
        _db.CoachMemoryFacts.Where(f => f.UserProfileId == owner.UserProfileId);

    private bool HasOwner(CoachOwner owner, string operation)
    {
        if (!owner.IsEmpty)
        {
            return true;
        }

        _logger.LogWarning("[Coach] {Operation} called with no active user id — returning no data.", operation);
        return false;
    }

    private CoachProtectionContext ValueContext(CoachOwner owner, string factId, int version) =>
        new(owner, CoachProtectedContentKind.MemoryFactValue, factId, version);

    // ---------------------------------------------------------------- create

    /// <inheritdoc />
    public async Task<CoachMemoryResult> CreateCandidateAsync(
        CoachOwner owner,
        CreateCoachMemoryCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Options.Enabled)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.Disabled);
        }

        if (owner.IsEmpty)
        {
            _logger.LogWarning("[Coach] {Operation} refused: no owning user profile in scope.", nameof(CreateCandidateAsync));
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.NoOwner);
        }

        var scopeCheck = ValidateScope(request.Scope, request.TargetLanguageCode);
        if (scopeCheck != CoachMemoryValueRejection.None)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.InvalidRequest, scopeCheck);
        }

        var rejection = CoachMemoryValueSerializer.Validate(request.Value);
        if (rejection != CoachMemoryValueRejection.None)
        {
            // The reason travels; the value does not.
            _logger.LogInformation("[Coach] Memory candidate refused. Kind={Kind} Reason={Reason}", request.Value?.Kind, rejection);
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.ValueRejected, rejection);
        }

        var evidence = VerifyEvidence(request.LearnerMessageText, request.EvidenceSpan);
        if (evidence != CoachMemoryStatusCode.Success)
        {
            return CoachMemoryResult.Failed(evidence);
        }

        if (request.SourceConversationId is { Length: > CoachMemoryLimits.IdMaxLength }
            || request.SourceMessageId is { Length: > CoachMemoryLimits.IdMaxLength })
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.InvalidRequest);
        }

        var openCandidates = await Owned(owner)
            .CountAsync(
                f => f.Status == CoachMemoryStatus.Candidate || f.Status == CoachMemoryStatus.ConflictPending,
                cancellationToken)
            .ConfigureAwait(false);

        if (openCandidates >= Options.MaxCandidates)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.LimitReached);
        }

        var now = UtcNow;
        var observed = request.ObservedAt ?? now;
        var scopeKey = ScopeKey(request.Scope, request.TargetLanguageCode);

        // A candidate that contradicts an active fact is parked, not applied. Nothing about an
        // existing memory changes until the learner says so.
        var conflicts = await Owned(owner)
            .AnyAsync(
                f => f.Status == CoachMemoryStatus.Active && f.Kind == request.Value.Kind && f.ScopeKey == scopeKey,
                cancellationToken)
            .ConfigureAwait(false);

        var id = Guid.NewGuid().ToString("n");
        var fact = new CoachMemoryFact
        {
            Id = id,
            UserProfileId = owner.UserProfileId,
            TenantId = owner.TenantId,
            Kind = request.Value.Kind,
            Scope = request.Scope,
            TargetLanguageCode = request.Scope == CoachMemoryScope.Global ? null : request.TargetLanguageCode,
            ScopeKey = scopeKey,
            ProtectedValue = _protector.Protect(
                ValueContext(owner, id, _protector.CurrentVersion),
                CoachMemoryValueSerializer.Serialize(request.Value)),
            ValueVersion = CoachMemorySchema.ValueVersion,
            ProtectionVersion = _protector.CurrentVersion,
            Status = conflicts ? CoachMemoryStatus.ConflictPending : CoachMemoryStatus.Candidate,
            Provenance = CoachMemoryProvenance.UserExplicit,
            SourceConversationId = request.SourceConversationId,
            SourceMessageId = request.SourceMessageId,
            EvidenceCount = 1,
            EvidenceFirstObservedAt = observed,
            EvidenceLastObservedAt = observed,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = Options.CandidateExpiryDays > 0 ? now.AddDays(Options.CandidateExpiryDays) : null,
            Version = 1
        };

        _db.CoachMemoryFacts.Add(fact);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CoachMemoryResult(CoachMemoryStatusCode.Success, Project(owner, fact));
    }

    // ------------------------------------------------------------------ read

    /// <inheritdoc />
    public async Task<CoachMemoryPage> ListAsync(
        CoachOwner owner,
        CoachMemoryListFilter filter,
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (!Options.Enabled)
        {
            return CoachMemoryPage.Empty(CoachMemoryStatusCode.Disabled);
        }

        if (!HasOwner(owner, nameof(ListAsync)))
        {
            return CoachMemoryPage.Empty(CoachMemoryStatusCode.NoOwner);
        }

        var size = Math.Clamp(pageSize ?? CoachMemoryLimits.PageSizeDefault, 1, CoachMemoryLimits.PageSizeMax);

        var query = filter switch
        {
            CoachMemoryListFilter.Active => Owned(owner).Where(f => f.Status == CoachMemoryStatus.Active),
            CoachMemoryListFilter.Candidates => Owned(owner)
                .Where(f => f.Status == CoachMemoryStatus.Candidate || f.Status == CoachMemoryStatus.ConflictPending),
            _ => Owned(owner)
        };

        if (!string.IsNullOrEmpty(cursor))
        {
            if (!TryDecodeCursor(owner, cursor, out var updatedAt, out var id))
            {
                return CoachMemoryPage.Empty(CoachMemoryStatusCode.InvalidCursor);
            }

            query = query.Where(f => f.UpdatedAt < updatedAt || (f.UpdatedAt == updatedAt && string.Compare(f.Id, id) < 0));
        }

        var rows = await query
            .OrderByDescending(f => f.UpdatedAt)
            .ThenByDescending(f => f.Id)
            .Take(size + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? nextCursor = null;
        if (rows.Count > size)
        {
            var last = rows[size - 1];
            nextCursor = EncodeCursor(owner, last.UpdatedAt, last.Id);
            rows.RemoveRange(size, rows.Count - size);
        }

        var items = new List<CoachMemoryFactRecord>(rows.Count);
        foreach (var row in rows)
        {
            var record = Project(owner, row);
            if (record is not null)
            {
                items.Add(record);
            }
        }

        return new CoachMemoryPage(CoachMemoryStatusCode.Success, items, nextCursor);
    }

    /// <inheritdoc />
    public async Task<CoachMemoryResult> GetAsync(CoachOwner owner, string factId, CancellationToken cancellationToken = default)
    {
        if (!Options.Enabled)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.Disabled);
        }

        if (!HasOwner(owner, nameof(GetAsync)))
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.NoOwner);
        }

        var row = await Owned(owner).FirstOrDefaultAsync(f => f.Id == factId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.NotFound);
        }

        var record = Project(owner, row);
        return record is null
            ? CoachMemoryResult.Failed(CoachMemoryStatusCode.NotFound)
            : new CoachMemoryResult(CoachMemoryStatusCode.Success, record);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CoachMemoryFactRecord>> ListEligibleForContextAsync(
        CoachOwner owner,
        CancellationToken cancellationToken = default)
    {
        if (!Options.Enabled || !HasOwner(owner, nameof(ListEligibleForContextAsync)))
        {
            return Array.Empty<CoachMemoryFactRecord>();
        }

        var now = UtcNow;
        var rows = await Owned(owner)
            .Where(f => f.Status == CoachMemoryStatus.Active && (f.ExpiresAt == null || f.ExpiresAt > now))
            .OrderByDescending(f => f.UpdatedAt)
            .Take(CoachMemoryLimits.ActiveFactsMax)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var records = new List<CoachMemoryFactRecord>(rows.Count);
        foreach (var row in rows)
        {
            var record = Project(owner, row);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    // ----------------------------------------------------------------- write

    /// <inheritdoc />
    public async Task<CoachMemoryResult> ApproveAsync(
        CoachOwner owner,
        string factId,
        int expectedVersion,
        CoachMemoryStoredValue? editedValue = null,
        CancellationToken cancellationToken = default)
    {
        if (!Options.Enabled)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.Disabled);
        }

        if (owner.IsEmpty)
        {
            _logger.LogWarning("[Coach] {Operation} refused: no owning user profile in scope.", nameof(ApproveAsync));
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.NoOwner);
        }

        var row = await Owned(owner).FirstOrDefaultAsync(f => f.Id == factId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.NotFound);
        }

        if (row.Status is not (CoachMemoryStatus.Candidate or CoachMemoryStatus.ConflictPending))
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.Conflict);
        }

        if (row.Version != expectedVersion)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.Conflict);
        }

        if (editedValue is not null)
        {
            if (editedValue.Kind != row.Kind)
            {
                // Changing the kind during approval would turn a reviewed candidate into an
                // unreviewed one.
                return CoachMemoryResult.Failed(CoachMemoryStatusCode.InvalidRequest, CoachMemoryValueRejection.UnsupportedKind);
            }

            var rejection = CoachMemoryValueSerializer.Validate(editedValue);
            if (rejection != CoachMemoryValueRejection.None)
            {
                return CoachMemoryResult.Failed(CoachMemoryStatusCode.ValueRejected, rejection);
            }
        }

        var activeCount = await Owned(owner)
            .CountAsync(f => f.Status == CoachMemoryStatus.Active, cancellationToken)
            .ConfigureAwait(false);

        var existing = await Owned(owner)
            .FirstOrDefaultAsync(
                f => f.Status == CoachMemoryStatus.Active && f.Kind == row.Kind && f.ScopeKey == row.ScopeKey,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null && activeCount >= Options.MaxActiveFacts)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.LimitReached);
        }

        var now = UtcNow;

        // Two active facts must never occupy one slot, so the approval and the supersede are one
        // transaction. They are also two statements in a fixed order: the filtered unique index is
        // evaluated per statement, so the incumbent has to be demoted before the candidate is
        // promoted or the index rejects the intermediate state.
        IDbContextTransaction? transaction = null;
        if (existing is not null && _db.Database.CurrentTransaction is null)
        {
            transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (existing is not null)
            {
                existing.Status = CoachMemoryStatus.Superseded;
                existing.UpdatedAt = now;
                existing.Version++;
                row.SupersedesId = existing.Id;

                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            ApplyApproval(owner, row, editedValue, now);

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.Conflict);
        }
        catch (DbUpdateException)
        {
            // The filtered unique index is the last line of defence against a concurrent approval.
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.Conflict);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }

        await NotifyAsync(owner, CoachMemoryChangeKind.Approved, 1, cancellationToken).ConfigureAwait(false);

        return new CoachMemoryResult(CoachMemoryStatusCode.Success, Project(owner, row));
    }

    /// <summary>Promotes a reviewed candidate in place, applying the learner's edit if there is one.</summary>
    private void ApplyApproval(CoachOwner owner, CoachMemoryFact row, CoachMemoryStoredValue? editedValue, DateTime now)
    {
        if (editedValue is not null)
        {
            row.ProtectedValue = _protector.Protect(
                ValueContext(owner, row.Id, _protector.CurrentVersion),
                CoachMemoryValueSerializer.Serialize(editedValue));
            row.ProtectionVersion = _protector.CurrentVersion;
            row.ValueVersion = CoachMemorySchema.ValueVersion;
        }

        row.Status = CoachMemoryStatus.Active;
        row.Provenance = CoachMemoryProvenance.UserConfirmed;
        row.ConfirmedAt = now;
        row.UpdatedAt = now;
        row.ExpiresAt = Options.ActiveFactExpiryDays > 0 ? now.AddDays(Options.ActiveFactExpiryDays) : null;
        row.Version++;
    }

    /// <inheritdoc />
    public async Task<CoachMemoryStatusCode> RejectAsync(
        CoachOwner owner,
        string factId,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (!Options.Enabled)
        {
            return CoachMemoryStatusCode.Disabled;
        }

        if (!HasOwner(owner, nameof(RejectAsync)))
        {
            return CoachMemoryStatusCode.NoOwner;
        }

        var row = await Owned(owner).FirstOrDefaultAsync(f => f.Id == factId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return CoachMemoryStatusCode.NotFound;
        }

        if (row.Status is not (CoachMemoryStatus.Candidate or CoachMemoryStatus.ConflictPending))
        {
            return CoachMemoryStatusCode.Conflict;
        }

        if (row.Version != expectedVersion)
        {
            return CoachMemoryStatusCode.Conflict;
        }

        _db.CoachMemoryFacts.Remove(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return CoachMemoryStatusCode.Success;
    }

    /// <inheritdoc />
    public async Task<CoachMemoryResult> EditActiveAsync(
        CoachOwner owner,
        string factId,
        int expectedVersion,
        CoachMemoryStoredValue value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!Options.Enabled)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.Disabled);
        }

        if (owner.IsEmpty)
        {
            _logger.LogWarning("[Coach] {Operation} refused: no owning user profile in scope.", nameof(EditActiveAsync));
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.NoOwner);
        }

        var row = await Owned(owner).FirstOrDefaultAsync(f => f.Id == factId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.NotFound);
        }

        if (row.Status != CoachMemoryStatus.Active)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.Conflict);
        }

        if (row.Version != expectedVersion)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.Conflict);
        }

        if (value.Kind != row.Kind)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.InvalidRequest, CoachMemoryValueRejection.UnsupportedKind);
        }

        var rejection = CoachMemoryValueSerializer.Validate(value);
        if (rejection != CoachMemoryValueRejection.None)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.ValueRejected, rejection);
        }

        var now = UtcNow;
        row.ProtectedValue = _protector.Protect(
            ValueContext(owner, row.Id, _protector.CurrentVersion),
            CoachMemoryValueSerializer.Serialize(value));
        row.ProtectionVersion = _protector.CurrentVersion;
        row.ValueVersion = CoachMemorySchema.ValueVersion;
        row.Provenance = CoachMemoryProvenance.UserConfirmed;
        row.ConfirmedAt = now;
        row.UpdatedAt = now;
        row.Version++;

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CoachMemoryResult.Failed(CoachMemoryStatusCode.Conflict);
        }

        await NotifyAsync(owner, CoachMemoryChangeKind.Edited, 1, cancellationToken).ConfigureAwait(false);

        return new CoachMemoryResult(CoachMemoryStatusCode.Success, Project(owner, row));
    }

    /// <inheritdoc />
    public async Task<CoachMemoryStatusCode> ForgetAsync(
        CoachOwner owner,
        string factId,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (!Options.Enabled)
        {
            return CoachMemoryStatusCode.Disabled;
        }

        if (!HasOwner(owner, nameof(ForgetAsync)))
        {
            return CoachMemoryStatusCode.NoOwner;
        }

        var row = await Owned(owner).FirstOrDefaultAsync(f => f.Id == factId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return CoachMemoryStatusCode.NotFound;
        }

        if (row.Version != expectedVersion)
        {
            return CoachMemoryStatusCode.Conflict;
        }

        _db.CoachMemoryFacts.Remove(row);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CoachMemoryStatusCode.Conflict;
        }

        await NotifyAsync(owner, CoachMemoryChangeKind.Forgotten, 1, cancellationToken).ConfigureAwait(false);
        return CoachMemoryStatusCode.Success;
    }

    /// <inheritdoc />
    public async Task<CoachMemoryForgetAllResult> ForgetAllAsync(CoachOwner owner, CancellationToken cancellationToken = default)
    {
        if (!Options.Enabled)
        {
            return CoachMemoryForgetAllResult.Failed(CoachMemoryStatusCode.Disabled);
        }

        if (!HasOwner(owner, nameof(ForgetAllAsync)))
        {
            return CoachMemoryForgetAllResult.Failed(CoachMemoryStatusCode.NoOwner);
        }

        var removed = await DeleteAllForOwnerAsync(owner, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(owner, CoachMemoryChangeKind.ForgottenAll, removed, cancellationToken).ConfigureAwait(false);
        return new CoachMemoryForgetAllResult(CoachMemoryStatusCode.Success, removed);
    }

    /// <inheritdoc />
    public async Task<int> MarkUsedAsync(
        CoachOwner owner,
        IReadOnlyCollection<string> factIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factIds);

        if (!Options.Enabled || owner.IsEmpty || factIds.Count == 0)
        {
            return 0;
        }

        var now = UtcNow;
        var ids = factIds.Take(CoachMemoryLimits.ContextFactsMax).ToArray();

        // Deliberately does not touch UpdatedAt or Version: recording that a fact was read must not
        // invalidate the version the learner is holding in an open editor.
        return await Owned(owner)
            .Where(f => ids.Contains(f.Id) && f.Status == CoachMemoryStatus.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.LastUsedAt, now), cancellationToken)
            .ConfigureAwait(false);
    }

    // -------------------------------------------------------------- deletion

    /// <inheritdoc />
    public async Task<int> DeleteForSourceConversationAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(DeleteForSourceConversationAsync)) || string.IsNullOrEmpty(conversationId))
        {
            return 0;
        }

        // In v1 a fact has exactly one source, so "only provenance is this conversation" is simply
        // "source is this conversation". When multi-evidence facts arrive, this predicate becomes a
        // check that no other source remains.
        var removed = await Owned(owner)
            .Where(f => f.SourceConversationId == conversationId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (removed > 0)
        {
            // ExecuteDelete bypasses the change tracker; a tracked copy would be resurrected by the
            // next SaveChanges on this context.
            _db.ChangeTracker.Clear();
            await NotifyAsync(owner, CoachMemoryChangeKind.SourceDeleted, removed, cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    /// <inheritdoc />
    public async Task<int> DeleteAllForOwnerAsync(CoachOwner owner, CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(DeleteAllForOwnerAsync)))
        {
            return 0;
        }

        var removed = await Owned(owner).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            _db.ChangeTracker.Clear();
        }

        return removed;
    }

    // --------------------------------------------------------------- helpers

    private async Task NotifyAsync(CoachOwner owner, CoachMemoryChangeKind change, int affected, CancellationToken cancellationToken)
    {
        try
        {
            await _notifier.MemoryChangedAsync(owner, change, affected, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A notifier failure must not turn a completed forget into an error the learner sees.
            _logger.LogWarning("[Coach] Memory change notification failed. Change={Change} Error={Error}", change, ex.GetType().Name);
        }
    }

    private CoachMemoryFactRecord? Project(CoachOwner owner, CoachMemoryFact row)
    {
        if (!_protector.TryUnprotect(
                ValueContext(owner, row.Id, row.ProtectionVersion),
                row.ProtectedValue,
                out var json)
            || !CoachMemoryValueSerializer.TryDeserialize(json, out var value)
            || value is null)
        {
            // Unreadable or now-forbidden rows are treated as absent. Failing closed here is what
            // stops a tampered or stale row from reaching a prompt.
            _logger.LogWarning(
                "[Coach] Memory value unreadable. Kind={Kind} ProtectionVersion={ProtectionVersion} ValueVersion={ValueVersion}",
                row.Kind,
                row.ProtectionVersion,
                row.ValueVersion);
            return null;
        }

        if (value.Kind != row.Kind)
        {
            // The plaintext kind and the column disagree: someone swapped a ciphertext.
            _logger.LogWarning("[Coach] Memory value kind mismatch. Column={Column}", row.Kind);
            return null;
        }

        return new CoachMemoryFactRecord(
            row.Id,
            row.Kind,
            row.Status,
            row.Scope,
            row.TargetLanguageCode,
            value,
            row.Provenance,
            row.EvidenceCount,
            row.CreatedAt,
            row.UpdatedAt,
            row.ConfirmedAt,
            row.LastUsedAt,
            row.ExpiresAt,
            row.SupersedesId,
            row.SourceConversationId,
            row.Version);
    }

    private static CoachMemoryValueRejection ValidateScope(CoachMemoryScope scope, string? languageCode)
    {
        if (!Enum.IsDefined(scope))
        {
            return CoachMemoryValueRejection.InvalidScope;
        }

        if (scope == CoachMemoryScope.Global)
        {
            // Global must be chosen, never inferred from a missing language.
            return string.IsNullOrWhiteSpace(languageCode)
                ? CoachMemoryValueRejection.None
                : CoachMemoryValueRejection.InvalidScope;
        }

        if (string.IsNullOrWhiteSpace(languageCode) || languageCode.Length > CoachMemoryLimits.LanguageCodeMaxLength)
        {
            return CoachMemoryValueRejection.InvalidScope;
        }

        return CoachMemoryValueRejection.None;
    }

    private static string ScopeKey(CoachMemoryScope scope, string? languageCode) =>
        scope == CoachMemoryScope.Global
            ? CoachMemorySchema.GlobalScopeKey
            : CoachMemorySchema.LanguageScopeKey(languageCode!);

    private static CoachMemoryStatusCode VerifyEvidence(string? messageText, string? span)
    {
        if (string.IsNullOrWhiteSpace(messageText)
            || messageText.Length > CoachMemoryLimits.EvidenceSourceMaxLength
            || string.IsNullOrWhiteSpace(span)
            || span.Length < CoachMemoryLimits.EvidenceSpanMinLength
            || span.Length > CoachMemoryLimits.EvidenceSpanMaxLength)
        {
            return CoachMemoryStatusCode.InvalidRequest;
        }

        // Ordinal, not culture-aware: the span must be the literal characters the learner sent, not
        // something a collation considers equivalent.
        return messageText.Contains(span, StringComparison.Ordinal)
            ? CoachMemoryStatusCode.Success
            : CoachMemoryStatusCode.EvidenceMismatch;
    }

    private string EncodeCursor(CoachOwner owner, DateTime updatedAt, string id)
    {
        var value = string.Create(CultureInfo.InvariantCulture, $"{CursorPrefix}|{updatedAt.Ticks}|{id}");
        return _protector.Protect(
            new CoachProtectionContext(owner, CoachProtectedContentKind.ListCursor, CursorScope, _protector.CurrentVersion),
            value);
    }

    private bool TryDecodeCursor(CoachOwner owner, string cursor, out DateTime updatedAt, out string id)
    {
        updatedAt = default;
        id = string.Empty;

        if (!_protector.TryUnprotect(
                new CoachProtectionContext(owner, CoachProtectedContentKind.ListCursor, CursorScope, _protector.CurrentVersion),
                cursor,
                out var value)
            || value is null)
        {
            return false;
        }

        var parts = value.Split('|');
        if (parts.Length != 3 || parts[0] != CursorPrefix)
        {
            return false;
        }

        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
        {
            return false;
        }

        updatedAt = new DateTime(ticks, DateTimeKind.Utc);
        id = parts[2];
        return true;
    }
}
