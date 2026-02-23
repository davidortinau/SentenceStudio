namespace SentenceStudio.Services.LanguageSegmentation;

/// <summary>
/// Factory for obtaining language-specific segmenters.
/// Resolves the appropriate ILanguageSegmenter based on language name or code.
/// </summary>
public class LanguageSegmenterFactory
{
    private readonly Dictionary<string, ILanguageSegmenter> _segmenters;
    private readonly ILanguageSegmenter _defaultSegmenter;

    public LanguageSegmenterFactory(IEnumerable<ILanguageSegmenter> segmenters)
    {
        _segmenters = new Dictionary<string, ILanguageSegmenter>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var segmenter in segmenters)
        {
            // Register by both code and name for flexible lookup
            _segmenters[segmenter.LanguageCode] = segmenter;
            _segmenters[segmenter.LanguageName] = segmenter;
        }
        
        // Default to English/Latin segmenter
        _defaultSegmenter = _segmenters.Values.FirstOrDefault(s => s.LanguageCode == "en") 
            ?? new GenericLatinSegmenter();
    }

    /// <summary>
    /// Gets the appropriate segmenter for the specified language.
    /// Falls back to the generic Latin segmenter if no specific segmenter is registered.
    /// </summary>
    /// <param name="language">Language name (e.g., "Korean", "German") or code (e.g., "ko", "de")</param>
    public ILanguageSegmenter GetSegmenter(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return _defaultSegmenter;

        if (_segmenters.TryGetValue(language, out var segmenter))
            return segmenter;

        return _defaultSegmenter;
    }

    /// <summary>
    /// Gets all registered segmenters.
    /// </summary>
    public IEnumerable<ILanguageSegmenter> GetAllSegmenters() => _segmenters.Values.Distinct();

    /// <summary>
    /// Gets all supported language names.
    /// </summary>
    public IEnumerable<string> GetSupportedLanguages() => 
        _segmenters.Values.Distinct().Select(s => s.LanguageName);

    /// <summary>
    /// Checks if a language has a specific (non-default) segmenter.
    /// </summary>
    public bool HasSpecificSegmenter(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return false;

        return _segmenters.ContainsKey(language);
    }
}
