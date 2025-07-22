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

        // Find the character being spoken at the current time
        var currentCharIndex = GetCharacterIndexAtTime(currentTimeSeconds, characters);
        
        if (currentCharIndex == -1) 
            return -1;

        // Find which sentence this character belongs to by looking backwards for sentence boundaries
        var sentenceIndex = GetSentenceIndexForCharacter(currentCharIndex, fullTranscript);
        
        // BOUNDS CHECK: Ensure we don't return invalid indices
        var sentences = SplitIntoSentences(fullTranscript);
        if (sentenceIndex < 0 || sentenceIndex >= sentences.Count)
        {
            sentenceIndex = Math.Max(0, Math.Min(sentenceIndex, sentences.Count - 1));
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
    /// </summary>
    private int FindSentenceStartPosition(int sentenceIndex, List<string> sentences, string fullTranscript)
    {
        if (sentenceIndex == 0) return 0;
        
        var currentPos = 0;
        for (int i = 0; i < sentenceIndex; i++)
        {
            // Find this sentence in the transcript
            var sentenceText = sentences[i].Trim();
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
            return -1;

        var sentences = SplitIntoSentences(fullTranscript);
        var currentPos = 0;
        
        for (int sentenceIndex = 0; sentenceIndex < sentences.Count; sentenceIndex++)
        {
            var sentenceText = sentences[sentenceIndex];
            var sentenceLength = sentenceText.Length;
            
            // Check if the character falls within this sentence
            if (charIndex >= currentPos && charIndex < currentPos + sentenceLength)
            {
                return sentenceIndex;
            }
            
            currentPos += sentenceLength;
            
            // Skip sentence delimiters and whitespace - BUT COUNT THEM!
            while (currentPos < fullTranscript.Length && 
                   (IsSentenceDelimiter(fullTranscript[currentPos]) || char.IsWhiteSpace(fullTranscript[currentPos])))
            {
                currentPos++;
            }
        }
        
        // SAFETY CHECK: Don't return invalid indices
        return Math.Max(0, sentences.Count - 1);
    }

    /// <summary>
    /// Splits text into sentences using proper sentence boundaries
    /// </summary>
    private List<string> SplitIntoSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var sentences = new List<string>();
        var currentSentence = "";
        
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            currentSentence += c;
            
            if (IsSentenceDelimiter(c))
            {
                // Check if this is actually the end of a sentence (not an abbreviation, etc.)
                if (IsEndOfSentence(text, i))
                {
                    var trimmedSentence = currentSentence.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmedSentence))
                    {
                        sentences.Add(trimmedSentence);
                    }
                    currentSentence = "";
                }
            }
        }
        
        // Add any remaining text as the last sentence
        if (!string.IsNullOrWhiteSpace(currentSentence))
        {
            var trimmedSentence = currentSentence.Trim();
            if (!string.IsNullOrWhiteSpace(trimmedSentence))
            {
                sentences.Add(trimmedSentence);
            }
        }
        
        return sentences;
    }

    /// <summary>
    /// Checks if character is a sentence delimiter
    /// </summary>
    private bool IsSentenceDelimiter(char c)
    {
        return c == '.' || c == '!' || c == '?' || c == '。' || c == '！' || c == '？'; // Include Korean punctuation
    }

    /// <summary>
    /// Determines if a delimiter actually ends a sentence (vs abbreviation, etc.)
    /// Updated to work with international text including Korean, Chinese, Japanese, etc.
    /// </summary>
    private bool IsEndOfSentence(string text, int delimiterIndex)
    {
        // If at end of text, it's definitely end of sentence
        if (delimiterIndex + 1 >= text.Length) 
            return true;
        
        var delimiter = text[delimiterIndex];
        var nextChar = text[delimiterIndex + 1];
        
        // Korean punctuation (。！？) are almost always sentence endings
        if (delimiter == '。' || delimiter == '！' || delimiter == '？')
            return true;
        
        // For Western punctuation, be more lenient:
        // 1. If followed by whitespace, it's likely a sentence end
        // 2. Don't require capital letters (for international text)
        if (char.IsWhiteSpace(nextChar))
            return true;
        
        // If not followed by whitespace, check for some obvious non-sentence cases
        // like decimal numbers (3.14) or abbreviations (Mr.)
        if (delimiter == '.' && char.IsDigit(nextChar))
            return false;
        
        // Default to true for ? and ! even without whitespace (more lenient)
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
