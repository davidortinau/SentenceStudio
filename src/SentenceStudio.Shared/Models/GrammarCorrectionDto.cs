using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// Represents a single grammar correction identified by the grading agent.
/// </summary>
public class GrammarCorrectionDto
{
    [Description("The original text that contains the grammar error")]
    [JsonPropertyName("original")]
    public string Original { get; set; } = string.Empty;

    [Description("The corrected version of the text")]
    [JsonPropertyName("corrected")]
    public string Corrected { get; set; } = string.Empty;

    [Description("Explanation of the grammar rule or why this correction was made")]
    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = string.Empty;
}
