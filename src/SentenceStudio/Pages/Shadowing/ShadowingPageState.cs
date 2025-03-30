using Plugin.Maui.Audio;
using System.Collections.ObjectModel;

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
    /// Gets or sets the current speed multiplier for audio playback.
    /// </summary>
    public float SpeedMultiplier { get; set; } = 1.0f;

    /// <summary>
    /// Gets or sets the URL or data for the current audio being played.
    /// </summary>
    public Stream CurrentAudioStream { get; set; }

    /// <summary>
    /// Gets or sets the playback mode.
    /// </summary>
    public ShadowingPlayMode PlayMode { get; set; } = ShadowingPlayMode.Normal;

    /// <summary>
    /// Gets the current sentence text, or an empty string if none is available.
    /// </summary>
    public string CurrentSentenceText =>
        Sentences.Count > 0 && CurrentSentenceIndex < Sentences.Count
            ? Sentences[CurrentSentenceIndex].NativeLanguageText
            : string.Empty;

    /// <summary>
    /// Gets the current sentence translation, or an empty string if none is available.
    /// </summary>
    public string CurrentSentenceTranslation =>
        Sentences.Count > 0 && CurrentSentenceIndex < Sentences.Count
            ? Sentences[CurrentSentenceIndex].TargetLanguageText
            : string.Empty;
            
public string CurrentSentencePronunciationNotes => 
    Sentences.Count > 0 && CurrentSentenceIndex < Sentences.Count
            ? Sentences[CurrentSentenceIndex].PronunciationNotes
            : string.Empty;
}