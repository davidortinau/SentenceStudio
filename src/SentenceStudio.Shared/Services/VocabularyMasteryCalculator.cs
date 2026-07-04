using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Pure, stateless scoring engine for vocabulary mastery. This is the single source of truth for
/// the streak-based mastery/spaced-repetition math so that the live attempt path
/// (<see cref="VocabularyProgressService.RecordAttemptAsync"/>), duplicate-merge recalculation
/// (<c>LearningResourceRepository.MergeVocabularyWordsAsync</c>), and the word-page mastery chart all
/// produce identical results.
///
/// "Recalculate as if it was always one word" is implemented by replaying every persisted
/// <see cref="VocabularyLearningContext"/> (which is recorded for every attempt) in chronological
/// order through <see cref="ApplyAttempt"/>.
/// </summary>
public static class VocabularyMasteryCalculator
{
    // Streak-based scoring constants — kept in lock-step with the values that previously lived in
    // VocabularyProgressService. See Issue #191 for the EFFECTIVE_STREAK_DIVISOR rationale.
    public const float MASTERY_THRESHOLD = 0.85f;
    public const int MIN_PRODUCTION_FOR_KNOWN = 2;
    public const float EFFECTIVE_STREAK_DIVISOR = 12.0f;
    public const float WRONG_ANSWER_FLOOR = 0.6f;
    public const float MAX_WRONG_PENALTY = 0.4f;
    public const float MAX_STREAK_PRESERVE = 0.5f;
    public const float STREAK_PRESERVE_DIVISOR = 8.0f;
    public const float RECOVERY_BOOST = 0.02f;

    private const int MAX_REVIEW_INTERVAL_DAYS = 365;
    private const int MASTERED_REVIEW_INTERVAL_DAYS = 60;

    /// <summary>
    /// A single point on a word's mastery-over-time curve, produced by <see cref="RecalculateInto"/>.
    /// </summary>
    public readonly record struct MasteryTrajectoryPoint(
        DateTime At,
        float MasteryScore,
        bool WasCorrect,
        string Activity,
        string InputMode);

    /// <summary>
    /// Applies a single attempt to the running <paramref name="progress"/> aggregate, mutating the
    /// streak, mastery score, spaced-repetition schedule, counts, and mastery timestamps. This is the
    /// exact core math the live attempt path uses (legacy phase metrics, passive cascade, and context
    /// persistence remain the caller's responsibility).
    /// </summary>
    /// <param name="attemptTime">
    /// Timestamp to attribute to this attempt. The live path passes <c>DateTime.Now</c>; replay passes
    /// the historical <see cref="VocabularyLearningContext.LearnedAt"/> so reconstructed schedules and
    /// mastery timestamps reflect when the practice actually happened.
    /// </param>
    public static void ApplyAttempt(VocabularyProgress progress, VocabularyAttempt attempt, DateTime attemptTime)
    {
        progress.TotalAttempts++;
        if (attempt.WasCorrect)
            progress.CorrectAttempts++;

        // Production = free recall (Text/Voice input) rather than recognition (MultipleChoice).
        // String comparison mirrors the live path exactly (InputMode.Text/Voice .ToString()).
        bool isProduction = attempt.InputMode == "Text" ||
                            attempt.InputMode == "Voice" ||
                            attempt.InputMode == "TextEntry"; // Legacy support

        if (attempt.WasCorrect)
        {
            float weight = attempt.DifficultyWeight > 0 ? attempt.DifficultyWeight : 1.0f;
            progress.CurrentStreak += weight;
            if (isProduction)
            {
                progress.ProductionInStreak++;
            }

            float effectiveStreak = progress.CurrentStreak + (progress.ProductionInStreak * 0.5f);
            float streakScore = MathF.Min(effectiveStreak / EFFECTIVE_STREAK_DIVISOR, 1.0f);
            float recoveryBoost = (progress.MasteryScore > streakScore) ? RECOVERY_BOOST : 0f;
            progress.MasteryScore = MathF.Max(streakScore, progress.MasteryScore) + recoveryBoost;
            progress.MasteryScore = MathF.Min(progress.MasteryScore, 1.0f);
        }
        else
        {
            float penaltyFactor = MathF.Max(
                WRONG_ANSWER_FLOOR,
                1.0f - (MAX_WRONG_PENALTY / (1f + MathF.Log(1 + progress.CorrectAttempts))));

            if (attempt.PenaltyOverride.HasValue)
            {
                penaltyFactor = attempt.PenaltyOverride.Value;
            }

            progress.MasteryScore *= penaltyFactor;

            float preserveFraction = MathF.Min(
                MAX_STREAK_PRESERVE,
                MathF.Log(1 + progress.CorrectAttempts) / STREAK_PRESERVE_DIVISOR);
            progress.CurrentStreak = progress.CurrentStreak * preserveFraction;
            progress.ProductionInStreak = (int)(progress.ProductionInStreak * preserveFraction);
        }

        UpdateSpacedRepetitionSchedule(progress, attempt.WasCorrect, attemptTime);

        progress.LastPracticedAt = attemptTime;
        progress.UpdatedAt = attemptTime;

        if (progress.MasteryScore >= MASTERY_THRESHOLD &&
            progress.ProductionInStreak >= MIN_PRODUCTION_FOR_KNOWN)
        {
            if (!progress.MasteredAt.HasValue)
            {
                progress.MasteredAt = attemptTime;
            }
            // Known words always get a long review interval — SM-2 would set a short one since it
            // doesn't know about mastery status.
            progress.ReviewInterval = MASTERED_REVIEW_INTERVAL_DAYS;
            progress.NextReviewDate = attemptTime.AddDays(MASTERED_REVIEW_INTERVAL_DAYS);
        }
    }

