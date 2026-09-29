using System.Collections.Immutable;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Platform heads translate these destinations into their own navigation APIs.
/// Neither a URL nor a MAUI page is an application destination.
/// </summary>
public enum NativeDestination { SignIn, Onboarding, Dashboard, TodaysPlan, VocabularyQuiz, QuizSummary }

public enum NativePhase { Idle, Loading, Ready, Empty, PendingSync, ResumeChoice, Feedback, Summary, Blocked, Error }

public enum NativeQuizOverlay { None, LearningDetails, FullscreenPhoto }

public static class NativeText
{
    public const string SignIn = "Login_SignIn";
    public const string InvalidCredentials = "Login_InvalidCredentials";
    public const string ConnectionError = "Login_ConnectionError";
    public const string UnexpectedError = "Login_UnexpectedError";
    public const string SignInUnavailable = "Login_NotAvailable";
    public const string Email = "Common_Email";
    public const string Password = "Common_Password";
    public const string RememberMe = "Common_RememberMe";
    public const string ShowPassword = "Login_ShowPassword";
    public const string HidePassword = "Login_HidePassword";
    public const string ShowPasswordShort = "Login_ShowPasswordShort";
    public const string HidePasswordShort = "Login_HidePasswordShort";
    public const string Retry = "Common_Retry";
    public const string SaveFailed = "Common_SaveFailed";
    public const string AnswerRequired = "VocabQuiz_TypeAnswer";
    public const string CorrectionRequired = "VocabQuiz_TypeCorrect";
    public const string Today = "Dashboard_TodaysPlan";
    public const string PlanVocabTitle = "PlanItemVocabReviewTitle";
    public const string PlanVocabDescription = "PlanItemVocabReviewDesc";
    public const string NoPlan = "Dashboard_NoPlanYet";
    public const string PlanError = "Dashboard_UnableToLoadPlan";
    public const string PlanGenerationError = "Dashboard_UnableToGeneratePlan";
    public const string PlanNetworkError = "Dashboard_NetworkErrorTryAgain";
    public const string Regenerate = "Dashboard_RegeneratePlan";
    public const string Quiz = "Activity_VocabQuiz";
    public const string QuizEmpty = "VocabQuiz_NoVocab";
    public const string QuizUnavailable = "VocabQuiz_LaunchUnavailable";
    public const string Resume = "VocabQuiz_Resume";
    public const string StartFresh = "VocabQuiz_StartFresh";
    public const string ResumeUnavailable = "VocabQuiz_ResumeUnavailable";
    public const string QuizSummary = "VocabQuiz_SessionSummary";
    public const string QuizNext = "VocabQuiz_Next";
    public const string QuizCorrect = "Common_Correct";
    public const string QuizIncorrect = "Common_Incorrect";
    public const string QuizOverride = "VocabQuiz_IWasCorrect";
    public const string QuizPhoto = "VocabQuiz_PhotoHint";
    public const string Done = "Common_Done";
}

public static class NativeAccess
{
    public const string Email = "native.auth.email";
    public const string Password = "native.auth.password";
    public const string PasswordVisibility = "native.auth.password.visibility";
    public const string RememberMe = "native.auth.remember";
    public const string SignIn = "native.auth.submit";
    public const string AuthError = "native.auth.error";
    public const string Plan = "native.dashboard.plan";
    public const string PlanCard = "native.plan.card";
    public const string QuizQuestion = "native.quiz.question";
    public const string QuizChoice = "native.quiz.choice";
    public const string QuizAnswer = "native.quiz.answer";
    public const string QuizFeedback = "native.quiz.feedback";
    public const string QuizPhoto = "native.quiz.photo";
    public const string QuizDetails = "native.quiz.details";
    public const string QuizResume = "native.quiz.resume";
    public const string QuizSummary = "native.quiz.summary";
    public const string QuizSummaryResult = "native.quiz.summary.result";
}

public sealed record NativeAuthState(
    NativePhase Phase,
    NativeDestination Destination,
    string? UserId = null,
    string? ErrorKey = null,
    bool HasPendingSync = false);

public sealed record NativePlanCard(
    string Id,
    PlanActivityType Activity,
    string Title,
    string Description,
    int EstimatedMinutes,
    int MinutesSpent,
    bool IsCompleted,
    string? ResourceId,
    string? SkillId,
    ImmutableArray<string> FocusVocabularyIds,
    string AccessibilityId = NativeAccess.PlanCard,
    string? TitleKey = null,
    string? DescriptionKey = null,
    string? ResourceTitle = null,
    int? VocabDueCount = null);

public sealed record NativeDashboardState(
    NativePhase Phase,
    DateOnly? PlanDate = null,
    ImmutableArray<NativePlanCard> Cards = default,
    string? ErrorKey = null,
    bool HasPendingSync = false,
    int CompletedCount = 0,
    int TotalCount = 0,
    int EstimatedTotalMinutes = 0,
    int MinutesSpent = 0,
    double CompletionPercentage = 0,
    int CurrentStreak = 0,
    int LongestStreak = 0,
    PlanNarrativeDto? Narrative = null,
    string? Rationale = null);

/// <summary>Owner/date/item and source launch context travel together; never accept a bare item ID.</summary>
public sealed record NativeQuizLaunch(
    string UserId,
    DateOnly PlanDate,
    string PlanItemId,
    ImmutableArray<string> ResourceIds,
    string? SkillId,
    ImmutableArray<string> FocusVocabularyIds,
    bool DueOnly = false);

public sealed record NativeQuizChoice(string Text, string AccessibilityId = NativeAccess.QuizChoice);

public sealed record NativeQuizResult(
    string WordId,
    string TargetLanguageTerm,
    string NativeLanguageTerm,
    bool WasCorrect);

public sealed record NativeQuizPrompt(
    string WordId,
    string? PromptText,
    string? PromptLanguage,
    string? PromptAudioText,
    string? PhotoUri,
    bool IsTargetLanguagePrompt,
    bool ShowWrittenPrompt,
    bool CanPlayPromptAudio,
    bool ShowPhoto,
    bool TargetLanguageEngaged,
    string PhotoAccessibilityKey = NativeText.QuizPhoto);

public sealed record NativeQuizSentencePrompt(
    string WordId,
    string TargetLanguageTerm,
    string NativeLanguageGloss,
    string TargetLanguage,
    string InstructionKey = "VocabQuiz_WriteOneSentence",
    string AccessibilityId = NativeAccess.QuizAnswer);

public sealed record NativeQuizState(
    NativePhase Phase,
    NativeDestination Destination,
    NativeQuizPrompt? Prompt = null,
    ImmutableArray<NativeQuizChoice> Choices = default,
    string? Mode = null,
    string? FeedbackKey = null,
    string? CorrectAnswerAfterGrading = null,
    string? ErrorKey = null,
    bool RequiresCorrection = false,
    bool CanSubmit = false,
    int CurrentTurn = 0,
    int TurnsInRound = 0,
    int TotalTurns = 0,
    int CorrectCount = 0,
    int RoundsCompleted = 0,
    TimeSpan Elapsed = default,
    bool HasPendingSync = false,
    NativeQuizOverlay Overlay = NativeQuizOverlay.None,
    NativeQuizSentencePrompt? Sentence = null,
    string? TurnId = null,
    string? SelectedAnswer = null,
    bool? AnswerWasCorrect = null,
    int WordsMastered = 0,
    ImmutableArray<NativeQuizResult> Results = default);

/// <summary>The original vocabulary attempt is passed to shared scoring unchanged.</summary>
public sealed record NativeQuizCommitReceipt(string TurnId, VocabularyProgress Progress);
