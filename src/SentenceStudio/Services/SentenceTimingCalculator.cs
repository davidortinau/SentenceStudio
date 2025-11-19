using SentenceStudio.Models;
using ElevenLabs;
using ElevenLabs.Models;
using ElevenLabs.TextToSpeech;

namespace SentenceStudio.Services;

/// <summary>
/// Provides real-time sentence detection based on character-level timestamps
/// No pre-calculation needed - determines current sentence dynamically from audio position
/// </summary>
public class SentenceTimingCalculator
{
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
        if (characters == null || characters.Length == 0 || string.IsNullOrEmpty(fullTranscript))
            return -1;
        var currentCharIndex = GetCharacterIndexAtTime(currentTimeSeconds, characters);
        if (currentCharIndex == -1)
        {
            System.Diagnostics.Debug.WriteLine($"[SentenceTimingCalculator] No character found for time {currentTimeSeconds:F2}s");
            return -1;
        }
        var sentenceIndex = GetSentenceIndexForCharacter(currentCharIndex, fullTranscript);
        var sentences = SplitIntoSentences(fullTranscript);
        if (sentenceIndex < 0 || sentenceIndex >= sentences.Count)
        {
            System.Diagnostics.Debug.WriteLine($"[SentenceTimingCalculator] Invalid sentence index {sentenceIndex} for char {currentCharIndex} (time {currentTimeSeconds:F2}s), sentences.Count={sentences.Count}");
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

        var sentences = SplitIntoSentences(fullTranscript);
        
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
    /// 🎯 CRITICAL: Skips PARAGRAPH_BREAK markers - they don't exist in the transcript
    /// </summary>
    private int FindSentenceEndPosition(int sentenceIndex, List<string> sentences, string fullTranscript)
    {
        var startPos = FindSentenceStartPosition(sentenceIndex, sentences, fullTranscript);
        if (startPos == -1) return -1;
        
        var sentenceText = sentences[sentenceIndex].Trim();
        
        // 🏴‍☠️ PARAGRAPH_BREAK markers should never be queried for timing
        if (sentenceText == "PARAGRAPH_BREAK")
            return -1;
        
        var foundPos = fullTranscript.IndexOf(sentenceText, startPos, StringComparison.Ordinal);
        
        if (foundPos == -1)
            return -1;
        
        return foundPos + sentenceText.Length - 1;
    }

    /// <summary>
    /// Determines which sentence the character at the given index belongs to
    /// 🎯 CRITICAL: Skips PARAGRAPH_BREAK markers when calculating character positions
    /// </summary>
    private int GetSentenceIndexForCharacter(int charIndex, string fullTranscript)
    {
        if (charIndex < 0 || charIndex >= fullTranscript.Length)
        {
            System.Diagnostics.Debug.WriteLine($"[SentenceTimingCalculator] Char index {charIndex} out of bounds for transcript length {fullTranscript.Length}");
            return -1;
        }
        
        var sentences = SplitIntoSentences(fullTranscript);
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
        
        System.Diagnostics.Debug.WriteLine($"[SentenceTimingCalculator] Could not map char index {charIndex} to a sentence");
        return -1;
    }

    /// <summary>
    /// Splits text into sentences WITH paragraph break markers to match ReadingPage
    /// 🎯 CRITICAL: Must match ReadingPage.SplitIntoSentences exactly for correct index mapping
    /// </summary>
    public static List<string> SplitIntoSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        // 🏴‍☠️ MATCH ReadingPage logic: Preserve paragraph structure
        var paragraphs = text.Split(new[] { "\r\n\r\n", "\n\n", "\r\r" }, StringSplitOptions.RemoveEmptyEntries);
        var sentences = new List<string>();

        foreach (var paragraph in paragraphs)
        {
            // Clean up the paragraph and split into sentences
            var paragraphText = paragraph.Trim().Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

            // Split by sentence delimiters
            var paragraphSentences = paragraphText
                .Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s + (s.EndsWith('.') || s.EndsWith('!') || s.EndsWith('?') ? "" : "."))
                .ToList();

            // Add sentences from this paragraph
            sentences.AddRange(paragraphSentences);

            // 🏴‍☠️ Add paragraph break marker if this isn't the last paragraph
            if (paragraph != paragraphs.Last() && paragraphSentences.Any())
            {
                sentences.Add("PARAGRAPH_BREAK"); // Special marker for paragraph breaks
            }
        }

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
        System.Diagnostics.Debug.WriteLine("🏴‍☠️ WARNING: Using deprecated CalculateSentenceTimings. Use GetCurrentSentenceIndex instead!");
        return new List<SentenceTimingInfo>();
    }
    
    #endregion
}
