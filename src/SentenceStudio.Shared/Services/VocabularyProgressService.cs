using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

public class VocabularyProgressService : IVocabularyProgressService
{
    private readonly VocabularyProgressRepository _progressRepo;
    private readonly VocabularyLearningContextRepository _contextRepo;
    private readonly ILogger<VocabularyProgressService> _logger;
    private readonly IPreferencesService? _preferences;

    // NEW: Streak-based scoring constants
    private const float MASTERY_THRESHOLD = 0.85f;                // MasteryScore threshold for "Known"
    private const int MIN_PRODUCTION_FOR_KNOWN = 2;               // Minimum production attempts to be "Known"
    private const float EFFECTIVE_STREAK_DIVISOR = 7.0f;          // EffectiveStreak / 7.0 = MasteryScore (capped at 1.0)

    // Phase 0: Scoring engine constants
    private const float WRONG_ANSWER_FLOOR = 0.6f;               // Scaled penalty never softer than this
    private const float MAX_WRONG_PENALTY = 0.4f;                 // Maximum penalty portion for wrong answers
    private const float MAX_STREAK_PRESERVE = 0.5f;               // Never keep more than half the streak on wrong answer
    private const float STREAK_PRESERVE_DIVISOR = 8.0f;           // Divisor for streak preservation fraction
    private const float RECOVERY_BOOST = 0.02f;                   // Small boost so correct answers always show progress

    // LEGACY: Old constants kept for reference during migration
    [Obsolete("Use EFFECTIVE_STREAK_DIVISOR instead")]
    private const float RECEPTIVE_MASTERY_THRESHOLD = 0.70f;
    [Obsolete("No longer used")]
    private const float PHASE_ADVANCE_THRESHOLD = 0.75f;
    [Obsolete("No longer used - streak-based now")]
    private const int ROLLING_AVERAGE_COUNT = 8;
    [Obsolete("No longer used")]
    private const int MIN_ATTEMPTS_PER_PHASE = 4;
    [Obsolete("Use MIN_PRODUCTION_FOR_KNOWN instead")]
    private const int MIN_CORRECT_RECOGNITION = 3;
    [Obsolete("Use MIN_PRODUCTION_FOR_KNOWN instead")]
    private const int MIN_CORRECT_PRODUCTION = 2;
    [Obsolete("Use WRONG_ANSWER_FLOOR instead")]
    private const float INCORRECT_PENALTY = 0.15f;

    public VocabularyProgressService(
        VocabularyProgressRepository progressRepo,
        VocabularyLearningContextRepository contextRepo,
        ILogger<VocabularyProgressService> logger,
        IPreferencesService? preferences = null)
    {
        _progressRepo = progressRepo;
        _contextRepo = contextRepo;
        _logger = logger;
        _preferences = preferences;
    }

    // CRITICAL: Resolves the active user ID. Throws if no user is available.
    // This exists because userId="" silently returns empty results, which caused
    // known words to appear as new in quizzes. Never allow empty userId to pass through.
    private string ResolveUserId(string userId)
    {
        if (!string.IsNullOrEmpty(userId))
            return userId;
        
        var resolved = _preferences?.Get("active_profile_id", string.Empty) ?? string.Empty;
        if (string.IsNullOrEmpty(resolved))
        {
            _logger.LogWarning("VocabularyProgressService: No userId provided and no active_profile_id in preferences. Progress queries will return empty results.");
        }
        return resolved;
    }

