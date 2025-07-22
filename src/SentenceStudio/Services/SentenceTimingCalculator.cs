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
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetCurrentSentenceIndex: Invalid inputs at {currentTimeSeconds:F3}s - chars: {characters?.Length ?? 0}, transcript: {fullTranscript?.Length ?? 0}");
            return -1;
        }

        // Find the character being spoken at the current time
        var currentCharIndex = GetCharacterIndexAtTime(currentTimeSeconds, characters);
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetCurrentSentenceIndex: At {currentTimeSeconds:F3}s -> character index {currentCharIndex}");
        
        if (currentCharIndex == -1) 
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetCurrentSentenceIndex: No character found at {currentTimeSeconds:F3}s");
            return -1;
        }

        // Find which sentence this character belongs to by looking backwards for sentence boundaries
        var sentenceIndex = GetSentenceIndexForCharacter(currentCharIndex, fullTranscript);
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetCurrentSentenceIndex: Character {currentCharIndex} belongs to sentence {sentenceIndex}");
        
        // BOUNDS CHECK: Ensure we don't return invalid indices
        var sentences = SplitIntoSentences(fullTranscript);
        if (sentenceIndex < 0 || sentenceIndex >= sentences.Count)
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetCurrentSentenceIndex: ⚠️ INVALID INDEX {sentenceIndex}! Total sentences: {sentences.Count}. Clamping to valid range.");
            sentenceIndex = Math.Max(0, Math.Min(sentenceIndex, sentences.Count - 1));
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetCurrentSentenceIndex: Clamped to index {sentenceIndex}");
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
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetSentenceTimingInfo: Invalid inputs - characters: {characters?.Length ?? 0}, transcript length: {fullTranscript?.Length ?? 0}");
            return null;
        }

        var sentences = SplitIntoSentences(fullTranscript);
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetSentenceTimingInfo: Split into {sentences.Count} sentences, requesting index {sentenceIndex}");
        
        if (sentenceIndex < 0 || sentenceIndex >= sentences.Count) 
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetSentenceTimingInfo: Invalid sentence index {sentenceIndex}, only have {sentences.Count} sentences");
            return null;
        }

        var sentence = sentences[sentenceIndex];
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetSentenceTimingInfo: Sentence {sentenceIndex}: '{sentence.Substring(0, Math.Min(50, sentence.Length))}...'");
        
        // ORIGINAL APPROACH: Find exact character positions for this sentence in the transcript
        var sentenceStartPos = FindSentenceStartPosition(sentenceIndex, sentences, fullTranscript);
        var sentenceEndPos = FindSentenceEndPosition(sentenceIndex, sentences, fullTranscript);
        
        if (sentenceStartPos == -1 || sentenceEndPos == -1)
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetSentenceTimingInfo: Could not find character positions for sentence {sentenceIndex}");
            return null;
        }
        
        // Ensure indices are within character array bounds
        var startCharIndex = Math.Max(0, Math.Min(sentenceStartPos, characters.Length - 1));
        var endCharIndex = Math.Max(0, Math.Min(sentenceEndPos, characters.Length - 1));

        var startTime = startCharIndex < characters.Length ? characters[startCharIndex].StartTime : 0;
        var endTime = endCharIndex < characters.Length ? characters[endCharIndex].EndTime : 
                     characters.Length > 0 ? characters[^1].EndTime : 0;

        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetSentenceTimingInfo: Sentence {sentenceIndex} at chars {startCharIndex}-{endCharIndex} (transcript pos {sentenceStartPos}-{sentenceEndPos}), time {startTime:F2}s-{endTime:F2}s");

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
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetCharacterIndexAtTime: Looking for time {timeSeconds:F3}s in {characters.Length} characters");
        
        for (int i = 0; i < characters.Length; i++)
        {
            var character = characters[i];
            if (timeSeconds >= character.StartTime && timeSeconds <= character.EndTime)
            {
                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetCharacterIndexAtTime: Found exact match at index {i}, char '{character.Character}', time {character.StartTime:F3}s-{character.EndTime:F3}s");
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
        
        if (closestIndex >= 0)
        {
            var closestChar = characters[closestIndex];
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetCharacterIndexAtTime: No exact match, closest is index {closestIndex}, char '{closestChar.Character}', time {closestChar.StartTime:F3}s-{closestChar.EndTime:F3}s (diff: {minDifference:F3}s)");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetCharacterIndexAtTime: No character found for time {timeSeconds:F3}s");
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
            {
                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ FindSentenceStartPosition: Could not find sentence {i} in transcript");
                return -1;
            }
            
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
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ FindSentenceEndPosition: Could not find sentence {sentenceIndex} in transcript");
            return -1;
        }
        
        return foundPos + sentenceText.Length - 1;
    }

    /// <summary>
    /// Determines which sentence the character at the given index belongs to
    /// </summary>
    private int GetSentenceIndexForCharacter(int charIndex, string fullTranscript)
    {
        if (charIndex < 0 || charIndex >= fullTranscript.Length) 
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetSentenceIndexForCharacter: Invalid charIndex {charIndex} for transcript length {fullTranscript?.Length ?? 0}");
            return -1;
        }

        var sentences = SplitIntoSentences(fullTranscript);
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetSentenceIndexForCharacter: Split text into {sentences.Count} sentences, looking for char {charIndex}");
        
        var currentPos = 0;
        
        for (int sentenceIndex = 0; sentenceIndex < sentences.Count; sentenceIndex++)
        {
            var sentenceText = sentences[sentenceIndex];
            var sentenceLength = sentenceText.Length;
            
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetSentenceIndexForCharacter: Sentence {sentenceIndex} at pos {currentPos}-{currentPos + sentenceLength - 1}: '{sentenceText.Substring(0, Math.Min(30, sentenceText.Length))}...'");
            
            // Check if the character falls within this sentence
            if (charIndex >= currentPos && charIndex < currentPos + sentenceLength)
            {
                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetSentenceIndexForCharacter: ✅ Character {charIndex} found in sentence {sentenceIndex} (pos {currentPos}-{currentPos + sentenceLength - 1})");
                return sentenceIndex;
            }
            
            currentPos += sentenceLength;
            
            // Skip sentence delimiters and whitespace - BUT COUNT THEM!
            var skippedChars = 0;
            while (currentPos < fullTranscript.Length && 
                   (IsSentenceDelimiter(fullTranscript[currentPos]) || char.IsWhiteSpace(fullTranscript[currentPos])))
            {
                currentPos++;
                skippedChars++;
            }
            
            if (skippedChars > 0)
            {
                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetSentenceIndexForCharacter: Skipped {skippedChars} delimiter/whitespace chars, now at pos {currentPos}");
            }
        }
        
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetSentenceIndexForCharacter: ⚠️ Character {charIndex} not found in any sentence! Returning last sentence {sentences.Count - 1}");
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ GetSentenceIndexForCharacter: Final position was {currentPos}, transcript length is {fullTranscript.Length}");
        
        // SAFETY CHECK: Don't return invalid indices
        return Math.Max(0, sentences.Count - 1);
    }

    /// <summary>
    /// Splits text into sentences using proper sentence boundaries
    /// </summary>
    private List<string> SplitIntoSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            System.Diagnostics.Debug.WriteLine("🏴‍☠️ SplitIntoSentences: Empty or null text");
            return new List<string>();
        }

        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ SplitIntoSentences: Input text length: {text.Length}");
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ SplitIntoSentences: First 100 chars: '{text.Substring(0, Math.Min(100, text.Length))}'");

        var sentences = new List<string>();
        var currentSentence = "";
        var delimiterCount = 0;
        
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            currentSentence += c;
            
            if (IsSentenceDelimiter(c))
            {
                delimiterCount++;
                System.Diagnostics.Debug.WriteLine($"🏴‍☠️ SplitIntoSentences: Found delimiter '{c}' at position {i} (total delimiters: {delimiterCount})");
                
                // Check if this is actually the end of a sentence (not an abbreviation, etc.)
                if (IsEndOfSentence(text, i))
                {
                    var trimmedSentence = currentSentence.Trim();
                    sentences.Add(trimmedSentence);
                    System.Diagnostics.Debug.WriteLine($"🏴‍☠️ SplitIntoSentences: Added sentence {sentences.Count}: '{trimmedSentence.Substring(0, Math.Min(50, trimmedSentence.Length))}...' (length: {trimmedSentence.Length})");
                    currentSentence = "";
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"🏴‍☠️ SplitIntoSentences: Delimiter '{c}' at {i} NOT considered end of sentence");
                }
            }
        }
        
        // Add any remaining text as the last sentence
        if (!string.IsNullOrWhiteSpace(currentSentence))
        {
            var trimmedSentence = currentSentence.Trim();
            sentences.Add(trimmedSentence);
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ SplitIntoSentences: Added final sentence {sentences.Count}: '{trimmedSentence.Substring(0, Math.Min(50, trimmedSentence.Length))}...' (length: {trimmedSentence.Length})");
        }
        
        var finalSentences = sentences.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ SplitIntoSentences: Final result: {finalSentences.Count} sentences from {delimiterCount} delimiters");
        
        return finalSentences;
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
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ IsEndOfSentence: Delimiter at end of text -> TRUE");
            return true;
        }
        
        var delimiter = text[delimiterIndex];
        var nextChar = text[delimiterIndex + 1];
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ IsEndOfSentence: Delimiter '{delimiter}' at {delimiterIndex}, next char: '{nextChar}' (IsWhiteSpace: {char.IsWhiteSpace(nextChar)})");
        
        // Korean punctuation (。！？) are almost always sentence endings
        if (delimiter == '。' || delimiter == '！' || delimiter == '？')
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ IsEndOfSentence: Korean punctuation '{delimiter}' -> TRUE");
            return true;
        }
        
        // For Western punctuation, be more lenient:
        // 1. If followed by whitespace, it's likely a sentence end
        // 2. Don't require capital letters (for international text)
        if (char.IsWhiteSpace(nextChar))
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ IsEndOfSentence: Whitespace after delimiter -> TRUE");
            return true;
        }
        
        // If not followed by whitespace, check for some obvious non-sentence cases
        // like decimal numbers (3.14) or abbreviations (Mr.)
        if (delimiter == '.' && char.IsDigit(nextChar))
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ IsEndOfSentence: Decimal number (digit after period) -> FALSE");
            return false;
        }
        
        // Default to true for ? and ! even without whitespace (more lenient)
        if (delimiter == '?' || delimiter == '!')
        {
            System.Diagnostics.Debug.WriteLine($"🏴‍☠️ IsEndOfSentence: Question/exclamation mark without whitespace -> TRUE");
            return true;
        }
        
        System.Diagnostics.Debug.WriteLine($"🏴‍☠️ IsEndOfSentence: Period without whitespace -> FALSE");
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
