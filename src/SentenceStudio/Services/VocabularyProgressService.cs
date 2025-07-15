using System.Diagnostics;

namespace SentenceStudio.Services;

public class VocabularyProgressService
{
    private readonly VocabularyProgressRepository _progressRepo;
    private readonly VocabularyLearningContextRepository _contextRepo;

    public VocabularyProgressService(
        VocabularyProgressRepository progressRepo,
        VocabularyLearningContextRepository contextRepo)
    {
        _progressRepo = progressRepo;
        _contextRepo = contextRepo;
    }

    /// <summary>
    /// Gets or creates progress for a vocabulary word
    /// </summary>
    public async Task<VocabularyProgress> GetOrCreateProgressAsync(int vocabularyWordId)
    {
        return await _progressRepo.GetOrCreateAsync(vocabularyWordId);
    }

    /// <summary>
    /// Gets progress for multiple vocabulary words
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
                progress = await _progressRepo.GetOrCreateAsync(wordId);
            }
            result[wordId] = progress;
        }
        
        return result;
    }

    /// <summary>
    /// Records a correct answer and updates global progress
    /// </summary>
    public async Task<VocabularyProgress> RecordCorrectAnswerAsync(
        int vocabularyWordId, 
        InputMode inputMode, 
        string activity = "VocabularyQuiz", 
        int? learningResourceId = null)
    {
        var progress = await GetOrCreateProgressAsync(vocabularyWordId);
        
        // Update global progress counters
        if (inputMode == InputMode.MultipleChoice)
        {
            progress.MultipleChoiceCorrect++;
            if (progress.MultipleChoiceCorrect >= 3 && !progress.IsPromoted)
            {
                progress.IsPromoted = true;
                Debug.WriteLine($"Word {vocabularyWordId} promoted to text entry after {progress.MultipleChoiceCorrect} correct MC answers");
            }
        }
        else if (inputMode == InputMode.Text)
        {
            progress.TextEntryCorrect++;
            if (progress.TextEntryCorrect >= 3 && !progress.IsCompleted)
            {
                progress.IsCompleted = true;
                Debug.WriteLine($"Word {vocabularyWordId} completed after {progress.TextEntryCorrect} correct text answers");
            }
        }
        
        progress.LastPracticedAt = DateTime.Now;
        
        // Save progress first
        progress = await _progressRepo.SaveAsync(progress);
        
        // Record learning context for analytics
        await RecordLearningContextAsync(
            progress.Id, 
            activity, 
            inputMode.ToString(), 
            learningResourceId);
        
        return progress;
    }

    /// <summary>
    /// Records an incorrect answer (for analytics)
    /// </summary>
    public async Task<VocabularyProgress> RecordIncorrectAnswerAsync(
        int vocabularyWordId, 
        InputMode inputMode, 
        string activity = "VocabularyQuiz", 
        int? learningResourceId = null)
    {
        var progress = await GetOrCreateProgressAsync(vocabularyWordId);
        progress.LastPracticedAt = DateTime.Now;
        
        // Save progress
        progress = await _progressRepo.SaveAsync(progress);
        
        // Record learning context (even for incorrect answers, for analytics)
        await RecordLearningContextAsync(
            progress.Id, 
            activity, 
            inputMode.ToString(), 
            learningResourceId,
            wasCorrect: false);
        
        return progress;
    }

    /// <summary>
    /// Records a learning context for detailed analytics
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
            CorrectAnswersInContext = wasCorrect ? 1 : 0
        };
        
        await _contextRepo.SaveAsync(context);
    }

    /// <summary>
    /// Gets learning statistics for a vocabulary word
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
    /// Gets overall learning statistics across all words
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