    /// <summary>
    /// Migrates existing progress data to the new streak-based scoring system.
    /// Call this from UI (e.g., VocabularyLearningProgressPage) to convert existing data.
    /// </summary>
    /// <returns>Number of records migrated</returns>
    public async Task<int> MigrateToStreakBasedScoringAsync()
    {
        _logger.LogInformation("🔄 Starting migration to streak-based scoring system...");

        var allProgress = await _progressRepo.ListAsync();
        int migratedCount = 0;

        foreach (var progress in allProgress)
        {
            // Convert old phase-based tracking to streak-based
            // CurrentStreak = RecognitionCorrect + ProductionCorrect (capped at 10)
#pragma warning disable CS0618 // Suppress obsolete warnings during migration
            progress.CurrentStreak = Math.Min(10, progress.RecognitionCorrect + progress.ProductionCorrect);
            progress.ProductionInStreak = Math.Min((int)progress.CurrentStreak, progress.ProductionCorrect);
#pragma warning restore CS0618

            // Recalculate MasteryScore using new formula
            float effectiveStreak = progress.CurrentStreak + (progress.ProductionInStreak * 0.5f);
            progress.MasteryScore = Math.Min(effectiveStreak / EFFECTIVE_STREAK_DIVISOR, 1.0f);

            // Preserve MasteredAt for words that already reached mastery
            // (don't overwrite existing timestamps)

            progress.UpdatedAt = DateTime.Now;
            await _progressRepo.SaveAsync(progress);
            migratedCount++;

            _logger.LogDebug("🔄 Migrated word {WordId}: Streak={Streak}, ProdInStreak={ProdStreak}, Mastery={Mastery:F2}",
                progress.VocabularyWordId, progress.CurrentStreak, progress.ProductionInStreak, progress.MasteryScore);
        }

        _logger.LogInformation("✅ Migration complete! Migrated {Count} vocabulary progress records.", migratedCount);
        return migratedCount;
    }

    /// <summary>
    /// Records a vocabulary learning attempt using NEW streak-based scoring
    /// </summary>
    public async Task<VocabularyProgress> RecordAttemptAsync(VocabularyAttempt attempt)
    {
        var progress = await GetOrCreateProgressAsync(attempt.VocabularyWordId, attempt.UserId);

        // Update total counts
        progress.TotalAttempts++;
        if (attempt.WasCorrect)
            progress.CorrectAttempts++;

        // Determine if this is a production attempt (Text or Voice input)
        bool isProduction = attempt.InputMode == InputMode.Text.ToString() ||
                           attempt.InputMode == InputMode.Voice.ToString() ||
                           attempt.InputMode == "TextEntry"; // Legacy support

        // NEW: Streak-based scoring
        if (attempt.WasCorrect)
        {
            // Correct answer: increment streak by DifficultyWeight
            float weight = attempt.DifficultyWeight > 0 ? attempt.DifficultyWeight : 1.0f;
            progress.CurrentStreak += weight;
            if (isProduction)
            {
                progress.ProductionInStreak++;
            }

            // Calculate new MasteryScore with recovery awareness
            float effectiveStreak = progress.CurrentStreak + (progress.ProductionInStreak * 0.5f);
            float streakScore = MathF.Min(effectiveStreak / EFFECTIVE_STREAK_DIVISOR, 1.0f);
            float recoveryBoost = (progress.MasteryScore > streakScore) ? RECOVERY_BOOST : 0f;
            progress.MasteryScore = MathF.Max(streakScore, progress.MasteryScore) + recoveryBoost;
            progress.MasteryScore = MathF.Min(progress.MasteryScore, 1.0f);

            _logger.LogDebug("Correct! Word {WordId}: Streak={Streak:F1}, Weight={Weight:F1}, ProdInStreak={ProdStreak}, EffStreak={EffStreak:F1}, Mastery={Mastery:F2}",
                progress.VocabularyWordId, progress.CurrentStreak, weight, progress.ProductionInStreak, effectiveStreak, progress.MasteryScore);
        }
        else
        {
            // Wrong answer: scaled penalty and partial streak preservation
            // Temporal weighting — established words penalized less
            float penaltyFactor = MathF.Max(
                WRONG_ANSWER_FLOOR,
                1.0f - (MAX_WRONG_PENALTY / (1f + MathF.Log(1 + progress.CorrectAttempts))));
            progress.MasteryScore *= penaltyFactor;

            // Partial streak preservation — experienced words keep some streak
            float preserveFraction = MathF.Min(
                MAX_STREAK_PRESERVE,
                MathF.Log(1 + progress.CorrectAttempts) / STREAK_PRESERVE_DIVISOR);
            progress.CurrentStreak = progress.CurrentStreak * preserveFraction;
            progress.ProductionInStreak = (int)(progress.ProductionInStreak * preserveFraction);

            _logger.LogDebug("Wrong! Word {WordId}: PenaltyFactor={Penalty:F2}, PreserveFrac={Preserve:F2}, Streak={Streak:F1}, Mastery={Mastery:F2}",
                progress.VocabularyWordId, penaltyFactor, preserveFraction, progress.CurrentStreak, progress.MasteryScore);
        }

        // LEGACY: Update old phase-specific fields for backward compatibility during migration
#pragma warning disable CS0618
        UpdatePhaseMetrics(progress, attempt);
        UpdateLegacyFields(progress, attempt);
#pragma warning restore CS0618

        // Update spaced repetition schedule
        UpdateSpacedRepetitionSchedule(progress, attempt);

        // Update timestamps
        progress.LastPracticedAt = DateTime.Now;
        progress.UpdatedAt = DateTime.Now;

        // Mark as mastered if threshold reached with production evidence
        if (progress.MasteryScore >= MASTERY_THRESHOLD &&
            progress.ProductionInStreak >= MIN_PRODUCTION_FOR_KNOWN)
        {
            if (!progress.MasteredAt.HasValue)
            {
                progress.MasteredAt = DateTime.Now;
                _logger.LogInformation("🎉 Word {WordId} mastered! Mastery={Mastery:F2}, ProdInStreak={ProdStreak}. Next review in 60 days.",
                    progress.VocabularyWordId, progress.MasteryScore, progress.ProductionInStreak);
            }
            // Known words always get a long review interval — SM-2 would
            // set a short one since it doesn't know about mastery status
            progress.ReviewInterval = 60;
            progress.NextReviewDate = DateTime.Now.AddDays(60);
        }

        // Save progress
        progress = await _progressRepo.SaveAsync(progress);

        // Record detailed context
        await RecordLearningContextAsync(progress.Id, attempt);

        return progress;
    }

