using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

public class ExampleSentence
{
    public int Id { get; set; }
    
    public string VocabularyWordId { get; set; } = string.Empty;
    
    public string? LearningResourceId { get; set; }
    
    [Required]
    [MaxLength(500)]
    public string TargetSentence { get; set; } = string.Empty;
    
    [MaxLength(500)]
    public string? NativeSentence { get; set; }
    
    [MaxLength(2000)]
    public string? AudioUri { get; set; }
    
    public bool IsCore { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // Navigation properties
    [JsonIgnore]
    public VocabularyWord? VocabularyWord { get; set; }
    
    [JsonIgnore]
    public LearningResource? LearningResource { get; set; }
}
