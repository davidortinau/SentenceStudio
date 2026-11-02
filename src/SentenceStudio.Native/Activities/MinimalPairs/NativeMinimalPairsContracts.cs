using System.Collections.Immutable;

namespace SentenceStudio.Native;

public static class NativeMinimalPairsAccess
{
    public const string Screen = "native.minimalpairs.screen";
    public const string Loading = "native.minimalpairs.loading";
    public const string Error = "native.minimalpairs.error";
    public const string Back = "native.minimalpairs.back";
    public const string GoBack = "native.minimalpairs.go-back";
    public const string Notices = "native.minimalpairs.notices";
    /// <summary>Notices are "native.minimalpairs.notice.{index}".</summary>
    public const string Notice = "native.minimalpairs.notice";
    public const string DismissNotices = "native.minimalpairs.notices.dismiss";
    // List (source MinimalPairs.razor)
    public const string List = "native.minimalpairs.list";
    public const string CreatePair = "native.minimalpairs.create-pair";
    public const string ModeLabel = "native.minimalpairs.mode";
    public const string ModeFocus = "native.minimalpairs.mode.focus";
    public const string ModeMixed = "native.minimalpairs.mode.mixed";
    public const string StartSession = "native.minimalpairs.start";
    public const string Empty = "native.minimalpairs.empty";
    public const string CreateFirstPair = "native.minimalpairs.create-first";
    public const string Pairs = "native.minimalpairs.pairs";
    public const string Deleting = "native.minimalpairs.deleting";
    public const string DeleteDialog = "native.minimalpairs.delete";
    public const string DeleteCancel = "native.minimalpairs.delete.cancel";
    public const string DeleteConfirm = "native.minimalpairs.delete.confirm";
    // Create (source MinimalPairCreate.razor)
    public const string Create = "native.minimalpairs.create";
    public const string CreateSubmit = "native.minimalpairs.create.submit";
    public const string CreateError = "native.minimalpairs.create.error";
    public const string SearchA = "native.minimalpairs.create.search-a";
    public const string SearchB = "native.minimalpairs.create.search-b";
    public const string WordsA = "native.minimalpairs.create.words-a";
    public const string WordsB = "native.minimalpairs.create.words-b";
    public const string SelectedA = "native.minimalpairs.create.selected-a";
    public const string SelectedB = "native.minimalpairs.create.selected-b";
    public const string ContrastLabel = "native.minimalpairs.create.contrast";
    public const string RefineSearch = "native.minimalpairs.create.refine";
    // Session (source MinimalPairSession.razor)
    public const string Session = "native.minimalpairs.session";
    public const string Trial = "native.minimalpairs.session.trial";
    public const string CorrectCount = "native.minimalpairs.session.correct";
    public const string IncorrectCount = "native.minimalpairs.session.incorrect";
    public const string AudioNotice = "native.minimalpairs.session.audio-notice";
    public const string LeftTile = "native.minimalpairs.session.tile.left";
    public const string RightTile = "native.minimalpairs.session.tile.right";
    public const string Replay = "native.minimalpairs.session.replay";
    public const string CheckAnswer = "native.minimalpairs.session.check";
    public const string SessionEmpty = "native.minimalpairs.session.empty";
    public const string Summary = "native.minimalpairs.summary";
    public const string SummaryCorrect = "native.minimalpairs.summary.correct";
    public const string SummaryIncorrect = "native.minimalpairs.summary.incorrect";
    public const string SummaryAccuracy = "native.minimalpairs.summary.accuracy";
    public const string SummaryDuration = "native.minimalpairs.summary.duration";
    public const string Done = "native.minimalpairs.summary.done";

    public static string Pair(int id) => $"native.minimalpairs.pair.{id}";
    public static string PairDelete(int id) => $"native.minimalpairs.pair.{id}.delete";
    public static string WordOption(char slot, string wordId) =>
        $"native.minimalpairs.create.word-{char.ToLowerInvariant(slot)}.{wordId}";
}

