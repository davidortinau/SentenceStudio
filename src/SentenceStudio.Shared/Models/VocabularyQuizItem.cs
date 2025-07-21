namespace SentenceStudio.Shared.Models;

public class VocabularyQuizItem
{
    public VocabularyWord Word { get; set; }
    public bool IsCurrent { get; set; }
    public UserActivity? UserActivity { get; set; }
    
    // Global progress from VocabularyProgress table (cross-activity mastery)
    public SentenceStudio.Shared.Models.VocabularyProgress? Progress { get; set; }
    
    // Activity-specific progress (THIS quiz activity only)
    public int QuizRecognitionStreak { get; set; } = 0;  // Consecutive multiple choice correct
    public int QuizProductionStreak { get; set; } = 0;   // Consecutive text entry correct
    public bool QuizRecognitionComplete => QuizRecognitionStreak >= RequiredCorrectAnswers;
    public bool QuizProductionComplete => QuizProductionStreak >= RequiredCorrectAnswers;
    public bool ReadyToRotateOut => QuizRecognitionComplete && QuizProductionComplete;
    
    // Current phase within THIS quiz (independent of global phase)
    public bool IsPromotedInQuiz => QuizRecognitionComplete;
    
    // Enhanced computed properties that delegate to new system (for UI display)
    public bool IsPromoted => Progress?.CurrentPhase >= LearningPhase.Production;
    public bool IsCompleted => Progress?.IsKnown ?? false;
    public int MultipleChoiceCorrect => Progress?.RecognitionCorrect ?? 0;
    public int TextEntryCorrect => Progress?.ProductionCorrect ?? 0;
    
    // Legacy support for backward compatibility
    public const int RequiredCorrectAnswers = 3;
    public bool HasConfidenceInMultipleChoice => Progress?.RecognitionAccuracy >= 0.7f;
    public bool HasConfidenceInTextEntry => Progress?.ProductionAccuracy >= 0.7f;
    
    // Enhanced progress indicators
    public float MultipleChoiceProgress => Progress?.RecognitionAccuracy ?? 0f;
    public float TextEntryProgress => Progress?.ProductionAccuracy ?? 0f;
    public float MasteryProgress => Progress?.MasteryScore ?? 0f;
    
    // Check if term is ready to be skipped in current phase
    public bool IsReadyToSkipInCurrentPhase { get; set; }
    
    // Enhanced status helpers
    public bool IsUnknown => Progress?.IsUnknown ?? true;
    public bool IsLearning => Progress?.IsLearning ?? false;
    public bool IsKnown => Progress?.IsKnown ?? false;
    
    // Spaced repetition support
    public bool IsDueForReview => Progress?.IsDueForReview ?? false;
}