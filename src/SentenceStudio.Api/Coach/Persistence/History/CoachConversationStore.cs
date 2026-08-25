using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// EF Core implementation of <see cref="ICoachConversationStore"/>.
/// </summary>
/// <remarks>
/// Every query starts from <see cref="Owned"/>, which requires a non-empty owner and always
/// applies the <c>UserProfileId</c> filter. There is no query in this file that reads a history
/// table without it.
/// </remarks>
public sealed class CoachConversationStore : ICoachConversationStore
{
    private readonly CoachDbContext _db;
    private readonly ICoachContentProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CoachConversationStore> _logger;

    public CoachConversationStore(
        CoachDbContext db,
        ICoachContentProtector protector,
        TimeProvider timeProvider,
        ILogger<CoachConversationStore> logger)
    {
        _db = db;
        _protector = protector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private IQueryable<CoachConversation> Owned(CoachOwner owner) =>
        _db.CoachConversations.Where(c => c.UserProfileId == owner.UserProfileId);

    private IQueryable<CoachConversation> OwnedVisible(CoachOwner owner) =>
        Owned(owner).Where(c => c.Status != CoachConversationStatus.Deleting && c.DeletedAt == null);

    private bool HasOwner(CoachOwner owner, string operation)
    {
        if (!owner.IsEmpty)
        {
            return true;
        }

        _logger.LogWarning("[Coach] {Operation} called with no active user id — returning no data.", operation);
        return false;
    }

    public async Task<CoachConversationResult> CreateAsync(
        CoachOwner owner,
        CreateCoachConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (owner.IsEmpty)
        {
            // Creation cannot degrade to "no data": writing an unowned row is the orphan state
            // the multi-tenant rule exists to prevent. Reported, not thrown, so a caller that
            // lost its scope gets a handled refusal instead of a 500.
            _logger.LogWarning("[Coach] {Operation} refused: no owning user profile in scope.", nameof(CreateAsync));
            return CoachConversationResult.Failed(CoachHistoryStatus.NoOwner);
        }

        var title = request.Title ?? string.Empty;
        if (title.Length > CoachHistoryLimits.TitleMaxLength
            || request.TargetLanguageCode is { Length: > CoachHistoryLimits.TargetLanguageCodeMaxLength })
        {
            return CoachConversationResult.Failed(CoachHistoryStatus.InvalidRequest);
        }

        var id = string.IsNullOrWhiteSpace(request.ConversationId)
            ? Guid.NewGuid().ToString("n")
            : request.ConversationId!;

        if (id.Length > CoachHistoryLimits.IdMaxLength)
        {
            return CoachConversationResult.Failed(CoachHistoryStatus.InvalidRequest);
        }

        var now = UtcNow;
        var version = _protector.CurrentVersion;

        var conversation = new CoachConversation
        {
            Id = id,
            UserProfileId = owner.UserProfileId,
            TenantId = owner.TenantId,
            ProtectedTitle = _protector.Protect(TitleContext(owner, id, version), title),
            TitleSource = request.TitleSource,
            TargetLanguageCode = request.TargetLanguageCode,
            Status = CoachConversationStatus.Active,
            HistoryStartsAt = now,
            LastSequence = 0,
            MetadataSchemaVersion = CoachHistorySchema.ConversationMetadataVersion,
            ContentProtectionVersion = version,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.CoachConversations.Add(conversation);
        await _db.SaveChangesAsync(cancellationToken);

        return new CoachConversationResult(CoachHistoryStatus.Success, Project(owner, conversation));
    }

    public async Task<CoachConversationPage> ListAsync(
        CoachOwner owner,
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(ListAsync)))
        {
            return CoachConversationPage.Empty(CoachHistoryStatus.NoOwner);
        }

        var size = Clamp(pageSize, CoachHistoryLimits.ConversationPageDefault, CoachHistoryLimits.ConversationPageMax);
        var query = OwnedVisible(owner);

        if (!string.IsNullOrEmpty(cursor))
        {
            if (!CoachHistoryCursor.TryDecodeConversation(_protector, owner, cursor, out var afterUpdatedAt, out var afterId))
            {
                // A cursor that does not decode is forged, tampered with, or another owner's.
                // Falling back to page one would turn that into a successful full read.
                _logger.LogWarning("[Coach] {Operation} received an unreadable cursor — refusing the read.", nameof(ListAsync));
                return CoachConversationPage.Empty(CoachHistoryStatus.InvalidCursor);
            }

            query = query.Where(c =>
                c.UpdatedAt < afterUpdatedAt
                || (c.UpdatedAt == afterUpdatedAt && string.Compare(c.Id, afterId) < 0));
        }

        // One extra row tells us whether another page exists without a second count query.
        var rows = await query
            .OrderByDescending(c => c.UpdatedAt)
            .ThenByDescending(c => c.Id)
            .Take(size + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > size;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.Select(row => Project(owner, row)).ToList();
        var nextCursor = hasMore && rows.Count > 0
            ? CoachHistoryCursor.EncodeConversation(_protector, owner, rows[^1].UpdatedAt, rows[^1].Id)
            : null;

        return new CoachConversationPage(CoachHistoryStatus.Success, items, nextCursor);
    }

    public async Task<CoachConversationResult> GetAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(GetAsync)) || string.IsNullOrWhiteSpace(conversationId))
        {
            return CoachConversationResult.Failed(owner.IsEmpty ? CoachHistoryStatus.NoOwner : CoachHistoryStatus.NotFound);
        }

        var conversation = await OwnedVisible(owner)
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

        return conversation is null
            ? CoachConversationResult.Failed(CoachHistoryStatus.NotFound)
            : new CoachConversationResult(CoachHistoryStatus.Success, Project(owner, conversation));
    }

    public async Task<CoachConversationResult> RenameAsync(
        CoachOwner owner,
        string conversationId,
        string title,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(RenameAsync)) || string.IsNullOrWhiteSpace(conversationId))
        {
            return CoachConversationResult.Failed(owner.IsEmpty ? CoachHistoryStatus.NoOwner : CoachHistoryStatus.NotFound);
        }

        title ??= string.Empty;
        if (title.Length > CoachHistoryLimits.TitleMaxLength)
        {
            return CoachConversationResult.Failed(CoachHistoryStatus.InvalidRequest);
        }

        var conversation = await OwnedVisible(owner)
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

        if (conversation is null)
        {
            return CoachConversationResult.Failed(CoachHistoryStatus.NotFound);
        }

        var version = _protector.CurrentVersion;
        conversation.ProtectedTitle = _protector.Protect(TitleContext(owner, conversation.Id, version), title);
        conversation.ContentProtectionVersion = version;
        conversation.TitleSource = CoachConversationTitleSource.Learner;
        conversation.UpdatedAt = UtcNow;
        conversation.Version++;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CoachConversationResult.Failed(CoachHistoryStatus.Conflict);
        }

        return new CoachConversationResult(CoachHistoryStatus.Success, Project(owner, conversation));
    }

    public async Task<CoachConversationResult> SetClosedAsync(
        CoachOwner owner,
        string conversationId,
        bool closed,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(SetClosedAsync)) || string.IsNullOrWhiteSpace(conversationId))
        {
            return CoachConversationResult.Failed(owner.IsEmpty ? CoachHistoryStatus.NoOwner : CoachHistoryStatus.NotFound);
        }

        var conversation = await OwnedVisible(owner)
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

        if (conversation is null)
        {
            return CoachConversationResult.Failed(CoachHistoryStatus.NotFound);
        }

        var target = closed ? CoachConversationStatus.Closed : CoachConversationStatus.Active;
        if (conversation.Status == target)
        {
            // Closing a closed conversation is a no-op, not a conflict. A retried request after a
            // dropped response must not look like a failure the learner has to act on.
            return new CoachConversationResult(CoachHistoryStatus.Success, Project(owner, conversation));
        }

        conversation.Status = target;
        conversation.UpdatedAt = UtcNow;
        conversation.Version++;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CoachConversationResult.Failed(CoachHistoryStatus.Conflict);
        }

        return new CoachConversationResult(CoachHistoryStatus.Success, Project(owner, conversation));
    }

    public async Task<CoachHistoryStatus> SoftDeleteAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(SoftDeleteAsync)) || string.IsNullOrWhiteSpace(conversationId))
        {
            return owner.IsEmpty ? CoachHistoryStatus.NoOwner : CoachHistoryStatus.NotFound;
        }

        var conversation = await OwnedVisible(owner)
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

        if (conversation is null)
        {
            return CoachHistoryStatus.NotFound;
        }

        var now = UtcNow;
        conversation.Status = CoachConversationStatus.Deleting;
        conversation.DeletedAt = now;
        conversation.UpdatedAt = now;
        conversation.Version++;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CoachHistoryStatus.Conflict;
        }

        return CoachHistoryStatus.Success;
    }

    public async Task<CoachHistoryStatus> PurgeAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!HasOwner(owner, nameof(PurgeAsync)) || string.IsNullOrWhiteSpace(conversationId))
        {
            return owner.IsEmpty ? CoachHistoryStatus.NoOwner : CoachHistoryStatus.NotFound;
        }

        var conversation = await Owned(owner)
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

        if (conversation is null)
        {
            return CoachHistoryStatus.NotFound;
        }

        // Children are removed explicitly rather than relying on the database cascade, so the
        // behaviour is identical on every provider the tests and production run on.
        await _db.CoachMessages
            .Where(m => m.UserProfileId == owner.UserProfileId && m.ConversationId == conversationId)
            .ExecuteDeleteAsync(cancellationToken);

        await _db.CoachTurnOperations
            .Where(o => o.UserProfileId == owner.UserProfileId && o.ConversationId == conversationId)
            .ExecuteDeleteAsync(cancellationToken);

        await Owned(owner)
            .Where(c => c.Id == conversationId)
            .ExecuteDeleteAsync(cancellationToken);

        // ExecuteDelete bypasses the change tracker, so a tracked copy would otherwise be
        // resurrected by the next SaveChanges.
        _db.Entry(conversation).State = EntityState.Detached;

        return CoachHistoryStatus.Success;
    }

    private CoachConversationRecord Project(CoachOwner owner, CoachConversation conversation)
    {
        var context = TitleContext(owner, conversation.Id, conversation.ContentProtectionVersion);
        var title = _protector.TryUnprotect(context, conversation.ProtectedTitle, out var plaintext) ? plaintext : null;

        return new CoachConversationRecord(
            conversation.Id,
            title,
            conversation.TitleSource,
            conversation.TargetLanguageCode,
            conversation.Status,
            conversation.HistoryStartsAt,
            conversation.LastSequence,
            conversation.Version,
            conversation.CreatedAt,
            conversation.UpdatedAt);
    }

    private static CoachProtectionContext TitleContext(CoachOwner owner, string conversationId, int version) =>
        new(owner, CoachProtectedContentKind.ConversationTitle, conversationId, version);

    private static int Clamp(int? requested, int fallback, int max)
    {
        if (requested is null or <= 0)
        {
            return fallback;
        }

        return requested.Value > max ? max : requested.Value;
    }
}
