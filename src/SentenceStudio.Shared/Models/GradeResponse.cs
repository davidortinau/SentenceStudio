using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

[Table("GradeResponses")]
public class GradeResponse
{
    public int Id { get; set; }
    public double Fluency { get; set; }
    public string? FluencyExplanation { get; set; }
    public double Accuracy { get; set; }
    public string? AccuracyExplanation { get; set; }
    public string? RecommendedTranslation { get; set; }
    
    [NotMapped]
    public GrammarNotes? GrammarNotes { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public int ChallengeID { get; set; }
}
