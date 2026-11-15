using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

[Table("VocabularyProgress")]
public class VocabularyProgress
{
    private const int MIN_PRODUCTION_FOR_KNOWN = 2;

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string VocabularyWordId { get; set; } = string.Empty; // FK - ONE record per word
    public string UserId { get; set; } = string.Empty; // FK - Support multiple users

    // Core mastery tracking
    public float MasteryScore { get; set; } = 0.0f; // 0.0 to 1.0
    public int TotalAttempts { get; set; } = 0;
    public int CorrectAttempts { get; set; } = 0;

    // NEW: Streak-based tracking (replaces phase-specific tracking)
    public float CurrentStreak { get; set; } = 0f;  // Weighted consecutive correct answers
    public int ProductionInStreak { get; set; } = 0;  // Production attempts within current streak

    // Dedicated Vocab Quiz demonstration counters. Cumulative lifetime proof used by
    // focus-plan rotation so demonstrations survive session resume/relaunch.
    public int QuizRecognitionDemonstrations { get; set; } = 0;
    public int QuizProductionDemonstrations { get; set; } = 0;

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

    // User-declared status ("Trust but Verify")
    public bool IsUserDeclared { get; set; } = false;
    public DateTime? UserDeclaredAt { get; set; }
    public VerificationStatus VerificationState { get; set; } = VerificationStatus.None;

    // Learning metadata
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastPracticedAt { get; set; } = DateTime.UtcNow;
    public DateTime? MasteredAt { get; set; } // When reached mastery threshold

    // Passive exposure tracking (Reading word lookups)
    public DateTime? LastExposedAt { get; set; }
    public int ExposureCount { get; set; } = 0;

    [JsonIgnore]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    [JsonIgnore]
    public VocabularyWord VocabularyWord { get; set; } = null!;

    [JsonIgnore]
    public List<VocabularyLearningContext> LearningContexts { get; set; } = new();

    // NEW: Effective streak calculation for mastery scoring
    [NotMapped]
    public float EffectiveStreak => CurrentStreak + (ProductionInStreak * 0.5f);

    // Known, Learning and Unknown are decided by SentenceStudio.Services.VocabularyKnownRule with
    // the learner's Learning mode (docs/known-words-plan.md); a row cannot know the mode, so it
    // has no status properties of its own.

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
    public bool IsDueForReview => NextReviewDate.HasValue && NextReviewDate.Value <= DateTime.UtcNow;

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
