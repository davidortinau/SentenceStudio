using Plugin.Maui.Audio;
using System.Collections.ObjectModel;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Pages.Shadowing;

/// <summary>
/// Playback mode for shadowing sentences.
/// </summary>
public enum ShadowingPlayMode
{
    /// <summary>
    /// Normal playback speed.
    /// </summary>
    Normal = 0,

    /// <summary>
    /// Slow playback speed.
    /// </summary>
    Slow = 1,

    /// <summary>
    /// Very slow playback speed.
    /// </summary>
    VerySlow = 2
}

/// <summary>
/// State class for the ShadowingPage component.
/// </summary>
class ShadowingPageState
{
    /// <summary>
    /// Gets or sets the list of sentences for shadowing practice.
    /// </summary>
    public List<ShadowingSentence> Sentences { get; set; } = new();

    /// <summary>
    /// Gets or sets the index of the current sentence being displayed.
    /// </summary>
    public int CurrentSentenceIndex { get; set; } = 0;

    /// <summary>
    /// Gets or sets a value indicating whether audio is being buffered.
    /// </summary>
    public bool IsBuffering { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether the page is busy with an operation.
    /// </summary>
    public bool IsBusy { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether audio is currently playing.
    /// </summary>
    public bool IsAudioPlaying { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether audio is paused.
    /// </summary>
    public bool IsPaused { get; set; } = false;

    /// <summary>
    /// Gets or sets the URL or data for the current audio being played.
    /// </summary>
    public Stream CurrentAudioStream { get; set; }

    /// <summary>
    /// Gets or sets the waveform data for the current audio.
    /// </summary>
    public float[] WaveformData { get; set; }

    /// <summary>
    /// Gets or sets the duration of the audio in seconds.
    /// Used for time-based scaling of the waveform.
    /// </summary>
    public double AudioDuration { get; set; }

    /// <summary>
    /// Gets or sets the playback mode.
    /// </summary>
    public ShadowingPlayMode PlayMode { get; set; } = ShadowingPlayMode.Normal;

    /// <summary>
    /// Gets or sets the current audio playback position (0 to 1).
    /// </summary>
    public float PlaybackPosition { get; set; } = 0f;

    /// <summary>
    /// Gets or sets the formatted current time display (MM:SS.MS).
    /// </summary>
    public string CurrentTimeDisplay { get; set; } = "--:--.---";

    /// <summary>
    /// Gets or sets the formatted duration display (MM:SS.MS).
    /// </summary>
    public string DurationDisplay { get; set; } = "--:--.---";

    /// <summary>
    /// Gets or sets whether the waveform is visible.
    /// </summary>
    public bool ShowWaveform { get; set; } = true;

    public int? SelectedSpeedIndex { get; set; } = 2;

    public float PlaybackSpeed { get; set; } = 1.0f;

    /// <summary>
    /// Gets or sets the currently selected voice ID.
    /// </summary>
    public string SelectedVoiceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the voice selection bottom sheet is visible.
    /// </summary>
    public bool IsVoiceSelectionVisible { get; set; } = false;

    /// <summary>
    /// Gets or sets the list of available voices from VoiceDiscoveryService.
    /// </summary>
    public List<VoiceInfo> AvailableVoices { get; set; } = new();

    /// <summary>
    /// Gets or sets whether voices are being loaded.
    /// </summary>
    public bool IsLoadingVoices { get; set; } = false;

    /// <summary>
    /// Gets or sets the target language for this shadowing session (from resource).
    /// </summary>
    public string TargetLanguage { get; set; } = "Korean";

    /// <summary>
    /// Gets or sets whether the export menu bottom sheet is visible.
    /// </summary>
    public bool IsExportMenuVisible { get; set; } = false;

    /// <summary>
    /// Gets or sets whether we are saving audio to a file.
    /// </summary>
    public bool IsSavingAudio { get; set; } = false;

    /// <summary>
    /// Gets or sets the progress message during export.
    /// </summary>
    public string ExportProgressMessage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path of the last saved file.
    /// </summary>
    public string LastSavedFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the screen is in narrow mode (e.g., phone portrait).
    /// </summary>
    public bool IsNarrowScreen { get; set; } = false;

    /// <summary>
    /// Gets or sets whether the narrow screen menu is visible.
    /// </summary>
    public bool IsNarrowScreenMenuVisible { get; set; } = false;

    /// <summary>
    /// Gets the display name for the currently selected voice.
    /// </summary>
    public string SelectedVoiceDisplayName
    {
        get
        {
            if (string.IsNullOrEmpty(SelectedVoiceId))
                return "Select Voice";
            var voice = AvailableVoices.FirstOrDefault(v => v.VoiceId == SelectedVoiceId);
            return voice?.Name ?? "Select Voice";
        }
    }

    /// <summary>
    /// Gets the current sentence text, or an empty string if none is available.
    /// </summary>
    public string CurrentSentenceText =>
        Sentences.Count > 0 && CurrentSentenceIndex < Sentences.Count
            ? Sentences[CurrentSentenceIndex].TargetLanguageText
            : string.Empty;

    /// <summary>
    /// Gets the current sentence translation, or an empty string if none is available.
    /// </summary>
    public string CurrentSentenceTranslation =>
        Sentences.Count > 0 && CurrentSentenceIndex < Sentences.Count
            ? Sentences[CurrentSentenceIndex].NativeLanguageText
            : string.Empty;

    /// <summary>
    /// Gets the current sentence pronunciation notes, or an empty string if none is available.
    /// </summary>
    public string CurrentSentencePronunciationNotes =>
        Sentences.Count > 0 && CurrentSentenceIndex < Sentences.Count
            ? Sentences[CurrentSentenceIndex].PronunciationNotes
            : string.Empty;

    // public bool IsExportMenuVisible { get; set; } = false;
    // public bool IsSavingAudio { get; set; } = false;
    // public string ExportProgressMessage { get; set; } = string.Empty;
    // public string LastSavedFilePath { get; set; } = string.Empty;
}