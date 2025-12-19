namespace SentenceStudio.Shared.Models;

public class VocabularyQuizItem
{
    public required VocabularyWord Word { get; set; }
    public bool IsCurrent { get; set; }
    public UserActivity? UserActivity { get; set; }

    // Global progress from VocabularyProgress table (cross-activity mastery)
    public SentenceStudio.Shared.Models.VocabularyProgress? Progress { get; set; }

    // Activity-specific progress (THIS quiz activity only)
    public int QuizRecognitionStreak { get; set; } = 0;  // Consecutive multiple choice correct
    public int QuizProductionStreak { get; set; } = 0;   // Consecutive text entry correct
    public bool QuizRecognitionComplete => QuizRecognitionStreak >= RequiredCorrectAnswers;
    public bool QuizProductionComplete => QuizProductionStreak >= RequiredCorrectAnswers;
    public bool ReadyToRotateOut => QuizRecognitionComplete && QuizProductionComplete;

    // Current phase within THIS quiz (independent of global phase)
    public bool IsPromotedInQuiz => QuizRecognitionComplete;

    // NEW: Streak-based computed properties
    // IsPromoted now based on MasteryScore threshold for text mode
    public bool IsPromoted => Progress?.MasteryScore >= 0.50f;

    // IsCompleted now uses the new IsKnown property (requires MasteryScore >= 0.85 AND ProductionInStreak >= 2)
    public bool IsCompleted => Progress?.IsKnown ?? false;

    // Legacy support for backward compatibility
    public const int RequiredCorrectAnswers = 3;

    // NEW: Streak-based progress indicators
    public int CurrentStreak => Progress?.CurrentStreak ?? 0;
    public int ProductionInStreak => Progress?.ProductionInStreak ?? 0;
    public float EffectiveStreak => Progress?.EffectiveStreak ?? 0f;
    public float MasteryProgress => Progress?.MasteryScore ?? 0f;

    // Check if term is ready to be skipped in current phase
    public bool IsReadyToSkipInCurrentPhase { get; set; }

    // Enhanced status helpers (use new IsKnown logic)
    public bool IsUnknown => Progress?.IsUnknown ?? true;
    public bool IsLearning => Progress?.IsLearning ?? false;
    public bool IsKnown => Progress?.IsKnown ?? false;

    // Spaced repetition support
    public bool IsDueForReview => Progress?.IsDueForReview ?? false;

    // LEGACY: Kept for backward compatibility but deprecated
    [Obsolete("Use CurrentStreak instead")]
    public int MultipleChoiceCorrect => Progress?.RecognitionCorrect ?? 0;
    [Obsolete("Use ProductionInStreak instead")]
    public int TextEntryCorrect => Progress?.ProductionCorrect ?? 0;
    [Obsolete("Use MasteryProgress >= 0.50 instead")]
    public bool HasConfidenceInMultipleChoice => Progress?.RecognitionAccuracy >= 0.7f;
    [Obsolete("Use MasteryProgress >= 0.50 instead")]
    public bool HasConfidenceInTextEntry => Progress?.ProductionAccuracy >= 0.7f;
    [Obsolete("Use MasteryProgress instead")]
    public float MultipleChoiceProgress => Progress?.RecognitionAccuracy ?? 0f;
    [Obsolete("Use MasteryProgress instead")]
    public float TextEntryProgress => Progress?.ProductionAccuracy ?? 0f;
}