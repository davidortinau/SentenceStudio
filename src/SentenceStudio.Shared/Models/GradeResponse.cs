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
    
    [NotMapped]
    public List<VocabularyAnalysis>? VocabularyAnalysis { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public int ChallengeID { get; set; }
}

public class VocabularyAnalysis
{
    public string UsedForm { get; set; } = string.Empty;
    public string DictionaryForm { get; set; } = string.Empty;
    public string Meaning { get; set; } = string.Empty;
    public bool UsageCorrect { get; set; }
    public string UsageExplanation { get; set; } = string.Empty;
}
