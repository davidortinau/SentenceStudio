using System.Diagnostics;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

public class VocabularyProgressService : IVocabularyProgressService
{
    private readonly VocabularyProgressRepository _progressRepo;
    private readonly VocabularyLearningContextRepository _contextRepo;
    
    // Configurable thresholds
    private const float MASTERY_THRESHOLD = 0.8f;
    private const float PHASE_ADVANCE_THRESHOLD = 0.7f;
    private const int ROLLING_AVERAGE_COUNT = 5; // Last N attempts for score calculation

    public VocabularyProgressService(
        VocabularyProgressRepository progressRepo,
        VocabularyLearningContextRepository contextRepo)
    {
        _progressRepo = progressRepo;
        _contextRepo = contextRepo;
    }

    /// <summary>
    /// Records a vocabulary learning attempt and updates progress using enhanced tracking
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
        
        // Calculate new mastery score
        var newScore = await CalculateMasteryScoreAsync(progress, attempt);
        progress.MasteryScore = newScore;
        
        // Update phase if appropriate
        UpdateLearningPhase(progress);
        
        // Update spaced repetition schedule
        UpdateSpacedRepetitionSchedule(progress, attempt);
        
        // Update timestamps
        progress.LastPracticedAt = DateTime.Now;
        if (newScore >= MASTERY_THRESHOLD && !progress.MasteredAt.HasValue)
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
            p.Status != LearningStatus.Known &&
            p.NextReviewDate.HasValue &&
            p.NextReviewDate.Value <= DateTime.Now
        ).OrderBy(p => p.NextReviewDate).ToList();
    }
    
    /// <summary>
    /// Gets all progress records for a user
    /// </summary>
    public async Task<List<VocabularyProgress>> GetAllProgressAsync(int userId = 1)
    {
        var allProgress = await _progressRepo.ListAsync();
        return allProgress.Where(p => p.UserId == userId).ToList();
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
        if (attempt.InputMode == "MultipleChoice" && attempt.WasCorrect)
        {
            progress.MultipleChoiceCorrect++;
            if (progress.MultipleChoiceCorrect >= 3 && !progress.IsPromoted)
            {
                progress.IsPromoted = true;
            }
        }
        else if (attempt.InputMode == "TextEntry" && attempt.WasCorrect)
        {
            progress.TextEntryCorrect++;
            if (progress.TextEntryCorrect >= 3 && !progress.IsCompleted)
            {
                progress.IsCompleted = true;
            }
        }
    }
    
    private async Task<float> CalculateMasteryScoreAsync(VocabularyProgress progress, VocabularyAttempt attempt)
    {
        // Get last N attempts from context
        var recentAttempts = await _contextRepo.GetRecentAttemptsAsync(
            progress.Id, 
            ROLLING_AVERAGE_COUNT
        );
        
        if (!recentAttempts.Any())
            return attempt.WasCorrect ? 0.2f : 0.0f;
        
        // Calculate weighted rolling average
        float totalWeight = 0;
        float weightedScore = 0;
        int index = 0;
        
        foreach (var context in recentAttempts.OrderByDescending(c => c.LearnedAt))
        {
            // More recent attempts have higher weight
            float recencyWeight = 1.0f - (index * 0.15f);
            float difficultyWeight = context.DifficultyScore;
            float confidenceBoost = context.UserConfidence ?? 1.0f;
            
            float weight = recencyWeight * difficultyWeight * confidenceBoost;
            weightedScore += (context.WasCorrect ? 1.0f : 0.0f) * weight;
            totalWeight += weight;
            index++;
        }
        
        // Add time decay factor
        var daysSinceLastPractice = (DateTime.Now - progress.LastPracticedAt).TotalDays;
        var timeFactor = Math.Max(0.8f, 1.0f - (float)(daysSinceLastPractice * 0.01));
        
        return Math.Min(1.0f, (weightedScore / totalWeight) * timeFactor);
    }
    
    private void UpdateLearningPhase(VocabularyProgress progress)
    {
        // Advance phases based on phase-specific performance
        if (progress.CurrentPhase == LearningPhase.Recognition && 
            progress.RecognitionAccuracy >= PHASE_ADVANCE_THRESHOLD &&
            progress.RecognitionAttempts >= 3)
        {
            progress.CurrentPhase = LearningPhase.Production;
        }
        else if (progress.CurrentPhase == LearningPhase.Production &&
                 progress.ProductionAccuracy >= PHASE_ADVANCE_THRESHOLD &&
                 progress.ProductionAttempts >= 3)
        {
            progress.CurrentPhase = LearningPhase.Application;
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

    // Legacy methods for backward compatibility
    
    /// <summary>
    /// Gets or creates progress for a vocabulary word (backward compatibility)
    /// </summary>
    public async Task<VocabularyProgress> GetOrCreateProgressAsync(int vocabularyWordId)
    {
        return await GetOrCreateProgressAsync(vocabularyWordId, userId: 1);
    }

    /// <summary>
    /// Gets progress for multiple vocabulary words (backward compatibility)
    /// </summary>
    public async Task<Dictionary<int, VocabularyProgress>> GetProgressForWordsAsync(List<int> vocabularyWordIds)
    {
        var progresses = await _progressRepo.GetByWordIdsAsync(vocabularyWordIds);
        
        var result = new Dictionary<int, VocabularyProgress>();
        
        foreach (var wordId in vocabularyWordIds)
        {
            var progress = progresses.FirstOrDefault(p => p.VocabularyWordId == wordId);
            if (progress == null)
            {
                // Create new progress record for words we haven't seen before
                progress = await GetOrCreateProgressAsync(wordId);
            }
            result[wordId] = progress;
        }
        
        return result;
    }

    /// <summary>
    /// Records a correct answer and updates global progress (backward compatibility)
    /// </summary>
    public async Task<VocabularyProgress> RecordCorrectAnswerAsync(
        int vocabularyWordId, 
        InputMode inputMode, 
        string activity = "VocabularyQuiz", 
        int? learningResourceId = null)
    {
        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = vocabularyWordId,
            UserId = 1, // Default user for backward compatibility
            Activity = activity,
            InputMode = inputMode.ToString(),
            WasCorrect = true,
            DifficultyWeight = inputMode == InputMode.Text ? 1.2f : 0.8f,
            ContextType = "Isolated",
            LearningResourceId = learningResourceId,
            ResponseTimeMs = 0 // Not tracked in legacy method
        };
        
        return await RecordAttemptAsync(attempt);
    }

    /// <summary>
    /// Records an incorrect answer (backward compatibility)
    /// </summary>
    public async Task<VocabularyProgress> RecordIncorrectAnswerAsync(
        int vocabularyWordId, 
        InputMode inputMode, 
        string activity = "VocabularyQuiz", 
        int? learningResourceId = null)
    {
        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = vocabularyWordId,
            UserId = 1, // Default user for backward compatibility
            Activity = activity,
            InputMode = inputMode.ToString(),
            WasCorrect = false,
            DifficultyWeight = inputMode == InputMode.Text ? 1.2f : 0.8f,
            ContextType = "Isolated",
            LearningResourceId = learningResourceId,
            ResponseTimeMs = 0 // Not tracked in legacy method
        };
        
        return await RecordAttemptAsync(attempt);
    }

    /// <summary>
    /// Records a learning context for detailed analytics (backward compatibility)
    /// </summary>
    public async Task RecordLearningContextAsync(
        int vocabularyProgressId, 
        string activity, 
        string inputMode, 
        int? learningResourceId = null,
        bool wasCorrect = true)
    {
        var context = new VocabularyLearningContext
        {
            VocabularyProgressId = vocabularyProgressId,
            Activity = activity,
            InputMode = inputMode,
            LearningResourceId = learningResourceId,
            LearnedAt = DateTime.Now,
            WasCorrect = wasCorrect,
            DifficultyScore = 0.5f, // Default difficulty
            ResponseTimeMs = 0,
            ContextType = "Isolated",
            // Legacy field
            CorrectAnswersInContext = wasCorrect ? 1 : 0
        };
        
        await _contextRepo.SaveAsync(context);
    }

    /// <summary>
    /// Gets learning statistics for a vocabulary word (backward compatibility)
    /// </summary>
    public async Task<VocabularyLearningStats> GetLearningStatsAsync(int vocabularyWordId)
    {
        var progress = await _progressRepo.GetByWordIdAsync(vocabularyWordId);
        if (progress == null)
        {
            return new VocabularyLearningStats
            {
                VocabularyWordId = vocabularyWordId,
                IsNew = true
            };
        }
        
        var contexts = await _contextRepo.GetByProgressIdAsync(progress.Id);
        
        return new VocabularyLearningStats
        {
            VocabularyWordId = vocabularyWordId,
            Progress = progress,
            TotalPracticeSessions = contexts.Count,
            ActivitiesUsed = contexts.Select(c => c.Activity).Distinct().ToList(),
            ResourcesUsed = contexts.Where(c => c.LearningResource != null)
                                  .Select(c => c.LearningResource!.Title ?? "Unknown")
                                  .Distinct()
                                  .ToList(),
            FirstSeenAt = progress.FirstSeenAt,
            LastPracticedAt = progress.LastPracticedAt,
            IsNew = false
        };
    }

    /// <summary>
    /// Gets overall learning statistics across all words (backward compatibility)
    /// </summary>
    public async Task<OverallLearningStats> GetOverallStatsAsync()
    {
        var allProgress = await _progressRepo.ListAsync();
        
        return new OverallLearningStats
        {
            TotalWords = allProgress.Count,
            UnknownWords = allProgress.Count(p => p.IsUnknown),
            LearningWords = allProgress.Count(p => p.IsLearning),
            KnownWords = allProgress.Count(p => p.IsKnown),
            TotalCorrectAnswers = allProgress.Sum(p => p.MultipleChoiceCorrect + p.TextEntryCorrect)
        };
    }
}

public class VocabularyLearningStats
{
    public int VocabularyWordId { get; set; }
    public VocabularyProgress? Progress { get; set; }
    public int TotalPracticeSessions { get; set; }
    public List<string> ActivitiesUsed { get; set; } = new();
    public List<string> ResourcesUsed { get; set; } = new();
    public DateTime? FirstSeenAt { get; set; }
    public DateTime? LastPracticedAt { get; set; }
    public bool IsNew { get; set; }
}

public class OverallLearningStats
{
    public int TotalWords { get; set; }
    public int UnknownWords { get; set; }
    public int LearningWords { get; set; }
    public int KnownWords { get; set; }
    public int TotalCorrectAnswers { get; set; }
}
