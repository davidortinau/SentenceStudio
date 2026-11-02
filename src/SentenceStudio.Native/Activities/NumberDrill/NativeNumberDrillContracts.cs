using System.Collections.Immutable;

namespace SentenceStudio.Native;

public static class NativeNumberDrillAccess
{
    public const string Screen = "native.numberdrill.screen";
    public const string Loading = "native.numberdrill.loading";
    public const string Back = "native.numberdrill.back";
    public const string Error = "native.numberdrill.error";
    public const string Notice = "native.numberdrill.notice";
    public const string Gate = "native.numberdrill.gate";
    public const string GateBack = "native.numberdrill.gate.back";
    // Setup
    public const string Setup = "native.numberdrill.setup";
    /// <summary>Context chips are "native.numberdrill.context.{Code}"; Any is ".any".</summary>
    public const string Context = "native.numberdrill.context";
    public const string ContextAny = "native.numberdrill.context.any";
    /// <summary>Sub-mode chips are "native.numberdrill.mode.{Code}".</summary>
    public const string Mode = "native.numberdrill.mode";
    public const string ItemCount = "native.numberdrill.items";
    public const string ItemsDecrease = "native.numberdrill.items.decrease";
    public const string ItemsIncrease = "native.numberdrill.items.increase";
    public const string DueOnly = "native.numberdrill.dueonly";
    public const string Start = "native.numberdrill.start";
    // Session
    public const string Item = "native.numberdrill.item";
    public const string System = "native.numberdrill.system";
    public const string ItemContext = "native.numberdrill.item.context";
    public const string Play = "native.numberdrill.play";
    public const string Replay = "native.numberdrill.replay";
    public const string Prompt = "native.numberdrill.prompt";
    public const string NounCue = "native.numberdrill.nouncue";
    /// <summary>Counter chips / time cards are "native.numberdrill.choice.{index}".</summary>
    public const string Choice = "native.numberdrill.choice";
    public const string PromptA = "native.numberdrill.prompt-a";
    public const string PromptB = "native.numberdrill.prompt-b";
    /// <summary>Paired choices are "native.numberdrill.choice-a.{index}" / "choice-b.{index}".</summary>
    public const string ChoiceA = "native.numberdrill.choice-a";
    public const string ChoiceB = "native.numberdrill.choice-b";
    public const string HintA = "native.numberdrill.hint-a";
    public const string HintB = "native.numberdrill.hint-b";
    public const string SubmitBoth = "native.numberdrill.submit-both";
    public const string HintToggle = "native.numberdrill.hint";
    public const string Hints = "native.numberdrill.hints";
    public const string Answer = "native.numberdrill.answer";
    public const string Submit = "native.numberdrill.submit";
    public const string Feedback = "native.numberdrill.feedback";
    public const string Verdict = "native.numberdrill.verdict";
    public const string YouTyped = "native.numberdrill.feedback.typed";
    public const string DigitAnswer = "native.numberdrill.feedback.number";
    public const string KoreanAnswer = "native.numberdrill.feedback.korean";
    public const string ErrorClass = "native.numberdrill.feedback.error-class";
    public const string MasteryProgress = "native.numberdrill.feedback.progress";
    public const string Override = "native.numberdrill.override";
    public const string Next = "native.numberdrill.next";
    public const string Progress = "native.numberdrill.progress";
    public const string CorrectCount = "native.numberdrill.correct-count";
    // Summary
    public const string Summary = "native.numberdrill.summary";
    public const string SummaryCorrect = "native.numberdrill.summary.correct";
    public const string SummaryMedian = "native.numberdrill.summary.median";
    public const string Patterns = "native.numberdrill.summary.patterns";
    public const string AnotherRound = "native.numberdrill.another-round";
    public const string Done = "native.numberdrill.done";
}

/// <summary>Localization keys. Source copy that was hard-coded English now has en and ko resources.</summary>
public static class NativeNumberDrillText
{
    public const string Title = "PlanItemNumberDrillTitle";
    public const string ChooseDrill = "NumberDrill_ChooseDrill";
    public const string Context = "NumberDrill_Context";
    public const string Mode = "NumberDrill_Mode";
    public const string AnyContext = "NumberDrill_AnyContext";
    public const string Items = "NumberDrill_Items";
    public const string FewerItems = "NumberDrill_FewerItems";
    public const string MoreItems = "NumberDrill_MoreItems";
    public const string DueOnly = "NumberDrill_DueOnly";
    public const string StartSession = "NumberDrill_StartSession";
    public const string Play = "NumberDrill_Play";
    public const string Playing = "NumberDrillPlaying";
    public const string PlayAudio = "NumberDrillPlayAudio";
    public const string Replay = "NumberDrillReplayAudio";
    public const string ListenAndPlaceInstructions = "NumberDrillListenAndPlaceInstructions";
    public const string SubmitBoth = "NumberDrillSubmitBoth";
    public const string BothCorrect = "NumberDrillBothCorrect";
    public const string PartialCorrect = "NumberDrillPartialCorrect";
    public const string BothIncorrect = "NumberDrillBothIncorrect";
    public const string PromptA = "NumberDrill_PromptA";
    public const string PromptB = "NumberDrill_PromptB";
    public const string ShowHint = "NumberDrill_ShowHint";
    public const string ExamplePattern = "NumberDrill_ExamplePattern";
    public const string Submit = "Common_Submit";
    public const string YouTyped = "NumberDrill_YouTyped";
    public const string Number = "NumberDrill_Number";
    public const string Korean = "NumberDrill_Korean";
    public const string MasteryProgress = "NumberDrill_MasteryProgress";
    public const string IWasCorrect = "NumberDrill_IWasCorrect";
    public const string OverriddenVerdict = "NumberDrill_OverriddenVerdict";
    public const string OverrideFailed = "NumberDrill_OverrideFailed";
    public const string DisambiguateTip = "NumberDrill_DisambiguateTip";
    public const string Next = "Common_Next";
    public const string Finish = "NumberDrill_Finish";
    public const string CorrectCount = "NumberDrill_CorrectCount";
    public const string SessionComplete = "NumberDrill_SessionComplete";
    public const string Correct = "Common_Correct";
    public const string MedianTime = "NumberDrill_MedianTime";
    public const string Milliseconds = "NumberDrill_Milliseconds";
    public const string PatternsToReview = "NumberDrill_PatternsToReview";
    public const string AnotherRound = "NumberDrill_AnotherRound";
    public const string Done = "Common_Done";
    public const string GoBack = "Common_GoBack";
    public const string KoreanOnly = "NumberDrill_KoreanOnly";
    public const string NoItems = "NumberDrill_NoItems";
    public const string LoadFailed = "NumberDrill_LoadFailed";
    public const string StartFailed = "NumberDrill_StartFailed";
    public const string SubmitFailed = "NumberDrill_SubmitFailed";
    public const string EndFailed = "NumberDrill_EndFailed";
    public const string AudioOffline = "NumberDrill_AudioOffline";
    public const string AudioFailed = "NumberDrill_AudioFailed";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";
}

