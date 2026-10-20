namespace SentenceStudio.Data;

/// <summary>
/// The unedited values from the original owner-scoped resource read.
/// Keep null distinct from empty text. For SQLite, project UpdatedAt.ToString()
/// in the database query to capture its exact stored text as SqliteUpdatedAtText.
/// </summary>
public sealed record ResourceMetadataSnapshot(
    DateTime UpdatedAt, string? Title, string? Description, string? Tags,
    string? SqliteUpdatedAtText = null);

/// <summary>The original values of every editable scalar; vocabulary is never part of a scalar save.</summary>
public sealed record ResourceContentSnapshot(
    ResourceMetadataSnapshot Metadata,
    string? MediaType, string? Language, string? MediaUrl,
    string? Transcript, string? Translation);

public sealed record ResourceContentUpdate(
    string Title, string? Description, string? Tags,
    string? MediaType, string? Language, string? MediaUrl,
    string? Transcript, string? Translation);

public enum ResourceCreationStatus
{
    Success, Invalid, NotFoundOrForbidden, AlreadyExists
}

public sealed record ResourceCreationResult(ResourceCreationStatus Status, string? ResourceId = null);

public enum ResourceVocabularyWriteStatus
{
    Success, NotFoundOrForbidden, SmartReadOnly, Invalid, AlreadyPresent
}

public sealed record ResourceVocabularyImportResult(
    ResourceVocabularyWriteStatus Status, int Added = 0, int Skipped = 0)
{
    public int CreatedCount { get; init; }
    public int ExistingLinkedCount { get; init; }
}

public enum ResourceMetadataUpdateResult
{
    Success,
    NotFoundOrForbidden,
    SmartReadOnly,
    ValidationFailed,
    Conflict
}
