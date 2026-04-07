using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

/// <summary>
/// Manages vocabulary quiz preferences through the platform preference abstraction.
/// Preferences are stored locally per host and persist across app sessions.
/// </summary>
public class VocabularyQuizPreferences
{
    private readonly ILogger<VocabularyQuizPreferences> _logger;
    private readonly IPreferencesService _preferences;

    // Preference keys
    private const string KEY_DISPLAY_DIRECTION = "vocab_quiz_display_direction";
    private const string KEY_AUTO_PLAY_VOCAB_AUDIO = "vocab_quiz_autoplay_vocab";
    private const string KEY_AUTO_PLAY_SAMPLE_AUDIO = "vocab_quiz_autoplay_sample";
    private const string KEY_SHOW_MNEMONIC_IMAGE = "vocab_quiz_show_mnemonic";
    private const string KEY_AUTO_ADVANCE_DURATION = "vocab_quiz_auto_advance_duration";
    private const string KEY_USE_TEXT_PROMPT = "vocab_quiz_use_text_prompt";
    private const string KEY_USE_AUDIO_PROMPT = "vocab_quiz_use_audio_prompt";
    private const string KEY_USE_PHOTO_PROMPT = "vocab_quiz_use_photo_prompt";

    // Default values
    private const string DEFAULT_DISPLAY_DIRECTION = "TargetToNative";
    private const bool DEFAULT_AUTO_PLAY_VOCAB_AUDIO = true;
    private const bool DEFAULT_AUTO_PLAY_SAMPLE_AUDIO = false;
    private const bool DEFAULT_SHOW_MNEMONIC_IMAGE = true;
    private const int DEFAULT_AUTO_ADVANCE_DURATION = 2000; // 2 seconds
    private const bool DEFAULT_USE_TEXT_PROMPT = true;
    private const bool DEFAULT_USE_AUDIO_PROMPT = true;
    private const bool DEFAULT_USE_PHOTO_PROMPT = false;

    private readonly SpeechVoicePreferences _speechVoicePreferences;

    public VocabularyQuizPreferences(
        ILogger<VocabularyQuizPreferences> logger,
        SpeechVoicePreferences speechVoicePreferences,
        IPreferencesService preferences)
    {
        _logger = logger;
        _speechVoicePreferences = speechVoicePreferences;
        _preferences = preferences;
    }

    /// <summary>
    /// Gets or sets the display direction for vocabulary quiz questions.
    /// "TargetToNative" = show Korean word, answer in English
    /// "NativeToTarget" = show English word, answer in Korean
    /// "Mixed" = vary the prompt direction per question
    /// </summary>
    public string DisplayDirection
    {
        get => NormalizeDisplayDirection(_preferences.Get(KEY_DISPLAY_DIRECTION, DEFAULT_DISPLAY_DIRECTION));
        set
        {
            var normalized = NormalizeDisplayDirection(value);

            if (!string.Equals(value, normalized, StringComparison.Ordinal))
            {
                _logger.LogWarning("⚠️ Invalid DisplayDirection: {Direction}. Defaulting to {DirectionValue}.", value, normalized);
            }

            _preferences.Set(KEY_DISPLAY_DIRECTION, normalized);
            _logger.LogInformation("📋 Vocab quiz display direction set to: {Direction}", normalized);
        }
    }

    /// <summary>
    /// Gets or sets whether to auto-play target language vocabulary word audio.
    /// </summary>
    public bool AutoPlayVocabAudio
    {
        get => _preferences.Get(KEY_AUTO_PLAY_VOCAB_AUDIO, DEFAULT_AUTO_PLAY_VOCAB_AUDIO);
        set
        {
            _preferences.Set(KEY_AUTO_PLAY_VOCAB_AUDIO, value);
            _logger.LogInformation("📋 Vocab quiz auto-play vocab audio set to: {Value}", value);
        }
    }

    /// <summary>
    /// Gets or sets whether to auto-play sample sentence audio after vocabulary audio.
    /// Only plays if AutoPlayVocabAudio is also enabled.
    /// </summary>
    public bool AutoPlaySampleAudio
    {
        get => _preferences.Get(KEY_AUTO_PLAY_SAMPLE_AUDIO, DEFAULT_AUTO_PLAY_SAMPLE_AUDIO);
        set
        {
            _preferences.Set(KEY_AUTO_PLAY_SAMPLE_AUDIO, value);
            _logger.LogInformation("📋 Vocab quiz auto-play sample audio set to: {Value}", value);
        }
    }

