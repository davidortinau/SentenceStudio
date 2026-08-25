namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// Owner-scoped storage for the canonical visible-message ledger.
/// </summary>
/// <remarks>
/// Append-only. There is no update method and no single-message delete: the ledger is what the
/// learner saw, and editing it after the fact would make the transcript untrustworthy. Removal
/// happens only through the conversation purge.
/// </remarks>
public interface ICoachMessageStore
{
    /// <summary>
    /// Appends one message, allocating the next sequence under a transaction so concurrent
    /// appends cannot produce a gap or a duplicate.
    /// </summary>
    Task<CoachMessageAppendResult> AppendAsync(
        CoachOwner owner,
        AppendCoachMessageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the newest page of a conversation, in chronological order. Pass the returned
    /// previous-cursor to walk backwards through history.
    /// </summary>
    Task<CoachMessagePage> GetLatestAsync(
        CoachOwner owner,
        string conversationId,
        int? pageSize = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the page immediately before <paramref name="cursor"/>, in chronological order.
    /// </summary>
    Task<CoachMessagePage> GetBeforeAsync(
        CoachOwner owner,
        string conversationId,
        string cursor,
        int? pageSize = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the newest page strictly below <paramref name="upperExclusiveSequence"/>, in
    /// chronological order.
    /// </summary>
    /// <remarks>
    /// The server-side twin of <see cref="GetBeforeAsync"/>. Paging clients hold an opaque,
    /// protected cursor because a raw sequence in a client's hands is a position it can forge;
    /// a caller inside the application already holds the sequence the ledger assigned it, and
    /// round-tripping that through the protector to read its own write would only add a failure
    /// mode. Exists so a bounded read stays bounded at the database: taking the newest page and
    /// discarding rows afterwards silently shrinks the window by however many rows were dropped.
    /// </remarks>
    Task<CoachMessagePage> GetBeforeSequenceAsync(
        CoachOwner owner,
        string conversationId,
        long upperExclusiveSequence,
        int? pageSize = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a bounded, inclusive sequence range in chronological order. Used to read back
    /// exactly the messages one turn appended.
    /// </summary>
    Task<CoachMessagePage> GetRangeAsync(
        CoachOwner owner,
        string conversationId,
        long fromSequence,
        long toSequence,
        CancellationToken cancellationToken = default);
}
