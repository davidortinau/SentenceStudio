using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// Persists the conversation memory state for Agent Framework's AIContextProvider.
/// Stores serialized memory data that survives across app sessions.
/// </summary>
[Table("ConversationMemoryStates")]
public class ConversationMemoryState
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The conversation this memory state belongs to.
    /// </summary>
    public int ConversationId { get; set; }

    /// <summary>
    /// JSON-serialized memory state from the AIContextProvider.
    /// </summary>
    public string SerializedState { get; set; } = "{}";

    /// <summary>
    /// Summary of key conversation topics for quick context injection.
    /// </summary>
    public string? ConversationSummary { get; set; }

    /// <summary>
    /// Comma-separated list of vocabulary words discussed in this conversation.
    /// </summary>
    public string? DiscussedVocabulary { get; set; }

    /// <summary>
    /// The user's detected proficiency level based on conversation analysis.
    /// </summary>
    public string? DetectedProficiencyLevel { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation property
    [ForeignKey(nameof(ConversationId))]
    public Conversation? Conversation { get; set; }
}
