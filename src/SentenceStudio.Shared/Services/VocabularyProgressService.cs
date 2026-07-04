using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

public class VocabularyProgressService : IVocabularyProgressService
{
    private readonly VocabularyProgressRepository _progressRepo;
    private readonly VocabularyLearningContextRepository _contextRepo;
    private readonly ILogger<VocabularyProgressService> _logger;
    private readonly IPreferencesService? _preferences;
    private readonly IServiceProvider _serviceProvider;

    // Streak-based scoring math now lives in VocabularyMasteryCalculator — the single source of truth
    // shared by the live attempt path, duplicate-merge recalculation, and the word-page mastery chart.
    // The constant below is still referenced by HandleVerificationProbeResultAsync; it aliases the
    // calculator's value so the two can never drift.
    private const int MIN_PRODUCTION_FOR_KNOWN = VocabularyMasteryCalculator.MIN_PRODUCTION_FOR_KNOWN;
    private const string VocabularyQuizActivity = "VocabularyQuiz";

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
        IServiceProvider serviceProvider,
        IPreferencesService? preferences = null)
    {
        _progressRepo = progressRepo;
        _contextRepo = contextRepo;
        _logger = logger;
        _serviceProvider = serviceProvider;
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
    /// Records a vocabulary learning attempt using NEW streak-based scoring
    /// </summary>
    public async Task<VocabularyProgress> RecordAttemptAsync(VocabularyAttempt attempt)
    {
        var progress = await GetOrCreateProgressAsync(attempt.VocabularyWordId, attempt.UserId);

        // Capture mastered state so the mastery transition can be logged without the pure calculator
        // needing a logger dependency.
        bool wasMasteredBefore = progress.MasteredAt.HasValue;

        // Core streak / mastery / spaced-repetition scoring. This is shared verbatim with duplicate-merge
        // recalculation and the word-page mastery chart via VocabularyMasteryCalculator so the live path,
        // a merge replay, and the rendered curve can never disagree.
        VocabularyMasteryCalculator.ApplyAttempt(progress, attempt, DateTime.Now);

        if (!wasMasteredBefore && progress.MasteredAt.HasValue)
        {
            _logger.LogInformation(
                "Word {WordId} mastered. Mastery={Mastery:F2}, ProdInStreak={ProdStreak}. Next review in {Interval} days.",
                progress.VocabularyWordId, progress.MasteryScore, progress.ProductionInStreak, progress.ReviewInterval);
        }

        // LEGACY: Update old phase-specific fields for backward compatibility during migration
#pragma warning disable CS0618
        UpdatePhaseMetrics(progress, attempt);
        UpdateLegacyFields(progress, attempt);
#pragma warning restore CS0618
        UpdateQuizDemonstrationCounters(progress, attempt);

        // Save progress
        progress = await _progressRepo.SaveAsync(progress);

        // CASCADE: Passive exposure for phrase/sentence constituents
        // Runs AFTER phrase's own mastery commits — best-effort per constituent
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var word = await db.VocabularyWords
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == attempt.VocabularyWordId);

            if (word != null &&
                (word.LexicalUnitType == LexicalUnitType.Phrase ||
                 word.LexicalUnitType == LexicalUnitType.Sentence))
            {
                var constituents = await db.PhraseConstituents
                    .AsNoTracking()
                    .Where(pc => pc.PhraseWordId == word.Id && pc.ConstituentWordId != null)
                    .Select(pc => pc.ConstituentWordId!)
                    .ToListAsync();

                _logger.LogInformation(
                    "PhraseCascade start: PhraseId={PhraseId} UserId={UserId} ConstituentCount={Count}",
                    word.Id, attempt.UserId, constituents.Count);

                // Tag cascaded activity with correctness suffix for analytics
                var correctnessSuffix = attempt.WasCorrect ? "" : ":Incorrect";
                var cascadeActivity = $"PhraseCascade:{attempt.Activity}{correctnessSuffix}";

                foreach (var constituentId in constituents)
                {
                    try
                    {
                        // Defense in depth: ensure progress row exists before passive exposure
                        await GetOrCreateProgressAsync(constituentId, attempt.UserId);
                        await RecordPassiveExposureAsync(constituentId, attempt.UserId, cascadeActivity);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "PhraseCascade constituent exposure failed. PhraseId={PhraseId} ConstituentId={ConstituentId} UserId={UserId}",
                            word.Id, constituentId, attempt.UserId);
                        // swallow — best effort
                    }
                }
            }
        }

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
            {
                progress.MultipleChoiceCorrect++;
            }
        }
        else if (attempt.InputMode == "Text" || attempt.InputMode == "TextEntry")
        {
            if (attempt.WasCorrect)
            {
                progress.TextEntryCorrect++;
            }
        }

        // Update promoted status based on MasteryScore (new logic)
        progress.IsPromoted = progress.MasteryScore >= 0.50f;

        // Update completed status based on IsKnown (new logic)
        progress.IsCompleted = progress.IsKnown;
