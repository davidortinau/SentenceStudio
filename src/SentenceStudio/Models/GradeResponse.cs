using System.ComponentModel;
using System.Text.Json.Serialization;
using SQLite;

namespace SentenceStudio.Models;
public class GradeResponse
{
    [JsonIgnore]
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }
    public double Fluency { get; set; }
    public string FluencyExplanation { get; set; }
    public double Accuracy { get; set; }
    public string AccuracyExplanation { get; set; }
    
    [Description("A best version of the sentence based on what you best think I was trying to say.")]
    public string RecommendedTranslation { get; set; }

    [Ignore]
    [Description("A list of grammar notes that can be used to improve the sentence.")]
    public GrammarNotes GrammarNotes { get; set; }

    [JsonIgnore]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public int ChallengeID { get; set; }
}