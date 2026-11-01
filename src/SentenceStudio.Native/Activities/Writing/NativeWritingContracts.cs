using System.Collections.Immutable;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public static class NativeWritingAccess
{
    public const string Screen = "native.writing.screen";
    public const string Loading = "native.writing.loading";
    public const string Empty = "native.writing.empty";
    public const string EmptyBack = "native.writing.empty.back";
    public const string Error = "native.writing.error";
    public const string Notices = "native.writing.notices";
    /// <summary>Notices are "native.writing.notice.{index}".</summary>
    public const string Notice = "native.writing.notice";
    public const string DismissNotices = "native.writing.notices.dismiss";
    public const string Sentences = "native.writing.sentences";
    /// <summary>Graded sentence cards are "native.writing.sentence.{index}" with ".accuracy", ".fluency", ".suggested", ".details", ".details.toggle" suffixes.</summary>
    public const string Sentence = "native.writing.sentence";
    public const string ChooseVocab = "native.writing.choose";
    /// <summary>Vocabulary blocks are "native.writing.block.{index}".</summary>
    public const string Block = "native.writing.block";
    public const string Shuffle = "native.writing.shuffle";
    public const string Input = "native.writing.input";
    public const string Translate = "native.writing.translate";
    public const string Grade = "native.writing.grade";
    public const string Count = "native.writing.count";
    public const string EndSession = "native.writing.end-session";
    public const string Summary = "native.writing.summary";
    public const string SummaryGraded = "native.writing.summary.graded";
    public const string SummaryAccuracy = "native.writing.summary.accuracy";
    public const string SummaryFluency = "native.writing.summary.fluency";
    public const string Continue = "native.writing.continue";
    public const string Done = "native.writing.done";
    public const string Back = "native.writing.back";
}

public static class NativeWritingText
{
    public const string Title = "Activity_Writing";
    public const string Loading = "Writing_LoadingVocab";
    public const string ChooseVocab = "Writing_ChooseVocab";
    public const string NoVocabulary = "Writing_NoVocab";
    public const string WriteSentence = "Writing_WriteSentence";
    public const string Grade = "Writing_Grade";
    public const string SentencesWritten = "Writing_SentencesWritten";
    public const string EndSession = "Writing_EndSession";
    public const string ShowDetails = "Writing_ShowDetails";
    public const string Fluency = "Writing_Fluency";
    public const string Notes = "Writing_Notes";
    public const string Accuracy = "Common_Accuracy";
    public const string Suggested = "Common_Suggested";
    public const string SessionComplete = "Common_SessionComplete";
    public const string Graded = "Common_Graded";
    public const string AvgAccuracy = "Common_AvgAccuracy";
    public const string AvgFluency = "Common_AvgFluency";
    public const string ContinuePractice = "Common_ContinuePractice";
    public const string Done = "Common_Done";
    public const string GoBack = "Common_GoBack";
    public const string Translate = "Writing_Translate";
    public const string Shuffle = "Writing_Shuffle";
    public const string Dismiss = "Writing_DismissNotices";
    // Source toasts (Writing.razor), localized instead of hard-coded English.
    public const string SelectResource = "Writing_SelectResource";
    public const string NoResourceVocabulary = "Writing_NoResourceVocab";
    public const string LoadFailed = "Writing_LoadFailed";
    public const string GradeFailed = "Writing_GradeFailed";
    /// <summary>Format: {0} = whole accuracy percent.</summary>
    public const string GradedAccuracy = "Writing_GradedAccuracy";
    public const string ProbeConfirmed = "Writing_ProbeConfirmed";
    public const string ProbeTricky = "Writing_ProbeTricky";
    public const string TranslateFailed = "Writing_TranslateFailed";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";
}

/// <summary>Source toast kinds: success, info, warning, error.</summary>
public enum NativeWritingNoticeKind { Success, Info, Warning, Error }

/// <summary>
/// A source toast shown inline. <see cref="Key"/> is a localization key
/// (formatted with <see cref="Argument"/> when present); when Key is null,
/// <see cref="Argument"/> is literal text (the translation result).
/// </summary>
public sealed record NativeWritingNotice(
    NativeWritingNoticeKind Kind, string? Key, string? Argument = null);

/// <summary>One of the four target-language vocabulary blocks.</summary>
public sealed record NativeWritingBlock(int Index, string WordId, string Text)
{
    public string AccessibilityId => $"{NativeWritingAccess.Block}.{Index}";
}

/// <summary>A graded sentence card, in source order (newest last).</summary>
public sealed record NativeWritingSentence(
    int Index,
    string Answer,
    double Accuracy,
    double Fluency,
    string? RecommendedSentence,
    string? AccuracyExplanation,
    string? FluencyExplanation,
    string? GrammarNotes)
{
    public string AccessibilityId => $"{NativeWritingAccess.Sentence}.{Index}";

    /// <summary>Source shows the collapsible details only when grammar notes exist.</summary>
    public bool HasDetails => !string.IsNullOrEmpty(GrammarNotes);
}

/// <summary>
/// Phases: Loading (spinner), Ready (blocks + input bar), Summary (session
/// complete), Empty (no usable vocabulary or load failure; Go Back), Blocked
/// (owner absent/changed or launch refused). Destination becomes TodaysPlan
/// after Back/Done.
/// </summary>
public sealed record NativeWritingState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    ImmutableArray<NativeWritingBlock> Blocks = default,
    ImmutableArray<NativeWritingSentence> Sentences = default,
    string Draft = "",
    bool IsGrading = false,
    bool IsTranslating = false,
    int GradedCount = 0,
    int AverageAccuracy = 0,
    int AverageFluency = 0,
    ImmutableArray<NativeWritingNotice> Notices = default,
    string? ErrorKey = null,
    bool HasPendingSync = false)
{
    public const int EndSessionThreshold = 3;

    public bool CanGrade => Phase == NativePhase.Ready && !IsGrading
        && !string.IsNullOrWhiteSpace(Draft);

    public bool CanTranslate => Phase == NativePhase.Ready && !IsTranslating
        && !string.IsNullOrWhiteSpace(Draft);

    /// <summary>Source shows "End session" after three graded sentences.</summary>
    public bool CanEndSession => Phase == NativePhase.Ready
        && !Sentences.IsDefault && Sentences.Length >= EndSessionThreshold;
}

/// <summary>
/// AI port for Writing: the source GradeSentence (Reasoning tier, minimal
/// effort, no meaning or target word) and Translate templates, through the
/// owner-bound native gateway.
/// </summary>
public interface INativeWritingAi
{
    Task<GradeResponse?> GradeAsync(string owner, string sentence,
        string targetLanguage, string nativeLanguage, CancellationToken cancellationToken);

    Task<string?> TranslateAsync(string owner, string text, CancellationToken cancellationToken);
}