public enum NativeNumberDrillStage { Setup, InSession, Summary }

/// <summary>Source sub-mode codes (NumberSubMode.Code).</summary>
public static class NativeNumberSubMode
{
    public const string ListenAndType = "ListenAndType";
    public const string ReadAndProduce = "ReadAndProduce";
    public const string TapTheCounter = "TapTheCounter";
    public const string Disambiguate = "Disambiguate";
    public const string ListenAndPlace = "ListenAndPlace";

    /// <summary>Text-entry modes: hint list, typed answer, manual Submit.</summary>
    public static bool IsTextEntry(string? code) =>
        code is not (TapTheCounter or Disambiguate or ListenAndPlace);

    /// <summary>Modes with an audio prompt.</summary>
    public static bool HasAudioPrompt(string? code) => code is ListenAndType or ListenAndPlace;
}

public sealed record NativeNumberChoice(string Code, string DisplayName);

/// <summary>The current item, projected from the shared NumberItem.</summary>
public sealed record NativeNumberDrillItem(
    string ContextCode,
    string SubModeCode,
    string System,
    string DisplayPrompt,
    string? NounCue,
    ImmutableArray<string> Hints,
    ImmutableArray<string> Choices,
    string? PromptA,
    string? PromptB,
    ImmutableArray<string> ChoicesA,
    ImmutableArray<string> ChoicesB,
    string? HintA,
    string? HintB,
    string SpacingHint,
    bool HasAudio);

/// <summary>Source feedback panel (NumberDrill.razor:354-424).</summary>
public sealed record NativeNumberDrillFeedback(
    bool IsCorrect,
    string? Verdict,
    string? VerdictKey,
    string? UserAnswer,
    string DigitDisplay,
    string CanonicalAnswer,
    string? ErrorClass,
    string? Tip,
    string? TipKey,
    int? ProgressCorrect,
    int? ProgressTotal,
    DateTime? NextReviewUtc,
    bool Overridden,
    bool? CorrectA = null,
    bool? CorrectB = null);

public sealed record NativeNumberDrillSummary(
    int CorrectCount,
    int TotalItems,
    int MedianLatencyMs,
    ImmutableArray<KeyValuePair<string, int>> ErrorClassBreakdown);

/// <summary>
/// Phases: Loading; Ready (setup or answering); Feedback (graded item);
/// Summary; Empty (target language is not Korean: message and Go Back);
/// Blocked (owner absent/changed or launch refused). Destination becomes
/// TodaysPlan after Back.
/// </summary>
public sealed record NativeNumberDrillState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    NativeNumberDrillStage Stage = NativeNumberDrillStage.Setup,
    ImmutableArray<NativeNumberChoice> Contexts = default,
    ImmutableArray<NativeNumberChoice> SubModes = default,
    string? SelectedContext = null,
    string SelectedSubMode = NativeNumberSubMode.ListenAndType,
    int ItemCount = 10,
    bool DueOnly = false,
    NativeNumberDrillItem? Item = null,
    int ItemIndex = 0,
    int ItemTotal = 0,
    int CorrectCount = 0,
    string Answer = "",
    string? SelectedAnswerA = null,
    string? SelectedAnswerB = null,
    NativeNumberDrillFeedback? Feedback = null,
    NativeNumberDrillSummary? Summary = null,
    bool IsBusy = false,
    bool IsPlayingAudio = false,
    bool IsOverriding = false,
    string? ErrorKey = null,
    string? NoticeKey = null,
    bool HasPendingSync = false)
{
    public bool IsLastItem => ItemIndex >= ItemTotal - 1;

    public bool CanSubmitText => Phase == NativePhase.Ready
        && Stage == NativeNumberDrillStage.InSession && !IsBusy
        && !string.IsNullOrWhiteSpace(Answer);

    public bool CanSubmitBoth => Phase == NativePhase.Ready
        && Stage == NativeNumberDrillStage.InSession && !IsBusy
        && !string.IsNullOrEmpty(SelectedAnswerA) && !string.IsNullOrEmpty(SelectedAnswerB);
}
