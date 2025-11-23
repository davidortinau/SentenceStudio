namespace SentenceStudio.Services;

public class VocabularyProgressService : IVocabularyProgressService
{
    private readonly VocabularyProgressRepository _progressRepo;
    private readonly VocabularyLearningContextRepository _contextRepo;

    // Enhanced rigorous thresholds - aligned with SLA developmental sequences! ⚓
    private const float MASTERY_THRESHOLD = 0.85f;                // Full productive mastery
    private const float RECEPTIVE_MASTERY_THRESHOLD = 0.70f;      // Recognition-only mastery
    private const float PHASE_ADVANCE_THRESHOLD = 0.75f;          // Must prove competency!
    private const int ROLLING_AVERAGE_COUNT = 8;                  // Increased from 5 - longer memory!
    private const int MIN_ATTEMPTS_PER_PHASE = 4;                 // Minimum attempts before phase advancement
    private const int MIN_CORRECT_RECOGNITION = 3;                // Recognition needs more evidence (easier task)
    private const int MIN_CORRECT_PRODUCTION = 2;                 // Production needs less evidence (harder task, more signal)
    private const float INCORRECT_PENALTY = 0.15f;                // Penalty for wrong answers

    public VocabularyProgressService(
        VocabularyProgressRepository progressRepo,
        VocabularyLearningContextRepository contextRepo)
    {
        _progressRepo = progressRepo;
        _contextRepo = contextRepo;
    }

    /// <summary>
    /// Records a vocabulary learning attempt and updates progress using RIGOROUS tracking
    /// </summary>
    public async Task<VocabularyProgress> RecordAttemptAsync(VocabularyAttempt attempt)
    {
        var progress = await GetOrCreateProgressAsync(attempt.VocabularyWordId, attempt.UserId);

        // Update total and phase-specific counts
        progress.TotalAttempts++;
        if (attempt.WasCorrect)
            progress.CorrectAttempts++;

        // Update phase-specific tracking
        UpdatePhaseMetrics(progress, attempt);

        // Update legacy fields for backward compatibility
        UpdateLegacyFields(progress, attempt);

        // Calculate new mastery score with RIGOROUS algorithm
        var newScore = await CalculateRigorousMasteryScoreAsync(progress, attempt);
        progress.MasteryScore = newScore;

        // Update phase if appropriate (now with stricter requirements)
        UpdateLearningPhaseRigorous(progress);

        // Update spaced repetition schedule
        UpdateSpacedRepetitionSchedule(progress, attempt);

        // Update timestamps
        progress.LastPracticedAt = DateTime.Now;
        if (newScore >= MASTERY_THRESHOLD && !progress.MasteredAt.HasValue && HasMixedModeCompetency(progress))
            progress.MasteredAt = DateTime.Now;

        // Save progress first
        progress = await _progressRepo.SaveAsync(progress);

        // Record detailed context
        await RecordLearningContextAsync(progress.Id, attempt);

        return progress;
    }

    /// <summary>
    /// Gets progress for a specific vocabulary word and user
    /// </summary>
    public Task<VocabularyProgress> GetProgressAsync(int vocabularyWordId, int userId = 1)
    {
        return GetOrCreateProgressAsync(vocabularyWordId, userId);
    }

    /// <summary>
    /// Gets words due for review based on spaced repetition
    /// </summary>
    public async Task<List<VocabularyProgress>> GetReviewCandidatesAsync(int userId = 1)
    {
        var allProgress = await _progressRepo.ListAsync();
        return allProgress.Where(p =>
            p.UserId == userId &&
            p.IsDueForReview &&
            !p.IsKnown).ToList();
    }

    /// <summary>
    /// Gets all progress records for a user
    /// </summary>
    public async Task<List<VocabularyProgress>> GetAllProgressAsync(int userId = 1)
    {
        var allProgress = await _progressRepo.ListAsync();
        return allProgress.Where(p => p.UserId == userId).ToList();
    }

    /// <summary>
    /// Gets progress for multiple vocabulary words and returns as dictionary
    /// </summary>
    public async Task<Dictionary<int, VocabularyProgress>> GetProgressForWordsAsync(List<int> vocabularyWordIds, int userId = 1)
    {
        // Get existing progress records in a single query
        var existingProgress = await _progressRepo.ListAsync();
        var existingDict = existingProgress
            .Where(p => vocabularyWordIds.Contains(p.VocabularyWordId) && p.UserId == userId)
            .ToDictionary(p => p.VocabularyWordId, p => p);

        var progressDict = new Dictionary<int, VocabularyProgress>();

        foreach (var wordId in vocabularyWordIds)
        {
            if (existingDict.TryGetValue(wordId, out var existingEntry))
            {
                progressDict[wordId] = existingEntry;
            }
            else
            {
                // Only create new progress records if needed
                var newProgress = await GetOrCreateProgressAsync(wordId, userId);
                progressDict[wordId] = newProgress;
            }
        }

        return progressDict;
    }