    /// <summary>
    /// Gets or sets whether to show mnemonic images on correct answer confirmation.
    /// </summary>
    public bool ShowMnemonicImage
    {
        get => _preferences.Get(KEY_SHOW_MNEMONIC_IMAGE, DEFAULT_SHOW_MNEMONIC_IMAGE);
        set
        {
            _preferences.Set(KEY_SHOW_MNEMONIC_IMAGE, value);
            _logger.LogInformation("📋 Vocab quiz show mnemonic image set to: {Value}", value);
        }
    }

    /// <summary>
    /// Gets the ElevenLabs voice ID from the global speech voice preference.
    /// Voice selection is now managed centrally in Settings rather than per-activity.
    /// </summary>
    public string VoiceId => _speechVoicePreferences.VoiceId;

    /// <summary>
    /// Gets or sets the auto-advance duration in milliseconds.
    /// Controls how long to show feedback before automatically advancing to the next question.
    /// Range: 1000ms (1 second) to 5000ms (5 seconds).
    /// </summary>
    public int AutoAdvanceDuration
    {
        get => _preferences.Get(KEY_AUTO_ADVANCE_DURATION, DEFAULT_AUTO_ADVANCE_DURATION);
        set
        {
            // Clamp value between 1000ms and 5000ms
            var clampedValue = Math.Max(1000, Math.Min(5000, value));
            if (value != clampedValue)
            {
                _logger.LogWarning("⚠️ Auto-advance duration {Value}ms out of range. Clamping to {Clamped}ms.", value, clampedValue);
            }
            _preferences.Set(KEY_AUTO_ADVANCE_DURATION, clampedValue);
            _logger.LogInformation("📋 Vocab quiz auto-advance duration set to: {Duration}ms", clampedValue);
        }
    }

    /// <summary>
    /// Gets or sets whether the written prompt text is shown during the quiz.
    /// </summary>
    public bool UseTextPrompt
    {
        get => _preferences.Get(KEY_USE_TEXT_PROMPT, DEFAULT_USE_TEXT_PROMPT);
        set
        {
            _preferences.Set(KEY_USE_TEXT_PROMPT, value);
            _logger.LogInformation("📋 Vocab quiz text prompt enabled: {Value}", value);
        }
    }

    /// <summary>
    /// Gets or sets whether audio is available as part of the quiz prompt.
    /// </summary>
    public bool UseAudioPrompt
    {
        get => _preferences.Get(KEY_USE_AUDIO_PROMPT, DEFAULT_USE_AUDIO_PROMPT);
        set
        {
            _preferences.Set(KEY_USE_AUDIO_PROMPT, value);
            _logger.LogInformation("📋 Vocab quiz audio prompt enabled: {Value}", value);
        }
    }

    /// <summary>
    /// Gets or sets whether photos are shown as part of the quiz prompt when available.
    /// </summary>
    public bool UsePhotoPrompt
    {
        get => _preferences.Get(KEY_USE_PHOTO_PROMPT, DEFAULT_USE_PHOTO_PROMPT);
        set
        {
            _preferences.Set(KEY_USE_PHOTO_PROMPT, value);
            _logger.LogInformation("📋 Vocab quiz photo prompt enabled: {Value}", value);
        }
    }

    /// <summary>
    /// Determines if sample audio should play based on both preferences.
    /// Sample audio only plays if vocabulary audio is also enabled.
    /// </summary>
    public bool ShouldPlaySampleAudio => AutoPlayVocabAudio && AutoPlaySampleAudio;

    /// <summary>
    /// Resets all vocabulary quiz preferences to their default values.
    /// Note: Voice preference is now managed globally and not reset here.
    /// </summary>
    public void ResetToDefaults()
    {
        DisplayDirection = DEFAULT_DISPLAY_DIRECTION;
        AutoPlayVocabAudio = DEFAULT_AUTO_PLAY_VOCAB_AUDIO;
        AutoPlaySampleAudio = DEFAULT_AUTO_PLAY_SAMPLE_AUDIO;
        ShowMnemonicImage = DEFAULT_SHOW_MNEMONIC_IMAGE;
        AutoAdvanceDuration = DEFAULT_AUTO_ADVANCE_DURATION;
        UseTextPrompt = DEFAULT_USE_TEXT_PROMPT;
        UseAudioPrompt = DEFAULT_USE_AUDIO_PROMPT;
        UsePhotoPrompt = DEFAULT_USE_PHOTO_PROMPT;
        _logger.LogInformation("🔄 Vocab quiz preferences reset to defaults");
    }

    private static string NormalizeDisplayDirection(string? value) => value switch
    {
        "Forward" or "TargetToNative" => "TargetToNative",
        "Reverse" or "NativeToTarget" => "NativeToTarget",
        "Mixed" => "Mixed",
        _ => DEFAULT_DISPLAY_DIRECTION
    };
}
