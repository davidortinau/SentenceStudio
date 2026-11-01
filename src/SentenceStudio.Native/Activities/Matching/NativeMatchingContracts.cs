using System.Collections.Immutable;

namespace SentenceStudio.Native;

public static class NativeMatchingAccess
{
    public const string Screen = "native.matching.screen";
    public const string Loading = "native.matching.loading";
    public const string Status = "native.matching.status";
    public const string MatchedCount = "native.matching.status.matched";
    public const string MissCount = "native.matching.status.misses";
    public const string Grid = "native.matching.grid";
    /// <summary>Tiles are "native.matching.tile.{Id}"; hidden native tiles are unlabeled placeholders.</summary>
    public const string Tile = "native.matching.tile";
    public const string Empty = "native.matching.empty";
    public const string EmptyBack = "native.matching.empty.back";
    public const string Complete = "native.matching.complete";
    public const string CompleteMatched = "native.matching.complete.matched";
    public const string CompleteMisses = "native.matching.complete.misses";
    public const string PlayAgain = "native.matching.play-again";
    public const string NewGame = "native.matching.new-game";
    public const string HideToggle = "native.matching.hide-native";
    public const string Menu = "native.matching.menu";
    public const string Error = "native.matching.error";
    public const string Back = "native.matching.back";
}

public static class NativeMatchingText
{
    public const string Title = "Activity_VocabMatching";
    public const string NewGame = "VocabMatching_NewGame";
    public const string ShowAll = "VocabMatching_ShowAll";
    public const string HideNative = "VocabMatching_HideNative";
    public const string Congratulations = "VocabMatching_Congratulations";
    public const string AllPairsMatched = "VocabMatching_AllPairsMatched";
    public const string Matched = "VocabMatching_Matched";
    public const string Misses = "VocabMatching_Misses";
    public const string Loading = "VocabMatching_LoadingVocab";
    public const string NoVocabulary = "VocabMatching_NoVocab";
    public const string PlayAgain = "Common_PlayAgain";
    public const string GoBack = "Common_GoBack";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";
    public const string LoadFailed = "NativeActivity_LoadFailed";
}

public sealed record NativeMatchingTile(
    int Id, string Text, bool IsTarget, string WordId,
    bool IsVisible, bool IsSelected, bool IsMatched)
{
    public string AccessibilityId => $"{NativeMatchingAccess.Tile}.{Id}";
}

/// <summary>
/// Phases: Loading, Ready (playing), Summary (all pairs matched), Empty (no
/// usable vocabulary; Go Back), Blocked (owner absent/changed or launch
/// refused). Destination becomes TodaysPlan after Back.
/// </summary>
public sealed record NativeMatchingState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    ImmutableArray<NativeMatchingTile> Tiles = default,
    int MatchedPairs = 0,
    int TotalPairs = 0,
    int Misses = 0,
    bool HideNativeWords = true,
    bool IsChecking = false,
    string? ErrorKey = null,
    bool HasPendingSync = false)
{
    public bool IsComplete => Phase == NativePhase.Summary;
}
