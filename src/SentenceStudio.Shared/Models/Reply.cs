using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

public class Reply
{
    [Description("The conversation partner's response message in Korean")]
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [Description("Comprehension score from 0.0 to 1.0 indicating how well the user's message was understood")]
    [JsonPropertyName("comprehension_score")]
    public double Comprehension { get; set; }

    [Description("Notes about the user's comprehension and communication effectiveness")]
    [JsonPropertyName("comprehension_notes")]
    public string? ComprehensionNotes { get; set; }

    [Description("List of grammar corrections found in the user's input")]
    [JsonPropertyName("grammar_corrections")]
    public List<GrammarCorrectionDto> GrammarCorrections { get; set; } = new();
}
