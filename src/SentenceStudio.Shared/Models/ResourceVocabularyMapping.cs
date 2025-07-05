namespace SentenceStudio.Shared.Models;

public class ResourceVocabularyMapping
{
    public int Id { get; set; }
    public int ResourceId { get; set; }
    public int VocabularyWordId { get; set; }
    
    // Navigation properties
    public LearningResource Resource { get; set; } = null!;
    public VocabularyWord VocabularyWord { get; set; } = null!;
    
    // Timestamps for tracking
    // public DateTime CreatedAt { get; set; }
    // public DateTime UpdatedAt { get; set; }
}
