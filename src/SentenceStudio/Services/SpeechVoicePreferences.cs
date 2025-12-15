using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

/// <summary>
/// Manages global speech voice preferences for learning activities.
/// This service provides a single default voice selection that applies across
/// vocabulary quiz, vocabulary editing, minimal pairs, and other TTS-enabled features.
/// </summary>
public class SpeechVoicePreferences
{
    private readonly ILogger<SpeechVoicePreferences> _logger;

    private const string KEY_GLOBAL_VOICE_ID = "global_speech_voice_id";
    private const string LEGACY_KEY_QUIZ_VOICE_ID = "vocab_quiz_voice_id"; // Legacy key from VocabularyQuizPreferences
    private const string DEFAULT_VOICE_ID = Voices.JiYoung; // Default Korean female voice

    public SpeechVoicePreferences(ILogger<SpeechVoicePreferences> logger)
    {
        _logger = logger;

        // Perform one-time migration from legacy quiz voice preference
        MigrateFromLegacyQuizVoiceIfNeeded();
    }

    /// <summary>
    /// Gets or sets the global voice ID used for text-to-speech generation
    /// across all learning activities.
    /// </summary>
    public string VoiceId
    {
        get => Preferences.Get(KEY_GLOBAL_VOICE_ID, DEFAULT_VOICE_ID);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _logger.LogWarning("⚠️ Invalid global VoiceId: empty. Defaulting to JiYoung.");
                value = DEFAULT_VOICE_ID;
            }
            Preferences.Set(KEY_GLOBAL_VOICE_ID, value);
            _logger.LogInformation("🔧 Global speech voice ID set to: {VoiceId}", value);
        }
    }

    /// <summary>
    /// One-time migration: if the global voice preference has never been set,
    /// but the legacy quiz voice preference exists, copy it to the global preference.
    /// This ensures users don't lose their existing voice selection.
    /// </summary>
    private void MigrateFromLegacyQuizVoiceIfNeeded()
    {
        const string MIGRATION_KEY = "global_voice_migration_complete";

        if (Preferences.Get(MIGRATION_KEY, false))
        {
            return; // Migration already done
        }

        var legacyVoiceId = Preferences.Get(LEGACY_KEY_QUIZ_VOICE_ID, string.Empty);
        var globalVoiceId = Preferences.Get(KEY_GLOBAL_VOICE_ID, string.Empty);

        if (string.IsNullOrWhiteSpace(globalVoiceId) && !string.IsNullOrWhiteSpace(legacyVoiceId))
        {
            _logger.LogInformation("🔄 Migrating legacy quiz voice '{LegacyVoice}' to global voice preference", legacyVoiceId);
            Preferences.Set(KEY_GLOBAL_VOICE_ID, legacyVoiceId);
        }

        Preferences.Set(MIGRATION_KEY, true);
        _logger.LogInformation("✅ Global voice migration check complete");
    }

    /// <summary>
    /// Resets the global voice preference to the default voice.
    /// </summary>
    public void ResetToDefault()
    {
        VoiceId = DEFAULT_VOICE_ID;
        _logger.LogInformation("🔄 Global speech voice reset to default: {VoiceId}", DEFAULT_VOICE_ID);
    }
}
