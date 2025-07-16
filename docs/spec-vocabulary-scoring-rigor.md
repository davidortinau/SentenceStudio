# Vocabulary Progress Tracking System Specification

## Overview

This specification defines an activity-independent vocabulary progress tracking system that supports multiple learning activities while maintaining pedagogical rigor and technical simplicity.

## Core Architecture 🗺️

### 1. **Global Progress Model**

```csharp
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

[Table("VocabularyProgress")]
public class VocabularyProgress
{
    public int Id { get; set; }
    public int VocabularyWordId { get; set; } // FK - ONE record per word
    public int UserId { get; set; } // FK - Support multiple users
    
    // Core mastery tracking
    public float MasteryScore { get; set; } = 0.0f; // 0.0 to 1.0
    public int TotalAttempts { get; set; } = 0;
    public int CorrectAttempts { get; set; } = 0;
    
    // Phase-specific tracking
    public int RecognitionAttempts { get; set; } = 0;
    public int RecognitionCorrect { get; set; } = 0;
    public int ProductionAttempts { get; set; } = 0;
    public int ProductionCorrect { get; set; } = 0;
    public int ApplicationAttempts { get; set; } = 0;
    public int ApplicationCorrect { get; set; } = 0;
    
    // Current learning phase
    public LearningPhase CurrentPhase { get; set; } = LearningPhase.Recognition;
    
    // Spaced repetition support
    public DateTime? NextReviewDate { get; set; }
    public int ReviewInterval { get; set; } = 1; // Days until next review
    public float EaseFactor { get; set; } = 2.5f; // SM-2 algorithm factor
    
    // Learning metadata
    public DateTime FirstSeenAt { get; set; } = DateTime.Now;
    public DateTime LastPracticedAt { get; set; } = DateTime.Now;
    public DateTime? MasteredAt { get; set; } // When reached mastery threshold
    
    [JsonIgnore]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    
    [JsonIgnore]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    
    // Navigation properties
    [JsonIgnore]
    public VocabularyWord VocabularyWord { get; set; } = null!;
    
    [JsonIgnore]
    public List<VocabularyLearningContext> LearningContexts { get; set; } = new();
    
    // Computed properties for status classification
    [NotMapped]
    public LearningStatus Status => MasteryScore switch
    {
        0 => LearningStatus.Unknown,
        >= 0.8f => LearningStatus.Known,
        _ => LearningStatus.Learning
    };
    
    [NotMapped]
    public bool IsKnown => Status == LearningStatus.Known;
    
    [NotMapped]
    public bool IsLearning => Status == LearningStatus.Learning;
    
    [NotMapped]
    public bool IsUnknown => Status == LearningStatus.Unknown;
    
    [NotMapped]
    public float Accuracy => TotalAttempts > 0 ? (float)CorrectAttempts / TotalAttempts : 0;
    
    // Phase-specific accuracy
    [NotMapped]
    public float RecognitionAccuracy => RecognitionAttempts > 0 ? 
        (float)RecognitionCorrect / RecognitionAttempts : 0;
    
    [NotMapped]
    public float ProductionAccuracy => ProductionAttempts > 0 ? 
        (float)ProductionCorrect / ProductionAttempts : 0;
    
    [NotMapped]
    public float ApplicationAccuracy => ApplicationAttempts > 0 ? 
        (float)ApplicationCorrect / ApplicationAttempts : 0;
    
    [NotMapped]
    public bool IsDueForReview => NextReviewDate.HasValue && NextReviewDate.Value <= DateTime.Now;
}

public enum LearningPhase
{
    Recognition,    // Can recognize meaning (multiple choice)
    Production,     // Can produce/write (text entry)
    Application     // Can use in context (conjugated, complex sentences)
}

public enum LearningStatus
{
    Unknown,
    Learning,
    Known
}
```

### 2. **Enhanced Learning Context**

