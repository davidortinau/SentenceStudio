using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

/// <summary>
/// Owner-scoped persistence for the legacy Conversation activity.
///
/// Every read, write and delete resolves the owner from a trusted source
/// (<see cref="IUserScopeProvider"/> first, then the host's claim-derived
/// <c>active_profile_id</c> preference) and filters on it. There is no code
/// path here that queries <c>Conversation</c> or <c>ConversationChunk</c>
/// without an owner predicate: an unresolved owner means "no data", never
/// "all data".
///
/// Legacy rows written before owner scoping carry <c>UserProfileId == null</c>.
/// They are deliberately invisible to every user and are never claimed,
/// backfilled, exported or deleted by a heuristic — attributing them would be
/// a guess, and this repository does not guess about ownership.
/// </summary>
public class ConversationRepository : IConversationOwnerDataService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConversationRepository> _logger;
    private readonly ISyncService? _syncService;
    private readonly IPreferencesService? _preferences;

    public ConversationRepository(
        IServiceProvider serviceProvider,
        ILogger<ConversationRepository> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncService = serviceProvider.GetService<ISyncService>();
        _preferences = serviceProvider.GetService<IPreferencesService>();
    }

    /// <summary>
    /// Trusted owner for the current caller. Never accepts a caller-supplied id:
    /// the only inputs are the request/circuit scope provider and the
    /// claim-derived active profile preference.
    /// </summary>
    private string ActiveUserId
    {
        get
        {
            // Resolved through a scope: hosts differ on the lifetime of
            // IUserScopeProvider (singleton on device/web, scoped on the API),
            // and pulling a scoped service straight off the root provider throws.
            using var scope = _serviceProvider.CreateScope();
            var scopeProvider = scope.ServiceProvider.GetService<IUserScopeProvider>();
            if (scopeProvider is not null && scopeProvider.TryGetUserProfileId(out var scopedId)
                && !string.IsNullOrWhiteSpace(scopedId))
            {
                return scopedId;
            }

            var preferenceId = _preferences?.Get("active_profile_id", string.Empty) ?? string.Empty;
            return string.IsNullOrWhiteSpace(preferenceId) ? string.Empty : preferenceId;
        }
    }

    private bool TryResolveOwner(string operation, out string userId)
    {
        userId = ActiveUserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            // No user id, no content — the log line names the operation only.
            _logger.LogWarning(
                "ConversationRepository.{Operation} called without an active user — refusing to touch conversation data to prevent cross-tenant access.",
                operation);
            return false;
        }

        return true;
    }

    /// <summary>All conversations owned by the active user, newest first, chunks included.</summary>
    public async Task<List<Conversation>> GetAllConversationsAsync()
    {
        if (!TryResolveOwner(nameof(GetAllConversationsAsync), out var userId))
        {
            return new List<Conversation>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversations = await db.Conversations
            .Where(c => c.UserProfileId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        foreach (var conversation in conversations)
        {
            conversation.Chunks = await OwnedChunksQuery(db, userId, conversation.Id).ToListAsync();
        }

        return conversations;
    }

    /// <summary>The active user's most recent conversation, or null when they have none.</summary>
    public async Task<Conversation?> GetMostRecentConversationAsync()
    {
        if (!TryResolveOwner(nameof(GetMostRecentConversationAsync), out var userId))
        {
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversation = await db.Conversations
            .Where(c => c.UserProfileId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync();

        if (conversation is not null)
        {
            conversation.Chunks = await OwnedChunksQuery(db, userId, conversation.Id).ToListAsync();
        }

        return conversation;
    }

    /// <summary>
    /// A single conversation by id, only when the active user owns it. An id that
    /// exists but belongs to someone else — or to no one — returns null, so the
    /// same id used by two accounts never crosses over.
    /// </summary>
    public async Task<Conversation?> GetConversationAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (!TryResolveOwner(nameof(GetConversationAsync), out var userId))
        {
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == id && c.UserProfileId == userId);

        if (conversation is not null)
        {
            conversation.Chunks = await OwnedChunksQuery(db, userId, conversation.Id).ToListAsync();
        }

        return conversation;
    }

    /// <summary>Chunks of a conversation the active user owns, oldest first.</summary>
    public async Task<List<ConversationChunk>> GetConversationChunksAsync(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return new List<ConversationChunk>();
        }

        if (!TryResolveOwner(nameof(GetConversationChunksAsync), out var userId))
        {
            return new List<ConversationChunk>();
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await OwnedChunksQuery(db, userId, conversationId).ToListAsync();
    }

    /// <summary>
    /// Inserts or updates a conversation, stamping the active user as owner on
    /// insert. Updating a row owned by someone else, or an ownerless legacy row,
    /// is refused — an update is not a claim mechanism.
    /// </summary>
    public async Task<string?> SaveConversationAsync(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (!TryResolveOwner(nameof(SaveConversationAsync), out var userId))
        {
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (string.IsNullOrWhiteSpace(conversation.Id))
        {
            conversation.Id = Guid.NewGuid().ToString();
        }

        if (conversation.CreatedAt == default)
        {
            conversation.CreatedAt = DateTime.UtcNow;
        }

        try
        {
            var existing = await db.Conversations
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == conversation.Id);

            if (existing is null)
            {
                conversation.UserProfileId = userId;
                db.Conversations.Add(conversation);
            }
            else if (existing.UserProfileId == userId)
            {
                conversation.UserProfileId = userId;
                db.Conversations.Update(conversation);
            }
            else
            {
                _logger.LogWarning(
                    "ConversationRepository.SaveConversationAsync refused: the target conversation is not owned by the active user (ownerless legacy row or another account).");
                return null;
            }

            await db.SaveChangesAsync();
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            return conversation.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in SaveConversationAsync");
            return null;
        }
    }

    /// <summary>
    /// Inserts or updates a chunk. The parent conversation must be owned by the
    /// active user, so a chunk can never be attached to somebody else's thread —
    /// or adopted into an ownerless legacy one.
    /// </summary>
    public async Task<bool> SaveConversationChunkAsync(ConversationChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (!TryResolveOwner(nameof(SaveConversationChunkAsync), out var userId))
        {
            return false;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (string.IsNullOrWhiteSpace(chunk.Id))
        {
            chunk.Id = Guid.NewGuid().ToString();
        }

        if (chunk.SentTime == default)
        {
            chunk.SentTime = DateTime.UtcNow;
        }

        try
        {
            var parentOwned = await db.Conversations
                .AnyAsync(c => c.Id == chunk.ConversationId && c.UserProfileId == userId);

            if (!parentOwned)
            {
                _logger.LogWarning(
                    "ConversationRepository.SaveConversationChunkAsync refused: parent conversation is missing or not owned by the active user.");
                return false;
            }

            var existing = await db.ConversationChunks
                .AsNoTracking()
                .FirstOrDefaultAsync(cc => cc.Id == chunk.Id);

            if (existing is not null && existing.UserProfileId != userId)
            {
                _logger.LogWarning(
                    "ConversationRepository.SaveConversationChunkAsync refused: the target chunk is not owned by the active user.");
                return false;
            }

            chunk.UserProfileId = userId;

            if (existing is null)
            {
                db.ConversationChunks.Add(chunk);
            }
            else
            {
                db.ConversationChunks.Update(chunk);
            }

            await db.SaveChangesAsync();
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in SaveConversationChunkAsync");
            return false;
        }
    }

    /// <summary>
    /// Deletes a conversation and its chunks, only when the active user owns it.
    /// Ownerless legacy rows are not deletable through this path.
    /// </summary>
    public async Task<bool> DeleteConversationAsync(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return false;
        }

        if (!TryResolveOwner(nameof(DeleteConversationAsync), out var userId))
        {
            return false;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            var conversation = await db.Conversations
                .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserProfileId == userId);

            if (conversation is null)
            {
                _logger.LogWarning(
                    "ConversationRepository.DeleteConversationAsync refused: conversation is missing or not owned by the active user.");
                return false;
            }

            var chunks = await OwnedChunksQuery(db, userId, conversationId).ToListAsync();
            db.ConversationChunks.RemoveRange(chunks);
            db.Conversations.Remove(conversation);

            await db.SaveChangesAsync();
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in DeleteConversationAsync");
            return false;
        }
    }

    // --- IConversationOwnerDataService (account export / deletion) -------------
    // These take an explicit id because the caller is trusted account-lifecycle
    // code operating on a user that may not be the active circuit user (e.g. an
    // admin-initiated deletion). They still refuse an empty id.
    //
    // They also join an ambient unit of work when the caller opened one, so an
    // account erasure that spans several contexts commits or rolls back as one.
    // See AmbientApplicationDbContext for why that matters here specifically.

    public async Task<ConversationOwnedExport> ExportOwnedAsync(
        string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            _logger.LogWarning(
                "ConversationRepository.ExportOwnedAsync called with no user id — returning an empty export instead of unfiltered conversation data.");
            return ConversationOwnedExport.Empty;
        }

        using var lease = LeaseContext();
        var db = lease.Db;

        var conversations = await db.Conversations
            .AsNoTracking()
            .Where(c => c.UserProfileId == userProfileId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

        foreach (var conversation in conversations)
        {
            conversation.Chunks = await OwnedChunksQuery(db, userProfileId, conversation.Id)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }

        return new ConversationOwnedExport(conversations);
    }

    public async Task<ConversationOwnedDeletionResult> DeleteOwnedAsync(
        string userProfileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            _logger.LogWarning(
                "ConversationRepository.DeleteOwnedAsync called with no user id — deleting nothing rather than running an unfiltered delete.");
            return ConversationOwnedDeletionResult.None;
        }

        using var lease = LeaseContext();
        var db = lease.Db;

        try
        {
            var chunks = await db.ConversationChunks
                .Where(cc => cc.UserProfileId == userProfileId)
                .ToListAsync(cancellationToken);
            var conversations = await db.Conversations
                .Where(c => c.UserProfileId == userProfileId)
                .ToListAsync(cancellationToken);

            if (chunks.Count == 0 && conversations.Count == 0)
            {
                return ConversationOwnedDeletionResult.None;
            }

            db.ConversationChunks.RemoveRange(chunks);
            db.Conversations.RemoveRange(conversations);
            await db.SaveChangesAsync(cancellationToken);

            return new ConversationOwnedDeletionResult(conversations.Count, chunks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in DeleteOwnedAsync");
            return ConversationOwnedDeletionResult.None;
        }
    }

    public async Task<ConversationUnownedDiagnostics> GetUnownedDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversations = await db.Conversations
            .CountAsync(c => c.UserProfileId == null, cancellationToken);
        var chunks = await db.ConversationChunks
            .CountAsync(cc => cc.UserProfileId == null, cancellationToken);

        return new ConversationUnownedDiagnostics(conversations, chunks);
    }

    /// <summary>
    /// The context an account-lifecycle operation must run on, together with the scope that owns
    /// it. An ambient context is joined rather than replaced, so the work enrolls in the caller's
    /// transaction instead of committing on a connection of its own; with no ambient context this
    /// behaves exactly as before and resolves a fresh one from a new scope.
    /// </summary>
    /// <remarks>
    /// The ambient context is never disposed here. It belongs to whoever opened the unit of work,
    /// and disposing it would detach the caller's transaction part-way through their own commit.
    /// </remarks>
    private ContextLease LeaseContext()
    {
        var ambient = AmbientApplicationDbContext.Current;
        if (ambient is not null)
        {
            return new ContextLease(ambient, scope: null);
        }

        var scope = _serviceProvider.CreateScope();
        return new ContextLease(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), scope);
    }

    private readonly struct ContextLease : IDisposable
    {
        private readonly IServiceScope? _scope;

        public ContextLease(ApplicationDbContext db, IServiceScope? scope)
        {
            Db = db;
            _scope = scope;
        }

        public ApplicationDbContext Db { get; }

        public void Dispose() => _scope?.Dispose();
    }

    /// <summary>
    /// The single chunk predicate used by every read path: owner match AND parent
    /// match. Ordering is on the mapped <c>SentTime</c> column (the
    /// <c>CreatedAt</c> alias is <c>[NotMapped]</c> and cannot be translated).
    /// </summary>
    private static IQueryable<ConversationChunk> OwnedChunksQuery(
        ApplicationDbContext db,
        string userId,
        string conversationId) =>
        db.ConversationChunks
            .Where(cc => cc.ConversationId == conversationId && cc.UserProfileId == userId)
            .OrderBy(cc => cc.SentTime);
}
