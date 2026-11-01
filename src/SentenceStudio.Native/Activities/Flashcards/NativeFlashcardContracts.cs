using System.Collections.Immutable;

namespace SentenceStudio.Native;

public static class NativeFlashcardAccess
{
    public const string Screen = "native.flashcards.screen";
    public const string Loading = "native.flashcards.loading";
    public const string Counter = "native.flashcards.counter";
    public const string CorrectTally = "native.flashcards.tally.correct";
    public const string IncorrectTally = "native.flashcards.tally.incorrect";
    public const string Card = "native.flashcards.card";
    public const string Incorrect = "native.flashcards.incorrect";
    public const string Correct = "native.flashcards.correct";
    public const string Empty = "native.flashcards.empty";
    public const string EmptyBack = "native.flashcards.empty.back";
    public const string Summary = "native.flashcards.summary";
    public const string SummaryCorrect = "native.flashcards.summary.correct";
    public const string SummaryIncorrect = "native.flashcards.summary.incorrect";
    public const string SummaryTotal = "native.flashcards.summary.total";
    /// <summary>Summary rows are "native.flashcards.summary.row.{Index}".</summary>
    public const string SummaryRow = "native.flashcards.summary.row";
    public const string Done = "native.flashcards.done";
    public const string Error = "native.flashcards.error";
    public const string Back = "native.flashcards.back";
}

public static class NativeFlashcardText
{
    public const string Title = "Flashcard_Title";
    public const string Summary = "Flashcard_Summary";
    public const string NoVocabulary = "Flashcard_NoPreviewVocabulary";
    /// <summary>"Card {0} / {1}".</summary>
    public const string CardCounter = "Flashcard_CardCounter";
    /// <summary>Accessibility hint for the card's tap-to-flip action.</summary>
    public const string Flip = "Flashcard_FlipCard";
    public const string Correct = "Common_Correct";
    public const string Incorrect = "Common_Incorrect";
    public const string Total = "Total";
    public const string Done = "Common_Done";
    public const string GoBack = "Common_GoBack";
    public const string Loading = "Common_Loading";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";
    public const string LoadFailed = "NativeActivity_LoadFailed";

    /// <summary>VoiceOver/TalkBack language for target-language terms.</summary>
    public const string TargetSpeechLanguage = "ko-KR";
}

/// <summary>One plan preview word (PlanPreviewWord) shown as a card.</summary>
public sealed record NativeFlashcard(int Index, string WordId, string TargetTerm, string NativeTerm);

public sealed record NativeFlashcardResult(NativeFlashcard Card, bool WasCorrect);

/// <summary>
/// Phases: Loading, Ready (cards), Summary (after the last card), Empty (no
/// preview vocabulary; Go Back), Error (plan could not be read; Go Back),
/// Blocked (owner absent/changed or launch refused). Destination becomes
/// TodaysPlan after Back/Done. <see cref="PendingAnswer"/> is the 160 ms swipe
/// animation (source isSwipeAnimating): the answer buttons are disabled and the
/// card is thrown right (true) or left (false).
/// </summary>
public sealed record NativeFlashcardState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    ImmutableArray<NativeFlashcard> Cards = default,
    int CurrentIndex = 0,
    bool IsFlipped = false,
    ImmutableArray<NativeFlashcardResult> Results = default,
    bool? PendingAnswer = null,
    string? ErrorKey = null)
{
    public int Total => Cards.IsDefault ? 0 : Cards.Length;

    public NativeFlashcard? Current =>
        Phase == NativePhase.Ready && CurrentIndex >= 0 && CurrentIndex < Total
            ? Cards[CurrentIndex] : null;

    public bool IsAnimating => PendingAnswer is not null;

    public int CorrectCount => Results.IsDefault ? 0 : Results.Count(result => result.WasCorrect);

    public int IncorrectCount => Results.IsDefault ? 0 : Results.Count(result => !result.WasCorrect);
}

public enum NativeFlashcardRelease { None, Flip, Incorrect, Correct }

/// <summary>
/// Source FlashcardActivity.razor pointer thresholds (CSS px, used as points/dp):
/// a release at least 110 from the start swipes (right = correct), a release
/// within 12 flips, anything between snaps back. Rotation follows the drag.
/// </summary>
public static class NativeFlashcardGesture
{
    public const double SwipeThreshold = 110d;
    public const double TapFlipThreshold = 12d;
    public const double ThrowDistance = 560d;

    public static NativeFlashcardRelease Classify(double delta) =>
        Math.Abs(delta) >= SwipeThreshold
            ? delta > 0 ? NativeFlashcardRelease.Correct : NativeFlashcardRelease.Incorrect
            : Math.Abs(delta) <= TapFlipThreshold
                ? NativeFlashcardRelease.Flip : NativeFlashcardRelease.None;

    public static double RotationDegrees(double offset) => Math.Clamp(offset / 18d, -18d, 18d);
}