    private void UpdatePhaseMetrics(VocabularyProgress progress, VocabularyAttempt attempt)
    {
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
    }

    private void UpdateLegacyFields(VocabularyProgress progress, VocabularyAttempt attempt)
    {
        // Update legacy fields for backward compatibility
        if (attempt.InputMode == "MultipleChoice")
        {
            if (attempt.WasCorrect)
                progress.MultipleChoiceCorrect++;
        }
        else if (attempt.InputMode == "Text")
        {
            if (attempt.WasCorrect)
                progress.TextEntryCorrect++;
        }

        // Update promoted status based on phase
        progress.IsPromoted = progress.CurrentPhase >= LearningPhase.Production;

        // Update completed status based on mastery
        progress.IsCompleted = progress.MasteryScore >= MASTERY_THRESHOLD && HasMixedModeCompetency(progress);
    }

    /// <summary>
    /// RIGOROUS mastery score calculation - Captain's enhanced algorithm! 🏴‍☠️
    /// </summary>
    private async Task<float> CalculateRigorousMasteryScoreAsync(VocabularyProgress progress, VocabularyAttempt attempt)
    {
        // Get last N attempts from context (increased from 5 to 8)
        var recentAttempts = await _contextRepo.GetRecentAttemptsAsync(
            progress.Id,
            ROLLING_AVERAGE_COUNT
        );

        // If this is the very first attempt, be much more conservative
        if (!recentAttempts.Any())
            return attempt.WasCorrect ? 0.1f : 0.0f; // Reduced from 0.2f to 0.1f

        // Calculate base score using phase-specific accuracy requirements
        float baseScore = CalculatePhaseSpecificScore(progress);

        // Apply weighted rolling average with penalties
        float rollingScore = CalculateWeightedRollingAverage(recentAttempts, attempt);

        // Combine base score and rolling average (weighted toward requiring both phases)
        float combinedScore = (baseScore * 0.4f) + (rollingScore * 0.6f);

        // Apply incorrect answer penalties
        float penalizedScore = ApplyIncorrectAnswerPenalties(combinedScore, recentAttempts);

        // Add time decay factor (keep existing)
        var daysSinceLastPractice = (DateTime.Now - progress.LastPracticedAt).TotalDays;
        var timeFactor = Math.Max(0.8f, 1.0f - (float)(daysSinceLastPractice * 0.01));

        var finalScore = Math.Min(1.0f, penalizedScore * timeFactor);

        // Debug output for Captain to monitor
        Debug.WriteLine($"🏴‍☠️ Word {progress.VocabularyWordId}: " +
                       $"Base={baseScore:F2}, Rolling={rollingScore:F2}, " +
                       $"Penalized={penalizedScore:F2}, " +
                       $"Final={finalScore:F2}");

        return finalScore;
    }

