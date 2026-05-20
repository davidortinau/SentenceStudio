namespace SentenceStudio.Contracts.Conversation;

/// <summary>
/// One turn in the conversation history. The client sends oldest -> newest.
/// </summary>
public sealed class ConversationHistoryItemDto
{
    /// <summary>
    /// Lowercase wire value: "user" or "assistant".
    /// </summary>
    public string Role { get; set; } = "user";

    /// <summary>
    /// Optional display name of the speaker (persona name for assistant turns).
    /// </summary>
    public string? Author { get; set; }

    public string Text { get; set; } = string.Empty;
}
