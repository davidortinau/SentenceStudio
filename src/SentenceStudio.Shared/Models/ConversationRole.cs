namespace SentenceStudio.Shared.Models;

/// <summary>
/// Represents the role of a participant in a conversation.
/// </summary>
public enum ConversationRole
{
    /// <summary>
    /// The human user in the conversation.
    /// </summary>
    User = 0,
    
    /// <summary>
    /// The AI assistant/bot in the conversation.
    /// </summary>
    Assistant = 1
}
