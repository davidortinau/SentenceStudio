
using System.Text.Json.Serialization;
using SQLite;

namespace SentenceStudio.Models;
public class Challenge
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    
    [JsonPropertyName("sentence")]
    public string SentenceText { get; set; }

    [JsonPropertyName("recommended_translation")]
    public string RecommendedTranslation { get; set; }

    [JsonPropertyName("vocabulary")]
    public List<VocabWord> Vocabulary { get; set; }
}