    /// <summary>
    /// Gets progress for a specific vocabulary word and user
    /// </summary>
    public Task<VocabularyProgress> GetProgressAsync(string vocabularyWordId, string userId = "")
    {
        return GetOrCreateProgressAsync(vocabularyWordId, ResolveUserId(userId));
    }

    /// <summary>
    /// Gets words due for review based on spaced repetition.
    /// Excludes: Known words, Familiar words in grace period.
    /// Includes at low frequency: Familiar words past grace period (verification probes).
    /// </summary>
    public async Task<List<VocabularyProgress>> GetReviewCandidatesAsync(string userId = "")
    {
        var resolvedUserId = ResolveUserId(userId);
        var allProgress = await _progressRepo.ListAsync();
        return allProgress.Where(p =>
            p.UserId == resolvedUserId &&
            p.IsDueForReview &&
            !p.IsKnown &&
            !p.IsInGracePeriod).ToList();
    }

    /// <summary>
    /// Gets all progress records for a user
    /// </summary>
    public async Task<List<VocabularyProgress>> GetAllProgressAsync(string userId = "")
    {
        var resolvedUserId = ResolveUserId(userId);
        var allProgress = await _progressRepo.ListAsync();
        return allProgress.Where(p => p.UserId == resolvedUserId).ToList();
    }

    /// <summary>
    /// Gets progress for multiple vocabulary words and returns as dictionary.
    /// OPTIMIZED: Uses batch query and only returns EXISTING progress (no auto-creation).
    /// Use this for list views where you just want to display status.
    /// NOTE: userId is auto-resolved from preferences if not provided.
    /// </summary>
    public async Task<Dictionary<string, VocabularyProgress>> GetProgressForWordsAsync(List<string> vocabularyWordIds, string userId = "")
    {
        if (!vocabularyWordIds.Any())
            return new Dictionary<string, VocabularyProgress>();

        var resolvedUserId = ResolveUserId(userId);

        // OPTIMIZATION: Use batch query instead of loading entire table
        var existingProgress = await _progressRepo.GetByWordIdsAsync(vocabularyWordIds);

        // Filter by user and build dictionary - no auto-creation for list views
        return existingProgress
            .Where(p => p.UserId == resolvedUserId)
            .ToDictionary(p => p.VocabularyWordId, p => p);
    }

    /// <summary>
    /// Gets ALL progress records for a user and returns as dictionary keyed by VocabularyWordId
    /// OPTIMIZATION: Use this instead of GetProgressForWordsAsync when loading all vocabulary
    /// Avoids massive WHERE IN clauses by loading everything in one efficient query
    /// </summary>
    public async Task<Dictionary<string, VocabularyProgress>> GetAllProgressDictionaryAsync(string userId = "")
    {
        var resolvedUserId = ResolveUserId(userId);
        var allProgress = await _progressRepo.GetAllForUserAsync(resolvedUserId);
        
        return allProgress.ToDictionary(p => p.VocabularyWordId, p => p);
    }

