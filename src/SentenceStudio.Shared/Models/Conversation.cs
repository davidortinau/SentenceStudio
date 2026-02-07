using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("Conversations")]
public partial class Conversation : ObservableObject
{
    // Id property for CoreSync (Entity Framework/Database)
    public int Id { get; set; }

    // Original properties
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// The target language for this conversation.
    /// Used to keep separate conversation histories per language.
    /// </summary>
    public string Language { get; set; } = "Korean";

    /// <summary>
    /// Optional reference to the scenario used for this conversation.
    /// Null for conversations started before scenario feature or without a specific scenario.
    /// </summary>
    public int? ScenarioId { get; set; }

    /// <summary>
    /// Navigation property to the conversation scenario.
    /// </summary>
    [ForeignKey(nameof(ScenarioId))]
    public ConversationScenario? Scenario { get; set; }

    // Navigation property for EF Core and CoreSync
    public List<ConversationChunk>? Chunks { get; set; }

    // Constructors
    public Conversation() { }

    public Conversation(DateTime createdAt)
    {
        CreatedAt = createdAt;
        Chunks = new List<ConversationChunk>();
    }

    public Conversation(DateTime createdAt, int? scenarioId)
    {
        CreatedAt = createdAt;
        ScenarioId = scenarioId;
        Chunks = new List<ConversationChunk>();
    }
}
