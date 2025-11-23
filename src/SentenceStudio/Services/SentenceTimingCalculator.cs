using SentenceStudio.Models;
using ElevenLabs;
using ElevenLabs.Models;
using ElevenLabs.TextToSpeech;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace SentenceStudio.Services;

/// <summary>
/// Provides real-time sentence detection based on character-level timestamps
/// No pre-calculation needed - determines current sentence dynamically from audio position
/// </summary>
public class SentenceTimingCalculator
{
    private readonly ILogger<SentenceTimingCalculator> _logger;
    private static int _splitCallCount = 0;
    private static readonly Stopwatch _perfWatch = new Stopwatch();

    // 🚀 PERFORMANCE: Cache split sentences to avoid repeated parsing
    private string _cachedTranscript = null;
    private List<string> _cachedSentences = null;

    public SentenceTimingCalculator(ILogger<SentenceTimingCalculator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets or splits sentences with caching to avoid repeated expensive parsing
    /// </summary>
    private List<string> GetSentences(string fullTranscript)
    {
        if (_cachedTranscript == fullTranscript && _cachedSentences != null)
        {
            return _cachedSentences;
        }

        _splitCallCount++;
        if (_splitCallCount % 10 == 0)
        {
            _logger.LogWarning("⚠️ PERFORMANCE: SplitIntoSentences called {Count} times", _splitCallCount);
        }

        _cachedTranscript = fullTranscript;
        _cachedSentences = SplitIntoSentences(fullTranscript);

        _logger.LogInformation("📚 Cached {Count} sentences from transcript", _cachedSentences.Count);

        return _cachedSentences;
    }
    /// <summary>
    /// Gets the current sentence index based on audio playback position using character timestamps
    /// This is much more accurate than pre-calculating sentence boundaries
    /// </summary>
    /// <param name="currentTimeSeconds">Current audio playback time in seconds</param>
    /// <param name="characters">Character-level timing data from ElevenLabs</param>
    /// <param name="fullTranscript">The complete transcript text</param>
    /// <returns>Current sentence index, or -1 if not found</returns>
    public int GetCurrentSentenceIndex(double currentTimeSeconds, TimestampedTranscriptCharacter[] characters, string fullTranscript)
    {
        _perfWatch.Restart();

        if (characters == null || characters.Length == 0 || string.IsNullOrEmpty(fullTranscript))
        {
            _logger.LogWarning("⚠️ GetCurrentSentenceIndex: Invalid inputs");
            return -1;
        }

        var currentCharIndex = GetCharacterIndexAtTime(currentTimeSeconds, characters);
        if (currentCharIndex == -1)
        {
            _logger.LogDebug("🔍 No character found for time {Time:F2}s", currentTimeSeconds);
            return -1;
        }

        var sentenceIndex = GetSentenceIndexForCharacter(currentCharIndex, fullTranscript);

        _perfWatch.Stop();
        if (_perfWatch.ElapsedMilliseconds > 10)
        {
            _logger.LogWarning("⚠️ SLOW GetCurrentSentenceIndex took {Ms}ms for time {Time:F2}s",
                _perfWatch.ElapsedMilliseconds, currentTimeSeconds);
        }

        var sentences = GetSentences(fullTranscript);
        if (sentenceIndex < 0 || sentenceIndex >= sentences.Count)
        {
            _logger.LogWarning("⚠️ Invalid sentence index {Index} for char {Char} (time {Time:F2}s), sentences.Count={Count}",
                sentenceIndex, currentCharIndex, currentTimeSeconds, sentences.Count);
            return -1;
        }

        return sentenceIndex;
    }

    /// <summary>
    /// Gets precise timing for a specific sentence by analyzing character timestamps in real-time
    /// </summary>
    /// <param name="sentenceIndex">The sentence index to get timing for</param>
    /// <param name="characters">Character-level timing data</param>
    /// <param name="fullTranscript">Complete transcript</param>
    /// <returns>Timing info for the sentence, or null if not found</returns>
    public SentenceTimingInfo? GetSentenceTimingInfo(int sentenceIndex, TimestampedTranscriptCharacter[] characters, string fullTranscript)
    {
        if (characters == null || characters.Length == 0 || string.IsNullOrEmpty(fullTranscript))
            return null;

        var sentences = GetSentences(fullTranscript);

        if (sentenceIndex < 0 || sentenceIndex >= sentences.Count)
            return null;

        var sentence = sentences[sentenceIndex];

        // ORIGINAL APPROACH: Find exact character positions for this sentence in the transcript
        var sentenceStartPos = FindSentenceStartPosition(sentenceIndex, sentences, fullTranscript);
        var sentenceEndPos = FindSentenceEndPosition(sentenceIndex, sentences, fullTranscript);

        if (sentenceStartPos == -1 || sentenceEndPos == -1)
            return null;

        // Ensure indices are within character array bounds
        var startCharIndex = Math.Max(0, Math.Min(sentenceStartPos, characters.Length - 1));
        var endCharIndex = Math.Max(0, Math.Min(sentenceEndPos, characters.Length - 1));

        var startTime = startCharIndex < characters.Length ? characters[startCharIndex].StartTime : 0;
        var endTime = endCharIndex < characters.Length ? characters[endCharIndex].EndTime :
                     characters.Length > 0 ? characters[^1].EndTime : 0;

        return new SentenceTimingInfo
        {
            SentenceIndex = sentenceIndex,
            Text = sentence,
            StartTime = startTime,
            EndTime = endTime,
            StartCharIndex = startCharIndex,
            EndCharIndex = endCharIndex
        };
    }

    /// <summary>
    /// Finds the character index that corresponds to the given audio time
    /// </summary>
    private int GetCharacterIndexAtTime(double timeSeconds, TimestampedTranscriptCharacter[] characters)
    {
        for (int i = 0; i < characters.Length; i++)
        {
            var character = characters[i];
            if (timeSeconds >= character.StartTime && timeSeconds <= character.EndTime)
            {
                return i;
            }
        }

        // If exact match not found, find the closest character
        var closestIndex = -1;
        var minDifference = double.MaxValue;

        for (int i = 0; i < characters.Length; i++)
        {
            var character = characters[i];
            var difference = Math.Abs(timeSeconds - character.StartTime);

            if (difference < minDifference)
            {
                minDifference = difference;
                closestIndex = i;
            }
        }

        return closestIndex;
    }

    /// <summary>
    /// Finds the exact character position where a sentence starts in the full transcript
    /// 🎯 CRITICAL: Skips PARAGRAPH_BREAK markers - they don't exist in the transcript
    /// </summary>
    private int FindSentenceStartPosition(int sentenceIndex, List<string> sentences, string fullTranscript)
    {
        if (sentenceIndex == 0) return 0;

        var currentPos = 0;
        for (int i = 0; i < sentenceIndex; i++)
        {
            var sentenceText = sentences[i].Trim();

            // 🏴‍☠️ Skip PARAGRAPH_BREAK markers
            if (sentenceText == "PARAGRAPH_BREAK")
                continue;

            // Find this sentence in the transcript
            var foundPos = fullTranscript.IndexOf(sentenceText, currentPos, StringComparison.Ordinal);

            if (foundPos == -1)
                return -1;

            currentPos = foundPos + sentenceText.Length;

            // Skip any sentence delimiters and whitespace
            while (currentPos < fullTranscript.Length &&
                   (IsSentenceDelimiter(fullTranscript[currentPos]) || char.IsWhiteSpace(fullTranscript[currentPos])))
            {
                currentPos++;
            }
        }

        return currentPos;
    }

    /// <summary>
    /// Finds the exact character position where a sentence ends in the full transcript
    /// </summary>
    private int FindSentenceEndPosition(int sentenceIndex, List<string> sentences, string fullTranscript)
    {
        var startPos = FindSentenceStartPosition(sentenceIndex, sentences, fullTranscript);
        if (startPos == -1) return -1;

        var sentenceText = sentences[sentenceIndex].Trim();
        var foundPos = fullTranscript.IndexOf(sentenceText, startPos, StringComparison.Ordinal);

        if (foundPos == -1)
            return -1;

        return foundPos + sentenceText.Length - 1;
    }

    /// <summary>
    /// Determines which sentence the character at the given index belongs to
    /// </summary>
    private int GetSentenceIndexForCharacter(int charIndex, string fullTranscript)
    {
        if (charIndex < 0 || charIndex >= fullTranscript.Length)
        {
            _logger.LogWarning("[SentenceTimingCalculator] Char index {CharIndex} out of bounds for transcript length {Length}", charIndex, fullTranscript.Length);
            return -1;
        }

        var sentences = GetSentences(fullTranscript);
        var currentPos = 0;

        for (int sentenceIndex = 0; sentenceIndex < sentences.Count; sentenceIndex++)
        {
            var sentenceText = sentences[sentenceIndex];

            // 🏴‍☠️ Skip PARAGRAPH_BREAK markers - they don't exist in the actual transcript
            if (sentenceText == "PARAGRAPH_BREAK")
                continue;

            var sentenceLength = sentenceText.Length;

            if (charIndex >= currentPos && charIndex < currentPos + sentenceLength)
            {
                return sentenceIndex;
            }

            currentPos += sentenceLength;

            // Skip whitespace and delimiters between sentences
            while (currentPos < fullTranscript.Length &&
                   (IsSentenceDelimiter(fullTranscript[currentPos]) || char.IsWhiteSpace(fullTranscript[currentPos])))
            {
                currentPos++;
            }
        }

        _logger.LogWarning("[SentenceTimingCalculator] Could not map char index {CharIndex} to a sentence", charIndex);
        return -1;
    }

    /// <summary>
    /// Splits text into sentences WITH paragraph break markers to match ReadingPage
    /// 🎯 CRITICAL: Must match ReadingPage.SplitIntoSentences exactly for correct index mapping
    /// </summary>
    /// <summary>
    /// Split transcript into sentences.
    /// 🏴‍☠️ Does NOT include PARAGRAPH_BREAK markers - those are for rendering only!
    /// Returns only actual sentences that can be looked up in the transcript.
    /// </summary>
    public static List<string> SplitIntoSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        // Split by common sentence delimiters, ignoring paragraph breaks
        var cleanedText = text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

        var sentences = cleanedText
            .Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s + (s.EndsWith('.') || s.EndsWith('!') || s.EndsWith('?') ? "" : "."))
            .ToList();