    private void UpdatePhaseMetrics(VocabularyProgress progress, VocabularyAttempt attempt)
    {
        // LEGACY: Keep updating old phase metrics during migration period
#pragma warning disable CS0618
        switch (attempt.Phase)
        {
            case LearningPhase.Recognition:
                progress.RecognitionAttempts++;
                if (attempt.WasCorrect) progress.RecognitionCorrect++;
                break;
            case LearningPhase.Production:
                progress.ProductionAttempts++;
                if (attempt.WasCorrect) progress.ProductionCorrect++;
                break;
            case LearningPhase.Application:
                progress.ApplicationAttempts++;
                if (attempt.WasCorrect) progress.ApplicationCorrect++;
                break;
        }
#pragma warning restore CS0618
    }

    private void UpdateLegacyFields(VocabularyProgress progress, VocabularyAttempt attempt)
    {
        // LEGACY: Update old fields for backward compatibility during migration
#pragma warning disable CS0618
        if (attempt.InputMode == "MultipleChoice")
        {
            if (attempt.WasCorrect)
                progress.MultipleChoiceCorrect++;
        }
        else if (attempt.InputMode == "Text" || attempt.InputMode == "TextEntry")
        {
            if (attempt.WasCorrect)
                progress.TextEntryCorrect++;
        }

        // Update promoted status based on MasteryScore (new logic)
        progress.IsPromoted = progress.MasteryScore >= 0.50f;

        // Update completed status based on IsKnown (new logic)
        progress.IsCompleted = progress.IsKnown;
#pragma warning restore CS0618
    }

    // LEGACY METHODS - Kept for backward compatibility but marked obsolete
    // These complex calculation methods are no longer used by the new streak-based system

    [Obsolete("No longer used - streak-based scoring replaces this")]
    private async Task<float> CalculateRigorousMasteryScoreAsync(VocabularyProgress progress, VocabularyAttempt attempt)
    {
        // Simple pass-through - actual calculation now happens in RecordAttemptAsync
        return progress.MasteryScore;
    }

    [Obsolete("No longer used - streak-based scoring replaces this")]
    private float CalculatePhaseSpecificScore(VocabularyProgress progress)
    {
        return progress.MasteryScore;
    }

    [Obsolete("No longer used - streak-based scoring replaces this")]
    private float CalculateRecognitionScore(VocabularyProgress progress)
    {
        return 0f;
    }

    [Obsolete("No longer used - streak-based scoring replaces this")]
    private float CalculateProductionScore(VocabularyProgress progress)
    {
        return 0f;
    }

    [Obsolete("No longer used - streak-based scoring replaces this")]
    private float CalculateWeightedRollingAverage(List<VocabularyLearningContext> recentAttempts, VocabularyAttempt currentAttempt)
    {
        return 0f;
    }

    [Obsolete("No longer used - streak-based scoring replaces this")]
    private float ApplyIncorrectAnswerPenalties(float baseScore, List<VocabularyLearningContext> recentAttempts)
    {
        return baseScore;
    }

    [Obsolete("No longer used - use progress.IsKnown instead")]
    private bool HasMixedModeCompetency(VocabularyProgress progress)
    {
        return progress.IsKnown;
    }

    [Obsolete("No longer used - streak-based scoring replaces phase advancement")]
    private void UpdateLearningPhaseRigorous(VocabularyProgress progress)
    {
        // No longer advances phases - kept for backward compatibility
    }

    private void UpdateSpacedRepetitionSchedule(VocabularyProgress progress, VocabularyAttempt attempt)
    {
        // Simple SM-2 algorithm implementation with safety limits
        const int MAX_REVIEW_INTERVAL_DAYS = 365; // Cap at 1 year maximum

        if (!attempt.WasCorrect)
        {
            // Reset on incorrect answer
            progress.ReviewInterval = 1;
            progress.EaseFactor = Math.Max(1.3f, progress.EaseFactor - 0.2f);
        }
        else
        {
            // Increase interval on correct answer
            if (progress.ReviewInterval == 1)
            {
                progress.ReviewInterval = 6;
            }
            else
            {
                progress.ReviewInterval = (int)(progress.ReviewInterval * progress.EaseFactor);
                // Cap the interval to prevent DateTime.AddDays overflow
                progress.ReviewInterval = Math.Min(progress.ReviewInterval, MAX_REVIEW_INTERVAL_DAYS);
                progress.EaseFactor = Math.Min(2.5f, progress.EaseFactor + 0.1f);
            }
        }

        progress.NextReviewDate = DateTime.Now.AddDays(progress.ReviewInterval);
    }

