using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

[Table("VocabularyProgress")]
public class VocabularyProgress
{
    // Minimum production attempts required to be considered "Known"
    private const int MIN_PRODUCTION_FOR_KNOWN = 2;
    private const float MASTERY_THRESHOLD = 0.85f;

    public int Id { get; set; }
    public int VocabularyWordId { get; set; } // FK - ONE record per word
    public int UserId { get; set; } = 1; // FK - Support multiple users (default to 1 for backward compatibility)

    // Core mastery tracking
    public float MasteryScore { get; set; } = 0.0f; // 0.0 to 1.0
    public int TotalAttempts { get; set; } = 0;
    public int CorrectAttempts { get; set; } = 0;

    // NEW: Streak-based tracking (replaces phase-specific tracking)
    public int CurrentStreak { get; set; } = 0;  // Consecutive correct answers
    public int ProductionInStreak { get; set; } = 0;  // Production attempts within current streak

    // LEGACY: Phase-specific tracking - to be dropped in future migration
    [Obsolete("Use CurrentStreak instead. Will be removed in future version.")]
    public int RecognitionAttempts { get; set; } = 0;
    [Obsolete("Use CurrentStreak instead. Will be removed in future version.")]
    public int RecognitionCorrect { get; set; } = 0;
    [Obsolete("Use ProductionInStreak instead. Will be removed in future version.")]
    public int ProductionAttempts { get; set; } = 0;
    [Obsolete("Use ProductionInStreak instead. Will be removed in future version.")]
    public int ProductionCorrect { get; set; } = 0;
    [Obsolete("No longer used. Will be removed in future version.")]
    public int ApplicationAttempts { get; set; } = 0;
    [Obsolete("No longer used. Will be removed in future version.")]
    public int ApplicationCorrect { get; set; } = 0;

    // LEGACY: Current learning phase - no longer used for scoring
    [Obsolete("No longer used for scoring. Will be removed in future version.")]
    public LearningPhase CurrentPhase { get; set; } = LearningPhase.Recognition;

    // Spaced repetition support
    public DateTime? NextReviewDate { get; set; }
    public int ReviewInterval { get; set; } = 1; // Days until next review
    public float EaseFactor { get; set; } = 2.5f; // SM-2 algorithm factor

    // LEGACY: Old tracking fields - to be dropped in future migration
    [Obsolete("No longer used. Will be removed in future version.")]
    public int MultipleChoiceCorrect { get; set; } = 0;
    [Obsolete("No longer used. Will be removed in future version.")]
    public int TextEntryCorrect { get; set; } = 0;
    [Obsolete("Use MasteryScore >= 0.50 instead. Will be removed in future version.")]
    public bool IsPromoted { get; set; } = false;
    [Obsolete("Use IsKnown instead. Will be removed in future version.")]
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

    // NEW: Effective streak calculation for mastery scoring
    [NotMapped]
    public float EffectiveStreak => CurrentStreak + (ProductionInStreak * 0.5f);

    // Computed properties for status classification
    [NotMapped]
    public LearningStatus Status => MasteryScore switch
    {
        0 => LearningStatus.Unknown,
        _ when IsKnown => LearningStatus.Known,
        _ => LearningStatus.Learning
    };

    // NEW: IsKnown requires both mastery threshold AND production evidence
    [NotMapped]
    public bool IsKnown => MasteryScore >= MASTERY_THRESHOLD && ProductionInStreak >= MIN_PRODUCTION_FOR_KNOWN;

    [NotMapped]
    public bool IsLearning => MasteryScore > 0 && !IsKnown;

    [NotMapped]
    public bool IsUnknown => MasteryScore == 0;

    [NotMapped]
    public float Accuracy => TotalAttempts > 0 ? (float)CorrectAttempts / TotalAttempts : 0;

    // LEGACY: Phase-specific accuracy - kept for backward compatibility during migration
    [NotMapped]
    [Obsolete("Use EffectiveStreak instead. Will be removed in future version.")]
    public float RecognitionAccuracy => RecognitionAttempts > 0 ?
        (float)RecognitionCorrect / RecognitionAttempts : 0;

    [NotMapped]
    [Obsolete("Use ProductionInStreak instead. Will be removed in future version.")]
    public float ProductionAccuracy => ProductionAttempts > 0 ?
        (float)ProductionCorrect / ProductionAttempts : 0;

    [NotMapped]
    [Obsolete("No longer used. Will be removed in future version.")]
    public float ApplicationAccuracy => ApplicationAttempts > 0 ?
        (float)ApplicationCorrect / ApplicationAttempts : 0;

    [NotMapped]
    public bool IsDueForReview => NextReviewDate.HasValue && NextReviewDate.Value <= DateTime.Now;

    // LEGACY: computed properties for backward compatibility
    [NotMapped]
    [Obsolete("Use MasteryScore instead. Will be removed in future version.")]
    public bool HasConfidenceInMultipleChoice => MultipleChoiceCorrect >= 3;

    [NotMapped]
    [Obsolete("Use MasteryScore instead. Will be removed in future version.")]
    public bool HasConfidenceInTextEntry => TextEntryCorrect >= 3;

    [NotMapped]
    [Obsolete("Use MasteryScore instead. Will be removed in future version.")]
    public float MultipleChoiceProgress => Math.Min(1.0f, (float)MultipleChoiceCorrect / 3);

    [NotMapped]
    [Obsolete("Use MasteryScore instead. Will be removed in future version.")]
    public float TextEntryProgress => Math.Min(1.0f, (float)TextEntryCorrect / 3);

    // NEW: Streak-based progress helpers
    [NotMapped]
    public int StreakToKnown => Math.Max(0, (int)Math.Ceiling((6.0f - EffectiveStreak)));

    [NotMapped]
    public int ProductionNeededForKnown => Math.Max(0, MIN_PRODUCTION_FOR_KNOWN - ProductionInStreak);
}