    /// <summary>
    /// Calculate score based on phase-specific requirements.
    /// LEARNING SCIENCE: Production implies recognition, but recognition doesn't imply production.
    /// This implements developmental sequence from Laufer & Nation (1999):
    /// - Receptive knowledge develops first (0.70-0.85)
    /// - Productive knowledge emerges later (0.85-1.0)
    /// </summary>
    private float CalculatePhaseSpecificScore(VocabularyProgress progress)
    {
        // Calculate recognition score
        float recognitionScore = CalculateRecognitionScore(progress);
        
        // Calculate production score
        float productionScore = CalculateProductionScore(progress);
        
        // CASE 1: Has demonstrated production competency
        // Production IMPLIES recognition (can't produce what you don't recognize)
        if (productionScore >= RECEPTIVE_MASTERY_THRESHOLD && 
            progress.ProductionAttempts >= MIN_CORRECT_PRODUCTION &&
            progress.ProductionCorrect >= MIN_CORRECT_PRODUCTION)
        {
            // Production evidence is strongest - give full credit
            // Even if recognition score is lower (maybe they haven't seen it in recognition contexts)
            return productionScore;
        }
        
        // CASE 2: Has strong recognition, with some production evidence
        if (recognitionScore >= RECEPTIVE_MASTERY_THRESHOLD && 
            progress.RecognitionAttempts >= MIN_CORRECT_RECOGNITION)
        {
            if (progress.ProductionAttempts >= MIN_CORRECT_PRODUCTION && 
                progress.ProductionCorrect >= MIN_CORRECT_PRODUCTION)
            {
                // Has both - blend with emphasis on production (harder skill)
                return Math.Max(recognitionScore, 
                               (recognitionScore * 0.4f) + (productionScore * 0.6f));
            }
            else if (progress.ProductionAttempts > 0)
            {
                // Some production attempts but not enough evidence yet
                // Give receptive mastery credit with slight production boost
                return Math.Min(0.85f, recognitionScore + (productionScore * 0.15f));
            }
            else
            {
                // Recognition-only mastery - cap at 0.85 to leave room for production growth
                return Math.Min(0.85f, recognitionScore);
            }
        }
        
        // CASE 3: Building competency - not enough attempts yet
        if (progress.RecognitionAttempts > 0 || progress.ProductionAttempts > 0)
        {
            // Take the better of the two scores but cap low until evidence threshold met
            float bestScore = Math.Max(recognitionScore, productionScore);
            return Math.Min(0.60f, bestScore);
        }
        else
        {
            // No attempts yet
            return 0f;
        }
    }
    
    /// <summary>
    /// Calculate recognition score with proper thresholds
    /// </summary>
    private float CalculateRecognitionScore(VocabularyProgress progress)
    {
        if (progress.RecognitionAttempts >= MIN_CORRECT_RECOGNITION)
        {
            // Full credit based on accuracy
            return Math.Min(1.0f, progress.RecognitionAccuracy);
        }
        else if (progress.RecognitionAttempts > 0)
        {
            // Partial credit but capped until minimum evidence threshold
            return Math.Min(0.5f, progress.RecognitionAccuracy * 0.7f);
        }
        return 0f;
    }
    
    /// <summary>
    /// Calculate production score with proper thresholds
    /// </summary>
    private float CalculateProductionScore(VocabularyProgress progress)
    {
        if (progress.ProductionAttempts >= MIN_CORRECT_PRODUCTION)
        {
            // Full credit - production is harder, so fewer attempts needed
            return Math.Min(1.0f, progress.ProductionAccuracy);
        }
        else if (progress.ProductionAttempts > 0)
        {
            // Partial credit but capped until minimum evidence threshold
            // More generous than recognition because each production attempt is harder
            return Math.Min(0.6f, progress.ProductionAccuracy * 0.8f);
        }
        return 0f;
    }

    /// <summary>
    /// Calculate weighted rolling average of recent attempts
    /// </summary>
    private float CalculateWeightedRollingAverage(List<VocabularyLearningContext> recentAttempts, VocabularyAttempt currentAttempt)
    {
        // Add current attempt to the mix
        var allAttempts = recentAttempts.ToList();
        allAttempts.Insert(0, new VocabularyLearningContext
        {
            WasCorrect = currentAttempt.WasCorrect,
            DifficultyScore = currentAttempt.DifficultyWeight,
            UserConfidence = currentAttempt.UserConfidence,
            LearnedAt = DateTime.Now
        });

        float totalWeight = 0;
        float weightedScore = 0;
        int index = 0;

        foreach (var context in allAttempts.OrderByDescending(c => c.LearnedAt))
        {
            // More recent attempts have higher weight, but less dramatic falloff
            float recencyWeight = 1.0f - (index * 0.1f); // Reduced from 0.15f
            float difficultyWeight = Math.Max(0.5f, context.DifficultyScore);
            float confidenceBoost = context.UserConfidence ?? 1.0f;

            float weight = recencyWeight * difficultyWeight * confidenceBoost;
            weightedScore += (context.WasCorrect ? 1.0f : 0.0f) * weight;
            totalWeight += weight;
            index++;

            if (index >= ROLLING_AVERAGE_COUNT) break;
        }

        return totalWeight > 0 ? (weightedScore / totalWeight) : 0f;
    }

