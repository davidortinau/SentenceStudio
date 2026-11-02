using System.Collections.Immutable;
using SentenceStudio.Models;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public static class NativeReadingAccess
{
    public const string Screen = "native.reading.screen";
    public const string Loading = "native.reading.loading";
    public const string Title = "native.reading.title";
    public const string Passage = "native.reading.passage";
    /// <summary>Tappable words are "native.reading.word.{Id}".</summary>
    public const string Word = "native.reading.word";
    public const string AudioBanner = "native.reading.audio.banner";
    public const string AudioStatus = "native.reading.audio.status";
    public const string Notice = "native.reading.notice";
    public const string Error = "native.reading.error";
    public const string Empty = "native.reading.empty";
    public const string GoBack = "native.reading.go-back";
    public const string Back = "native.reading.back";
    public const string Footer = "native.reading.footer";
    public const string Previous = "native.reading.previous";
    public const string PlayPause = "native.reading.play";
    public const string Next = "native.reading.next";
    public const string SentenceCounter = "native.reading.sentence-counter";
    public const string Time = "native.reading.time";
    public const string Speed = "native.reading.speed";
    public const string FontSize = "native.reading.font-size";
    public const string WordPanel = "native.reading.word-panel";
    public const string WordPanelWord = "native.reading.word-panel.word";
    public const string WordPanelDefinition = "native.reading.word-panel.definition";
    public const string WordPanelLookingUp = "native.reading.word-panel.looking-up";
    public const string WordPanelInVocabulary = "native.reading.word-panel.in-vocab";
    public const string WordPanelSave = "native.reading.word-panel.save";
    public const string WordPanelSaving = "native.reading.word-panel.saving";
    public const string WordPanelClose = "native.reading.word-panel.close";
}

public static class NativeReadingText
{
    public const string Title = "Activity_Reading";
    public const string LoadingPassage = "Reading_LoadingPassage";
    public const string NoContent = "Reading_NoContent";
    public const string LookingUp = "Reading_LookingUpDef";
    public const string InYourVocab = "Reading_InYourVocab";
    public const string RememberThisWord = "Reading_RememberThisWord";
    public const string Saving = "Reading_Saving";
    public const string GoBack = "Common_GoBack";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";
    public const string LoadFailed = "NativeActivity_LoadFailed";
    // Source literals in Reading.razor, now localized.
    public const string ResourceNotFound = "Reading_ResourceNotFound";
    public const string NoTranscript = "Reading_NoTranscript";
    public const string AudioInitializing = "Reading_AudioInitializing";
    public const string AudioGenerating = "Reading_AudioGenerating";
    public const string AudioProcessing = "Reading_AudioProcessing";
    public const string AudioLoading = "Reading_AudioLoading";
    public const string AudioFailed = "Reading_AudioFailed";
    public const string AudioError = "Reading_AudioError";
    public const string AudioStillLoading = "Reading_AudioStillLoading";
    public const string NoAudio = "Reading_NoAudio";
    public const string JumpHint = "Reading_JumpHint";
    /// <summary>Accessibility action on each passage word: double tap jumps playback to its sentence.</summary>
    public const string PlayFromSentence = "Reading_PlayFromSentence";
    /// <summary>Format: {0} is the saved word.</summary>
    public const string WordSaved = "Reading_WordSaved";
    public const string CouldNotSaveWord = "Reading_CouldNotSaveWord";
    public const string ErrorSavingWord = "Reading_ErrorSavingWord";
    public const string TranslationUnavailable = "Reading_TranslationUnavailable";
    public const string Play = "Reading_Play";
    public const string Pause = "Reading_Pause";
    public const string PreviousSentence = "Reading_PreviousSentence";
    public const string NextSentence = "Reading_NextSentence";
    public const string PlaybackSpeed = "Reading_PlaybackSpeed";
    public const string FontSize = "Reading_FontSize";
    public const string Close = "Common_Close";
}

/// <summary>
/// One InteractiveText token. Words are tappable; resource vocabulary words
/// are highlighted (word--vocab) and plan focus words additionally
/// (word--focus-vocab). Non-word tokens (spaces, punctuation) are plain text.
/// </summary>
public sealed record NativeReadingToken(
    int Id, int SentenceIndex, string Text, bool IsWord,
    bool IsVocabulary, bool IsFocusVocabulary, string? VocabularyWordId)
{
    public string AccessibilityId => $"{NativeReadingAccess.Word}.{Id}";
}

