
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

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [Ignore]
    [JsonPropertyName("vocabulary")]
    public List<VocabularyWord> Vocabulary { get; set; }

    public string VocabularyWord {get;set;}
}