    private static void UpdateSpacedRepetitionSchedule(VocabularyProgress progress, bool wasCorrect, DateTime attemptTime)
    {
        if (!wasCorrect)
        {
            // Soften the lapse: proportional stability reduction, NOT a hard reset to 1.
            // A single slip (typo / momentary miss) on a long-interval word should not
            // send it back to "due tomorrow" and flood the due pool with already-known
            // words. Keep ~20% of the interval; repeated failures still compound down
            // (365 -> 73 -> 15 -> 3 -> 1), so genuine forgetting recovers quickly while a
            // one-off slip stays well-spaced. Relearning-step pattern (FSRS/SM-17). See SME review.
            progress.ReviewInterval = Math.Max(1, (int)Math.Round(progress.ReviewInterval * 0.2));
            progress.EaseFactor = Math.Max(1.3f, progress.EaseFactor - 0.2f);
        }
        else
        {
            if (progress.ReviewInterval == 1)
            {
                progress.ReviewInterval = 6;
            }
            else
            {
                progress.ReviewInterval = (int)(progress.ReviewInterval * progress.EaseFactor);
                progress.ReviewInterval = Math.Min(progress.ReviewInterval, MAX_REVIEW_INTERVAL_DAYS);
                progress.EaseFactor = Math.Min(2.5f, progress.EaseFactor + 0.1f);
            }
        }

        progress.NextReviewDate = attemptTime.AddDays(progress.ReviewInterval);
    }

    /// <summary>
    /// True when a context represents passive exposure (a Reading lookup / phrase cascade) rather than
    /// an active attempt. Passive exposures are recorded as contexts but never affect streak or mastery
    /// — they only bump exposure tracking, mirroring <c>RecordPassiveExposureAsync</c>.
    /// </summary>
    public static bool IsPassive(VocabularyLearningContext context) =>
        string.Equals(context.InputMode, "Passive", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(context.ContextType, "Exposure", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reconstructs the <see cref="VocabularyAttempt"/> that produced a persisted context, for replay.
    /// Note: <see cref="VocabularyAttempt.PenaltyOverride"/> is not persisted on the context, so replay
    /// falls back to the computed scaled penalty for soft-penalty activities (e.g. Conversation). This
    /// is a negligible approximation compared with the prior behavior of destroying the history entirely.
    /// </summary>
    public static VocabularyAttempt ToAttempt(VocabularyLearningContext context, string vocabularyWordId, string userId) => new()
    {
        VocabularyWordId = vocabularyWordId,
        UserId = userId,
        Activity = context.Activity,
        InputMode = context.InputMode,
        WasCorrect = context.WasCorrect,
        DifficultyWeight = context.DifficultyScore,
        ContextType = context.ContextType,
        LearningResourceId = context.LearningResourceId,
        UserInput = context.UserInput,
        ExpectedAnswer = context.ExpectedAnswer,
        ResponseTimeMs = context.ResponseTimeMs,
        UserConfidence = context.UserConfidence
    };

    /// <summary>
    /// Resets the replay-derived fields of <paramref name="target"/> and replays every supplied context
    /// in chronological order, producing the aggregate that would exist if all the contexts had always
    /// belonged to a single word record. Identity fields (Id, VocabularyWordId, UserId), FirstSeenAt,
    /// CreatedAt, and user-declared status are left untouched for the caller to manage.
    /// </summary>
    /// <param name="trajectory">
    /// The mastery-after-each-active-attempt curve, suitable for charting. Passive exposures do not
    /// emit points.
    /// </param>
    public static void RecalculateInto(
        VocabularyProgress target,
        IEnumerable<VocabularyLearningContext> contexts,
        out List<MasteryTrajectoryPoint> trajectory)
    {
        target.TotalAttempts = 0;
        target.CorrectAttempts = 0;
        target.CurrentStreak = 0f;
        target.ProductionInStreak = 0;
        target.MasteryScore = 0f;
        target.ReviewInterval = 1;
        target.EaseFactor = 2.5f;
        target.NextReviewDate = null;
        target.MasteredAt = null;
        target.ExposureCount = 0;
        target.LastExposedAt = null;
        target.QuizRecognitionDemonstrations = 0;
        target.QuizProductionDemonstrations = 0;

        trajectory = new List<MasteryTrajectoryPoint>();

        // Defensive chronological ordering — callers should not have to guarantee sort order. CreatedAt
        // is the stable tie-breaker for contexts that share a LearnedAt timestamp.
        var ordered = contexts
            .OrderBy(c => c.LearnedAt)
            .ThenBy(c => c.CreatedAt);

        foreach (var context in ordered)
        {
            if (IsPassive(context))
            {
                target.ExposureCount++;
                target.LastExposedAt = context.LearnedAt;
                continue;
            }

            var attempt = ToAttempt(context, target.VocabularyWordId, target.UserId);
            ApplyAttempt(target, attempt, context.LearnedAt);
            UpdateQuizDemonstrationCounters(target, attempt);
            trajectory.Add(new MasteryTrajectoryPoint(
                context.LearnedAt,
                target.MasteryScore,
                attempt.WasCorrect,
                context.Activity ?? string.Empty,
                context.InputMode ?? string.Empty));
        }
    }

    private static void UpdateQuizDemonstrationCounters(VocabularyProgress progress, VocabularyAttempt attempt)
    {
        if (!attempt.WasCorrect || attempt.Activity != "VocabularyQuiz")
            return;

        if (attempt.InputMode == "MultipleChoice")
            progress.QuizRecognitionDemonstrations++;
        else if (attempt.InputMode == "Text" || attempt.InputMode == "TextEntry")
            progress.QuizProductionDemonstrations++;
    }
}