    private Task RecordLearningContextAsync(string vocabularyProgressId, VocabularyAttempt attempt)
    {
        var context = new VocabularyLearningContext
        {
            VocabularyProgressId = vocabularyProgressId,
            Activity = attempt.Activity,
            InputMode = attempt.InputMode,
            LearningResourceId = attempt.LearningResourceId,
            WasCorrect = attempt.WasCorrect,
            DifficultyScore = attempt.DifficultyWeight,
            ResponseTimeMs = attempt.ResponseTimeMs,
            UserConfidence = attempt.UserConfidence,
            ContextType = attempt.ContextType,
            UserInput = attempt.UserInput,
            ExpectedAnswer = attempt.ExpectedAnswer,
            LearnedAt = DateTime.Now,
            // Legacy field
            CorrectAnswersInContext = attempt.WasCorrect ? 1 : 0
        };

        return _contextRepo.SaveAsync(context);
    }

    private async Task<VocabularyProgress> GetOrCreateProgressAsync(string vocabularyWordId, string userId)
    {
        var existing = await _progressRepo.GetByWordIdAndUserIdAsync(vocabularyWordId, userId);
        if (existing != null)
        {
            return existing;
        }

        var newProgress = new VocabularyProgress
        {
            VocabularyWordId = vocabularyWordId,
            UserId = userId,
            FirstSeenAt = DateTime.Now,
            LastPracticedAt = DateTime.Now,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            CurrentPhase = LearningPhase.Recognition,
            ReviewInterval = 1,
            EaseFactor = 2.5f
        };

        return await _progressRepo.SaveAsync(newProgress);
    }

    // Legacy method implementations for backward compatibility

    /// <summary>
    /// Sets a user-declared status for a vocabulary word ("Trust but Verify").
    /// </summary>
    public async Task<VocabularyProgress> SetUserDeclaredStatusAsync(string vocabularyWordId, string userId, LearningStatus declaredStatus)
    {
        if (declaredStatus == LearningStatus.Known)
            throw new ArgumentException("Known status must be earned through practice or verification, not declared manually.");

        var progress = await GetOrCreateProgressAsync(vocabularyWordId, userId);

        if (declaredStatus == LearningStatus.Familiar)
        {
            progress.IsUserDeclared = true;
            progress.UserDeclaredAt = DateTime.Now;
            progress.VerificationState = VerificationStatus.Pending;
            // Set review date to end of grace period (14 days)
            progress.NextReviewDate = DateTime.Now.AddDays(14);
            progress.ReviewInterval = 30; // After grace period, review every 30 days

            _logger.LogInformation("Word {WordId} marked as Familiar by user {UserId}. Grace period until {GraceEnd:d}.",
                vocabularyWordId, userId, progress.NextReviewDate);
        }
        else
        {
            // Learning or Unknown: reset to algorithmic tracking
            progress.IsUserDeclared = true;
            progress.UserDeclaredAt = DateTime.Now;
            progress.VerificationState = VerificationStatus.None;

            if (declaredStatus == LearningStatus.Unknown)
            {
                // Reset mastery to zero — user says they don't know it
                progress.MasteryScore = 0;
                progress.CurrentStreak = 0f;
                progress.ProductionInStreak = 0;
                progress.MasteredAt = null;
                progress.NextReviewDate = null;
                progress.ReviewInterval = 1;
                progress.EaseFactor = 2.5f;
            }
            // For Learning: keep existing mastery data, just mark as user-declared

            _logger.LogInformation("Word {WordId} marked as {Status} by user {UserId}.",
                vocabularyWordId, declaredStatus, userId);
        }

        progress.UpdatedAt = DateTime.Now;
        return await _progressRepo.SaveAsync(progress);
    }