    /// <summary>
    /// Apply penalties for incorrect answers - Captain's discipline! ⚓
    /// </summary>
    private float ApplyIncorrectAnswerPenalties(float baseScore, List<VocabularyLearningContext> recentAttempts)
    {
        // Count recent incorrect answers
        var recentIncorrect = recentAttempts.Count(a => !a.WasCorrect);

        // Apply cumulative penalty for recent mistakes
        float penalty = recentIncorrect * INCORRECT_PENALTY;

        // Extra penalty for consecutive wrong answers
        var consecutiveWrong = 0;
        foreach (var attempt in recentAttempts.OrderByDescending(a => a.LearnedAt))
        {
            if (!attempt.WasCorrect)
                consecutiveWrong++;
            else
                break;
        }

        if (consecutiveWrong > 1)
        {
            penalty += (consecutiveWrong - 1) * 0.1f; // Additional penalty for consecutive errors
        }

        return Math.Max(0f, baseScore - penalty);
    }

    /// <summary>
    /// Check if word has demonstrated competency in both recognition and production
    /// Used for marking MasteredAt timestamp
    /// </summary>
    private bool HasMixedModeCompetency(VocabularyProgress progress)
    {
        bool hasRecognitionCompetency = progress.RecognitionAttempts >= MIN_CORRECT_RECOGNITION &&
                                      progress.RecognitionAccuracy >= RECEPTIVE_MASTERY_THRESHOLD;

        bool hasProductionCompetency = progress.ProductionAttempts >= MIN_CORRECT_PRODUCTION &&
                                     progress.ProductionAccuracy >= RECEPTIVE_MASTERY_THRESHOLD;

        return hasRecognitionCompetency && hasProductionCompetency;
    }

    /// <summary>
    /// RIGOROUS learning phase advancement - much stricter requirements!
    /// </summary>
    /// <summary>
    /// RIGOROUS learning phase advancement - SLA-aligned requirements!
    /// Recognition → Production → Application
    /// </summary>
    private void UpdateLearningPhaseRigorous(VocabularyProgress progress)
    {
        // Advance from Recognition to Production
        if (progress.CurrentPhase == LearningPhase.Recognition &&
            progress.RecognitionAccuracy >= PHASE_ADVANCE_THRESHOLD &&
            progress.RecognitionAttempts >= MIN_ATTEMPTS_PER_PHASE &&
            progress.RecognitionCorrect >= MIN_CORRECT_RECOGNITION)
        {
            progress.CurrentPhase = LearningPhase.Production;
            Debug.WriteLine($"🎯 Word {progress.VocabularyWordId} advanced to Production phase! " +
                          $"Recognition: {progress.RecognitionCorrect}/{progress.RecognitionAttempts} " +
                          $"({progress.RecognitionAccuracy:P})");
        }
        // Advance from Production to Application
        else if (progress.CurrentPhase == LearningPhase.Production &&
                 progress.ProductionAccuracy >= PHASE_ADVANCE_THRESHOLD &&
                 progress.ProductionAttempts >= MIN_CORRECT_PRODUCTION &&
                 progress.ProductionCorrect >= MIN_CORRECT_PRODUCTION &&
                 progress.RecognitionAccuracy >= RECEPTIVE_MASTERY_THRESHOLD) // Must maintain recognition skills!
        {
            progress.CurrentPhase = LearningPhase.Application;
            Debug.WriteLine($"🚀 Word {progress.VocabularyWordId} advanced to Application phase! " +
                          $"Production: {progress.ProductionCorrect}/{progress.ProductionAttempts} " +
                          $"({progress.ProductionAccuracy:P})");
        }
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

    private Task RecordLearningContextAsync(int vocabularyProgressId, VocabularyAttempt attempt)
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

    private async Task<VocabularyProgress> GetOrCreateProgressAsync(int vocabularyWordId, int userId)
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

    // Legacy method implementations for backward compatibility ⚓

    /// <summary>
    /// Legacy method: Gets or creates progress for a vocabulary word (backward compatibility)
    /// </summary>
    public Task<VocabularyProgress> GetOrCreateProgressAsync(int vocabularyWordId)
    {
        return GetOrCreateProgressAsync(vocabularyWordId, userId: 1);
    }

    /// <summary>
    /// Legacy method: Records a correct answer (backward compatibility)
    /// </summary>
    public Task<VocabularyProgress> RecordCorrectAnswerAsync(
        int vocabularyWordId,
        InputMode inputMode,
        string activity = "VocabularyQuiz",
        int? learningResourceId = null)
    {
        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = vocabularyWordId,
            UserId = 1, // Default user
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
        int vocabularyWordId,
        InputMode inputMode,
        string activity = "VocabularyQuiz",
        int? learningResourceId = null)
    {
        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = vocabularyWordId,
            UserId = 1, // Default user
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
