namespace SentenceStudio.Services;

/// <summary>
/// Interface for language-specific text segmentation and paragraph detection
/// </summary>
public interface ILanguageSegmenter
{
    /// <summary>
    /// Gets the language code this segmenter handles (e.g., "ko", "en", "ja")
    /// </summary>
    string LanguageCode { get; }

    /// <summary>
    /// Gets the language name this segmenter handles (e.g., "Korean", "English")
    /// </summary>
    string LanguageName { get; }

    /// <summary>
    /// Determines if a line break should be preserved based on sentence patterns
    /// </summary>
    bool ShouldPreserveLineBreak(string currentLine, string nextLine);

    /// <summary>
    /// Detects if a line contains a transition word that indicates a paragraph break
    /// </summary>
    bool IsTransitionPoint(string line);

    /// <summary>
    /// Checks if a line appears to be an incomplete sentence
    /// </summary>
    bool IsIncompleteSentence(string line);

    /// <summary>
    /// Gets common sentence-ending patterns for this language
    /// </summary>
    IEnumerable<string> GetSentenceEndings();
    
    /// <summary>
    /// Gets the minimum word length to consider meaningful for this language.
    /// Korean: 2 (particles are single char), Latin languages: 3
    /// </summary>
    int GetMinimumWordLength();
    
    /// <summary>
    /// Gets trivial/greeting patterns that should be filtered out (e.g., "hello", "안녕하세요")
    /// </summary>
    IEnumerable<string> GetTrivialPatterns();
    
    /// <summary>
    /// Gets function words/particles that can be stripped for word matching (e.g., Korean 은/는/이/가)
    /// </summary>
    IEnumerable<string> GetFunctionWords();
}
