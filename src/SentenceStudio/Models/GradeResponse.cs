using System.Text.Json.Serialization;
using SQLite;

namespace SentenceStudio.Models;
public class GradeResponse
{
    [JsonIgnore]
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }

    [JsonPropertyName("fluency_score")]
    public double Fluency { get; set; }

    [JsonPropertyName("fluency_explanation")]
    public string FluencyExplanation { get; set; }

    [JsonPropertyName("accuracy_score")]
    public double Accuracy { get; set; }

    [JsonPropertyName("accuracy_explanation")]
    public string AccuracyExplanation { get; set; }

    [JsonPropertyName("recommended_translation")]
    public string RecommendedTranslation { get; set; }

    [Ignore]
    [JsonPropertyName("grammar_notes")]
    public GrammarNotes GrammarNotes { get; set; }

    [JsonIgnore]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public int ChallengeID { get; set; }
}