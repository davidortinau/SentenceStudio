using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Calculates encoding strength for vocabulary words based on presence of memory aids
/// </summary>
public class EncodingStrengthCalculator
{
    private readonly ILogger<EncodingStrengthCalculator> _logger;

    public EncodingStrengthCalculator(ILogger<EncodingStrengthCalculator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Calculate encoding strength for a single vocabulary word
    /// </summary>
    /// <param name="word">The vocabulary word</param>
    /// <param name="exampleSentenceCount">Number of example sentences for this word</param>
    /// <returns>Score from 0.0 to 1.0</returns>
    public double Calculate(VocabularyWord word, int exampleSentenceCount)
    {
        int present = 0;
        int possible = 6;

        if (!string.IsNullOrWhiteSpace(word.TargetLanguageTerm)) present++;
        if (!string.IsNullOrWhiteSpace(word.NativeLanguageTerm)) present++;
        if (!string.IsNullOrWhiteSpace(word.MnemonicText)) present++;
        if (!string.IsNullOrWhiteSpace(word.MnemonicImageUri)) present++;
        if (!string.IsNullOrWhiteSpace(word.AudioPronunciationUri)) present++;
        if (exampleSentenceCount > 0) present++;

        var score = possible == 0 ? 0 : (double)present / possible;
        
        _logger.LogDebug("📊 Encoding strength for '{Word}': {Score:F2} ({Present}/{Possible} factors)",
            word.TargetLanguageTerm ?? word.NativeLanguageTerm, score, present, possible);
        
        return score;
    }

    /// <summary>
    /// Get human-readable label for encoding strength score
    /// </summary>
    public string GetLabel(double score)
    {
        return score switch
        {
            <= 0.33 => "Basic",
            <= 0.66 => "Good",
            _ => "Strong"
        };
    }

    /// <summary>
    /// Calculate encoding strength for multiple words (batch operation)
    /// </summary>
    public Dictionary<int, (double Score, string Label)> CalculateBatch(
        IEnumerable<VocabularyWord> words,
        Dictionary<int, int> exampleSentenceCounts)
    {
        var results = new Dictionary<int, (double, string)>();
        
        foreach (var word in words)
        {
            var count = exampleSentenceCounts.GetValueOrDefault(word.Id, 0);
            var score = Calculate(word, count);
            var label = GetLabel(score);
            results[word.Id] = (score, label);
        }
        
        _logger.LogDebug("📊 Batch calculated encoding strength for {Count} words", results.Count);
        
        return results;
    }
}
