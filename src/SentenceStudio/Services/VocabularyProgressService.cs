using System.Diagnostics;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

public class VocabularyProgressService : IVocabularyProgressService
{
    private readonly VocabularyProgressRepository _progressRepo;
    private readonly VocabularyLearningContextRepository _contextRepo;
    
    // Enhanced rigorous thresholds - Captain's orders! ⚓
    private const float MASTERY_THRESHOLD = 0.85f;        // Raised from 0.8f - more rigorous!
    private const float PHASE_ADVANCE_THRESHOLD = 0.75f;  // Raised from 0.7f - must prove competency!
    private const int ROLLING_AVERAGE_COUNT = 8;          // Increased from 5 - longer memory!
    private const int MIN_ATTEMPTS_PER_PHASE = 4;         // Minimum attempts before phase advancement
    private const int MIN_CORRECT_PER_PHASE = 3;          // Minimum correct answers per phase
    private const float INCORRECT_PENALTY = 0.15f;        // Penalty for wrong answers
    private const float MIXED_MODE_REQUIREMENT = 0.7f;    // Must show competency in both modes

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
    public async Task<VocabularyProgress> GetProgressAsync(int vocabularyWordId, int userId = 1)
    {
        return await GetOrCreateProgressAsync(vocabularyWordId, userId);
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
        var progressDict = new Dictionary<int, VocabularyProgress>();
        
        foreach (var wordId in vocabularyWordIds)
        {
            var progress = await GetOrCreateProgressAsync(wordId, userId);
            progressDict[wordId] = progress;
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
        
        // Apply mixed-mode requirement penalty
        float mixedModeScore = ApplyMixedModeRequirement(penalizedScore, progress);
        
        // Add time decay factor (keep existing)
        var daysSinceLastPractice = (DateTime.Now - progress.LastPracticedAt).TotalDays;
        var timeFactor = Math.Max(0.8f, 1.0f - (float)(daysSinceLastPractice * 0.01));
        
        var finalScore = Math.Min(1.0f, mixedModeScore * timeFactor);
        
        // Debug output for Captain to monitor
        Debug.WriteLine($"🏴‍☠️ Word {progress.VocabularyWordId}: " +
                       $"Base={baseScore:F2}, Rolling={rollingScore:F2}, " +
                       $"Penalized={penalizedScore:F2}, Mixed={mixedModeScore:F2}, " +
                       $"Final={finalScore:F2}");
        
        return finalScore;
    }
    
    /// <summary>
    /// Calculate score based on phase-specific requirements
    /// </summary>
    private float CalculatePhaseSpecificScore(VocabularyProgress progress)
    {
        // Recognition phase score (multiple choice)
        float recognitionScore = 0f;
        if (progress.RecognitionAttempts >= MIN_CORRECT_PER_PHASE)
        {
            recognitionScore = Math.Min(1.0f, progress.RecognitionAccuracy);
        }
        else if (progress.RecognitionAttempts > 0)
        {
            // Partial credit but capped low until minimum attempts met
            recognitionScore = Math.Min(0.4f, progress.RecognitionAccuracy * 0.5f);
        }
        
        // Production phase score (text entry)
        float productionScore = 0f;
        if (progress.ProductionAttempts >= MIN_CORRECT_PER_PHASE)
        {
            productionScore = Math.Min(1.0f, progress.ProductionAccuracy);
        }
        else if (progress.ProductionAttempts > 0)
        {
            // Partial credit but capped low until minimum attempts met
            productionScore = Math.Min(0.4f, progress.ProductionAccuracy * 0.5f);
        }
        
        // For true mastery, need competency in BOTH phases
        if (progress.RecognitionAttempts >= MIN_CORRECT_PER_PHASE && 
            progress.ProductionAttempts >= MIN_CORRECT_PER_PHASE)
        {
            // Take the LOWER of the two scores (weakest link determines mastery)
            return Math.Min(recognitionScore, productionScore);
        }
        else if (progress.ProductionAttempts > 0)
        {
            // In production phase, weight both but require recognition foundation
            return (recognitionScore * 0.4f) + (productionScore * 0.6f);
        }
        else
        {
            // Still in recognition phase, cap at 60% until advancing
            return Math.Min(0.6f, recognitionScore);
        }
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
    /// Apply mixed-mode requirement - must show competency in BOTH modes for mastery
    /// </summary>
    private float ApplyMixedModeRequirement(float score, VocabularyProgress progress)
    {
        // For scores above 70%, start applying mixed-mode requirements
        if (score < 0.7f) return score;
        
        bool hasRecognitionCompetency = progress.RecognitionAttempts >= MIN_CORRECT_PER_PHASE && 
                                      progress.RecognitionAccuracy >= MIXED_MODE_REQUIREMENT;
        
        bool hasProductionCompetency = progress.ProductionAttempts >= MIN_CORRECT_PER_PHASE && 
                                     progress.ProductionAccuracy >= MIXED_MODE_REQUIREMENT;
        
        // If attempting mastery (80%+) but missing mixed-mode competency, cap the score
        if (score >= 0.8f && (!hasRecognitionCompetency || !hasProductionCompetency))
        {
            return Math.Min(0.75f, score); // Cap at 75% until both modes proven
        }
        
        return score;
    }
    
    /// <summary>
    /// Check if word has demonstrated competency in both recognition and production
    /// </summary>
    private bool HasMixedModeCompetency(VocabularyProgress progress)
    {
        bool hasRecognitionCompetency = progress.RecognitionAttempts >= MIN_CORRECT_PER_PHASE && 
                                      progress.RecognitionAccuracy >= MIXED_MODE_REQUIREMENT;
        
        bool hasProductionCompetency = progress.ProductionAttempts >= MIN_CORRECT_PER_PHASE && 
                                     progress.ProductionAccuracy >= MIXED_MODE_REQUIREMENT;
        
        return hasRecognitionCompetency && hasProductionCompetency;
    }
    
    /// <summary>
    /// RIGOROUS learning phase advancement - much stricter requirements!
    /// </summary>
    private void UpdateLearningPhaseRigorous(VocabularyProgress progress)
    {
        // Advance from Recognition to Production - stricter requirements
        if (progress.CurrentPhase == LearningPhase.Recognition && 
            progress.RecognitionAccuracy >= PHASE_ADVANCE_THRESHOLD &&
            progress.RecognitionAttempts >= MIN_ATTEMPTS_PER_PHASE &&
            progress.RecognitionCorrect >= MIN_CORRECT_PER_PHASE)
        {
            progress.CurrentPhase = LearningPhase.Production;
            Debug.WriteLine($"🎯 Word {progress.VocabularyWordId} advanced to Production phase! " +
                          $"Recognition: {progress.RecognitionCorrect}/{progress.RecognitionAttempts} " +
                          $"({progress.RecognitionAccuracy:P})");
        }
        // Advance from Production to Application - even stricter!
        else if (progress.CurrentPhase == LearningPhase.Production &&
                 progress.ProductionAccuracy >= PHASE_ADVANCE_THRESHOLD &&
                 progress.ProductionAttempts >= MIN_ATTEMPTS_PER_PHASE &&
                 progress.ProductionCorrect >= MIN_CORRECT_PER_PHASE &&
                 progress.RecognitionAccuracy >= MIXED_MODE_REQUIREMENT) // Must maintain recognition skills!
        {
            progress.CurrentPhase = LearningPhase.Application;
            Debug.WriteLine($"🚀 Word {progress.VocabularyWordId} advanced to Application phase! " +
                          $"Production: {progress.ProductionCorrect}/{progress.ProductionAttempts} " +
                          $"({progress.ProductionAccuracy:P})");
        }
    }
    
    private void UpdateSpacedRepetitionSchedule(VocabularyProgress progress, VocabularyAttempt attempt)
    {
        // Simple SM-2 algorithm implementation
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
                progress.EaseFactor = Math.Min(2.5f, progress.EaseFactor + 0.1f);
            }
        }
        
        progress.NextReviewDate = DateTime.Now.AddDays(progress.ReviewInterval);
    }

    private async Task RecordLearningContextAsync(int vocabularyProgressId, VocabularyAttempt attempt)
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
        
        await _contextRepo.SaveAsync(context);
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
