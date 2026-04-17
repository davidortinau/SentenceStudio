using SQLite;

namespace Plugin.Maui.HelpKit.Storage;

/// <summary>
/// Tracks the applied schema version for the HelpKit database. A single row
/// is maintained (Id = 1). The migration runner reads and writes this row.
/// </summary>
[Table("schema_version")]
internal sealed class SchemaVersionRow
{
    [PrimaryKey]
    public int Id { get; set; } = 1;

    public int Version { get; set; }

    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A conversation groups one or more messages under a stable GUID id. Scoped
/// per user via <see cref="UserId"/> (nullable for single-user apps).
/// </summary>
[Table("conversation")]
internal sealed class ConversationRow
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Indexed]
    public string? UserId { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A single chat message belonging to a <see cref="ConversationRow"/>. Citations
/// are stored as a JSON blob so the schema doesn't need to evolve when the
/// citation shape changes.
/// </summary>
[Table("message")]
internal sealed class MessageRow
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Indexed]
    public string ConversationId { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string CitationsJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Records the last-ingested pipeline fingerprint (embedding model + chunker
/// settings). Mismatch against the current fingerprint triggers a full
/// re-ingestion. Single-row table (Id = 1).
/// </summary>
[Table("ingestion_fingerprint")]
internal sealed class IngestionFingerprintRow
{
    [PrimaryKey]
    public int Id { get; set; } = 1;

    public string Fingerprint { get; set; } = string.Empty;

    public DateTime IngestedAt { get; set; } = DateTime.UtcNow;

    public int ChunkCount { get; set; }
}

/// <summary>
/// Per-question cached answer. Keyed by a SHA-256 hash of the normalized
/// question combined with the current pipeline fingerprint — so fingerprint
/// changes transparently invalidate all cached entries.
/// </summary>
[Table("answer_cache")]
internal sealed class AnswerCacheRow
{
    [PrimaryKey]
    public string QuestionHash { get; set; } = string.Empty;

    public string AnswerContent { get; set; } = string.Empty;

    public string CitationsJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow;
}
