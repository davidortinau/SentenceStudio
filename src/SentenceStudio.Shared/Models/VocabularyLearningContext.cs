using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

[Table("VocabularyLearningContexts")]
public class VocabularyLearningContext
{
    public int Id { get; set; }
    public int VocabularyProgressId { get; set; }
    public int? LearningResourceId { get; set; }
    
    // Activity tracking
    public string Activity { get; set; } = string.Empty; // "VocabularyQuiz", "Clozure", etc.
    public string InputMode { get; set; } = string.Empty; // "MultipleChoice", "TextEntry", "Conjugation"
    
    // Performance data
    public bool WasCorrect { get; set; }
    public float DifficultyScore { get; set; } = 0.5f; // 0-1, how hard was this usage
    public int ResponseTimeMs { get; set; } // How long to answer
    public float? UserConfidence { get; set; } // 0-1, optional self-assessment
    
    // Context-specific data
    public string? ContextType { get; set; } // "Isolated", "Sentence", "Conjugated"
    public string? UserInput { get; set; }
    public string? ExpectedAnswer { get; set; }
    
    public DateTime LearnedAt { get; set; } = DateTime.Now;
    
    // Legacy field for backward compatibility
    public int CorrectAnswersInContext { get; set; } = 0; // How many correct in this specific context
    
    [JsonIgnore]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    
    [JsonIgnore]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    
    // Navigation properties
    [JsonIgnore]
    public VocabularyProgress VocabularyProgress { get; set; } = null!;
    
    [JsonIgnore]
    public LearningResource? LearningResource { get; set; }
}
