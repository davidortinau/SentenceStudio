# Enhanced Vocabulary Progress Tracking System

## Overview

This document provides a comprehensive guide to the enhanced vocabulary progress tracking system implemented in SentenceStudio. The system provides rigorous tracking and learning analytics while maintaining backward compatibility with existing activities.

## Key Features

### 🎯 Mastery-Based Scoring
- **Mastery Score**: 0.0 to 1.0 scale using weighted rolling averages
- **Learning Status**: Unknown (0), Learning (0.1-0.79), Known (0.8+)
- **Time Decay**: Scores decrease over time without practice

### 📈 Phase-Based Learning
- **Recognition Phase**: Multiple choice, basic comprehension
- **Production Phase**: Text entry, active recall
- **Application Phase**: Conjugated forms, complex usage

### 🔄 Spaced Repetition
- **SM-2 Algorithm**: Intelligent review scheduling
- **Dynamic Intervals**: Adapts based on performance
- **Review Candidates**: Automatic identification of words due for review

### 📊 Enhanced Analytics
- **Response Time Tracking**: Measure cognitive load
- **Difficulty Weighting**: Context-aware difficulty assessment  
- **Confidence Ratings**: Optional self-assessment
- **Multi-User Support**: Individual progress tracking

## Implementation Guide

### 1. Basic Usage

```csharp
// Record a vocabulary attempt
var attempt = new VocabularyAttempt
{
    VocabularyWordId = wordId,
    UserId = currentUserId,
    Activity = "VocabularyQuiz",
    InputMode = "MultipleChoice",
    WasCorrect = isCorrect,
    DifficultyWeight = 0.8f, // Multiple choice is easier
    ContextType = "Isolated",
    ResponseTimeMs = responseTime,
    UserInput = userAnswer,
    ExpectedAnswer = correctAnswer
};

var progress = await progressService.RecordAttemptAsync(attempt);
```

### 2. Activity Integration

#### VocabularyQuiz Integration
```csharp
private async Task RecordQuizAnswer(bool isCorrect, string userInput, int responseTimeMs)
{
    var inputMode = currentItem.IsPromoted ? InputMode.Text : InputMode.MultipleChoice;
    var difficultyWeight = inputMode == InputMode.Text ? 1.2f : 0.8f;
    
    var attempt = new VocabularyAttempt
    {
        VocabularyWordId = currentItem.Word.Id,
        UserId = GetCurrentUserId(),
        Activity = "VocabularyQuiz", 
        InputMode = inputMode.ToString(),
        WasCorrect = isCorrect,
        DifficultyWeight = difficultyWeight,
        ContextType = "Isolated",
        LearningResourceId = GetCurrentResourceId(),
        UserInput = userInput,
        ExpectedAnswer = GetExpectedAnswer(),
        ResponseTimeMs = responseTimeMs
    };
    
    var updatedProgress = await progressService.RecordAttemptAsync(attempt);
    
    // Update UI based on new mastery score
    UpdateProgressDisplay(updatedProgress);
}
```

#### Clozure Activity Integration
```csharp
private async Task RecordClozureAnswer(bool isCorrect, string userInput, int responseTimeMs)
{
    var contextType = DetermineContextType(); // "Sentence", "Conjugated", etc.
    var difficultyWeight = CalculateDifficulty(contextType);
    
    var attempt = new VocabularyAttempt
    {
        VocabularyWordId = challenge.VocabularyWordId,
        UserId = GetCurrentUserId(),
        Activity = "Clozure",
        InputMode = GetInputMode(),
        WasCorrect = isCorrect,
        DifficultyWeight = difficultyWeight,
        ContextType = contextType,
        LearningResourceId = Props.Resource?.Id,
        UserInput = userInput,
        ExpectedAnswer = challenge.ExpectedAnswer,
        ResponseTimeMs = responseTimeMs
    };
    
    await progressService.RecordAttemptAsync(attempt);
}
```

### 3. Progress Querying

```csharp
// Get individual word progress
var progress = await progressService.GetProgressAsync(wordId, userId);

// Get words due for review
var reviewWords = await progressService.GetReviewCandidatesAsync(userId);

// Get all user progress
var allProgress = await progressService.GetAllProgressAsync(userId);

// Check learning status
if (progress.Status == LearningStatus.Known)
{
    // Word is mastered (80%+ mastery score)
}
else if (progress.Status == LearningStatus.Learning)
{
    // Word is in progress
}
```

## Difficulty Weight Guidelines

### Input Mode Weights
- **Multiple Choice**: 0.6 - 0.8 (easier)
- **Text Entry**: 1.0 - 1.2 (standard)
- **Voice Input**: 1.3 - 1.5 (harder)

### Context Type Weights
- **Isolated**: 1.0 (base difficulty)
- **Sentence**: 1.2 - 1.4 (moderate context)
- **Conjugated**: 1.5 - 2.0 (complex usage)

