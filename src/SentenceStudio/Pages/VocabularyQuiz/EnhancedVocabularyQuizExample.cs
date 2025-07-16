using SentenceStudio.Shared.Models;
using System.Diagnostics;

namespace SentenceStudio.Pages.VocabularyQuiz;

/// <summary>
/// Example implementation showing how VocabularyQuiz can be enhanced to use
/// the new rigorous tracking system while maintaining backward compatibility
/// </summary>
public partial class EnhancedVocabularyQuizExample
{
    // Example method showing how to record an answer using the new system
    private async Task RecordAnswerWithEnhancedTracking(
        VocabularyQuizItem currentItem, 
        bool isCorrect, 
        string userInput, 
        Stopwatch responseTimer)
    {
        // Get current resource ID for context tracking
        var currentResourceId = GetCurrentResourceId();
        var inputMode = currentItem.IsPromoted ? InputMode.Text : InputMode.MultipleChoice;
        
        // Determine context type based on the quiz mode and word usage
        var contextType = DetermineContextType(currentItem, inputMode);
        
        // Determine difficulty weight based on various factors
        var difficultyWeight = CalculateDifficultyWeight(currentItem, inputMode, contextType);
        
        // Create detailed attempt record
        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = currentItem.Word.Id,
            UserId = GetCurrentUserId(), // New: support for multiple users
            Activity = "VocabularyQuiz",
            InputMode = inputMode.ToString(),
            WasCorrect = isCorrect,
            DifficultyWeight = difficultyWeight,
            ContextType = contextType,
            LearningResourceId = currentResourceId,
            UserInput = userInput,
            ExpectedAnswer = GetExpectedAnswer(currentItem, inputMode),
            ResponseTimeMs = (int)responseTimer.ElapsedMilliseconds,
            UserConfidence = GetUserConfidenceRating() // Optional: from UI slider
        };
        
        // Record attempt using enhanced service
        var updatedProgress = await _progressService.RecordAttemptAsync(attempt);
        
        // Update the quiz item with new progress
        currentItem.Progress = updatedProgress;
        
        // Update UI based on new mastery score and learning phase
        UpdateUIBasedOnProgress(currentItem, updatedProgress, isCorrect);
    }
    
    private string DetermineContextType(VocabularyQuizItem item, InputMode inputMode)
    {
        // Enhanced context detection logic
        if (inputMode == InputMode.MultipleChoice)
            return "Isolated"; // Multiple choice is always isolated recognition
            
        // For text entry, check if word appears in conjugated form
        var word = item.Word;
        var expectedAnswer = GetExpectedAnswer(item, inputMode);
        
        if (word.TargetLanguageTerm != expectedAnswer)
            return "Conjugated"; // Word appears in different form
            
        return "Isolated"; // Standard text entry
    }
    
    private float CalculateDifficultyWeight(VocabularyQuizItem item, InputMode inputMode, string contextType)
    {
        float baseWeight = 1.0f;
        
        // Adjust based on input mode
        if (inputMode == InputMode.Text)
            baseWeight *= 1.2f; // Text entry is harder than multiple choice
            
        // Adjust based on context
        if (contextType == "Conjugated")
            baseWeight *= 1.5f; // Conjugated forms are more difficult
            
        // Adjust based on word characteristics (length, complexity, etc.)
        if (item.Word.TargetLanguageTerm?.Length > 10)
            baseWeight *= 1.1f; // Longer words are slightly harder
            
        // Adjust based on previous performance
        if (item.Progress?.Accuracy < 0.5f)
            baseWeight *= 0.9f; // Lower weight if consistently struggling
            
        return Math.Min(2.0f, Math.Max(0.5f, baseWeight)); // Clamp between 0.5 and 2.0
    }
    
    private void UpdateUIBasedOnProgress(VocabularyQuizItem item, VocabularyProgress progress, bool wasCorrect)
    {
        if (wasCorrect)
        {
            // Use mastery score for more nuanced feedback
            var masteryScore = progress.MasteryScore;
            var currentPhase = progress.CurrentPhase;
            
            if (masteryScore >= 0.8f)
            {
                // Word is now known (mastered)
                SetState(s => {
                    s.ShowAnswer = true;
                    s.IsCorrect = true;
                    s.FeedbackMessage = "🎉 Excellent! Word mastered!";
                    s.CorrectAnswersInRound++;
                });
                ShowCelebration("✨ Perfect! Word mastered!");
            }
            else if (currentPhase == LearningPhase.Production && progress.CurrentPhase != item.Progress?.CurrentPhase)
            {
                // Phase advanced from Recognition to Production
                SetState(s => {
                    s.ShowAnswer = true;
                    s.IsCorrect = true;
                    s.FeedbackMessage = "🎯 Great! Advanced to typing practice.";
                    s.CorrectAnswersInRound++;
                });
            }
            else if (currentPhase == LearningPhase.Application && progress.CurrentPhase != item.Progress?.CurrentPhase)
            {
                // Phase advanced from Production to Application
                SetState(s => {
                    s.ShowAnswer = true;
                    s.IsCorrect = true;
                    s.FeedbackMessage = "🚀 Excellent! Ready for advanced usage.";
                    s.CorrectAnswersInRound++;
                });
            }
            else
            {
                // Show progress toward mastery
                var progressPercent = (int)(masteryScore * 100);
                SetState(s => {
                    s.ShowAnswer = true;
                    s.IsCorrect = true;
                    s.FeedbackMessage = $"✅ Correct! Mastery: {progressPercent}%";
                    s.CorrectAnswersInRound++;
                });
            }
            
            // Show spaced repetition info if relevant
            if (progress.NextReviewDate.HasValue)
            {
                var nextReview = progress.NextReviewDate.Value;
                var daysUntilReview = (nextReview - DateTime.Now).Days;
                Debug.WriteLine($"Next review for word {item.Word.Id} in {daysUntilReview} days");
            }
        }
        else
        {
            // Handle incorrect answers with enhanced feedback
            var masteryScore = progress.MasteryScore;
            var attempts = progress.TotalAttempts;
            
            SetState(s => {
                s.ShowAnswer = true;
                s.IsCorrect = false;
                s.FeedbackMessage = $"❌ Not quite. Mastery: {(int)(masteryScore * 100)}% ({attempts} attempts)";
            });
        }
    }
    
    private float? GetUserConfidenceRating()
    {
        // This could come from a UI slider asking "How confident were you?"
        // Returning null for now to indicate no confidence rating was provided
        return null;
    }
    
    private int GetCurrentUserId()
    {
        // For now, return default user ID
        // In a real implementation, this would come from user authentication
        return 1;
    }
    
    private string GetExpectedAnswer(VocabularyQuizItem item, InputMode inputMode)
    {
        // This logic would depend on quiz configuration
        // For now, return the target language term
        return item.Word.TargetLanguageTerm ?? "";
    }
    
    private int? GetCurrentResourceId()
    {
        // This would return the current learning resource ID
        // Implementation depends on the quiz context
        return null;
    }
    
    // Placeholder methods that would exist in the real VocabularyQuizPage
    private void SetState(Action<VocabularyQuizPageState> updateAction) { }
    private void ShowCelebration(string message) { }
}