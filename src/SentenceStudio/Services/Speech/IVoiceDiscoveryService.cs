namespace SentenceStudio.Services.Speech;

/// <summary>
/// Represents a voice available for text-to-speech, with metadata for UI display.
/// </summary>
public class VoiceInfo
{
    public string VoiceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public string Accent { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PreviewUrl { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    
    /// <summary>
    /// Display name for UI (e.g., "Ji-Young (Female, Warm)")
    /// </summary>
    public string DisplayName => string.IsNullOrEmpty(Gender) 
        ? Name 
        : $"{Name} ({Gender}{(string.IsNullOrEmpty(Accent) ? "" : $", {Accent}")})";
}

/// <summary>
/// Service for discovering available voices from ElevenLabs API by language.
/// </summary>
public interface IVoiceDiscoveryService
{
    /// <summary>
    /// Gets available voices for a specific language.
    /// </summary>
    /// <param name="language">Language name (e.g., "German", "Korean")</param>
    /// <param name="forceRefresh">If true, bypasses cache and fetches fresh data</param>
    /// <returns>List of voices available for the language</returns>
    Task<List<VoiceInfo>> GetVoicesForLanguageAsync(string language, bool forceRefresh = false);
    
    /// <summary>
    /// Gets the ElevenLabs language code for a given language name.
    /// </summary>
    /// <param name="language">Language name (e.g., "German")</param>
    /// <returns>Language code (e.g., "de") or null if not supported</returns>
    string? GetLanguageCode(string language);
    
    /// <summary>
    /// Gets all supported languages.
    /// </summary>
    IReadOnlyList<string> SupportedLanguages { get; }
    
    /// <summary>
    /// Clears the voice cache for all languages.
    /// </summary>
    void ClearCache();
}
