using System.Collections.Immutable;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public static class NativeWordAssociationAccess
{
    public const string Screen = "native.word-association.screen";
    public const string Loading = "native.word-association.loading";
    public const string Empty = "native.word-association.empty";
    public const string EmptyBack = "native.word-association.empty.back";
    public const string Error = "native.word-association.error";
    /// <summary>Notices are "native.word-association.notice.{index}".</summary>
    public const string Notice = "native.word-association.notice";
    public const string Notices = "native.word-association.notices";
    public const string DismissNotices = "native.word-association.notices.dismiss";
    public const string Progress = "native.word-association.progress";
    public const string Target = "native.word-association.target";
    public const string NativeTerm = "native.word-association.native";
    public const string Instructions = "native.word-association.instructions";
    /// <summary>Clue rows are "native.word-association.clue.{index}" with ".status", ".text", ".double", ".explanation", ".corrected", ".remove" suffixes.</summary>
    public const string Clue = "native.word-association.clue";
    public const string Grading = "native.word-association.grading";
    public const string WordScore = "native.word-association.word-score";
    public const string Input = "native.word-association.input";
    public const string Add = "native.word-association.add";
    public const string Grade = "native.word-association.grade";
    public const string Previous = "native.word-association.previous";
    public const string Position = "native.word-association.position";
    public const string Next = "native.word-association.next";
    public const string Finish = "native.word-association.finish";
    public const string Summary = "native.word-association.summary";
    public const string SummaryRelated = "native.word-association.summary.related";
    public const string SummaryNotRelated = "native.word-association.summary.not-related";
    public const string SummaryScore = "native.word-association.summary.score";
    public const string HighScore = "native.word-association.summary.high-score";
    public const string PlayAgain = "native.word-association.play-again";
    public const string Done = "native.word-association.done";
    public const string Back = "native.word-association.back";
}

public static class NativeWordAssociationText
{
    public const string Title = "Activity_WordAssociation";
    public const string RoundComplete = "WordAssoc_RoundComplete";
    public const string Related = "WordAssoc_Related";
    public const string NotRelated = "WordAssoc_NotRelated";
    public const string Score = "WordAssoc_Score";
    public const string NewAllTimeHigh = "WordAssoc_NewAllTimeHigh";
    public const string BestWeek = "WordAssoc_BestWeek";
    public const string Loading = "WordAssoc_LoadingVocab";
    /// <summary>Format: {0} = 1-based word number, {1} = word count.</summary>
    public const string Word = "WordAssoc_Word";
    public const string WriteClues = "WordAssoc_WriteClues";
    public const string ClueBonus = "WordAssoc_ClueBonus";
    public const string TypeClue = "WordAssoc_TypeClue";
    public const string GradeClues = "WordAssoc_GradeClues";
    public const string GradingClues = "WordAssoc_GradingClues";
    /// <summary>Format: {0} = word score, {1} = possible points.</summary>
    public const string WordScore = "WordAssoc_WordScore";
    public const string Next = "WordAssoc_Next";
    public const string FinishRound = "WordAssoc_FinishRound";
    public const string NoVocabulary = "WordAssoc_NoVocab";
    public const string PlayAgain = "Common_PlayAgain";
    public const string Done = "Common_Done";
    public const string GoBack = "Common_GoBack";
    public const string Previous = "Common_Previous";
    // Source hard-coded toasts and labels (WordAssociation.razor), localized.
    public const string NoResourceVocabulary = "WordAssoc_NoResourceVocab";
    public const string LoadFailed = "WordAssoc_LoadFailed";
    public const string GradeFailed = "WordAssoc_GradeFailed";
    /// <summary>Format: {0} = corrected clue text.</summary>
    public const string Corrected = "WordAssoc_Corrected";
    public const string AddClue = "WordAssoc_AddClue";
    public const string RemoveClue = "WordAssoc_RemoveClue";
    public const string Pending = "WordAssoc_Pending";
    public const string DoublePoints = "WordAssoc_DoublePoints";
    public const string Dismiss = "Writing_DismissNotices";
    public const string ProbeConfirmed = "Writing_ProbeConfirmed";
    public const string ProbeTricky = "Writing_ProbeTricky";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";
}

