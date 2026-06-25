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

    /// <summary>Where this sentence came from (AI, learner, reading capture).</summary>
    public ExampleSentenceSource Source { get; set; } = ExampleSentenceSource.Unknown;

    /// <summary>
    /// Curation state. Only Curated/Verified sentences feed quizzes and review.
    /// AI suggestions start as <see cref="ExampleSentenceStatus.Suggested"/>.
    /// </summary>
    public ExampleSentenceStatus Status { get; set; } = ExampleSentenceStatus.Curated;

    /// <summary>Korean speech level / register of the target sentence.</summary>
    public SpeechRegister Register { get; set; } = SpeechRegister.Unspecified;

    /// <summary>
    /// Optional difficulty hint (1 = easiest .. 5 = hardest), based on length,
    /// rare-word count, and honorific complexity. Used to match sentences to mastery.
    /// </summary>
    public int? DifficultyLevel { get; set; }

    /// <summary>Learner flagged this sentence as a bad/unnatural example.</summary>
    public bool IsFlagged { get; set; }

    /// <summary>True when this sentence may be used to generate quiz/review items.</summary>
    public bool IsQuizEligible =>
        !IsFlagged && Status is ExampleSentenceStatus.Curated or ExampleSentenceStatus.Verified;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // Navigation properties
    [JsonIgnore]
    public VocabularyWord? VocabularyWord { get; set; }
    
    [JsonIgnore]
    public LearningResource? LearningResource { get; set; }
}
