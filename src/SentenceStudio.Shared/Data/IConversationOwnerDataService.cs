namespace SentenceStudio.Data;

/// <summary>
/// Owner-scoped export/delete surface for the legacy Conversation activity.
/// Account deletion and data export call through this contract so the endpoint
/// layer never has to know how conversation rows are scoped — and so the
/// endpoint file itself does not have to change when the scoping does.
///
/// Ownerless legacy rows (<c>UserProfileId IS NULL</c>) are NEVER exported and
/// NEVER deleted by these methods. They predate owner scoping, so attributing
/// them to whoever happens to be deleting their account would be a guess, and a
/// guess in this direction destroys another person's data. They are reported
/// only as an aggregate count for operator diagnostics.
/// </summary>
public interface IConversationOwnerDataService
{
    /// <summary>
    /// Every conversation (with its chunks) owned by <paramref name="userProfileId"/>.
    /// An empty/whitespace id returns an empty export — never the whole table.
    /// </summary>
    Task<ConversationOwnedExport> ExportOwnedAsync(string userProfileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes conversations and chunks owned by <paramref name="userProfileId"/>.
    /// An empty/whitespace id deletes nothing. Ownerless legacy rows are left intact.
    /// </summary>
    Task<ConversationOwnedDeletionResult> DeleteOwnedAsync(string userProfileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregate counts of rows that carry no owner. Operator diagnostics only —
    /// this returns counts, never conversation content or user identifiers.
    /// </summary>
    Task<ConversationUnownedDiagnostics> GetUnownedDiagnosticsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Owned legacy conversation data for a single user.</summary>
/// <param name="Conversations">Owned conversations, each with its owned chunks attached.</param>
public sealed record ConversationOwnedExport(
    IReadOnlyList<SentenceStudio.Shared.Models.Conversation> Conversations)
{
    public static ConversationOwnedExport Empty { get; } =
        new(Array.Empty<SentenceStudio.Shared.Models.Conversation>());
}

/// <summary>Result of an owner-scoped legacy conversation deletion.</summary>
public sealed record ConversationOwnedDeletionResult(int ConversationsDeleted, int ChunksDeleted)
{
    public static ConversationOwnedDeletionResult None { get; } = new(0, 0);
}

/// <summary>
/// Counts of ownerless legacy rows. Diagnostics only — not user-facing data.
/// </summary>
public sealed record ConversationUnownedDiagnostics(int UnownedConversations, int UnownedChunks);
