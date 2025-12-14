namespace SentenceStudio.Shared.Models;

/// <summary>
/// Represents the result of a fuzzy text matching evaluation.
/// </summary>
public class FuzzyMatchResult
{
    /// <summary>
    /// Gets or sets whether the user's answer is considered correct.
    /// </summary>
    public bool IsCorrect { get; set; }
    
    /// <summary>
    /// Gets or sets the type of match: "Exact" for exact matches, "Fuzzy" for normalized matches.
    /// </summary>
    public string MatchType { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the complete/preferred form of the term when a fuzzy match occurs.
    /// Null for exact matches or incorrect answers.
    /// </summary>
    public string? CompleteForm { get; set; }
}
