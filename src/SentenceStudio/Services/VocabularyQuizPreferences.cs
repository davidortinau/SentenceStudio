using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services;

/// <summary>
/// Manages vocabulary quiz preferences using .NET MAUI Preferences API.
/// Preferences are stored locally per-device and persist across app sessions.
/// </summary>
public class VocabularyQuizPreferences
{
    private readonly ILogger<VocabularyQuizPreferences> _logger;
    
    // Preference keys
    private const string KEY_DISPLAY_DIRECTION = "vocab_quiz_display_direction";
    private const string KEY_AUTO_PLAY_VOCAB_AUDIO = "vocab_quiz_autoplay_vocab";
    private const string KEY_AUTO_PLAY_SAMPLE_AUDIO = "vocab_quiz_autoplay_sample";
    private const string KEY_SHOW_MNEMONIC_IMAGE = "vocab_quiz_show_mnemonic";
    private const string KEY_VOICE_ID = "vocab_quiz_voice_id";
    
    // Default values
    private const string DEFAULT_DISPLAY_DIRECTION = "TargetToNative";
    private const bool DEFAULT_AUTO_PLAY_VOCAB_AUDIO = true;
    private const bool DEFAULT_AUTO_PLAY_SAMPLE_AUDIO = false;
    private const bool DEFAULT_SHOW_MNEMONIC_IMAGE = true;
    private const string DEFAULT_VOICE_ID = Voices.JiYoung; // Default Korean female voice
    
    public VocabularyQuizPreferences(ILogger<VocabularyQuizPreferences> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Gets or sets the display direction for vocabulary quiz questions.
    /// "TargetToNative" = show Korean word, answer in English
    /// "NativeToTarget" = show English word, answer in Korean
    /// </summary>
    public string DisplayDirection
    {
        get => Preferences.Get(KEY_DISPLAY_DIRECTION, DEFAULT_DISPLAY_DIRECTION);
        set
        {
            if (value != "TargetToNative" && value != "NativeToTarget")
            {
                _logger.LogWarning("⚠️ Invalid DisplayDirection: {Direction}. Defaulting to TargetToNative.", value);
                value = "TargetToNative";
            }
            Preferences.Set(KEY_DISPLAY_DIRECTION, value);
            _logger.LogInformation("📋 Vocab quiz display direction set to: {Direction}", value);
        }
    }
    
    /// <summary>
    /// Gets or sets whether to auto-play target language vocabulary word audio.
    /// </summary>
    public bool AutoPlayVocabAudio
    {
        get => Preferences.Get(KEY_AUTO_PLAY_VOCAB_AUDIO, DEFAULT_AUTO_PLAY_VOCAB_AUDIO);
        set
        {
            Preferences.Set(KEY_AUTO_PLAY_VOCAB_AUDIO, value);
            _logger.LogInformation("📋 Vocab quiz auto-play vocab audio set to: {Value}", value);
        }
    }
    
    /// <summary>
    /// Gets or sets whether to auto-play sample sentence audio after vocabulary audio.
    /// Only plays if AutoPlayVocabAudio is also enabled.
    /// </summary>
    public bool AutoPlaySampleAudio
    {
        get => Preferences.Get(KEY_AUTO_PLAY_SAMPLE_AUDIO, DEFAULT_AUTO_PLAY_SAMPLE_AUDIO);
        set
        {
            Preferences.Set(KEY_AUTO_PLAY_SAMPLE_AUDIO, value);
            _logger.LogInformation("📋 Vocab quiz auto-play sample audio set to: {Value}", value);
        }
    }
    
    /// <summary>
    /// Gets or sets whether to show mnemonic images on correct answer confirmation.
    /// </summary>
    public bool ShowMnemonicImage
    {
        get => Preferences.Get(KEY_SHOW_MNEMONIC_IMAGE, DEFAULT_SHOW_MNEMONIC_IMAGE);
        set
        {
            Preferences.Set(KEY_SHOW_MNEMONIC_IMAGE, value);
            _logger.LogInformation("📋 Vocab quiz show mnemonic image set to: {Value}", value);
        }
    }
    
    /// <summary>
    /// Gets or sets the ElevenLabs voice ID to use for audio playback.
    /// Uses Voices.JiYoung (Korean female) as default.
    /// </summary>
    public string VoiceId
    {
        get => Preferences.Get(KEY_VOICE_ID, DEFAULT_VOICE_ID);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _logger.LogWarning("⚠️ Invalid VoiceId: empty. Defaulting to JiYoung.", value);
                value = DEFAULT_VOICE_ID;
            }
            Preferences.Set(KEY_VOICE_ID, value);
            _logger.LogInformation("📋 Vocab quiz voice ID set to: {VoiceId}", value);
        }
    }
    
    /// <summary>
    /// Determines if sample audio should play based on both preferences.
    /// Sample audio only plays if vocabulary audio is also enabled.
    /// </summary>
    public bool ShouldPlaySampleAudio => AutoPlayVocabAudio && AutoPlaySampleAudio;
    
    /// <summary>
    /// Resets all vocabulary quiz preferences to their default values.
    /// </summary>
    public void ResetToDefaults()
    {
        DisplayDirection = DEFAULT_DISPLAY_DIRECTION;
        AutoPlayVocabAudio = DEFAULT_AUTO_PLAY_VOCAB_AUDIO;
        AutoPlaySampleAudio = DEFAULT_AUTO_PLAY_SAMPLE_AUDIO;
        ShowMnemonicImage = DEFAULT_SHOW_MNEMONIC_IMAGE;
        VoiceId = DEFAULT_VOICE_ID;
        _logger.LogInformation("🔄 Vocab quiz preferences reset to defaults");
    }
}
