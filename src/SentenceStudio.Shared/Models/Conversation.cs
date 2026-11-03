using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("Conversations")]
public partial class Conversation : ObservableObject
{
    // Id property for CoreSync (Entity Framework/Database)
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Owning learner profile id. Nullable ONLY because rows written before
    /// owner scoping existed have no trustworthy owner — they stay null and
    /// remain invisible to every user rather than being guessed into an
    /// account. Every conversation created from now on carries an owner.
    /// </summary>
    public string? UserProfileId { get; set; }

    // Original properties
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The target language for this conversation.
    /// Used to keep separate conversation histories per language.
    /// </summary>
    public string Language { get; set; } = "Korean";

    /// <summary>
    /// Optional reference to the scenario used for this conversation.
    /// Null for conversations started before scenario feature or without a specific scenario.
    /// </summary>
    /// <remarks>
    /// A plain reference with no database constraint (phase 3, Captain 2026-11-03): a built-in
    /// scenario has the same fixed ID everywhere, a custom one syncs with its owner, and a
    /// conversation never fails to sync because a scenario row has not arrived. Readers treat an
    /// unknown ID as no scenario.
    /// </remarks>
    public string? ScenarioId { get; set; }

    // Navigation property for EF Core and CoreSync
    public List<ConversationChunk>? Chunks { get; set; }

    // Constructors
    public Conversation() { }

    public Conversation(DateTime createdAt)
    {
        CreatedAt = createdAt;
        Chunks = new List<ConversationChunk>();
    }

    public Conversation(DateTime createdAt, string? scenarioId)
    {
        CreatedAt = createdAt;
        ScenarioId = scenarioId;
        Chunks = new List<ConversationChunk>();
    }
}