#pragma warning restore CS0618
    }

    private static void UpdateQuizDemonstrationCounters(VocabularyProgress progress, VocabularyAttempt attempt)
    {
        if (!attempt.WasCorrect || attempt.Activity != VocabularyQuizActivity)
            return;

        if (attempt.InputMode == "MultipleChoice")
            progress.QuizRecognitionDemonstrations++;
        else if (attempt.InputMode == "Text" || attempt.InputMode == "TextEntry")
            progress.QuizProductionDemonstrations++;
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

    /// <summary>
    /// Persists updated SRS fields on an existing progress record.
    /// Used for IsKnown re-qualification with shortened review intervals (spec §4.3.1).
    /// </summary>
    public async Task UpdateProgressAsync(VocabularyProgress progress)
    {
        progress.UpdatedAt = DateTime.Now;
        await _progressRepo.SaveAsync(progress);
    }

    /// <summary>
    /// Scores all tracked vocabulary words found in an AI grading response.
    /// Each word in VocabularyAnalysis is matched against the user's vocabulary
    /// and scored independently via RecordAttemptAsync.
    /// Verification probes are collected during the loop and fired after all scoring completes.
    /// </summary>
    public async Task<List<VocabScoringResult>> ExtractAndScoreVocabularyAsync(
        List<VocabularyAnalysis>? vocabularyAnalysis,
        List<VocabularyWord> userVocabulary,
        string userId,
        string activity,
        float difficultyWeight,
        float? penaltyOverride = null)
    {
        if (vocabularyAnalysis == null || !vocabularyAnalysis.Any())
            return new List<VocabScoringResult>();

        // Deduplicate by DictionaryForm — first occurrence wins
        var deduplicated = vocabularyAnalysis
            .Where(v => !string.IsNullOrEmpty(v.DictionaryForm))
            .DistinctBy(v => v.DictionaryForm)
            .ToList();

        var results = new List<VocabScoringResult>();
        var verificationProbes = new List<(string WordId, string UserId, bool WasCorrect)>();

        foreach (var vocabItem in deduplicated)
        {
            var matched = userVocabulary.FirstOrDefault(v =>
                v.TargetLanguageTerm.Equals(vocabItem.DictionaryForm, StringComparison.OrdinalIgnoreCase));

            if (matched == null)
                continue;

            var attempt = new VocabularyAttempt
            {
                VocabularyWordId = matched.Id,
                UserId = userId,
                Activity = activity,
                InputMode = InputMode.Text.ToString(),
                WasCorrect = vocabItem.UsageCorrect,
                DifficultyWeight = difficultyWeight,
                PenaltyOverride = penaltyOverride,
                ContextType = "Application",
                UserInput = vocabItem.UsedForm ?? vocabItem.DictionaryForm,
                ExpectedAnswer = vocabItem.DictionaryForm
            };

            var updatedProgress = await RecordAttemptAsync(attempt);

            results.Add(new VocabScoringResult
            {
                Word = matched,
                UpdatedProgress = updatedProgress,
                WasCorrect = vocabItem.UsageCorrect,
                UsedForm = vocabItem.UsedForm,
                UsageExplanation = vocabItem.UsageExplanation
            });

            // Collect verification probes — do NOT fire inline
            if (updatedProgress.IsFamiliar)
            {
                verificationProbes.Add((matched.Id, userId, vocabItem.UsageCorrect));
            }
        }

        // Handle verification probes AFTER all scoring completes
        foreach (var (wordId, uid, wasCorrect) in verificationProbes)
        {
            await HandleVerificationProbeResultAsync(wordId, uid, wasCorrect);
        }

        _logger.LogInformation("ExtractAndScoreVocabulary: {Activity} scored {Count} words for user {UserId}",
            activity, results.Count, userId);

        return results;
    }

    /// <summary>
    /// Records a passive exposure (e.g., Reading word lookup).
    /// Does NOT call RecordAttemptAsync — no streak/mastery change.
    /// Only creates a VocabularyLearningContext entry for analytics
    /// and updates LastExposedAt / ExposureCount.
    /// </summary>
    public async Task RecordPassiveExposureAsync(
        string vocabularyWordId, string userId, string activity)
    {
        var progress = await GetOrCreateProgressAsync(vocabularyWordId, userId);

        var context = new VocabularyLearningContext
        {
            VocabularyProgressId = progress.Id,
            Activity = activity,
            InputMode = "Passive",
            WasCorrect = true,
            DifficultyScore = 0f,
            ContextType = "Exposure",
            LearnedAt = DateTime.Now
        };

        await _contextRepo.SaveAsync(context);

        // Update passive exposure tracking — NOT LastPracticedAt (SRS uses that)
        progress.LastExposedAt = DateTime.Now;
        progress.ExposureCount++;
        progress.UpdatedAt = DateTime.Now;
        await _progressRepo.SaveAsync(progress);

        _logger.LogDebug("Passive exposure recorded: Word {WordId}, User {UserId}, Activity {Activity}, ExposureCount {Count}",
            vocabularyWordId, userId, activity, progress.ExposureCount);
    }
}

/// <summary>
/// Result of scoring a single vocabulary word from an AI grading response.
/// </summary>
public class VocabScoringResult
{
    public VocabularyWord Word { get; set; } = null!;
    public VocabularyProgress UpdatedProgress { get; set; } = null!;
    public bool WasCorrect { get; set; }
    public string? UsedForm { get; set; }
    public string? UsageExplanation { get; set; }
}
