using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Plugin.Maui.HelpKit.Storage;

/// <summary>
/// CRUD for messages within a conversation.
/// </summary>
internal sealed class MessageRepository
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly HelpKitDatabase _db;
    private readonly ILogger<MessageRepository> _logger;

    public MessageRepository(HelpKitDatabase db, ILogger<MessageRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<string> AppendAsync(
        string conversationId,
        string role,
        string content,
        IReadOnlyList<HelpKitCitation>? citations,
        CancellationToken ct = default)
    {
        var row = new MessageRow
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = conversationId,
            Role = role,
            Content = content ?? string.Empty,
            CitationsJson = citations is null || citations.Count == 0
                ? "[]"
                : JsonSerializer.Serialize(citations, s_json),
            CreatedAt = DateTime.UtcNow,
        };

        lock (_db.SyncRoot)
        {
            _db.Connection.Insert(row);
        }
        return Task.FromResult(row.Id);
    }

    public Task<IReadOnlyList<MessageRow>> GetForConversationAsync(
        string conversationId, CancellationToken ct = default)
    {
        lock (_db.SyncRoot)
        {
            var list = _db.Connection.Table<MessageRow>()
                .Where(m => m.ConversationId == conversationId)
                .OrderBy(m => m.CreatedAt)
                .ToList();
            return Task.FromResult((IReadOnlyList<MessageRow>)list);
        }
    }

    /// <summary>
    /// Deserializes <see cref="MessageRow.CitationsJson"/> to a strongly-typed
    /// list. Tolerates missing / malformed JSON by returning an empty list.
    /// </summary>
    public static IReadOnlyList<HelpKitCitation> DeserializeCitations(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return Array.Empty<HelpKitCitation>();

        try
        {
            return JsonSerializer.Deserialize<List<HelpKitCitation>>(json, s_json)
                ?? (IReadOnlyList<HelpKitCitation>)Array.Empty<HelpKitCitation>();
        }
        catch
        {
            return Array.Empty<HelpKitCitation>();
        }
    }
}
