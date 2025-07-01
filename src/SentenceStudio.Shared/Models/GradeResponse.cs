using System;

namespace SentenceStudio.Shared.Models;

public class GradeResponse
{
    public int ID { get; set; }
    public double Fluency { get; set; }
    public string? FluencyExplanation { get; set; }
    public double Accuracy { get; set; }
    public string? AccuracyExplanation { get; set; }
    public string? RecommendedTranslation { get; set; }
    public GrammarNotes? GrammarNotes { get; set; }
    public DateTime CreatedAt { get; set; }
    public int ChallengeID { get; set; }
}
