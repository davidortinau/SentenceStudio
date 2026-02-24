using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Utility class for calculating vocabulary word encoding strength.
/// Encoding strength measures how well a word is supported by multiple learning cues
/// (visual, audio, mnemonic, contextual examples).
/// </summary>
public static class EncodingScoreHelper
{
    /// <summary>
    /// Calculates a 0-1 encoding score based on presence of encoding fields.
    /// </summary>
    /// <param name="word">The vocabulary word to score</param>
    /// <param name="exampleSentenceCount">Number of example sentences for this word</param>
    /// <returns>Score between 0 and 1</returns>
    public static double Calculate(VocabularyWord word, int exampleSentenceCount)
    {
        int present = 0;
        int possible = 0;

        void CountField(bool hasValue)
        {
            possible++;
            if (hasValue) present++;
        }

        // Core fields
        CountField(!string.IsNullOrWhiteSpace(word.TargetLanguageTerm));
        CountField(!string.IsNullOrWhiteSpace(word.NativeLanguageTerm));
        
        // Encoding enhancement fields
        CountField(!string.IsNullOrWhiteSpace(word.MnemonicText));
        CountField(!string.IsNullOrWhiteSpace(word.MnemonicImageUri));
        CountField(!string.IsNullOrWhiteSpace(word.AudioPronunciationUri));
        CountField(exampleSentenceCount > 0);

        return possible == 0 ? 0 : (double)present / possible;
    }

    /// <summary>
    /// Converts a numeric score (0-1) to a friendly label.
    /// </summary>
    /// <param name="score">Score between 0 and 1</param>
    /// <returns>Human-readable label: Basic, Good, or Strong</returns>
    public static string GetLabel(double score)
    {
        return score switch
        {
            >= 0.67 => "Strong",
            >= 0.34 => "Good",
            _ => "Basic"
        };
    }

    /// <summary>
    /// Calculates both score and label for a vocabulary word.
    /// </summary>
    /// <param name="word">The vocabulary word to evaluate</param>
    /// <param name="exampleSentenceCount">Number of example sentences</param>
    /// <returns>Tuple of (score, label)</returns>
    public static (double Score, string Label) CalculateWithLabel(VocabularyWord word, int exampleSentenceCount)
    {
        var score = Calculate(word, exampleSentenceCount);
        var label = GetLabel(score);
        return (score, label);
    }
}