### Activity-Specific Adjustments
```csharp
private float CalculateDifficultyWeight(string activity, string inputMode, string contextType)
{
    float baseWeight = 1.0f;
    
    // Activity adjustments
    switch (activity)
    {
        case "VocabularyQuiz":
            baseWeight = inputMode == "Text" ? 1.2f : 0.8f;
            break;
        case "Clozure":
            baseWeight = 1.5f; // Context makes it harder
            break;
        case "Conversation":
            baseWeight = 1.8f; // Real-time usage is hardest
            break;
    }
    
    // Context adjustments
    if (contextType == "Conjugated")
        baseWeight *= 1.5f;
    
    return Math.Min(2.0f, Math.Max(0.5f, baseWeight));
}
```

## Migration and Backward Compatibility

### Database Migration
The system includes a migration that:
- Adds new fields to VocabularyProgress
- Preserves existing MultipleChoiceCorrect/TextEntryCorrect fields
- Updates table names and constraints
- Populates new fields with sensible defaults

### Legacy API Support
All existing methods continue to work:

```csharp
// Legacy methods still work
var progress = await progressService.GetOrCreateProgressAsync(wordId);
await progressService.RecordCorrectAnswerAsync(wordId, InputMode.Text);
await progressService.RecordIncorrectAnswerAsync(wordId, InputMode.MultipleChoice);
```

## Performance Considerations

### Rolling Average Calculation
- Limited to last 5 attempts by default
- Uses recency weighting (more recent = higher weight)
- Includes time decay factor
- Optimized database queries

### Spaced Repetition
- Review intervals: 1 day → 6 days → exponential growth
- EaseFactor adjustment: ±0.1 based on performance
- Automatic review candidate identification

## Analytics and Insights

### Response Time Analysis
```csharp
if (attempt.ResponseTimeMs > 10000)
{
    // Slow response - word may be difficult
    await LogInsight(wordId, "slow_response");
}
else if (attempt.ResponseTimeMs < 2000 && attempt.WasCorrect)
{
    // Fast correct response - good mastery
    await LogInsight(wordId, "quick_mastery");
}
```

### Error Pattern Detection
```csharp
if (!attempt.WasCorrect && attempt.ContextType == "Conjugated")
{
    // User struggles with conjugation
    await LogInsight(wordId, "conjugation_difficulty");
}
```

## Configuration Options

### Thresholds (Adjustable)
```csharp
private const float MASTERY_THRESHOLD = 0.8f;        // Known status
private const float PHASE_ADVANCE_THRESHOLD = 0.7f;  // Phase progression
private const int ROLLING_AVERAGE_COUNT = 5;         // Attempts to consider
```

### Phase Advancement Rules
- Recognition → Production: 70%+ accuracy, 3+ attempts
- Production → Application: 70%+ accuracy, 3+ attempts
- Automatic progression based on performance

## Best Practices

### 1. Accurate Difficulty Weights
- Consider all factors: input mode, context, activity
- Use consistent scales across activities
- Test and calibrate based on user performance

### 2. Meaningful Context Types
- Be specific: "Conjugated", "Passive Voice", "Conditional"
- Group similar contexts for analysis
- Use consistent naming across activities

### 3. Response Time Tracking
- Start timer when question is presented
- Stop when answer is submitted
- Handle pauses and interruptions appropriately

### 4. User Confidence (Optional)
- Simple 1-5 scale or slider
- Collect after answer, not before
- Use for additional score weighting

## Troubleshooting

### Common Issues

**Mastery scores not updating**
- Check that WasCorrect is set correctly
- Verify DifficultyWeight is reasonable (0.5-2.0)
- Ensure RecordAttemptAsync is being called

**Phase advancement not working**
- Check accuracy calculation for specific phase
- Verify minimum attempt count is met
- Review PHASE_ADVANCE_THRESHOLD setting

**Review candidates not appearing**
- Check NextReviewDate is being set
- Verify current date comparison
- Ensure Status is not already "Known"

### Debug Information
```csharp
Debug.WriteLine($"Word {wordId}: Mastery={progress.MasteryScore:F2}, " +
               $"Phase={progress.CurrentPhase}, " +
               $"NextReview={progress.NextReviewDate}");
```

## Future Enhancements

### Planned Features
- Adaptive difficulty based on user performance
- Learning streak tracking
- Cross-word interference analysis
- Personalized review scheduling
- Export/import of progress data

### API Extensions
- Batch attempt recording
- Progress synchronization across devices
- Learning analytics dashboard
- Custom mastery calculation algorithms

## Conclusion

The enhanced vocabulary progress tracking system provides a solid foundation for rigorous language learning while maintaining simplicity and backward compatibility. The system can be extended and customized based on specific pedagogical needs and user feedback.

For implementation questions or feature requests, refer to the codebase examples and consider the pedagogical principles outlined in the original specification.