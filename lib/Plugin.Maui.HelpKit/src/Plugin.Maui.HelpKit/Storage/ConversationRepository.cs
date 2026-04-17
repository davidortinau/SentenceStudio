using Microsoft.Extensions.Logging;

namespace Plugin.Maui.HelpKit.Storage;

/// <summary>
/// CRUD for conversations. All writes are serialized on
/// <see cref="HelpKitDatabase.SyncRoot"/>.
/// </summary>
internal sealed class ConversationRepository
{
    private readonly HelpKitDatabase _db;
    private readonly ILogger<ConversationRepository> _logger;

    public ConversationRepository(HelpKitDatabase db, ILogger<ConversationRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<string> CreateAsync(string? userId, string title, CancellationToken ct = default)
    {
        var row = new ConversationRow
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        lock (_db.SyncRoot)
        {
            _db.Connection.Insert(row);
        }
        return Task.FromResult(row.Id);
    }

    public Task TouchAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_db.SyncRoot)
        {
            var row = _db.Connection.Find<ConversationRow>(conversationId);
            if (row is null) return Task.CompletedTask;
            row.UpdatedAt = DateTime.UtcNow;
            _db.Connection.Update(row);
        }
        return Task.CompletedTask;
    }

    public Task<ConversationRow?> GetAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_db.SyncRoot)
        {
            return Task.FromResult(_db.Connection.Find<ConversationRow>(conversationId));
        }
    }

    /// <summary>
    /// Returns conversations for <paramref name="userId"/> ordered by
    /// most-recently-updated first.
    /// </summary>
    public Task<IReadOnlyList<ConversationRow>> GetHistoryAsync(
        string? userId, int take = 50, int skip = 0, CancellationToken ct = default)
    {
        if (take <= 0) take = 50;
        if (skip < 0) skip = 0;

        lock (_db.SyncRoot)
        {
            var query = _db.Connection.Table<ConversationRow>()
                .Where(c => c.UserId == userId)
                .OrderByDescending(c => c.UpdatedAt)
                .Skip(skip)
                .Take(take)
                .ToList();
            return Task.FromResult((IReadOnlyList<ConversationRow>)query);
        }
    }

    /// <summary>
    /// Deletes all conversations and messages for <paramref name="userId"/>.
    /// Used by <see cref="IHelpKit.ClearHistoryAsync"/>.
    /// </summary>
    public Task<int> ClearAsync(string? userId, CancellationToken ct = default)
    {
        lock (_db.SyncRoot)
        {
            var conversations = _db.Connection.Table<ConversationRow>()
                .Where(c => c.UserId == userId)
                .ToList();
            if (conversations.Count == 0) return Task.FromResult(0);

            var ids = conversations.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var conv in conversations)
                _db.Connection.Delete<ConversationRow>(conv.Id);

            var messages = _db.Connection.Table<MessageRow>().ToList();
            foreach (var msg in messages)
            {
                if (ids.Contains(msg.ConversationId))
                    _db.Connection.Delete<MessageRow>(msg.Id);
            }

            _logger.LogInformation(
                "Cleared {Count} conversations for user {UserId}",
                conversations.Count, userId ?? "(anon)");
            return Task.FromResult(conversations.Count);
        }
    }

    /// <summary>
    /// Deletes conversations whose <c>UpdatedAt</c> is older than <c>now - retention</c>.
    /// </summary>
    public Task<int> PurgeOlderThanAsync(TimeSpan retention, CancellationToken ct = default)
    {
        if (retention == TimeSpan.MaxValue) return Task.FromResult(0);
        var cutoff = DateTime.UtcNow - retention;

        lock (_db.SyncRoot)
        {
            var stale = _db.Connection.Table<ConversationRow>()
                .Where(c => c.UpdatedAt < cutoff)
                .ToList();
            if (stale.Count == 0) return Task.FromResult(0);

            var ids = stale.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var conv in stale)
                _db.Connection.Delete<ConversationRow>(conv.Id);

            var messages = _db.Connection.Table<MessageRow>().ToList();
            foreach (var msg in messages)
            {
                if (ids.Contains(msg.ConversationId))
                    _db.Connection.Delete<MessageRow>(msg.Id);
            }

            _logger.LogInformation(
                "Purged {Count} conversations older than {Retention}", stale.Count, retention);
            return Task.FromResult(stale.Count);
        }
    }
}