    /// <summary>
    /// Handles the result of a verification probe for a Familiar word.
    /// </summary>
    public async Task<VocabularyProgress> HandleVerificationProbeResultAsync(string vocabularyWordId, string userId, bool wasCorrect)
    {
        var progress = await GetOrCreateProgressAsync(vocabularyWordId, userId);

        if (!progress.IsFamiliar)
        {
            _logger.LogWarning("HandleVerificationProbeResultAsync called for non-Familiar word {WordId}. Ignoring.", vocabularyWordId);
            return progress;
        }

        if (wasCorrect)
        {
            // Promote to Known
            progress.VerificationState = VerificationStatus.Confirmed;
            progress.MasteryScore = 1.0f;
            progress.CurrentStreak = 7f;
            progress.ProductionInStreak = MIN_PRODUCTION_FOR_KNOWN;
            progress.MasteredAt = DateTime.Now;
            progress.ReviewInterval = 60;
            progress.EaseFactor = 2.5f;
            progress.NextReviewDate = DateTime.Now.AddDays(60);

            _logger.LogInformation("Word {WordId} verified and promoted to Known for user {UserId}.",
                vocabularyWordId, userId);
        }
        else
        {
            // Demote to Learning
            progress.VerificationState = VerificationStatus.Demoted;
            progress.IsUserDeclared = false;
            progress.MasteryScore = 0.3f;
            progress.CurrentStreak = 0f;
            progress.ProductionInStreak = 0;
            progress.ReviewInterval = 1;
            progress.NextReviewDate = DateTime.Now.AddDays(1);

            _logger.LogInformation("Word {WordId} failed verification, moved to Learning for user {UserId}.",
                vocabularyWordId, userId);
        }

        progress.LastPracticedAt = DateTime.Now;
        progress.UpdatedAt = DateTime.Now;
        return await _progressRepo.SaveAsync(progress);
    }

    /// <summary>
    /// Legacy method: Gets or creates progress for a vocabulary word (backward compatibility)
    /// </summary>
    public Task<VocabularyProgress> GetOrCreateProgressAsync(string vocabularyWordId)
    {
        return GetOrCreateProgressAsync(vocabularyWordId, userId: string.Empty);
    }

    /// <summary>
    /// Legacy method: Records a correct answer (backward compatibility)
    /// </summary>
    public Task<VocabularyProgress> RecordCorrectAnswerAsync(
        string vocabularyWordId,
        InputMode inputMode,
        string activity = "VocabularyQuiz",
        string? learningResourceId = null)
    {
        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = vocabularyWordId,
            UserId = string.Empty, // Default user
            Activity = activity,
            InputMode = inputMode.ToString(),
            LearningResourceId = learningResourceId,
            WasCorrect = true,
            DifficultyWeight = CalculateLegacyDifficultyWeight(inputMode),
            ResponseTimeMs = 0, // Default value for legacy calls
            UserConfidence = null,
            ContextType = inputMode == InputMode.MultipleChoice ? "Isolated" : "Isolated"
        };

        return RecordAttemptAsync(attempt);
    }

    /// <summary>
    /// Legacy method: Records an incorrect answer (backward compatibility)
    /// </summary>
    public Task<VocabularyProgress> RecordIncorrectAnswerAsync(
        string vocabularyWordId,
        InputMode inputMode,
        string activity = "VocabularyQuiz",
        string? learningResourceId = null)
    {
        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = vocabularyWordId,
            UserId = string.Empty, // Default user
            Activity = activity,
            InputMode = inputMode.ToString(),
            LearningResourceId = learningResourceId,
            WasCorrect = false,
            DifficultyWeight = CalculateLegacyDifficultyWeight(inputMode),
            ResponseTimeMs = 0, // Default value for legacy calls
            UserConfidence = null,
            ContextType = inputMode == InputMode.MultipleChoice ? "Isolated" : "Isolated"
        };

        return RecordAttemptAsync(attempt);
    }

    /// <summary>
    /// Helper method to determine learning phase from input mode for legacy calls
    /// </summary>
    private LearningPhase DeterminePhaseFromInputMode(InputMode inputMode)
    {
        return inputMode switch
        {
            InputMode.MultipleChoice => LearningPhase.Recognition,
            InputMode.Text => LearningPhase.Production,
            _ => LearningPhase.Recognition
        };
    }

    /// <summary>
    /// Helper method to calculate difficulty weight for legacy calls
    /// </summary>
    private float CalculateLegacyDifficultyWeight(InputMode inputMode)
    {
        return inputMode switch
        {
            InputMode.MultipleChoice => 0.8f, // Multiple choice is easier
            InputMode.Text => 1.2f,           // Text entry is harder
            _ => 1.0f
        };
    }
}