/// <summary>Source CheckHighScoreAsync result ("alltime", "weekly" or null).</summary>
public enum NativeWordAssociationHighScore { None, AllTime, Weekly }

/// <summary>One learner clue for the current word; ungraded clues can be removed.</summary>
public sealed record NativeWordAssociationClue(
    int Index,
    string Text,
    bool Graded,
    bool Related,
    bool IsCloze,
    string? Explanation,
    string? CorrectedText)
{
    public string AccessibilityId => $"{NativeWordAssociationAccess.Clue}.{Index}";

    /// <summary>Source shows "(Corrected: ...)" only when it differs from the clue.</summary>
    public bool ShowsCorrection => Graded && !string.IsNullOrEmpty(CorrectedText)
        && !string.Equals(CorrectedText, Text, StringComparison.Ordinal);

    /// <summary>Source "2x" badge: a related full-sentence clue.</summary>
    public bool ShowsDouble => Graded && IsCloze && Related;
}

/// <summary>
/// Phases: Loading, Ready (one word with its clues and the input bar),
/// Summary (round complete), Empty (no vocabulary or load failure; Go Back),
/// Blocked (owner absent or changed). Destination becomes TodaysPlan after
/// Back / Done / Go Back (the Dashboard).
/// </summary>
public sealed record NativeWordAssociationState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    int Index = 0,
    int WordCount = 0,
    string TargetTerm = "",
    string? NativeTerm = null,
    ImmutableArray<NativeWordAssociationClue> Clues = default,
    string Draft = "",
    bool IsGrading = false,
    int TotalRelated = 0,
    int TotalClues = 0,
    int TotalScore = 0,
    NativeWordAssociationHighScore HighScore = NativeWordAssociationHighScore.None,
    ImmutableArray<NativeMessage> Notices = default,
    string? ErrorKey = null,
    bool HasPendingSync = false)
{
    private ImmutableArray<NativeWordAssociationClue> SafeClues => Clues.IsDefault ? [] : Clues;

    public bool HasUngraded => SafeClues.Any(clue => !clue.Graded);

    public bool HasGraded => SafeClues.Any(clue => clue.Graded);

    /// <summary>Source per-word score: related clue 1, related full sentence 2.</summary>
    public int WordScore => SafeClues.Where(clue => clue.Graded)
        .Sum(clue => clue.Related ? (clue.IsCloze ? 2 : 1) : 0);

    public int WordMax => SafeClues.Where(clue => clue.Graded).Sum(clue => clue.IsCloze ? 2 : 1);

    public bool IsLastWord => Index >= WordCount - 1;

    /// <summary>Source shows the instructions only before the first clue and not while grading.</summary>
    public bool ShowsInstructions => SafeClues.Length == 0 && !IsGrading;

    public bool CanAddClue => Phase == NativePhase.Ready && !IsGrading
        && !string.IsNullOrWhiteSpace(Draft);

    public bool CanGrade => Phase == NativePhase.Ready && !IsGrading && HasUngraded;

    public bool CanPrevious => Phase == NativePhase.Ready && Index > 0 && !IsGrading;

    /// <summary>Source Next / Finish Round: enabled only when every clue of the word is graded.</summary>
    public bool CanAdvance => Phase == NativePhase.Ready && !IsGrading && !HasUngraded;
}

/// <summary>
/// AI port for Word Association: the source GradeWordAssociation template
/// sent at the Reasoning tier (WordAssociationService.GradeCluesAsync)
/// through the owner-bound native gateway.
/// </summary>
public interface INativeWordAssociationAi
{
    Task<WordAssociationGradeResponse?> GradeAsync(string owner, string targetTerm,
        string? nativeTerm, IReadOnlyList<string> clues, string targetLanguage,
        string nativeLanguage, CancellationToken cancellationToken);
}

/// <summary>
/// Owner-bound AI prompt at the Reasoning tier with the server's default
/// reasoning effort, for source callers of AiService.SendPrompt(prompt, AiTier.Reasoning).
/// </summary>
public interface IOwnerBoundNativeReasoningAiGateway
{
    Task<T?> SendReasoningPromptAsync<T>(
        string owner, string prompt, CancellationToken cancellationToken);
}
