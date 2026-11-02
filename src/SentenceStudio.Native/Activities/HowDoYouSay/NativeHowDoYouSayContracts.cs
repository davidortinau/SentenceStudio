using System.Collections.Immutable;

namespace SentenceStudio.Native;

public static class NativeHowDoYouSayAccess
{
    public const string Screen = "native.howdoyousay.screen";
    public const string Loading = "native.howdoyousay.loading";
    public const string InputLabel = "native.howdoyousay.input-label";
    public const string Input = "native.howdoyousay.input";
    public const string VoiceLabel = "native.howdoyousay.voice-label";
    public const string Voice = "native.howdoyousay.voice";
    public const string Speak = "native.howdoyousay.speak";
    public const string Generating = "native.howdoyousay.generating";
    public const string Notice = "native.howdoyousay.notice";
    public const string HistoryLoading = "native.howdoyousay.history.loading";
    public const string HistoryTitle = "native.howdoyousay.history.title";
    public const string History = "native.howdoyousay.history";
    public const string Error = "native.howdoyousay.error";
    public const string Back = "native.howdoyousay.back";

    public static string Entry(int id) => $"native.howdoyousay.history.{id}";
    public static string EntryPhrase(int id) => $"native.howdoyousay.history.{id}.phrase";
    public static string EntryTime(int id) => $"native.howdoyousay.history.{id}.time";
    public static string EntryPlay(int id) => $"native.howdoyousay.history.{id}.play";
    public static string EntryDelete(int id) => $"native.howdoyousay.history.{id}.delete";
}

public static class NativeHowDoYouSayText
{
    public const string Title = "Activity_HowDoYouSay";
    public const string EnterPhrase = "HowDoYouSay_EnterPhrase";
    public const string TypePhrase = "HowDoYouSay_TypePhrase";
    public const string Voice = "HowDoYouSay_Voice";
    public const string Generating = "HowDoYouSay_Generating";
    public const string Speak = "HowDoYouSay_Speak";
    public const string History = "HowDoYouSay_History";
    public const string Play = "NativeShadowing_Play";
    public const string Pause = "NativeShadowing_Pause";
    public const string Delete = "Common_Delete";
    public const string Loading = "Common_Loading";
    // Source toasts (HowDoYouSay.razor:173-294).
    public const string SelectVoice = "NativeHowDoYouSay_SelectVoice";
    public const string Generated = "NativeHowDoYouSay_Generated";
    public const string GenerateFailed = "NativeHowDoYouSay_GenerateFailed";
    public const string PlaybackError = "NativeHowDoYouSay_PlaybackError";
    public const string FileNotFound = "NativeHowDoYouSay_FileNotFound";
    public const string Deleted = "Deleted";
    public const string DeleteFailed = "NativeHowDoYouSay_DeleteFailed";
    public const string AudioOffline = "OfflineAudioUnavailable";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";
    public const string GoBack = "Common_GoBack";

    /// <summary>
    /// The source shows the profile's English language name in the label; the
    /// native heads localize the five supported names and show others as stored.
    /// </summary>
    public static string? LanguageKey(string language) => language switch
    {
        "Korean" => "NativeVocabQuiz_Language_Korean",
        "English" => "NativeVocabQuiz_Language_English",
        "French" => "NativeVocabQuiz_Language_French",
        "German" => "NativeVocabQuiz_Language_German",
        "Spanish" => "NativeVocabQuiz_Language_Spanish",
        _ => null
    };
}

/// <summary>Source voice option text: "Name (Gender)" when the API reports a gender.</summary>
public sealed record NativeHowDoYouSayVoice(string Id, string Label);

/// <summary>One owned StreamHistory row: phrase and its creation time in local time.</summary>
public sealed record NativeHowDoYouSayEntry(int Id, string Phrase, DateTime CreatedAt);

/// <summary>
/// Phases: Loading (profile), Ready (input card and history), Blocked (owner
/// absent/changed or launch refused; Go Back). Voices and history load inside
/// Ready with their own flags, as the source does. Destination becomes
/// TodaysPlan after Back.
/// </summary>
public sealed record NativeHowDoYouSayState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    string TargetLanguage = "Korean",
    string Input = "",
    bool VoicesLoading = false,
    ImmutableArray<NativeHowDoYouSayVoice> Voices = default,
    string SelectedVoiceId = "",
    bool IsBusy = false,
    bool HistoryLoading = false,
    ImmutableArray<NativeHowDoYouSayEntry> History = default,
    int? PlayingId = null,
    NativeMessage? Notice = null,
    string? ErrorKey = null)
{
    /// <summary>Source: the voice dropdown appears only when voices were found.</summary>
    public bool ShowsVoices => !Voices.IsDefaultOrEmpty;

    public bool CanSpeak => Phase == NativePhase.Ready
        && Destination == NativeDestination.Activity
        && !IsBusy && !string.IsNullOrWhiteSpace(Input);

    /// <summary>Source: the History header and list render only when rows exist.</summary>
    public bool ShowsHistory => !HistoryLoading && !History.IsDefaultOrEmpty;
}
