using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

[Table("VocabularyProgress")]
public class VocabularyProgress
{
    public int Id { get; set; }
    public int VocabularyWordId { get; set; } // FK - ONE record per word
    
    // Global mastery tracking
    public int MultipleChoiceCorrect { get; set; } = 0;
    public int TextEntryCorrect { get; set; } = 0;
    public bool IsPromoted { get; set; } = false;
    public bool IsCompleted { get; set; } = false;
    
    // Learning metadata
    public DateTime FirstSeenAt { get; set; } = DateTime.Now;
    public DateTime LastPracticedAt { get; set; } = DateTime.Now;
    
    [JsonIgnore]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    
    [JsonIgnore]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    
    // Navigation properties
    [JsonIgnore]
    public VocabularyWord VocabularyWord { get; set; } = null!;
    
    [JsonIgnore]
    public List<VocabularyLearningContext> LearningContexts { get; set; } = new();
    
    // Computed properties matching the quiz requirements
    [NotMapped]
    public bool HasConfidenceInMultipleChoice => MultipleChoiceCorrect >= 3;
    
    [NotMapped] 
    public bool HasConfidenceInTextEntry => TextEntryCorrect >= 3;
    
    [NotMapped]
    public bool IsKnown => IsCompleted;
    
    [NotMapped]
    public float MultipleChoiceProgress => Math.Min(1.0f, (float)MultipleChoiceCorrect / 3);
    
    [NotMapped]
    public float TextEntryProgress => Math.Min(1.0f, (float)TextEntryCorrect / 3);
    
    [NotMapped]
    public bool IsUnknown => MultipleChoiceCorrect == 0 && TextEntryCorrect == 0;
    
    [NotMapped]
    public bool IsLearning => (MultipleChoiceCorrect > 0 || TextEntryCorrect > 0) && !IsCompleted;
}
