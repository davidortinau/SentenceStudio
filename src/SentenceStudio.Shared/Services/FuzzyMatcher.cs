using System.Text;
using System.Text.RegularExpressions;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Shared.Services;

/// <summary>
/// Static utility class for fuzzy text matching in vocabulary quiz answers.
/// Handles annotation removal (parentheses, tildes), normalization, bidirectional matching, and typo tolerance.
/// </summary>
public static class FuzzyMatcher
{
    private const double TYPO_THRESHOLD = 0.75; // 75% similarity for typo tolerance
    private const int MAX_LEVENSHTEIN_DISTANCE = 2; // Maximum edit distance allowed
    
    private static readonly Regex ParenthesesPattern = 
        new Regex(@"\s*\([^)]*\)", RegexOptions.Compiled);
    
    private static readonly Regex TildePattern = 
        new Regex(@"~.*$", RegexOptions.Compiled);
    
    private static readonly Regex PunctuationPattern = 
        new Regex(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);

    /// <summary>
    /// Evaluates whether the user's answer matches the expected answer using fuzzy matching rules.
    /// Now includes typo tolerance using Levenshtein distance.
    /// </summary>
    /// <param name="userInput">The answer provided by the user</param>
    /// <param name="expectedAnswer">The correct answer expected</param>
    /// <returns>A FuzzyMatchResult indicating correctness, match type, and complete form if applicable</returns>
    public static FuzzyMatchResult Evaluate(string userInput, string expectedAnswer)
    {
        if (string.IsNullOrWhiteSpace(userInput) || string.IsNullOrWhiteSpace(expectedAnswer))
        {
            return new FuzzyMatchResult { IsCorrect = false };
        }

        var normalizedUser = NormalizeText(userInput);
        var normalizedExpected = NormalizeText(expectedAnswer);
        
        // 1. Check exact match after normalization
        bool normalizedMatch = string.Equals(normalizedUser, normalizedExpected, 
            StringComparison.OrdinalIgnoreCase);
        
        if (normalizedMatch)
        {
            bool exactMatch = string.Equals(userInput.Trim(), expectedAnswer.Trim(), 
                StringComparison.OrdinalIgnoreCase);
            
            return new FuzzyMatchResult
            {
                IsCorrect = true,
                MatchType = exactMatch ? "Exact" : "Fuzzy",
                CompleteForm = exactMatch ? null : expectedAnswer
            };
        }
        
        // 2. Check if normalized strings match with word boundary awareness
        // Only accept substring matches if they're complete words
        var userWords = normalizedUser.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expectedWords = normalizedExpected.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        // Check if all user words are present in expected (handles "cloudy" in "get cloudy")
        bool allUserWordsPresent = userWords.Length > 0 && 
            userWords.All(uw => expectedWords.Any(ew => 
                string.Equals(uw, ew, StringComparison.OrdinalIgnoreCase)));
        
        // Check if all expected words are present in user (handles "get cloudy" when user types "get cloudy")
        bool allExpectedWordsPresent = expectedWords.Length > 0 && 
            expectedWords.All(ew => userWords.Any(uw => 
                string.Equals(ew, uw, StringComparison.OrdinalIgnoreCase)));
        
        if (allUserWordsPresent || allExpectedWordsPresent)
        {
            return new FuzzyMatchResult
            {
                IsCorrect = true,
                MatchType = "Fuzzy",
                CompleteForm = expectedAnswer
            };
        }
        
        // 3. Check typo tolerance using Levenshtein distance with dual thresholds
        var distance = LevenshteinDistance(normalizedUser, normalizedExpected);
        var maxLength = Math.Max(normalizedUser.Length, normalizedExpected.Length);
        var similarity = 1.0 - ((double)distance / maxLength);
        
        // Accept if EITHER similarity threshold is met OR edit distance is small enough
        if (similarity >= TYPO_THRESHOLD || distance <= MAX_LEVENSHTEIN_DISTANCE)
        {
            return new FuzzyMatchResult
            {
                IsCorrect = true,
                MatchType = "Fuzzy",
                CompleteForm = expectedAnswer
            };
        }
        
        return new FuzzyMatchResult { IsCorrect = false };
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        
        // Unicode normalization (NFC for Korean support)
        text = text.Normalize(NormalizationForm.FormC);
        
        // Convert to lowercase for case-insensitive comparison
        text = text.ToLowerInvariant();
        
        // Remove parenthetical annotations: "take (a photo)" -> "take"
        text = ParenthesesPattern.Replace(text, "");
        
        // Remove tilde descriptors: "ding~ (a sound)" -> "ding"
        text = TildePattern.Replace(text, "");
        
        // Trim whitespace
        text = text.Trim();
        
        // Remove punctuation for comparison: "don't" -> "dont"
        text = PunctuationPattern.Replace(text, "");
        
        // Remove "to " prefix for English infinitives (bidirectional matching)
        if (text.StartsWith("to ", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(3).Trim();
        }
        
        return text;
    }

    /// <summary>
    /// Calculates Levenshtein distance between two strings.
    /// </summary>
    private static int LevenshteinDistance(string s1, string s2)
    {
        var len1 = s1.Length;
        var len2 = s2.Length;

        var matrix = new int[len1 + 1, len2 + 1];

        // Initialize first column and row
        for (int i = 0; i <= len1; i++)
            matrix[i, 0] = i;

        for (int j = 0; j <= len2; j++)
            matrix[0, j] = j;

        // Fill the matrix
        for (int i = 1; i <= len1; i++)
        {
            for (int j = 1; j <= len2; j++)
            {
                var cost = (s1[i - 1] == s2[j - 1]) ? 0 : 1;

                matrix[i, j] = Math.Min(
                    Math.Min(
                        matrix[i - 1, j] + 1,     // Deletion
                        matrix[i, j - 1] + 1),    // Insertion
                    matrix[i - 1, j - 1] + cost); // Substitution
            }
        }

        return matrix[len1, len2];
    }
}
