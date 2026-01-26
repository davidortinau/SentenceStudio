namespace SentenceStudio.Shared.Models;

/// <summary>
/// Defines the type of conversation flow for a scenario.
/// </summary>
public enum ConversationType
{
    /// <summary>
    /// Conversation continues until user explicitly ends it.
    /// Examples: "First Meeting", "Weekend Plans"
    /// </summary>
    OpenEnded = 0,
    
    /// <summary>
    /// Conversation concludes naturally when the transactional goal is achieved.
    /// Examples: "Ordering Coffee", "Asking for Directions"
    /// </summary>
    Finite = 1
}
