namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// Owner-scoped storage for durable conversations.
/// </summary>
/// <remarks>
/// Every method takes an explicit <see cref="CoachOwner"/> the caller resolved from the server's
/// request scope. There is no overload that omits it, no method that reads across owners, and no
/// path where an empty owner widens to an unfiltered query.
/// </remarks>
public interface ICoachConversationStore
{
    /// <summary>Creates a conversation owned by <paramref name="owner"/>.</summary>
    Task<CoachConversationResult> CreateAsync(
        CoachOwner owner,
        CreateCoachConversationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one page of the owner's active conversations, newest-updated first, then by
    /// descending id so equal timestamps still order deterministically.
    /// </summary>
    Task<CoachConversationPage> ListAsync(
        CoachOwner owner,
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one active conversation, or <see cref="CoachHistoryStatus.NotFound"/>.</summary>
    Task<CoachConversationResult> GetAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Renames a conversation and marks the title learner-authored.</summary>
    Task<CoachConversationResult> RenameAsync(
        CoachOwner owner,
        string conversationId,
        string title,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes or reopens a conversation. A closed conversation stays readable, listable, and
    /// exportable; it only refuses new turns. Idempotent.
    /// </summary>
    Task<CoachConversationResult> SetClosedAsync(
        CoachOwner owner,
        string conversationId,
        bool closed,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes a conversation. The row is hidden from every read path before this returns;
    /// the physical purge follows separately.
    /// </summary>
    Task<CoachHistoryStatus> SoftDeleteAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently removes a soft-deleted conversation with its messages and turn operations.
    /// The plan revision audit is never touched.
    /// </summary>
    Task<CoachHistoryStatus> PurgeAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken = default);
}
