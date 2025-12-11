# Contract: EncodingStrengthCalculator

**Purpose**: Calculate encoding strength score (0-1.0) based on vocabulary word completeness

```csharp
namespace SentenceStudio.Services;

/// <summary>
/// Calculates encoding strength score for vocabulary words
/// DESIGN: Stateless service with pure functions - no database access
/// </summary>
public interface IEncodingStrengthCalculator
{
    /// <summary>
    /// Calculate encoding strength score based on presence of encoding metadata
    /// </summary>
    /// <param name="word">Vocabulary word</param>
    /// <param name="exampleSentenceCount">Number of associated example sentences</param>
    /// <returns>Score from 0.0 to 1.0</returns>
    /// <calculation>
    /// Factors checked (each worth 1/6 of score):
    /// 1. TargetLanguageTerm (required, always present)
    /// 2. NativeLanguageTerm (required, always present)
    /// 3. MnemonicText (optional)
    /// 4. MnemonicImageUri (optional)
    /// 5. AudioPronunciationUri (optional)
    /// 6. ExampleSentences > 0 (optional)
    /// </calculation>
    /// <performance>Pure function, ~1μs per call</performance>
    double Calculate(VocabularyWord word, int exampleSentenceCount);

    /// <summary>
    /// Get user-friendly encoding strength label
    /// </summary>
    /// <param name="score">Encoding strength score (0-1.0)</param>
    /// <returns>"Basic" (0-0.33), "Good" (0.34-0.66), or "Strong" (0.67-1.0)</returns>
    string GetLabel(double score);

    /// <summary>
    /// Calculate encoding strength for multiple words (batch operation)
    /// </summary>
    /// <param name="wordsWithCounts">Dictionary mapping vocabulary words to example sentence counts</param>
    /// <returns>Dictionary mapping vocabulary word ID to encoding strength</returns>
    /// <performance>Target: &lt;30ms for 100 words</performance>
    Dictionary<int, double> CalculateBatch(Dictionary<VocabularyWord, int> wordsWithCounts);
}
```

## Implementation Reference

```csharp
public class EncodingStrengthCalculator : IEncodingStrengthCalculator
{
    public double Calculate(VocabularyWord word, int exampleSentenceCount)
    {
        int present = 0;
        const int possible = 6;

        // Count present encoding factors
        if (!string.IsNullOrWhiteSpace(word.TargetLanguageTerm)) present++;
        if (!string.IsNullOrWhiteSpace(word.NativeLanguageTerm)) present++;
        if (!string.IsNullOrWhiteSpace(word.MnemonicText)) present++;
        if (!string.IsNullOrWhiteSpace(word.MnemonicImageUri)) present++;
        if (!string.IsNullOrWhiteSpace(word.AudioPronunciationUri)) present++;
        if (exampleSentenceCount > 0) present++;

        return (double)present / possible;
    }

    public string GetLabel(double score)
    {
        return score switch
        {
            < 0.34 => "Basic",
            < 0.67 => "Good",
            _ => "Strong"
        };
    }

    public Dictionary<int, double> CalculateBatch(Dictionary<VocabularyWord, int> wordsWithCounts)
    {
        var results = new Dictionary<int, double>(wordsWithCounts.Count);
        
        foreach (var (word, count) in wordsWithCounts)
        {
            results[word.Id] = Calculate(word, count);
        }
        
        return results;
    }
}
```

## Design Rationale

**Stateless**: No caching or persistence. Calculation is fast enough (~1μs) to compute on-demand.

**Equal Weighting**: Each factor contributes 1/6 of the score. Future enhancement could add weighted factors (e.g., mnemonics 2x more valuable than images).

**Not Persisted**: Avoids database triggers and stale data risk. Always up-to-date because it's calculated.

**Batch Operation**: For list views showing 50-100 words, batch calculation avoids repeated calls.

**Localization**: Labels ("Basic", "Good", "Strong") should be localized in UI layer, not service layer.
