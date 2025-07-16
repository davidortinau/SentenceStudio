using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

[Table("VocabularyProgress")]
public class VocabularyProgress
{
    public int Id { get; set; }
    public int VocabularyWordId { get; set; } // FK - ONE record per word
    public int UserId { get; set; } = 1; // FK - Support multiple users (default to 1 for backward compatibility)
    
    // Core mastery tracking
    public float MasteryScore { get; set; } = 0.0f; // 0.0 to 1.0
    public int TotalAttempts { get; set; } = 0;
    public int CorrectAttempts { get; set; } = 0;
    
    // Phase-specific tracking
    public int RecognitionAttempts { get; set; } = 0;
    public int RecognitionCorrect { get; set; } = 0;
    public int ProductionAttempts { get; set; } = 0;
    public int ProductionCorrect { get; set; } = 0;
    public int ApplicationAttempts { get; set; } = 0;
    public int ApplicationCorrect { get; set; } = 0;
    
    // Current learning phase
    public LearningPhase CurrentPhase { get; set; } = LearningPhase.Recognition;
    
    // Spaced repetition support
    public DateTime? NextReviewDate { get; set; }
    public int ReviewInterval { get; set; } = 1; // Days until next review
    public float EaseFactor { get; set; } = 2.5f; // SM-2 algorithm factor
    
    // Legacy fields for backward compatibility
    public int MultipleChoiceCorrect { get; set; } = 0;
    public int TextEntryCorrect { get; set; } = 0;
    public bool IsPromoted { get; set; } = false;
    public bool IsCompleted { get; set; } = false;
    
    // Learning metadata
    public DateTime FirstSeenAt { get; set; } = DateTime.Now;
    public DateTime LastPracticedAt { get; set; } = DateTime.Now;
    public DateTime? MasteredAt { get; set; } // When reached mastery threshold
    
    [JsonIgnore]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    
    [JsonIgnore]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    
    // Navigation properties
    [JsonIgnore]
    public VocabularyWord VocabularyWord { get; set; } = null!;
    
    [JsonIgnore]
    public List<VocabularyLearningContext> LearningContexts { get; set; } = new();
    
    // Computed properties for status classification
    [NotMapped]
    public LearningStatus Status => MasteryScore switch
    {
        0 => LearningStatus.Unknown,
        >= 0.8f => LearningStatus.Known,
        _ => LearningStatus.Learning
    };
    
    [NotMapped]
    public bool IsKnown => Status == LearningStatus.Known;
    
    [NotMapped]
    public bool IsLearning => Status == LearningStatus.Learning;
    
    [NotMapped]
    public bool IsUnknown => Status == LearningStatus.Unknown;
    
    [NotMapped]
    public float Accuracy => TotalAttempts > 0 ? (float)CorrectAttempts / TotalAttempts : 0;
    
    // Phase-specific accuracy
    [NotMapped]
    public float RecognitionAccuracy => RecognitionAttempts > 0 ? 
        (float)RecognitionCorrect / RecognitionAttempts : 0;
    
    [NotMapped]
    public float ProductionAccuracy => ProductionAttempts > 0 ? 
        (float)ProductionCorrect / ProductionAttempts : 0;
    
    [NotMapped]
    public float ApplicationAccuracy => ApplicationAttempts > 0 ? 
        (float)ApplicationCorrect / ApplicationAttempts : 0;
    
    [NotMapped]
    public bool IsDueForReview => NextReviewDate.HasValue && NextReviewDate.Value <= DateTime.Now;
    
    // Legacy computed properties for backward compatibility
    [NotMapped]
    public bool HasConfidenceInMultipleChoice => MultipleChoiceCorrect >= 3;
    
    [NotMapped] 
    public bool HasConfidenceInTextEntry => TextEntryCorrect >= 3;
    
    [NotMapped]
    public float MultipleChoiceProgress => Math.Min(1.0f, (float)MultipleChoiceCorrect / 3);
    
    [NotMapped]
    public float TextEntryProgress => Math.Min(1.0f, (float)TextEntryCorrect / 3);
}
