using System.Text.RegularExpressions;

namespace SentenceStudio.Services;

/// <summary>
/// Extracts meaningful sentences from learning resource transcripts.
/// Filters out trivial content like greetings.
/// </summary>
public class TranscriptSentenceExtractor
{
    // Simple filters for trivial sentences
    private static readonly HashSet<string> TrivialPatternsEnglish = new(StringComparer.OrdinalIgnoreCase)
    {
        "hello", "hi", "hey", "goodbye", "bye", "thanks", "thank you",
        "yes", "no", "ok", "okay", "sure", "please", "sorry"
    };

    private static readonly HashSet<string> TrivialPatternsKorean = new()
    {
        "안녕하세요", "안녕", "감사합니다", "고맙습니다", "네", "아니요",
        "좋아요", "괜찮아요", "죄송합니다", "미안합니다"
    };

    /// <summary>
    /// Extracts random subset of meaningful sentences from transcript.
    /// </summary>
    /// <param name="transcript">Raw transcript text</param>
    /// <param name="language">Target language (e.g., "Korean")</param>
    /// <param name="count">Number of sentences to return</param>
    /// <param name="minWordCount">Minimum word count for meaningful sentences</param>
    /// <returns>Randomized list of sentences</returns>
    public List<string> ExtractRandomSentences(
        string transcript,
        string language,
        int count = 10,
        int minWordCount = 4)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return new List<string>();

        // Use existing sentence splitter from SentenceTimingCalculator
        var allSentences = SentenceTimingCalculator.SplitIntoSentences(transcript);

        // Filter trivial sentences
        var filteredSentences = allSentences
            .Where(s => !IsTrivialSentence(s, language))
            .Where(s => MeetsMinimumLength(s, minWordCount))
            .ToList();

        if (filteredSentences.Count == 0)
            return new List<string>();

        // Shuffle and take requested count
        var random = new Random();
        return filteredSentences
            .OrderBy(_ => random.Next())
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Checks if sentence is trivial (greeting, single word, etc.)
    /// </summary>
    private bool IsTrivialSentence(string sentence, string language)
    {
        if (string.IsNullOrWhiteSpace(sentence))
            return true;

        var cleaned = sentence.Trim().TrimEnd('.', '!', '?', '。', '！', '？');

        // Check against language-specific trivial patterns
        var patterns = language.ToLower() switch
        {
            "korean" or "ko" => TrivialPatternsKorean,
            "english" or "en" => TrivialPatternsEnglish,
            _ => TrivialPatternsEnglish
        };

        return patterns.Any(pattern =>
            cleaned.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains(pattern, StringComparison.OrdinalIgnoreCase) && cleaned.Length < 20);
    }

    /// <summary>
    /// Checks if sentence meets minimum word count.
    /// Handles CJK languages (character-based) vs space-delimited languages.
    /// </summary>
    private bool MeetsMinimumLength(string sentence, int minWordCount)
    {
        if (string.IsNullOrWhiteSpace(sentence))
            return false;

        // For CJK languages, use character count
        if (ContainsCJK(sentence))
        {
            var charCount = sentence.Count(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c));
            return charCount >= minWordCount; // Treat characters as words for CJK
        }

        // For space-delimited languages, count words
        var wordCount = sentence.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        return wordCount >= minWordCount;
    }

    private bool ContainsCJK(string text)
    {
        return text.Any(c =>
            (c >= 0x4E00 && c <= 0x9FFF) ||   // CJK Unified Ideographs
            (c >= 0x3400 && c <= 0x4DBF) ||   // CJK Extension A
            (c >= 0xAC00 && c <= 0xD7AF) ||   // Hangul Syllables
            (c >= 0x3040 && c <= 0x309F) ||   // Hiragana
            (c >= 0x30A0 && c <= 0x30FF));    // Katakana
    }
}
