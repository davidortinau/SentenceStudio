namespace SentenceStudio.Shared.Models;

/// <summary>
/// Represents one answered trial within a minimal pair practice session.
/// Records which pair was tested, what was played, what was selected, and whether correct.
/// </summary>
public class MinimalPairAttempt
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// User who made this attempt (default 1 for single-user app)
    /// </summary>
    public int UserId { get; set; } = 1;

    /// <summary>
    /// The session this attempt belongs to
    /// </summary>
    public int SessionId { get; set; }

    /// <summary>
    /// The minimal pair being tested
    /// </summary>
    public int PairId { get; set; }

    /// <summary>
    /// Which vocabulary word was played as the prompt (the correct answer)
    /// </summary>
    public int PromptWordId { get; set; }

    /// <summary>
    /// Which vocabulary word the user selected
    /// </summary>
    public int SelectedWordId { get; set; }

    /// <summary>
    /// Whether the user's selection was correct (PromptWordId == SelectedWordId)
    /// </summary>
    public bool IsCorrect { get; set; }

    /// <summary>
    /// Trial number within the session (1-based, for ordering)
    /// </summary>
    public int SequenceNumber { get; set; }

    /// <summary>
    /// When this attempt was made
    /// </summary>
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public MinimalPairSession? Session { get; set; }
    public MinimalPair? Pair { get; set; }
    public VocabularyWord? PromptWord { get; set; }
    public VocabularyWord? SelectedWord { get; set; }
}
