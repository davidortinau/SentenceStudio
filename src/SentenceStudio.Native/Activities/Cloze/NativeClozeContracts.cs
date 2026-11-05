using System.Collections.Immutable;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

public static class NativeClozeAccess
{
    public const string Screen = "native.cloze.screen";
    public const string Loading = "native.cloze.loading";
    public const string Sentence = "native.cloze.sentence";
    public const string Translation = "native.cloze.translation";
    public const string Feedback = "native.cloze.feedback";
    public const string Notice = "native.cloze.notice";
    /// <summary>Choice buttons are "native.cloze.choice.{index}".</summary>
    public const string Choice = "native.cloze.choice";
    /// <summary>The docked input bar (answer, choices, GO, previous/next), outside <see cref="Screen"/>.</summary>
    public const string InputBar = "native.cloze.input-bar";
    public const string Answer = "native.cloze.answer";
    public const string ModeToggle = "native.cloze.mode";
    public const string Submit = "native.cloze.go";
    public const string Previous = "native.cloze.previous";
    public const string Next = "native.cloze.next";
    public const string Position = "native.cloze.position";
    public const string Summary = "native.cloze.summary";
    public const string SummaryCorrect = "native.cloze.summary.correct";
    public const string SummaryIncorrect = "native.cloze.summary.incorrect";
    public const string SummaryAccuracy = "native.cloze.summary.accuracy";
    public const string ContinuePractice = "native.cloze.continue";
    public const string Done = "native.cloze.done";
    public const string Empty = "native.cloze.empty";
    public const string EmptyBack = "native.cloze.empty.back";
    public const string Error = "native.cloze.error";
    public const string Back = "native.cloze.back";
}

public static class NativeClozeText
{
    public const string Title = "Activity_Cloze";
    public const string Loading = "Cloze_LoadingSentences";
    public const string NoSentences = "Cloze_NoSentences";
    public const string TypeMissingWord = "Cloze_TypeMissingWord";
    public const string Choices = "Cloze_Choices";
    public const string Type = "Cloze_Type";
    public const string Go = "Common_GoLabel";
    public const string Correct = "Cloze_CorrectExclamation";
    public const string AnswerWas = "Cloze_AnswerWas";
    public const string SessionComplete = "Common_SessionComplete";
    public const string CorrectCount = "Common_Correct";
    public const string IncorrectCount = "Common_Incorrect";
    public const string Accuracy = "Common_Accuracy";
    public const string ContinuePractice = "Common_ContinuePractice";
    public const string Done = "Common_Done";
    public const string GoBack = "Common_GoBack";
    public const string Previous = "Common_Previous";
    public const string Next = "Common_Next";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";
    public const string GenerationFailed = "NativeCloze_GenerationFailed";
    public const string FamiliarConfirmed = "NativeCloze_FamiliarConfirmed";
    public const string FamiliarTricky = "NativeCloze_FamiliarTricky";
    public const string Blank = "NativeCloze_Blank";
}

public static class NativeClozeInputMode
{
    public const string Text = "Text";
    public const string MultipleChoice = "MultipleChoice";
}

/// <summary>
/// One AI cloze sentence after the source self-healing (ClozureService):
/// <see cref="Answer"/> is the as-used form, <see cref="Options"/> holds
/// exactly the repaired guesses, and <see cref="Word"/> is the owner's linked
/// vocabulary word when the dictionary form matched one.
/// </summary>
public sealed record NativeClozeChallenge(
    string SentenceText,
    string Translation,
    string DictionaryForm,
    string Answer,
    ImmutableArray<string> Options,
    VocabularyWord? Word)
{
    /// <summary>The blank every client shows in place of the answer.</summary>
    public const string BlankMarker = ClozureAnswerHelper.BlankMarker;

    /// <summary>
    /// Source SetCurrentChallenge, through the rule every client shares: blank the
    /// as-used form, else the dictionary form. Null when no blank is possible
    /// (including an empty or whitespace answer), so the caller drops the sentence.
    /// </summary>
    public string? BlankedSentence =>
        ClozureAnswerHelper.TryBlankSentence(SentenceText, Answer, DictionaryForm);
}

/// <summary>
/// Phases: Loading, Ready (a sentence; <see cref="IsGraded"/> after an
/// answer), Summary (session complete), Empty (no usable vocabulary or
/// sentences; Go Back), Error (AI generation failed; Go Back), Blocked (owner
/// absent/changed or launch refused). Destination becomes TodaysPlan after Back.
/// </summary>
public sealed record NativeClozeState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    string DisplaySentence = "",
    string Translation = "",
    int Index = 0,
    int Total = 0,
    string InputMode = NativeClozeInputMode.Text,
    ImmutableArray<string> Options = default,
    string TypedAnswer = "",
    string? SelectedOption = null,
    string? CorrectAnswer = null,
    bool IsGraded = false,
    bool? LastWasCorrect = null,
    int SessionCorrect = 0,
    int SessionTotal = 0,
    NativeMessage? Notice = null,
    string? ErrorKey = null,
    bool HasPendingSync = false,
    int Generation = 0)
{
    public bool CanGoPrevious => Phase == NativePhase.Ready && Index > 0;

    public int SessionIncorrect => SessionTotal - SessionCorrect;

    /// <summary>Source summary: (int)((float)correct / total * 100), 0 when nothing was answered.</summary>
    public int AccuracyPercent => SessionTotal > 0
        ? (int)((float)SessionCorrect / SessionTotal * 100) : 0;
}
