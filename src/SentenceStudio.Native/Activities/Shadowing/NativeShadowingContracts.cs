using System.Collections.Immutable;

namespace SentenceStudio.Native;

public static class NativeShadowingAccess
{
    public const string Screen = "native.shadowing.screen";
    public const string Loading = "native.shadowing.loading";
    public const string Card = "native.shadowing.card";
    public const string Target = "native.shadowing.target";
    public const string Translation = "native.shadowing.translation";
    public const string Pronunciation = "native.shadowing.pronunciation";
    public const string TranslationToggle = "native.shadowing.translation-toggle";
    public const string Waveform = "native.shadowing.waveform";
    public const string Seek = "native.shadowing.seek";
    public const string Elapsed = "native.shadowing.elapsed";
    public const string Duration = "native.shadowing.duration";
    public const string Previous = "native.shadowing.previous";
    public const string Next = "native.shadowing.next";
    public const string Slow = "native.shadowing.slow";
    public const string Normal = "native.shadowing.normal";
    public const string Play = "native.shadowing.play";
    public const string Counter = "native.shadowing.counter";
    public const string AudioStatus = "native.shadowing.audio-status";
    public const string Empty = "native.shadowing.empty";
    public const string EmptyBack = "native.shadowing.empty.back";
    public const string Error = "native.shadowing.error";
    public const string Back = "native.shadowing.back";
}

public static class NativeShadowingText
{
    public const string Title = "Activity_Shadowing";
    public const string Generating = "Shadowing_GeneratingSentences";
    public const string NoSentences = "Shadowing_NoSentences";
    public const string Show = "Shadowing_ShowTranslation";
    public const string Hide = "Shadowing_HideTranslation";
    public const string Translation = "Shadowing_Translation";
    public const string Previous = "Shadowing_Prev";
    public const string Next = "Shadowing_Next";
    public const string Slow = "Shadowing_Slow";
    public const string Normal = "Shadowing_Normal";
    public const string Play = "NativeShadowing_Play";
    public const string Pause = "NativeShadowing_Pause";
    public const string Position = "NativeShadowing_PlaybackPosition";
    public const string AudioOffline = "OfflineAudioUnavailable";
    public const string AudioFailed = "NativeShadowing_AudioFailed";
    public const string GoBack = "Common_GoBack";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";
    public const string LoadFailed = "NativeActivity_LoadFailed";
}

/// <summary>Source Shadowing.razor speeds: "Slow" 0.6 and "Normal" 1.0.</summary>
public static class NativeShadowingSpeed
{
    public const float Slow = 0.6f;
    public const float Normal = 1.0f;
}

/// <summary>Source ShadowingSentence: target text always, translation and notes when the source has them.</summary>
public sealed record NativeShadowingSentence(
    string TargetText, string? NativeText, string? PronunciationNotes)
{
    public bool HasTranslation => !string.IsNullOrWhiteSpace(NativeText);
}

/// <summary>Why the current sentence has no playable audio.</summary>
public enum NativeShadowingAudioIssue { None, Offline, Failed }

/// <summary>
/// Phases: Loading ("Generating sentences"), Ready (sentence card, waveform
/// and footer), Empty (no sentences; Go Back), Error (content could not be
/// loaded; Go Back), Blocked (owner absent/changed or launch refused).
/// Destination becomes TodaysPlan after Back. <see cref="Position"/> is the
/// playback fraction 0..1 of <see cref="DurationSeconds"/>.
/// </summary>
public sealed record NativeShadowingState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    ImmutableArray<NativeShadowingSentence> Sentences = default,
    int Index = 0,
    bool ShowTranslation = false,
    float Speed = NativeShadowingSpeed.Normal,
    bool IsBuffering = false,
    bool IsPlaying = false,
    bool HasAudio = false,
    double Position = 0,
    double DurationSeconds = 0,
    NativeShadowingAudioIssue AudioIssue = NativeShadowingAudioIssue.None,
    string? ErrorKey = null,
    bool HasPendingSync = false)
{
    public int Count => Sentences.IsDefault ? 0 : Sentences.Length;

    public NativeShadowingSentence? Current =>
        Count == 0 || Index < 0 || Index >= Count ? null : Sentences[Index];

    public bool CanGoPrevious => Phase == NativePhase.Ready && Index > 0;

    public bool CanGoNext => Phase == NativePhase.Ready && Index < Count - 1;

    /// <summary>The source shows the waveform once audio for the current sentence was prepared.</summary>
    public bool ShowsWaveform => HasAudio;
}
