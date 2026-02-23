namespace SentenceStudio.Services.LanguageSegmentation;

/// <summary>
/// Base segmenter for Latin-script languages (English, French, German, Spanish, etc.)
/// Provides common functionality that can be overridden by language-specific implementations.
/// </summary>
public class GenericLatinSegmenter : ILanguageSegmenter
{
    public virtual string LanguageCode => "en";
    public virtual string LanguageName => "English";

    // Common transition words for Latin-script languages
    protected virtual HashSet<string> TransitionWords => new(StringComparer.OrdinalIgnoreCase)
    {
        "however", "therefore", "moreover", "furthermore", "additionally",
        "consequently", "nevertheless", "meanwhile", "finally", "firstly",
        "secondly", "lastly", "in addition", "on the other hand", "for example",
        "in conclusion", "as a result", "in contrast", "similarly"
    };

    // Standard sentence endings for Latin languages
    protected virtual HashSet<string> SentenceEndings => new(StringComparer.OrdinalIgnoreCase)
    {
        ".", "!", "?", "...", "!!", "?!"
    };

    // Common trivial patterns
    protected virtual HashSet<string> TrivialPatterns => new(StringComparer.OrdinalIgnoreCase)
    {
        "hello", "hi", "yes", "no", "thanks", "thank you", "sorry",
        "okay", "ok", "bye", "goodbye", "please", "welcome"
    };

    // Common function words (articles, prepositions, etc.)
    protected virtual string[] FunctionWords => new[]
    {
        "the", "a", "an", "of", "to", "in", "for", "on", "with", "at",
        "by", "from", "as", "is", "was", "are", "were", "been", "be",
        "have", "has", "had", "do", "does", "did", "will", "would",
        "could", "should", "may", "might", "must", "shall"
    };

    public virtual bool ShouldPreserveLineBreak(string currentLine, string nextLine)
    {
        if (string.IsNullOrWhiteSpace(currentLine))
            return true;

        if (string.IsNullOrWhiteSpace(nextLine))
            return true;

        var trimmedCurrent = currentLine.Trim();
        var trimmedNext = nextLine.Trim();

        // Preserve if next line starts with transition word
        if (IsTransitionPoint(trimmedNext))
            return true;

        // Don't preserve if current line is incomplete
        if (IsIncompleteSentence(trimmedCurrent))
            return false;

        return false;
    }

    public virtual bool IsTransitionPoint(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var trimmedLine = line.Trim();
        var lowerLine = trimmedLine.ToLowerInvariant();

        foreach (var transition in TransitionWords)
        {
            if (lowerLine.StartsWith(transition, StringComparison.OrdinalIgnoreCase))
            {
                // Ensure it's a word boundary
                if (lowerLine.Length == transition.Length ||
                    !char.IsLetter(lowerLine[transition.Length]))
                    return true;
            }
        }

        return false;
    }

    public virtual bool IsIncompleteSentence(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        var trimmedLine = line.Trim();

        // Check for standard sentence-ending punctuation
        foreach (var ending in SentenceEndings)
        {
            if (trimmedLine.EndsWith(ending))
                return false;
        }

        return true;
    }

    public virtual IEnumerable<string> GetSentenceEndings() => SentenceEndings;

    public virtual int GetMinimumWordLength() => 3;

    public virtual IEnumerable<string> GetTrivialPatterns() => TrivialPatterns;

    public virtual IEnumerable<string> GetFunctionWords() => FunctionWords;
}
