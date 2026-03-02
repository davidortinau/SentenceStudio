using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

/// <summary>
/// Manages speech voice preferences for learning activities.
/// Supports per-language voice selection with fallback to global default.
/// </summary>
public class SpeechVoicePreferences
{
    private readonly ILogger<SpeechVoicePreferences> _logger;
    private readonly IPreferencesService _preferences;

    private const string KEY_GLOBAL_VOICE_ID = "global_speech_voice_id";
    private const string KEY_VOICE_PREFIX = "speech_voice_"; // Per-language: speech_voice_Korean, speech_voice_German
    private const string LEGACY_KEY_QUIZ_VOICE_ID = "vocab_quiz_voice_id"; // Legacy key from VocabularyQuizPreferences
    private const string DEFAULT_VOICE_ID = Voices.JiYoung; // Default Korean female voice
    
    /// <summary>
    /// Default voice IDs per language. Used when no preference is set.
    /// </summary>
    private static readonly Dictionary<string, string> DefaultVoicesByLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Korean", Voices.JiYoung },      // Korean female - warm, clear
        { "English", Voices.Rachel },       // English female - American
        { "German", "pNInz6obpgDQGcFmaJgB" }, // Multilingual Adam (works well for German)
        { "French", "pNInz6obpgDQGcFmaJgB" }, // Multilingual Adam (works well for French)
        { "Spanish", "pNInz6obpgDQGcFmaJgB" } // Multilingual Adam (works well for Spanish)
    };

    public SpeechVoicePreferences(ILogger<SpeechVoicePreferences> logger, IPreferencesService preferences)
    {
        _logger = logger;
        _preferences = preferences;

        // Perform one-time migration from legacy quiz voice preference
        MigrateFromLegacyQuizVoiceIfNeeded();
    }

    /// <summary>
    /// Gets or sets the global voice ID used for text-to-speech generation
    /// when no language-specific preference exists.
    /// </summary>
    public string VoiceId
    {
        get => _preferences.Get(KEY_GLOBAL_VOICE_ID, DEFAULT_VOICE_ID);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _logger.LogWarning("⚠️ Invalid global VoiceId: empty. Defaulting to JiYoung.");
                value = DEFAULT_VOICE_ID;
            }
            _preferences.Set(KEY_GLOBAL_VOICE_ID, value);
            _logger.LogInformation("🔧 Global speech voice ID set to: {VoiceId}", value);
        }
    }

    /// <summary>
    /// Gets the voice ID for a specific language.
    /// Falls back to language default, then global default if not set.
    /// </summary>
    /// <param name="language">Language name (e.g., "German", "Korean")</param>
    /// <returns>Voice ID to use for TTS</returns>
    public string GetVoiceForLanguage(string language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return VoiceId; // Fall back to global
        }

        var key = KEY_VOICE_PREFIX + language;
        var voiceId = _preferences.Get(key, string.Empty);
        
        if (!string.IsNullOrEmpty(voiceId))
        {
            return voiceId;
        }

        // Fall back to language default, then global default
        if (DefaultVoicesByLanguage.TryGetValue(language, out var defaultVoice))
        {
            return defaultVoice;
        }

        return VoiceId;
    }

    /// <summary>
    /// Sets the voice ID for a specific language.
    /// </summary>
    /// <param name="language">Language name (e.g., "German", "Korean")</param>
    /// <param name="voiceId">ElevenLabs voice ID</param>
    public void SetVoiceForLanguage(string language, string voiceId)
    {
        if (string.IsNullOrEmpty(language))
        {
            _logger.LogWarning("⚠️ Cannot set voice for empty language");
            return;
        }

        var key = KEY_VOICE_PREFIX + language;
        
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            // Clear the preference to use default
            _preferences.Remove(key);
            _logger.LogInformation("🔧 Cleared voice preference for {Language}", language);
        }
        else
        {
            _preferences.Set(key, voiceId);
            _logger.LogInformation("🔧 Voice for {Language} set to: {VoiceId}", language, voiceId);
        }
    }

    /// <summary>
    /// Gets the name of the voice preference key for a language (for debugging).
    /// </summary>
    public static string GetPreferenceKey(string language) => KEY_VOICE_PREFIX + language;

    /// <summary>
    /// One-time migration: if the global voice preference has never been set,
    /// but the legacy quiz voice preference exists, copy it to the global preference.
    /// Also migrates global preference to Korean-specific preference.
    /// </summary>
    private void MigrateFromLegacyQuizVoiceIfNeeded()
    {
        const string MIGRATION_KEY = "global_voice_migration_complete";
        const string MIGRATION_V2_KEY = "per_language_voice_migration_complete";

        // V1 migration: Legacy quiz voice to global
        if (!_preferences.Get(MIGRATION_KEY, false))
        {
            var legacyVoiceId = _preferences.Get(LEGACY_KEY_QUIZ_VOICE_ID, string.Empty);
            var globalVoiceId = _preferences.Get(KEY_GLOBAL_VOICE_ID, string.Empty);

            if (string.IsNullOrWhiteSpace(globalVoiceId) && !string.IsNullOrWhiteSpace(legacyVoiceId))
            {
                _logger.LogInformation("🔄 Migrating legacy quiz voice '{LegacyVoice}' to global voice preference", legacyVoiceId);
                _preferences.Set(KEY_GLOBAL_VOICE_ID, legacyVoiceId);
            }

            _preferences.Set(MIGRATION_KEY, true);
            _logger.LogInformation("✅ Global voice migration check complete");
        }

        // V2 migration: Global voice to Korean-specific (since most existing users are studying Korean)
        if (!_preferences.Get(MIGRATION_V2_KEY, false))
        {
            var globalVoiceId = _preferences.Get(KEY_GLOBAL_VOICE_ID, string.Empty);
            var koreanKey = KEY_VOICE_PREFIX + "Korean";
            var koreanVoiceId = _preferences.Get(koreanKey, string.Empty);

            if (!string.IsNullOrWhiteSpace(globalVoiceId) && string.IsNullOrWhiteSpace(koreanVoiceId))
            {
                _logger.LogInformation("🔄 Migrating global voice '{GlobalVoice}' to Korean-specific preference", globalVoiceId);
                _preferences.Set(koreanKey, globalVoiceId);
            }

            _preferences.Set(MIGRATION_V2_KEY, true);
            _logger.LogInformation("✅ Per-language voice migration complete");
        }
    }

    /// <summary>
    /// Resets all voice preferences to defaults.
    /// </summary>
    public void ResetToDefault()
    {
        VoiceId = DEFAULT_VOICE_ID;
        
        // Clear all language-specific preferences
        foreach (var language in DefaultVoicesByLanguage.Keys)
        {
            var key = KEY_VOICE_PREFIX + language;
            _preferences.Remove(key);
        }
        
        _logger.LogInformation("🔄 Speech voice preferences reset to defaults");
    }

    /// <summary>
    /// Resets voice preference for a specific language to its default.
    /// </summary>
    public void ResetLanguageToDefault(string language)
    {
        var key = KEY_VOICE_PREFIX + language;
        _preferences.Remove(key);
        _logger.LogInformation("🔄 Voice preference for {Language} reset to default", language);
    }
}
