using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

[Table("GradeResponses")]
public class GradeResponse
{
    public int Id { get; set; }
    
    [JsonPropertyName("fluency_score")]
    public double Fluency { get; set; }
    
    [JsonPropertyName("fluency_explanation")]
    public string? FluencyExplanation { get; set; }
    
    [JsonPropertyName("accuracy_score")]
    public double Accuracy { get; set; }
    
    [JsonPropertyName("accuracy_explanation")]
    public string? AccuracyExplanation { get; set; }
    
    public string? RecommendedTranslation { get; set; }
    
    [NotMapped]
    [JsonPropertyName("grammar_notes")]
    public GrammarNotes? GrammarNotes { get; set; }
    
    [NotMapped]
    [JsonPropertyName("vocabulary_analysis")]
    public List<VocabularyAnalysis>? VocabularyAnalysis { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public int ChallengeID { get; set; }
}

public class VocabularyAnalysis
{
    [JsonPropertyName("used_form")]
    public string UsedForm { get; set; } = string.Empty;
    
    [JsonPropertyName("dictionary_form")]
    public string DictionaryForm { get; set; } = string.Empty;
    
    [JsonPropertyName("meaning")]
    public string Meaning { get; set; } = string.Empty;
    
    [JsonPropertyName("usage_correct")]
    public bool UsageCorrect { get; set; }
    
    [JsonPropertyName("usage_explanation")]
    public string UsageExplanation { get; set; } = string.Empty;
}
