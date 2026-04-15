namespace SentenceStudio.Shared.Models;

public class VocabularyQuizItem
{
    public required VocabularyWord Word { get; set; }
    public bool IsCurrent { get; set; }
    public UserActivity? UserActivity { get; set; }

    // Global progress from VocabularyProgress table (cross-activity mastery)
    public SentenceStudio.Shared.Models.VocabularyProgress? Progress { get; set; }

    // --- Session-local counters (cumulative, never reset on wrong answers) ---
    // Replace old QuizRecognitionStreak/QuizProductionStreak per spec §1.2.3
    public int SessionCorrectCount { get; set; }   // Total correct this session (any mode)
    public int SessionMCCorrect { get; set; }       // MC correct this session
    public int SessionTextCorrect { get; set; }     // Text correct this session

    // Gentle demotion flag (spec §1.2.1): wrong Text → force MC until correct MC clears it
    public bool PendingRecognitionCheck { get; set; }

    // IsKnown re-qualification tracking (spec §4.3.1): 14-day interval instead of 60
    public bool LostKnownThisSession { get; set; }

    // Legacy streaks — kept for backward compat but no longer drive mode or rotation
    [Obsolete("Replaced by SessionMCCorrect for rotation and Progress.CurrentStreak for mode selection")]
    public int QuizRecognitionStreak { get; set; } = 0;
    [Obsolete("Replaced by SessionTextCorrect for rotation")]
    public int QuizProductionStreak { get; set; } = 0;

    // Track if this is a DueOnly session
    public bool IsDueOnlySession { get; set; }

    // Tiered rotation model (spec §1.2.2 / §1.3)
    public bool ReadyToRotateOut
    {
        get
        {
            var mastery = Progress?.MasteryScore ?? 0f;
            var streak = Progress?.CurrentStreak ?? 0f;

            bool tieredReady;

            // Tier 1: High mastery — 1 correct text + recognition cleared
            if (mastery >= 0.80f || streak >= 8f)
                tieredReady = SessionTextCorrect >= 1 && !PendingRecognitionCheck;
            // Tier 2: Mid mastery — 2 correct (at least 1 text)
            else if (mastery >= 0.50f || streak >= 3f)
                tieredReady = SessionCorrectCount >= 2 && SessionTextCorrect >= 1;
            // Tier 3: Low mastery — full 3+3 demonstration
            else
                tieredReady = SessionMCCorrect >= 3 && SessionTextCorrect >= 3;

            // DueOnly bonus: globally known words can also rotate out
            return tieredReady || (Progress?.IsKnown ?? false);
        }
    }

    // Streak-based computed properties
    public bool IsPromoted => Progress?.MasteryScore >= 0.50f;
    public bool IsCompleted => Progress?.IsKnown ?? false;

    public const int RequiredCorrectAnswers = 3;

    public float CurrentStreak => Progress?.CurrentStreak ?? 0f;
    public int ProductionInStreak => Progress?.ProductionInStreak ?? 0;
    public float EffectiveStreak => Progress?.EffectiveStreak ?? 0f;
    public float MasteryProgress => Progress?.MasteryScore ?? 0f;

    public bool IsReadyToSkipInCurrentPhase { get; set; }
    public bool WasCorrectThisSession { get; set; }

    // Status helpers
    public bool IsUnknown => Progress?.IsUnknown ?? true;
    public bool IsLearning => Progress?.IsLearning ?? false;
    public bool IsKnown => Progress?.IsKnown ?? false;
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