public static class NativeMinimalPairsText
{
    public const string Title = "Activity_MinimalPairs";
    public const string CreatePair = "MinimalPairs_CreatePair";
    public const string Mode = "MinimalPairs_Mode";
    public const string Focus = "MinimalPairs_Focus";
    public const string Mixed = "MinimalPairs_Mixed";
    public const string StartSession = "MinimalPairs_StartSession";
    /// <summary>Format: {0} = pair count (source "Start Session (n pairs)").</summary>
    public const string StartSessionCount = "NativeMinimalPairs_StartSessionCount";
    public const string NoPairs = "MinimalPairs_NoPairs";
    public const string CreateFirstPair = "MinimalPairs_CreateFirstPair";
    public const string Vs = "MinimalPairs_Vs";
    public const string ConfirmDelete = "MinimalPairs_ConfirmDelete";
    /// <summary>Format: {0} = word A, {1} = word B (source "Delete A vs B?").</summary>
    public const string DeleteMessage = "NativeMinimalPairs_DeleteMessage";
    public const string Cancel = "Common_Cancel";
    public const string Delete = "Common_Delete";
    public const string SelectPairFirst = "NativeMinimalPairs_SelectPairFirst";
    public const string NoPairsToStart = "NativeMinimalPairs_NoPairsToStart";
    public const string LoadFailed = "NativeMinimalPairs_LoadFailed";
    public const string Deleted = "NativeMinimalPairs_Deleted";
    public const string DeleteFailed = "NativeMinimalPairs_DeleteFailed";
    // Create
    public const string CreateTitle = "MinimalPair_Create";
    public const string Create = "Common_Create";
    public const string SelectFirstWord = "MinimalPair_SelectFirstWord";
    public const string SelectSecondWord = "MinimalPair_SelectSecondWord";
    public const string SearchVocabulary = "MinimalPair_SearchVocab";
    public const string ContrastLabel = "MinimalPair_ContrastLabel";
    /// <summary>Source placeholder "e.g., ㅂ vs ㅃ" (target-language sample, kept verbatim).</summary>
    public const string ContrastPlaceholder = "e.g., ㅂ vs ㅃ";
    public const string SelectBothWords = "MinimalPair_SelectBothWords";
    public const string SelectDifferent = "MinimalPair_SelectDifferent";
    public const string Created = "MinimalPair_Created";
    public const string FailedToCreate = "MinimalPair_FailedToCreate";
    public const string VocabularyLoadFailed = "NativeMinimalPairs_VocabularyLoadFailed";
    /// <summary>Format: {0} = rows shown, {1} = matching rows.</summary>
    public const string RefineSearch = "NativeMinimalPairs_RefineSearch";
    // Session
    public const string SessionSummary = "MinimalPairSession_SessionSummary";
    public const string Correct = "MinimalPairSession_Correct";
    public const string Incorrect = "MinimalPairSession_Incorrect";
    public const string Accuracy = "Common_Accuracy";
    public const string Duration = "MinimalPairSession_Duration";
    /// <summary>Format: {0} = minutes (source "{Duration:F1} min").</summary>
    public const string DurationValue = "NativeMinimalPairs_DurationValue";
    /// <summary>Format: {0} = trial number, {1} = total trials.</summary>
    public const string Trial = "MinimalPairSession_Trial";
    public const string Replay = "MinimalPairSession_Replay";
    public const string CheckAnswer = "MinimalPairSession_CheckAnswer";
    public const string SessionNoPairs = "MinimalPairSession_NoPairs";
    public const string AudioOffline = "OfflineAudioUnavailable";
    public const string PlaybackError = "NativeMinimalPairs_PlaybackError";
    /// <summary>Spoken after Check Answer only (never before: the prompt is audio-only).</summary>
    public const string AnswerCorrect = "MinimalPairsCorrect";
    public const string AnswerIncorrect = "MinimalPairsIncorrect";
    public const string Done = "Common_Done";
    public const string GoBack = "Common_GoBack";
    public const string Dismiss = "Writing_DismissNotices";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";
    public const string SaveFailed = "NativeCommon_SaveFailed";
}

/// <summary>Which source page shows: /minimal-pairs, /minimal-pairs/create or /minimal-pairs/session/{Id}.</summary>
public enum NativeMinimalPairsPage { List, Create, Session }

/// <summary>Source modes: Focus (one pair, 10 trials) and Mixed (all pairs, 20 trials).</summary>
public enum NativeMinimalPairsMode { Focus, Mixed }

/// <summary>A pair card: the two target terms and the optional contrast label.</summary>
public sealed record NativeMinimalPairItem(int Id, string WordA, string WordB, string? ContrastLabel)
{
    public string AccessibilityId => NativeMinimalPairsAccess.Pair(Id);
}

public sealed record NativeMinimalPairsListState(
    bool IsLoading,
    ImmutableArray<NativeMinimalPairItem> Pairs,
    NativeMinimalPairsMode Mode = NativeMinimalPairsMode.Focus,
    int? SelectedPairId = null,
    NativeMinimalPairItem? PendingDelete = null,
    bool IsDeleting = false)
{
    public bool IsEmpty => !IsLoading && Pairs.IsDefaultOrEmpty;

    /// <summary>Source shows the start button only while pairs exist.</summary>
    public bool ShowsStart => !IsLoading && !Pairs.IsDefaultOrEmpty;

    /// <summary>Focus needs a selected pair (the button is disabled otherwise); Mixed uses all pairs.</summary>
    public bool CanStart => ShowsStart && !IsDeleting
        && (Mode == NativeMinimalPairsMode.Mixed || SelectedPairId is not null);

    /// <summary>Source: every card is highlighted in Mixed; only the chosen card in Focus.</summary>
    public bool IsHighlighted(int pairId) =>
        Mode == NativeMinimalPairsMode.Mixed || SelectedPairId == pairId;
}

/// <summary>A vocabulary row in the create pickers (source "target — native").</summary>
public sealed record NativeMinimalPairWordOption(string Id, string Target, string Native);

