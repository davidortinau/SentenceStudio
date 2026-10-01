namespace SentenceStudio.Data;

/// <summary>
/// The unedited values from the original owner-scoped resource read.
/// Keep null distinct from empty text. For SQLite, project UpdatedAt.ToString()
/// in the database query to capture its exact stored text as SqliteUpdatedAtText.
/// </summary>
public sealed record ResourceMetadataSnapshot(
    DateTime UpdatedAt, string? Title, string? Description, string? Tags,
    string? SqliteUpdatedAtText = null);

public enum ResourceMetadataUpdateResult
{
    Success,
    NotFoundOrForbidden,
    SmartReadOnly,
    ValidationFailed,
    Conflict
}
