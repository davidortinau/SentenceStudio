namespace SentenceStudio.Shared.Models;

public class ResourceVocabularyMapping
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ResourceId { get; set; } = string.Empty;
    public string VocabularyWordId { get; set; } = string.Empty;
    
    // Navigation properties
    public LearningResource Resource { get; set; } = null!;
    public VocabularyWord VocabularyWord { get; set; } = null!;
    
    // Timestamps for tracking
    // public DateTime CreatedAt { get; set; }
    // public DateTime UpdatedAt { get; set; }
}
