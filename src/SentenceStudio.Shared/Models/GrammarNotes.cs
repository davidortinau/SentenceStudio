using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

public class GrammarNotes
{
    [JsonPropertyName("original_sentence")]
    public string? OriginalSentence { get; set; }
    
    [JsonPropertyName("recommended_translation")]
    public string? RecommendedTranslation { get; set; }
    
    [JsonPropertyName("explanation")]
    public string? Explanation { get; set; }
}
