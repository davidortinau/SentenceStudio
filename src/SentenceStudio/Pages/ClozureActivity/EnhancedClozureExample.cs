using SentenceStudio.Shared.Models;
using System.Diagnostics;

namespace SentenceStudio.Pages.ClozureActivity;

/// <summary>
/// Example implementation showing how Clozure activity can use
/// the enhanced vocabulary tracking system to provide detailed analytics
/// </summary>
public partial class EnhancedClozureExample
{
    /// <summary>
    /// Enhanced grading method that uses the new tracking system
    /// </summary>
    private async Task GradeMeWithEnhancedTracking()
    {
        var currentChallenge = State.Sentences.FirstOrDefault(s => s.IsCurrent);
        if (currentChallenge?.VocabularyWordId == null) return;
        
        var stopwatch = Stopwatch.StartNew();
        
        var answer = State.UserMode == InputMode.MultipleChoice.ToString() ? 
            State.UserGuess : State.UserInput;
        
        var isCorrect = answer.Equals(currentChallenge.VocabularyWordAsUsed, 
            StringComparison.CurrentCultureIgnoreCase);
        
        stopwatch.Stop();
        
        // Determine context type based on word usage in sentence
        var contextType = DetermineClozureContextType(currentChallenge);
        
        // Calculate difficulty based on sentence complexity and word usage
        var difficultyWeight = CalculateClozureDifficulty(currentChallenge, answer);
        
        // Create detailed attempt record
        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = currentChallenge.VocabularyWordId.Value,
            UserId = GetCurrentUserId(),
            Activity = "Clozure",
            InputMode = State.UserMode,
            WasCorrect = isCorrect,
            DifficultyWeight = difficultyWeight,
            ContextType = contextType,
            LearningResourceId = Props.Resource?.Id,
            UserInput = answer,
            ExpectedAnswer = currentChallenge.VocabularyWordAsUsed,
            ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
            UserConfidence = GetUserConfidenceFromUI()
        };
        
        // Record attempt using enhanced service
        var updatedProgress = await _progressService.RecordAttemptAsync(attempt);
        
        // Update challenge with new progress information
        UpdateChallengeProgress(currentChallenge, updatedProgress);
        
        // Provide enhanced feedback based on progress
        ShowEnhancedFeedback(currentChallenge, updatedProgress, isCorrect, attempt);
        
