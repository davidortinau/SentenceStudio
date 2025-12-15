namespace SentenceStudio.Shared.Models;

/// <summary>
/// Represents a defined minimal pair linking exactly two vocabulary words.
/// Minimal pairs are words that differ by only one sound/character and help
/// learners distinguish between similar sounds.
/// </summary>
public class MinimalPair
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// User who owns this minimal pair (default 1 for single-user app)
    /// </summary>
    public int UserId { get; set; } = 1;

    /// <summary>
    /// First vocabulary word in the pair (must be &lt; VocabularyWordBId for normalization)
    /// </summary>
    public int VocabularyWordAId { get; set; }

    /// <summary>
    /// Second vocabulary word in the pair (must be &gt; VocabularyWordAId for normalization)
    /// </summary>
    public int VocabularyWordBId { get; set; }

    /// <summary>
    /// Optional label describing the contrast (e.g., "ㅅ vs ㅆ", "Initial consonant")
    /// </summary>
    public string? ContrastLabel { get; set; }

    /// <summary>
    /// When this pair was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this pair was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public VocabularyWord? VocabularyWordA { get; set; }
    public VocabularyWord? VocabularyWordB { get; set; }
}
