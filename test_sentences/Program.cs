using System;
using System.Collections.Generic;

// Quick test to verify sentence splitting works correctly
var testText = "안녕하세요! 오늘은 좋은 날이에요. 여러분은 어떤 계획이 있나요? 저도 마찬가지입니다. 포기해도 괜찮아요.";

//Console.Writeline($"Test text: {testText}");
//Console.Writeline($"Length: {testText.Length}");

var sentences = SplitIntoSentences(testText);
//Console.Writeline($"Found {sentences.Count} sentences:");

for (int i = 0; i < sentences.Count; i++)
{
    //Console.Writeline($"  {i}: '{sentences[i]}'");
}

static List<string> SplitIntoSentences(string text)
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

static bool IsSentenceDelimiter(char c)
{
    return c == '.' || c == '!' || c == '?' || c == '。' || c == '！' || c == '？'; // Include Korean punctuation
}

static bool IsEndOfSentence(string text, int delimiterIndex)
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