        return sentences;
    }

    public static bool IsSentenceDelimiter(char c)
    {
        return c == '.' || c == '!' || c == '?' || c == '。' || c == '！' || c == '？';
    }

    public static bool IsEndOfSentence(string text, int delimiterIndex)
    {
        if (delimiterIndex + 1 >= text.Length)
            return true;
        var delimiter = text[delimiterIndex];
        var nextChar = text[delimiterIndex + 1];
        if (delimiter == '。' || delimiter == '！' || delimiter == '？')
            return true;
        if (char.IsWhiteSpace(nextChar))
            return true;
        if (delimiter == '.' && char.IsDigit(nextChar))
            return false;
        if (delimiter == '?' || delimiter == '!')
            return true;
        return false;
    }

    #region Legacy Methods (kept for compatibility during transition)

    /// <summary>
    /// DEPRECATED: Use GetCurrentSentenceIndex and GetSentenceTimingInfo instead
    /// This method is kept for compatibility but should not be used for real-time playback
    /// </summary>
    [Obsolete("Use GetCurrentSentenceIndex for real-time sentence detection")]
    public List<SentenceTimingInfo> CalculateSentenceTimings(
        List<string> sentences,
        TimestampedTranscriptCharacter[] characters,
        string fullTranscript)
    {
        // Return empty list to encourage migration to new approach
        _logger.LogWarning("🏴‍☠️ WARNING: Using deprecated CalculateSentenceTimings. Use GetCurrentSentenceIndex instead!");
        return new List<SentenceTimingInfo>();
    }

    #endregion
}
