using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// Persisted conversation memory state.
///
/// RETAINED, NOT LIVE: the only writer was the preview-era conversation agent path
/// (deleted in Learning Coach Phase 0). The entity, table, and migrations are intentionally
/// kept — dropping them is destructive and needs its own data-removal decision plus a
/// dual-provider (PostgreSQL + SQLite) migration.
/// See tests/SentenceStudio.UnitTests/Services/Agents/ConversationAgentRemovalTests.cs.
/// </summary>
[Table("ConversationMemoryStates")]
public class ConversationMemoryState
{
    [Key]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Owner of the conversation this memory belongs to. Set from the conversation, so sync
    /// can scope the row directly (Captain 2026-11-02, docs/sync-all-tables-plan.md).
    /// </summary>
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>
    /// The conversation this memory state belongs to.
    /// </summary>
    public string ConversationId { get; set; } = string.Empty;

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
