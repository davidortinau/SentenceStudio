namespace SentenceStudio.Contracts.Ai;

/// <summary>
/// Request for multi-message chat completion (used by ConversationMemory for summarization/extraction).
/// </summary>
public sealed class ChatMessagesRequest
{
    /// <summary>
    /// Ordered list of chat messages (role + content pairs).
    /// </summary>
    public List<ChatMessageDto> Messages { get; set; } = new();

    /// <summary>
    /// Optional system-level instructions (maps to ChatOptions.Instructions).
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// Optional response type for typed deserialization (assembly-qualified or simple name).
    /// </summary>
    public string? ResponseType { get; set; }
}

public sealed class ChatMessageDto
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
}
