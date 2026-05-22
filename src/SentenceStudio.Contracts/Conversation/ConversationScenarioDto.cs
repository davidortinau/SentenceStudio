using System.Text.Json.Serialization;

namespace SentenceStudio.Contracts.Conversation;

/// <summary>
/// Wire shape for a conversation scenario.
/// Uses explicit PascalCase JSON property names to match the
/// existing `ConversationScenario` EF entity shape the MAUI app
/// already consumes (the Flutter client's <c>conversation_dtos.dart</c>
/// is the source of truth — see spec 004).
/// </summary>
public sealed class ConversationScenarioDto
{
    [JsonPropertyName("Id")]
    public int Id { get; set; }

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("NameKorean")]
    public string? NameKorean { get; set; }

    [JsonPropertyName("PersonaName")]
    public string PersonaName { get; set; } = string.Empty;

    [JsonPropertyName("PersonaDescription")]
    public string PersonaDescription { get; set; } = string.Empty;

    [JsonPropertyName("SituationDescription")]
    public string SituationDescription { get; set; } = string.Empty;

    /// <summary>
    /// Wire values are the strings "OpenEnded" or "Finite" (PascalCase).
    /// Unknown values fall back to OpenEnded on the Flutter side.
    /// </summary>
    [JsonPropertyName("ConversationType")]
    public string ConversationType { get; set; } = "OpenEnded";

    [JsonPropertyName("QuestionBank")]
    public string? QuestionBank { get; set; }

    [JsonPropertyName("IsPredefined")]
    public bool IsPredefined { get; set; }
}
