
using System.Text.Json.Serialization;

namespace SentenceStudio.Models;
public class Challenge
{
    [JsonPropertyName("sentence")]
    public string SentenceText { get; set; }

    [JsonPropertyName("recommended_translation")]
    public string RecommendedTranslation { get; set; }

    [JsonPropertyName("vocabulary")]
    public List<VocabWord> Vocabulary { get; set; }
}