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
    //
    // Issue #191: Tier 2 trigger tightened from OR → AND, and demonstration
    // floor raised from (SessC≥2, ST≥1) to (SessC≥4, ST≥2). Under the old
    // OR-trigger, a fresh word's first three correct MC turns alone (streak=3)
    // dropped it from the strict Tier 3 floor into Tier 2's lenient floor,
    // and the very next Text turn satisfied (2,1) — rotating fresh words at
    // turn 4. The AND trigger keeps Tier 2 reserved for words that have BOTH
    // accumulated mid-mastery AND a real streak, and the higher floor demands
    // visible session-level demonstration. See:
    //   .squad/decisions/inbox/wash-vocab-quiz-scoring-proposal-191.md
    //   tools/quiz-rotation-sim/sim.py
    public bool ReadyToRotateOut
    {
        get
        {
            var mastery = Progress?.MasteryScore ?? 0f;
            var streak = Progress?.CurrentStreak ?? 0f;

            bool tieredReady;

            // Tier 1: High mastery — 1 correct text + recognition cleared (UNCHANGED)
            if (mastery >= 0.80f || streak >= 8f)
                tieredReady = SessionTextCorrect >= 1 && !PendingRecognitionCheck;
            // Tier 2: Mid mastery — must demonstrate BOTH mid-mastery AND streak,
            // then prove with 4 in-session corrects including 2 text. (#191)
            else if (mastery >= 0.50f && streak >= 3f)
                tieredReady = SessionCorrectCount >= 4 && SessionTextCorrect >= 2;
            // Tier 3: Low mastery — full 3+3 demonstration (UNCHANGED)
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