using System.ComponentModel.DataAnnotations.Schema;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// Represents a placement/baseline vocabulary assessment to establish
/// initial learner proficiency. Based on Laufer & Nation's VST and 
/// Productive Vocabulary Levels Test methodologies.
/// </summary>
[Table("PlacementTests")]
public class PlacementTest
{
    public int Id { get; set; }
    public int UserId { get; set; }
    
    public PlacementTestType TestType { get; set; }
    public PlacementTestStatus Status { get; set; } = PlacementTestStatus.NotStarted;
    
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    
    // Results
    public int TotalItemsPresented { get; set; }
    public int TotalCorrect { get; set; }
    public int RecognitionItemsCorrect { get; set; }
    public int ProductionItemsCorrect { get; set; }
    
    // Inferred proficiency
    public int EstimatedVocabularySizeMin { get; set; }
    public int EstimatedVocabularySizeMax { get; set; }
    public string? EstimatedCEFRLevel { get; set; }
    
    // Batch processing result
    public int WordsMarkedAsKnown { get; set; }
    public int WordsMarkedAsLearning { get; set; }
    
    public List<PlacementTestItem> Items { get; set; } = new();
}

/// <summary>
/// Individual test item within a placement test.
/// </summary>
[Table("PlacementTestItems")]
public class PlacementTestItem
{
    public int Id { get; set; }
    public int PlacementTestId { get; set; }
    public int VocabularyWordId { get; set; }
    
    public PlacementItemType ItemType { get; set; }
    public int FrequencyRank { get; set; } // 1-10000 range
    
    public bool? IsCorrect { get; set; }
    public DateTime? PresentedAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public int ResponseTimeMs { get; set; }
    
    public string? UserAnswer { get; set; }
    
    public PlacementTest PlacementTest { get; set; } = null!;
    public VocabularyWord VocabularyWord { get; set; } = null!;
}

public enum PlacementTestType
{
    QuickRecognition,      // Option 1: 5-10 min, recognition only
    AdaptiveRecognition,   // Option 2: 10-15 min, CAT-style
    HybridAssessment       // Option 3: 15-20 min, recognition + production
}

public enum PlacementTestStatus
{
    NotStarted,
    InProgress,
    Completed,
    Cancelled
}

public enum PlacementItemType
{
    Recognition,  // Multiple choice: "Which means 'hello'?"
    Production    // Cloze/fill-blank: "How do you say 'hello'? ____"
}