public sealed record NativeReadingSentence(int Index, string Text,
    ImmutableArray<NativeReadingToken> Tokens);

/// <summary>Bottom word-lookup sheet (source offcanvas).</summary>
public sealed record NativeReadingWordPanel(
    string Word,
    string Definition,
    bool IsTranslating,
    bool IsInVocabulary,
    bool IsSaving)
{
    /// <summary>Source: "Remember this word" only for a looked-up, available, unsaved word.</summary>
    public bool CanSave => !IsInVocabulary && !IsTranslating && !IsSaving
        && !string.IsNullOrEmpty(Definition)
        && Definition != NativeReadingPresenter.TranslationUnavailableMarker;
}

/// <summary>
/// Phases: Loading (busy spinner), Ready (passage), Empty (no resource; Go Back),
/// Error ("Resource not found." / "Resource has no transcript." / load failure;
/// Go Back), Blocked (owner absent or changed). Destination becomes TodaysPlan after Back.
/// </summary>
public sealed record NativeReadingState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    string? ResourceTitle = null,
    ImmutableArray<NativeReadingSentence> Sentences = default,
    int CurrentSentenceIndex = -1,
    bool IsGeneratingAudio = false,
    string? AudioStatusKey = null,
    string? AudioStatusDetail = null,
    double AudioProgress = 0,
    bool IsAudioLoaded = false,
    bool IsPlaying = false,
    double CurrentTime = 0,
    double Duration = 0,
    int FontSize = NativeReadingPresenter.DefaultFontSize,
    float PlaybackSpeed = 1.0f,
    NativeReadingWordPanel? WordPanel = null,
    NativeMessage? Notice = null,
    string? ErrorKey = null,
    bool HasPendingSync = false,
    string? PassageLanguageTag = null)
{
    public int SentenceCount => Sentences.IsDefault ? 0 : Sentences.Length;

    /// <summary>Source footer: Prev/Next need loaded audio; Play is enabled while loading so it can say so.</summary>
    public bool CanNavigate => IsAudioLoaded;

    public bool CanTogglePlayback => IsAudioLoaded || IsGeneratingAudio;

    /// <summary>"3 / 12", or the source em dash when no sentence is current.</summary>
    public string SentenceCounter => CurrentSentenceIndex >= 0
        ? $"{CurrentSentenceIndex + 1} / {SentenceCount}"
        : $"— / {SentenceCount}";

    /// <summary>
    /// The first word of the current sentence: auto-scroll keeps it centered
    /// (source scrollIntoView block:center on .sentence--selected).
    /// </summary>
    public string? CurrentSentenceAnchorId =>
        CurrentSentenceIndex >= 0 && CurrentSentenceIndex < SentenceCount
            ? Sentences[CurrentSentenceIndex].Tokens.FirstOrDefault(token => token.IsWord)?.AccessibilityId
            : null;

    public string TimeDisplay =>
        $"{NativeReadingPresenter.FormatTime(CurrentTime)} / {NativeReadingPresenter.FormatTime(Duration)}";
}

/// <summary>
/// Timestamped transcript audio for one owned resource
/// (POST /api/v1/speech/synthesize-timestamped), cached like the source.
/// </summary>
public interface INativeReadingAudioSource
{
    Task<TimestampedAudio?> GetAsync(
        string owner, LearningResource resource, CancellationToken cancellationToken);
}

public sealed class UnavailableNativeReadingAudioSource : INativeReadingAudioSource
{
    public Task<TimestampedAudio?> GetAsync(
        string owner, LearningResource resource, CancellationToken cancellationToken) =>
        Task.FromResult<TimestampedAudio?>(null);
}

/// <summary>Default until a head registers local playback; Reading then reads without audio.</summary>
public sealed class UnavailableNativeAudioPlayerFactory : SentenceStudio.Abstractions.IAudioPlayerFactory
{
    public bool CanPlayLocally => false;

    public SentenceStudio.Abstractions.IUiAudioPlayer? CreatePlayer(Stream audioStream) => null;
}