        // Update learning analytics
        await UpdateLearningAnalytics(currentChallenge, attempt, updatedProgress);
    }
    
    private string DetermineClozureContextType(dynamic currentChallenge)
    {
        var originalWord = currentChallenge.VocabularyWord?.TargetLanguageTerm ?? "";
        var wordAsUsed = currentChallenge.VocabularyWordAsUsed ?? "";
        
        // Check if word appears in conjugated or modified form
        if (!string.Equals(originalWord, wordAsUsed, StringComparison.CurrentCultureIgnoreCase))
        {
            return "Conjugated"; // Word is conjugated/modified
        }
        
        // Check sentence complexity (could be enhanced with NLP analysis)
        var sentence = currentChallenge.Sentence ?? "";
        if (sentence.Split(' ').Length > 10)
        {
            return "Complex"; // Long, complex sentence
        }
        
        return "Sentence"; // Standard sentence context
    }
    
    private float CalculateClozureDifficulty(dynamic currentChallenge, string userAnswer)
    {
        float difficulty = 1.0f; // Base difficulty for clozure
        
        // Adjust based on context type
        var contextType = DetermineClozureContextType(currentChallenge);
        switch (contextType)
        {
            case "Conjugated":
                difficulty *= 1.8f; // Conjugated forms are significantly harder
                break;
            case "Complex":
                difficulty *= 1.4f; // Complex sentences are moderately harder
                break;
            case "Sentence":
                difficulty *= 1.2f; // Standard sentence context is moderately challenging
                break;
        }
        
        // Adjust based on input mode
        if (State.UserMode == InputMode.Text.ToString())
        {
            difficulty *= 1.3f; // Text entry is harder than multiple choice in context
        }
        
        // Adjust based on sentence length and complexity
        var sentence = currentChallenge.Sentence ?? "";
        var wordCount = sentence.Split(' ').Length;
        if (wordCount > 15)
        {
            difficulty *= 1.2f; // Very long sentences are harder
        }
        
        // Adjust based on position of missing word
        var wordPosition = GetWordPositionInSentence(currentChallenge);
        if (wordPosition == "middle")
        {
            difficulty *= 1.1f; // Words in middle are slightly harder due to more context needed
        }
        
        return Math.Min(2.0f, Math.Max(0.8f, difficulty)); // Clamp between 0.8 and 2.0
    }
    
    private void ShowEnhancedFeedback(dynamic challenge, VocabularyProgress progress, bool isCorrect, VocabularyAttempt attempt)
    {
        if (isCorrect)
        {
            var masteryScore = progress.MasteryScore;
            var phaseText = GetPhaseDisplayText(progress.CurrentPhase);
            
            // Show mastery-based feedback
            if (masteryScore >= 0.8f)
            {
                ShowFeedback($"🎉 Perfect! Word mastered! ({phaseText})", "success");
            }
            else if (masteryScore >= 0.6f)
            {
                ShowFeedback($"🎯 Excellent! Strong progress - {(int)(masteryScore * 100)}% mastery", "success");
            }
            else
            {
                ShowFeedback($"✅ Correct! Building mastery - {(int)(masteryScore * 100)}%", "success");
            }
            
            // Show context-specific achievements
            if (attempt.ContextType == "Conjugated")
            {
                ShowFeedback("💪 Great job with the conjugated form!", "achievement");
            }
            else if (attempt.DifficultyWeight > 1.5f)
            {
                ShowFeedback("🔥 Impressive! That was a challenging usage!", "achievement");
            }
        }
        else
        {
            var masteryScore = progress.MasteryScore;
            var phaseText = GetPhaseDisplayText(progress.CurrentPhase);
            
            // Show encouraging feedback with context
            if (attempt.ContextType == "Conjugated")
            {
                ShowFeedback($"📚 Conjugated forms are tricky! Current mastery: {(int)(masteryScore * 100)}%", "info");
            }
            else
            {
                ShowFeedback($"🔍 Keep practicing! Current mastery: {(int)(masteryScore * 100)}% ({phaseText})", "info");
            }
            
            // Show helpful hints based on the error type
            ShowContextualHints(challenge, attempt);
        }
        
        // Show spaced repetition information
        if (progress.NextReviewDate.HasValue)
        {
            var daysUntilReview = (progress.NextReviewDate.Value - DateTime.Now).Days;
            if (daysUntilReview > 0)
            {
                ShowFeedback($"📅 Next review in {daysUntilReview} day{(daysUntilReview == 1 ? "" : "s")}", "info");
            }
        }
    }
    
    private async Task UpdateLearningAnalytics(dynamic challenge, VocabularyAttempt attempt, VocabularyProgress progress)
    {
        // This method could update various analytics and insights
        
        // Track error patterns for personalized feedback
        if (!attempt.WasCorrect && attempt.ContextType == "Conjugated")
        {
            // User struggles with conjugated forms - could suggest focused practice
            await LogLearningInsight(attempt.VocabularyWordId, "conjugation_difficulty");
        }
        
        // Track response time patterns
        if (attempt.ResponseTimeMs > 10000) // More than 10 seconds
        {
            await LogLearningInsight(attempt.VocabularyWordId, "slow_response");
        }
        else if (attempt.ResponseTimeMs < 2000 && attempt.WasCorrect)
        {
            await LogLearningInsight(attempt.VocabularyWordId, "quick_correct");
        }
        
        // Track difficulty adaptation
        if (attempt.DifficultyWeight > 1.5f && attempt.WasCorrect)
        {
            await LogLearningInsight(attempt.VocabularyWordId, "high_difficulty_success");
        }
    }
    
    private string GetPhaseDisplayText(LearningPhase phase)
    {
        return phase switch
        {
            LearningPhase.Recognition => "Recognition Phase",
            LearningPhase.Production => "Production Phase", 
            LearningPhase.Application => "Application Phase",
            _ => "Learning"
        };
    }
    
    private string GetWordPositionInSentence(dynamic challenge)
    {
        // Simplified position detection - could be enhanced
        var sentence = challenge.Sentence ?? "";
        var words = sentence.Split(' ');
        var blankIndex = Array.FindIndex(words, w => w.Contains("____") || w.Contains("..."));
        
        if (blankIndex == -1) return "unknown";
        
        var position = (float)blankIndex / words.Length;
        return position switch
        {
            < 0.3f => "beginning",
            > 0.7f => "end",
            _ => "middle"
        };
    }
    
    private void ShowContextualHints(dynamic challenge, VocabularyAttempt attempt)
    {
        if (attempt.ContextType == "Conjugated")
        {
            ShowFeedback("💡 Hint: Check if the word needs to be conjugated for this context", "hint");
        }
        else if (attempt.InputMode == "TextEntry")
        {
            ShowFeedback("💡 Hint: Pay attention to the exact spelling and form", "hint");
        }
    }
    
    // Helper methods (would be implemented in actual component)
    private void ShowFeedback(string message, string type) { }
    private void UpdateChallengeProgress(dynamic challenge, VocabularyProgress progress) { }
    private float? GetUserConfidenceFromUI() => null;
    private int GetCurrentUserId() => 1;
    private async Task LogLearningInsight(int wordId, string insightType) { }
    
    // Placeholder state and props (would exist in real component)
    private dynamic State { get; set; }
    private dynamic Props { get; set; }
}