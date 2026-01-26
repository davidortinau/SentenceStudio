using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// Represents a conversation practice scenario with persona, context, and conversation type.
/// </summary>
[Table("ConversationScenario")]
public partial class ConversationScenario : ObservableObject
{
    /// <summary>
    /// Unique identifier for the scenario.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Display name for the scenario (English).
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Description("The name of this conversation scenario")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Display name for the scenario (Korean).
    /// </summary>
    [MaxLength(100)]
    [Description("The Korean name of this conversation scenario")]
    public string? NameKorean { get; set; }

    /// <summary>
    /// Name of the AI persona for this scenario.
    /// </summary>
    [Required]
    [MaxLength(50)]
    [Description("The name of the AI conversation partner")]
    public string PersonaName { get; set; } = string.Empty;

    /// <summary>
    /// Description/backstory of the AI persona.
    /// </summary>
    [Required]
    [MaxLength(500)]
    [Description("A description of who the AI conversation partner is and their role")]
    public string PersonaDescription { get; set; } = string.Empty;

    /// <summary>
    /// Context/situation description for the conversation.
    /// </summary>
    [Required]
    [MaxLength(500)]
    [Description("The situation or context for the conversation")]
    public string SituationDescription { get; set; } = string.Empty;

    /// <summary>
    /// Whether the conversation is open-ended or finite (transactional).
    /// </summary>
    [Description("The type of conversation flow")]
    public ConversationType ConversationType { get; set; } = ConversationType.OpenEnded;

    /// <summary>
    /// Optional scenario-specific questions/phrases (newline-separated).
    /// </summary>
    [Description("Scenario-specific questions or phrases to use in conversation")]
    public string? QuestionBank { get; set; }

    /// <summary>
    /// True for system-provided scenarios (read-only), false for user-created.
    /// </summary>
    [Description("Whether this is a predefined system scenario")]
    public bool IsPredefined { get; set; }

    /// <summary>
    /// When the scenario was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the scenario was last modified.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets the localized display name based on current culture.
    /// </summary>
    [NotMapped]
    public string DisplayName => !string.IsNullOrEmpty(NameKorean) && 
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" 
        ? NameKorean 
        : Name;
}
