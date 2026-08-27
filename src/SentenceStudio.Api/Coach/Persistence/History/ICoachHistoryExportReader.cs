namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// Streaming read primitives for a learner data export.
/// </summary>
/// <remarks>
/// Streaming only. Nothing here writes a file, buffers a whole transcript, or produces an
/// archive: materializing decrypted history to disk would put plaintext outside the database's
/// protection for the life of a temporary file. The caller streams straight to the response.
/// </remarks>
public interface ICoachHistoryExportReader
{
    /// <summary>Streams the owner's active conversations, oldest first.</summary>
    IAsyncEnumerable<CoachConversationRecord> StreamConversationsAsync(
        CoachOwner owner,
        CancellationToken cancellationToken = default);

    /// <summary>Streams one conversation's messages in chronological order.</summary>
    IAsyncEnumerable<CoachMessageRecord> StreamMessagesAsync(
        CoachOwner owner,
        string conversationId,
        CancellationToken cancellationToken = default);
}