```csharp
[Table("VocabularyLearningContexts")]
public class VocabularyLearningContext
{
    public int Id { get; set; }
    public int VocabularyProgressId { get; set; }
    public int? LearningResourceId { get; set; }
    
    // Activity tracking
    public string Activity { get; set; } = string.Empty; // "VocabularyQuiz", "Clozure", etc.
    public string InputMode { get; set; } = string.Empty; // "MultipleChoice", "TextEntry", "Conjugation"
    
    // Performance data
    public bool WasCorrect { get; set; }
    public float DifficultyScore { get; set; } = 0.5f; // 0-1, how hard was this usage
    public int ResponseTimeMs { get; set; } // How long to answer
    public float? UserConfidence { get; set; } // 0-1, optional self-assessment
    
    // Context-specific data
    public string? ContextType { get; set; } // "Isolated", "Sentence", "Conjugated"
    public string? UserInput { get; set; }
    public string? ExpectedAnswer { get; set; }
    
    public DateTime LearnedAt { get; set; } = DateTime.Now;
    
    // Navigation
    [JsonIgnore]
    public VocabularyProgress VocabularyProgress { get; set; } = null!;
}
```

### 3. **Vocabulary Attempt Model**

```csharp
public class VocabularyAttempt
{
    public int VocabularyWordId { get; set; }
    public int UserId { get; set; }
    public string Activity { get; set; } = string.Empty;
    public string InputMode { get; set; } = string.Empty;
    public bool WasCorrect { get; set; }
    public float DifficultyWeight { get; set; } = 1.0f;
    public string? ContextType { get; set; }
    public int? LearningResourceId { get; set; }
    public string? UserInput { get; set; }
    public string? ExpectedAnswer { get; set; }
    public int ResponseTimeMs { get; set; }
    public float? UserConfidence { get; set; } // Optional: "How confident were you?"
    
    // Computed property to determine phase
    public LearningPhase Phase => (InputMode, ContextType) switch
    {
        ("MultipleChoice", _) => LearningPhase.Recognition,
        ("TextEntry", "Isolated") => LearningPhase.Production,
        ("TextEntry", "Sentence") => LearningPhase.Production,
        ("TextEntry", "Conjugated") => LearningPhase.Application,
        _ => LearningPhase.Recognition
    };
}
```

### 4. **Progress Service Implementation**

```csharp
public interface IVocabularyProgressService
{
    Task<VocabularyProgress> RecordAttemptAsync(VocabularyAttempt attempt);
    Task<VocabularyProgress> GetProgressAsync(int vocabularyWordId, int userId);
    Task<List<VocabularyProgress>> GetReviewCandidatesAsync(int userId);
    Task<List<VocabularyProgress>> GetAllProgressAsync(int userId);
}

public class VocabularyProgressService : IVocabularyProgressService
{
    private readonly VocabularyProgressRepository _progressRepo;
    private readonly VocabularyLearningContextRepository _contextRepo;
    
    // Configurable thresholds
    private const float MASTERY_THRESHOLD = 0.8f;
    private const float PHASE_ADVANCE_THRESHOLD = 0.7f;
    private const int ROLLING_AVERAGE_COUNT = 5; // Last N attempts for score calculation
    
    public async Task<VocabularyProgress> RecordAttemptAsync(VocabularyAttempt attempt)
    {
        var progress = await GetOrCreateProgressAsync(attempt.VocabularyWordId, attempt.UserId);
        
        // Update total and phase-specific counts
        progress.TotalAttempts++;
        if (attempt.WasCorrect)
            progress.CorrectAttempts++;
        
        // Update phase-specific tracking
        UpdatePhaseMetrics(progress, attempt);
        
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
        
        // Save progress
        await _progressRepo.UpdateAsync(progress);
        
        // Record detailed context
        await RecordLearningContextAsync(progress.Id, attempt);
        
        return progress;
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
    
    public async Task<List<VocabularyProgress>> GetReviewCandidatesAsync(int userId)
    {
        return await _progressRepo.Where(p =>
            p.UserId == userId &&
            p.Status != LearningStatus.Known &&
            p.NextReviewDate.HasValue &&
            p.NextReviewDate.Value <= DateTime.Now
        ).OrderBy(p => p.NextReviewDate).ToListAsync();
    }
}
```