public sealed record NativeMinimalPairsCreateState(
    bool IsLoading,
    ImmutableArray<NativeMinimalPairWordOption> Words,
    string SearchA = "",
    string SearchB = "",
    string? WordAId = null,
    string? WordBId = null,
    string ContrastLabel = "",
    bool IsSaving = false,
    string? ErrorKey = null)
{
    /// <summary>Rows a picker renders at most; the search narrows a longer list.</summary>
    public const int MaxRows = 200;

    public ImmutableArray<NativeMinimalPairWordOption> FilteredA => Filter(SearchA);

    public ImmutableArray<NativeMinimalPairWordOption> FilteredB => Filter(SearchB);

    public NativeMinimalPairWordOption? WordA => Words.IsDefaultOrEmpty ? null
        : Words.FirstOrDefault(word => word.Id == WordAId);

    public NativeMinimalPairWordOption? WordB => Words.IsDefaultOrEmpty ? null
        : Words.FirstOrDefault(word => word.Id == WordBId);

    /// <summary>Source: Create is enabled when both words are chosen and nothing is saving.</summary>
    public bool CanCreate => !IsLoading && !IsSaving && WordAId is not null && WordBId is not null;

    /// <summary>Source filter: target term starts with the search, or native term contains it (case-insensitive).</summary>
    public ImmutableArray<NativeMinimalPairWordOption> Filter(string? search)
    {
        if (Words.IsDefaultOrEmpty)
            return [];
        if (string.IsNullOrWhiteSpace(search))
            return Words;
        return [.. Words.Where(word =>
            word.Target.StartsWith(search, StringComparison.OrdinalIgnoreCase)
            || word.Native.Contains(search, StringComparison.OrdinalIgnoreCase))];
    }
}

/// <summary>An answer tile. Its label is the target term only, so it never tells which tile was spoken.</summary>
public sealed record NativeMinimalPairTile(string WordId, string Term);

/// <summary>Source tile styles after Check Answer (border-info / border-danger, check icon on the answer).</summary>
public enum NativeMinimalPairTileMark { None, Selected, SelectedCorrect, SelectedWrong, Answer }

public sealed record NativeMinimalPairsSessionState(
    bool IsInitializing,
    bool IsEmpty = false,
    NativeMinimalPairsMode Mode = NativeMinimalPairsMode.Focus,
    int TrialIndex = 0,
    int TotalTrials = 0,
    int CorrectCount = 0,
    int IncorrectCount = 0,
    NativeMinimalPairTile? Left = null,
    NativeMinimalPairTile? Right = null,
    string? SelectedWordId = null,
    bool HasChecked = false,
    bool? WasCorrect = null,
    // The spoken word, published only after Check Answer (audio-only prompt).
    string? AnswerWordId = null,
    bool IsPlayingAudio = false,
    string? AudioNoticeKey = null,
    bool IsAdvancing = false,
    bool ShowSummary = false,
    double DurationMinutes = 0)
{
    /// <summary>Source "Trial {n} / {total}": the trial being answered (1-based) until the summary.</summary>
    public int TrialNumber => Math.Min(TrialIndex + 1, Math.Max(TotalTrials, 1));

    public double Accuracy => CorrectCount + IncorrectCount > 0
        ? (double)CorrectCount / (CorrectCount + IncorrectCount) * 100 : 0;

    public bool HasTrial => !IsInitializing && !ShowSummary && Left is not null && Right is not null;

    /// <summary>Source: Check Answer needs a selection and is off while debouncing or after checking.</summary>
    public bool CanCheck => HasTrial && SelectedWordId is not null && !HasChecked && !IsAdvancing;

    /// <summary>Source: Replay is off while audio is being prepared and after checking.</summary>
    public bool CanReplay => HasTrial && !IsPlayingAudio && !HasChecked;

    public bool CanSelect => HasTrial && !HasChecked && !IsAdvancing;

    public NativeMinimalPairTileMark MarkFor(NativeMinimalPairTile tile)
    {
        var selected = SelectedWordId == tile.WordId;
        if (!HasChecked)
            return selected ? NativeMinimalPairTileMark.Selected : NativeMinimalPairTileMark.None;
        if (selected)
            return WasCorrect == true ? NativeMinimalPairTileMark.SelectedCorrect
                : NativeMinimalPairTileMark.SelectedWrong;
        return tile.WordId == AnswerWordId && WasCorrect == false
            ? NativeMinimalPairTileMark.Answer : NativeMinimalPairTileMark.None;
    }

    /// <summary>Source shows the check icon on the correct tile once the answer is checked.</summary>
    public bool ShowsCheck(NativeMinimalPairTile tile) => HasChecked && tile.WordId == AnswerWordId;
}

/// <summary>
/// Phases: Loading (first list load), Ready (one of the three pages), Blocked
/// (owner absent/changed or launch refused). Destination becomes TodaysPlan
/// after the list's Back (source NavigateTo("/")).
/// </summary>
public sealed record NativeMinimalPairsState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    NativeMinimalPairsPage Page = NativeMinimalPairsPage.List,
    NativeMinimalPairsListState? List = null,
    NativeMinimalPairsCreateState? Create = null,
    NativeMinimalPairsSessionState? Session = null,
    ImmutableArray<NativeMessage> Notices = default,
    string? ErrorKey = null);
