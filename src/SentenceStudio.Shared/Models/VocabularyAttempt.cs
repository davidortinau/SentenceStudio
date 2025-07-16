namespace SentenceStudio.Shared.Models;

/// <summary>
/// Represents a single vocabulary learning attempt with contextual information
/// </summary>
public class VocabularyAttempt
{
    public int VocabularyWordId { get; set; }
    public int UserId { get; set; }
    public string Activity { get; set; } = string.Empty; // "VocabularyQuiz", "Clozure", etc.
    public string InputMode { get; set; } = string.Empty; // "MultipleChoice", "TextEntry", "Conjugation"
    public bool WasCorrect { get; set; }
    public float DifficultyWeight { get; set; } = 1.0f; // 0.0-2.0, how hard was this usage
    public string? ContextType { get; set; } // "Isolated", "Sentence", "Conjugated"
    public int? LearningResourceId { get; set; }
    public string? UserInput { get; set; }
    public string? ExpectedAnswer { get; set; }
    public int ResponseTimeMs { get; set; }
    public float? UserConfidence { get; set; } // 0.0-1.0, optional self-assessment

    /// <summary>
    /// Determines the learning phase based on input mode and context
    /// </summary>
    public LearningPhase Phase => (InputMode, ContextType) switch
    {
        ("MultipleChoice", _) => LearningPhase.Recognition,
        ("TextEntry", "Isolated") => LearningPhase.Production,
        ("TextEntry", "Sentence") => LearningPhase.Production,
        ("TextEntry", "Conjugated") => LearningPhase.Application,
        _ => LearningPhase.Recognition
    };
}