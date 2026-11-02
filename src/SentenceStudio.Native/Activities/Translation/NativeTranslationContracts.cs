using System.Collections.Immutable;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

public static class NativeTranslationAccess
{
    public const string Screen = "native.translation.screen";
    public const string Loading = "native.translation.loading";
    public const string Intro = "native.translation.intro";
    public const string IntroResource = "native.translation.intro.resource";
    public const string IntroVocabulary = "native.translation.intro.vocabulary";
    public const string IntroGrammar = "native.translation.intro.grammar";
    public const string Start = "native.translation.start";
    public const string Prompt = "native.translation.prompt";
    public const string Blocks = "native.translation.blocks";
    /// <summary>Blocks are "native.translation.block.{index}".</summary>
    public const string Block = "native.translation.block";
    public const string Answer = "native.translation.answer";
    public const string ModeToggle = "native.translation.mode";
    public const string Grade = "native.translation.grade";
    public const string Grading = "native.translation.grading";
    public const string Previous = "native.translation.previous";
    public const string Next = "native.translation.next";
    public const string Position = "native.translation.position";
    public const string Feedback = "native.translation.feedback";
    public const string FeedbackInput = "native.translation.feedback.input";
    public const string Accuracy = "native.translation.feedback.accuracy";
    public const string Fluency = "native.translation.feedback.fluency";
    public const string Suggested = "native.translation.feedback.suggested";
    public const string DetailsToggle = "native.translation.feedback.details";
    public const string Details = "native.translation.feedback.details.content";
    public const string Notice = "native.translation.notice";
    public const string Summary = "native.translation.summary";
    public const string SummaryGraded = "native.translation.summary.graded";
    public const string SummaryAccuracy = "native.translation.summary.accuracy";
    public const string SummaryFluency = "native.translation.summary.fluency";
    public const string Continue = "native.translation.continue";
    public const string Done = "native.translation.done";
    public const string Empty = "native.translation.empty";
    public const string EmptyBack = "native.translation.empty.back";
    public const string Error = "native.translation.error";
    public const string Back = "native.translation.back";
}

public static class NativeTranslationText
{
    public const string Title = "Activity_Translate";
    public const string Loading = "Translation_LoadingSentences";
    public const string IntroTitle = "Translation_Intro_Title";
    public const string IntroResource = "Translation_Intro_Resource";
    public const string IntroVocabulary = "Translation_Intro_Vocabulary";
    public const string IntroGrammar = "Translation_Intro_GrammarFocus";
    public const string Start = "Translation_Intro_Start";
    public const string NoSentences = "Translation_NoSentences";
    public const string TranslateTo = "Translation_TranslateTo";
    public const string Blocks = "Translation_Blocks";
    public const string Type = "Translation_Type";
    public const string Grade = "Translation_Grade";
    public const string SessionComplete = "Common_SessionComplete";
    public const string Graded = "Common_Graded";
    public const string AvgAccuracy = "Common_AvgAccuracy";
    public const string AvgFluency = "Common_AvgFluency";
    public const string ContinuePractice = "Common_ContinuePractice";
    public const string Done = "Common_Done";
    public const string Accuracy = "Common_Accuracy";
    public const string Fluency = "Writing_Fluency";
    public const string Suggested = "Common_Suggested";
    public const string ShowDetails = "Writing_ShowDetails";
    public const string Notes = "Writing_Notes";
    public const string GoBack = "Common_GoBack";
    public const string Previous = "Common_Previous";
    public const string Next = "Common_Next";
    /// <summary>Source toasts (Translation.razor:302, 470, 475), now localized.</summary>
    public const string NoSentencesReturned = "Translation_NoSentencesReturned";
    public const string GradedAccuracy = "Translation_GradedAccuracy";
    public const string GradeFailed = "Translation_GradeFailed";
    public const string SaveFailed = "Translation_SaveFailed";
    public const string Unavailable = "NativeActivity_LaunchUnavailable";
    public const string LoadFailed = "NativeActivity_LoadFailed";
}

public sealed record NativeTranslationIntroWord(string Target, string Native);

/// <summary>Source GetScoreColor: >= 80 success, >= 50 warning, else danger.</summary>
public enum NativeScoreTone { Success, Warning, Danger }

public sealed record NativeTranslationFeedback(
    string SubmittedInput,
    double Accuracy,
    double Fluency,
    string RecommendedTranslation,
    string AccuracyExplanation,
    string FluencyExplanation,
    string GrammarNotes)
{
    public bool HasDetails =>
        !string.IsNullOrEmpty(AccuracyExplanation)
        || !string.IsNullOrEmpty(FluencyExplanation)
        || !string.IsNullOrEmpty(GrammarNotes);

    public static NativeScoreTone Tone(double score) => score switch
    {
        >= 80 => NativeScoreTone.Success,
        >= 50 => NativeScoreTone.Warning,
        _ => NativeScoreTone.Danger
    };
}

/// <summary>
/// Phases: Loading (spinner), Ready (intro when <see cref="ShowIntro"/>, else
/// the active sentence), Summary, Empty ("no sentences" + Go Back), Error
/// (load failure + Go Back), Blocked (owner absent/changed or launch refused).
/// Destination becomes TodaysPlan after Back/Done.
/// </summary>
public sealed record NativeTranslationState(
    NativePhase Phase,
    NativeDestination Destination = NativeDestination.Activity,
    bool ShowIntro = false,
    string IntroResourceTitle = "",
    ImmutableArray<NativeTranslationIntroWord> IntroVocabulary = default,
    ImmutableArray<string> IntroGrammar = default,
    int Index = 0,
    int Count = 0,
    string Prompt = "",
    string TargetLanguage = "",
    bool BlocksMode = false,
    ImmutableArray<string> Blocks = default,
    string Input = "",
    bool IsGrading = false,
    NativeTranslationFeedback? Feedback = null,
    bool DetailsExpanded = false,
    int GradedCount = 0,
    int AverageAccuracy = 0,
    int AverageFluency = 0,
    NativeMessage? Notice = null,
    string? ErrorKey = null,
    bool HasPendingSync = false)
{
    public bool CanGoPrevious => Index > 0 && !IsGrading;
    public bool CanGrade => !IsGrading && !string.IsNullOrWhiteSpace(Input);
    public bool ShowsBlocks => BlocksMode && !Blocks.IsDefaultOrEmpty;
}

/// <summary>
/// AI for Translation, always bound to the launch owner: sentence generation
/// on the source default (Fast) tier and grading on the Reasoning tier with
/// effort "minimal" (TeacherService.GradeTranslation).
/// </summary>
public interface INativeTranslationAi
{
    Task<TranslationResponse?> GenerateAsync(
        string owner, string renderedPrompt, CancellationToken cancellationToken);

    Task<GradeResponse?> GradeAsync(
        string owner, string renderedPrompt, CancellationToken cancellationToken);
}