## Activity Integration Examples 🎯

### VocabularyQuiz Activity

```csharp
async Task RecordAnswer(bool isCorrect)
{
    var currentWord = CurrentWord;
    if (currentWord == null) return;
    
    var attempt = new VocabularyAttempt
    {
        VocabularyWordId = currentWord.VocabularyWord.Id,
        UserId = _currentUser.Id,
        Activity = "VocabularyQuiz",
        InputMode = State.UserMode,
        WasCorrect = isCorrect,
        DifficultyWeight = State.UserMode == InputMode.Text.ToString() ? 1.2f : 0.8f,
        ContextType = "Isolated",
        UserInput = State.UserInput,
        ExpectedAnswer = currentWord.VocabularyWord.Word,
        ResponseTimeMs = stopwatch.ElapsedMilliseconds,
        UserConfidence = State.UserConfidenceRating // Optional UI element
    };
    
    var progress = await _progressService.RecordAttemptAsync(attempt);
    
    // Update local state with new progress
    currentWord.Progress = progress;
}
```

### Clozure Activity

```csharp
async Task GradeMe()
{
    var currentChallenge = State.Sentences.FirstOrDefault(s => s.IsCurrent);
    if (currentChallenge == null) return;
    
    var answer = State.UserMode == InputMode.MultipleChoice.ToString() ? 
        State.UserGuess : State.UserInput;
    
    var isCorrect = answer.Equals(currentChallenge.VocabularyWordAsUsed, 
        StringComparison.CurrentCultureIgnoreCase);
    
    // Determine context type
    var contextType = "Sentence"; // Default
    if (currentChallenge.VocabularyWord != currentChallenge.VocabularyWordAsUsed)
    {
        contextType = "Conjugated";
    }
    
    var attempt = new VocabularyAttempt
    {
        VocabularyWordId = currentChallenge.VocabularyWordId.Value,
        UserId = _currentUser.Id,
        Activity = "Clozure",
        InputMode = State.UserMode,
        WasCorrect = isCorrect,
        DifficultyWeight = contextType == "Conjugated" ? 1.8f : 1.5f,
        ContextType = contextType,
        LearningResourceId = Props.Resource?.Id,
        UserInput = answer,
        ExpectedAnswer = currentChallenge.VocabularyWordAsUsed,
        ResponseTimeMs = _responseTimer.ElapsedMilliseconds
    };
    
    await _progressService.RecordAttemptAsync(attempt);
}
```

## Implementation Phases 🚢

### Phase 1: Core Progress System (Week 1-2)
- [ ] Update VocabularyProgress model with phase-specific tracking
- [ ] Implement rolling average mastery score calculation
- [ ] Add phase progression based on phase-specific performance
- [ ] Update existing activities to use new service

### Phase 2: Spaced Repetition (Week 3)
- [ ] Implement SM-2 algorithm for review scheduling
- [ ] Add review candidate queries
- [ ] Create review mode UI in activities
- [ ] Add "Due for Review" indicators

### Phase 3: Enhanced Analytics (Week 4)
- [ ] Add optional confidence ratings to UI
- [ ] Implement progress dashboards
- [ ] Add learning streak tracking
- [ ] Create vocabulary exposure tracking

## Benefits of This Approach ⚓

1. **Activity Independence**: Each activity contributes without imposing specific rules
2. **Phase-Specific Progress**: Accurate tracking of recognition vs. production skills
3. **Spaced Repetition**: Built-in review scheduling for long-term retention
4. **Time-Aware Scoring**: Accounts for forgetting over time
5. **Flexible Difficulty**: Activities can weight their contributions appropriately
6. **Multi-User Ready**: Supports multiple learners from the start
7. **Simple Implementation**: Uses rolling averages instead of complex algorithms

This approach provides a solid foundation that can grow with your application while keeping the implementation straightforward and maintainable.