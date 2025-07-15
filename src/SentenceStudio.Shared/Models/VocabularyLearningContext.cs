using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

[Table("VocabularyLearningContext")]
public class VocabularyLearningContext
{
    public int Id { get; set; }
    public int VocabularyProgressId { get; set; } // FK to VocabularyProgress
    public int? LearningResourceId { get; set; } // Where learned (nullable for standalone practice)
    public string Activity { get; set; } = string.Empty; // "VocabularyQuiz", "Cloze", "VocabularyMatching"
    public DateTime LearnedAt { get; set; } = DateTime.Now;
    public int CorrectAnswersInContext { get; set; } = 0; // How many correct in this specific context
    public string InputMode { get; set; } = string.Empty; // "MultipleChoice", "TextEntry"
    
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
