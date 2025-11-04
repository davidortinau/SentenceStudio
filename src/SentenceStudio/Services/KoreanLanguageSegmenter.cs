namespace SentenceStudio.Services;

/// <summary>
/// Korean-specific text segmentation and paragraph detection
/// </summary>
public class KoreanLanguageSegmenter : ILanguageSegmenter
{
    public string LanguageCode => "ko";
    public string LanguageName => "Korean";

    // Common Korean transition words that indicate paragraph breaks
    private readonly HashSet<string> _transitionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "그리고", "그래서", "그럼", "하지만", "그런데",
        "그러나", "또한", "따라서", "그러므로", "예를 들어",
        "즉", "다시 말해", "마지막으로", "먼저", "우선",
        "또", "그리고", "게다가", "반면에", "물론"
    };

    // Common Korean sentence endings (polite/casual forms)
    // Using longer patterns to avoid false matches on single characters
    private readonly HashSet<string> _sentenceEndings = new(StringComparer.OrdinalIgnoreCase)
    {
        "습니다", "ㅂ니다",
        "어요", "아요", "해요", "예요", "지요", "죠",
        "었어요", "았어요", "했어요",
        "군요", "네요", "세요",
        "ㄴ가요", "나요",
        "습니까", "ㅂ니까",
        "이에요", "예요"
    };

    // Standalone patterns (greetings, closings, etc.)
    private readonly HashSet<string> _standalonePatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "안녕하세요", "감사합니다", "여러분", "[음악]", "♪", "♫"
    };

    public bool ShouldPreserveLineBreak(string currentLine, string nextLine)
    {
        if (string.IsNullOrWhiteSpace(currentLine))
            return true;

        // If next line is empty, preserve the explicit paragraph break
        if (string.IsNullOrWhiteSpace(nextLine))
            return true;

        var trimmedCurrent = currentLine.Trim();
        var trimmedNext = nextLine.Trim();

        // Preserve if current line is standalone (greeting/closing)
        if (_standalonePatterns.Any(p => trimmedCurrent.Contains(p)))
            return true;

        // Preserve if next line starts with transition word (topic change)
        if (IsTransitionPoint(trimmedNext))
            return true;

        // DON'T preserve if current line is incomplete - always merge with next
        if (IsIncompleteSentence(trimmedCurrent))
            return false;

        // If current line is complete BUT next line doesn't signal a topic change,
        // DON'T preserve - merge into same paragraph for better readability
        return false;
    }

    public bool IsTransitionPoint(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var trimmedLine = line.Trim();

        // Check if line starts with a transition word
        foreach (var transition in _transitionWords)
        {
            if (trimmedLine.StartsWith(transition, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public bool IsIncompleteSentence(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        var trimmedLine = line.Trim();

        // First, check for punctuation endings - these are definitive
        if (trimmedLine.EndsWith(".") || trimmedLine.EndsWith("?") ||
            trimmedLine.EndsWith("!") || trimmedLine.EndsWith("。"))
            return false;

        // If no punctuation, it's incomplete
        return true;
    }

    public IEnumerable<string> GetSentenceEndings()
    {
        return _sentenceEndings;
    }
}
