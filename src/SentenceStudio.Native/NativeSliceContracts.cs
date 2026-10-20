using System.Collections.Immutable;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Platform heads translate these destinations into their own navigation APIs.
/// Neither a URL nor a MAUI page is an application destination.
/// </summary>
public enum NativeDestination
{
    SignIn, Onboarding, Dashboard, TodaysPlan, VocabularyQuiz, QuizSummary,
    Resources, ResourceDetail, ResourceEdit
}

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
    public const string NumberInsightError = "NativeDashboard_NumbersUnavailable";
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
    public const string QuizCompleteForm = "NativeVocabQuiz_FullForm";
    public const string QuizMnemonic = "NativeVocabQuiz_Mnemonic";
    public const string SentenceGradeUnavailable = "NativeVocabQuiz_SentenceGradeUnavailable";
    public const string SentenceTargetUnconfirmed = "NativeVocabQuiz_SentenceTargetUnconfirmed";
    public const string SentenceMeaningful = "NativeVocabQuiz_SentenceMeaningful";
    public const string SentenceUnclear = "NativeVocabQuiz_SentenceUnclear";
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
    public const string ResourcesTab = "native.tab.resources";
    public const string DashboardTab = "native.tab.dashboard";
    public const string ResourceSearch = "native.resources.search";
    public const string ResourceType = "native.resources.filter.type";
    public const string ResourceLanguage = "native.resources.filter.language";
    public const string ResourceRow = "native.resources.row";
    public const string ResourceDetail = "native.resources.detail";
    public const string ResourceTitle = "native.resources.edit.title";
    public const string ResourceDescription = "native.resources.edit.description";
    public const string ResourceTags = "native.resources.edit.tags";
    public const string ResourceSave = "native.resources.edit.save";
    public const string ResourceCancel = "native.resources.edit.cancel";
    public const string ResourceConflict = "native.resources.edit.conflict";
    public const string ResourceConflictAlert = "native.resources.edit.conflict.alert";
    public const string ResourceReload = "native.resources.edit.reload";
}

public sealed record NativeAuthState(
    NativePhase Phase,
    NativeDestination Destination,
    string? UserId = null,
    string? ErrorKey = null,
    bool HasPendingSync = false);

public enum NativeRegistrationOutcome
{
    SignedIn, ConfirmationRequired, Rejected, TransportFailure, Unavailable, Failed
}

public sealed record NativeRegistrationResult(
    NativeRegistrationOutcome Outcome, string? ErrorKey = null,
    NativeAuthState? AuthState = null);

public enum NativePasswordRecoveryOutcome
{
    Accepted, Rejected, TransportFailure, Unavailable
}

public sealed record NativePasswordRecoveryResult(
    NativePasswordRecoveryOutcome Outcome, string? ErrorKey = null);

public sealed record NativeOnboardingDraft(
    string Name, string NativeLanguage, string TargetLanguage,
    int PreferredSessionMinutes = 15, string TargetCefrLevel = "A2");

public enum NativeOnboardingOutcome { Success, Invalid, Blocked, Failed }

public sealed record NativeOnboardingResult(
    NativeOnboardingOutcome Outcome, string? ErrorKey = null,
    int ContentStepsDone = 0);

public static class NativeOnboardingOptions
{
    public static ImmutableArray<string> NativeLanguages { get; } =
        ["English", "Korean", "French", "German", "Spanish", "Japanese", "Chinese"];
    public static ImmutableArray<string> TargetLanguages { get; } =
        ["Korean", "English", "French", "German", "Spanish", "Japanese", "Chinese"];
    public static ImmutableArray<int> SessionMinutes { get; } =
        [5, 10, 15, 20, 30, 45];
    public static ImmutableArray<string> CefrLevels { get; } =
        ["A1", "A2", "B1", "B2", "C1", "C2"];
}

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

public enum NativeDashboardMode { TodaysPlan, ChooseOwn }

public sealed record NativeNumberContextStat(
    string ContextCode, int CorrectCount, int TotalCount,
    int DueCount, int MasteryPercentage);

public sealed record NativeChooseOwnOption(string Id, string Title);

public sealed record NativeChooseOwnActivity(string Route, string LabelKey);

public sealed record NativeChooseOwnState(
    ImmutableArray<NativeChooseOwnOption> Resources,
    ImmutableArray<NativeChooseOwnOption> Skills,
    ImmutableArray<string> SelectedResourceIds,
    string? SelectedSkillId,
    bool ShowAllActivities = false,
    string? ErrorKey = null);

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
    string? Rationale = null,
    VocabProgressSummary? VocabSummary = null)
{
    public NativeDashboardMode Mode { get; init; } = NativeDashboardMode.TodaysPlan;
    public bool IsNewUser { get; init; }
    public ImmutableArray<NativeNumberContextStat> NumberContexts { get; init; }
    public string? NumberErrorKey { get; init; }
    public NativeChooseOwnState? ChooseOwn { get; init; }

    public ImmutableArray<TagInsightDto> StrugglingCategories =>
        Narrative?.VocabInsight?.StrugglingCategories
            .Where(category => category.TotalAttempts > 0
                && category.AverageAccuracy < 0.6f).ToImmutableArray() ?? [];

    public ImmutableArray<TagInsightDto> NewCategories =>
        Narrative?.VocabInsight?.StrugglingCategories
            .Where(category => category.TotalAttempts == 0).ToImmutableArray() ?? [];

    public ImmutableArray<TagInsightDto> DoingWellCategories =>
        Narrative?.VocabInsight?.StrugglingCategories
            .Where(category => category.TotalAttempts > 0
                && category.AverageAccuracy >= 0.6f).ToImmutableArray() ?? [];
}

/// <summary>Owner/date/item and source launch context travel together; never accept a bare item ID.</summary>
public sealed record NativeQuizLaunch(
    string UserId,
    DateOnly PlanDate,
    string PlanItemId,
    ImmutableArray<string> ResourceIds,
    string? SkillId,
    ImmutableArray<string> FocusVocabularyIds,
    bool DueOnly = false)
{
    public bool IsRepractice { get; init; }
}

public sealed record NativeQuizSettingsState(
    string Direction, bool AutoPlayAudio, bool ShowMnemonics,
    bool UseTextPrompt, bool UseAudioPrompt, bool UsePhotoPrompt,
    int AutoAdvanceDurationMs, string VoiceLanguage, string VoiceId)
{
    public ImmutableArray<NativeQuizVoiceOption> VoiceOptions { get; init; } = [];
    public string? ErrorKey { get; init; }
}

public sealed record NativeQuizVoiceOption(string Id, string Name);

public sealed record NativeQuizChoice(string Text, string AccessibilityId = NativeAccess.QuizChoice)
{
    public string? AudioLanguage { get; init; }
}

public sealed record NativeQuizResult(
    string WordId,
    string TargetLanguageTerm,
    string NativeLanguageTerm,
    bool WasCorrect,
    bool ReadyToRotateOut = false);

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
    string PhotoAccessibilityKey = NativeText.QuizPhoto)
{
    public NativeQuizPhotoAsset? Photo { get; init; }
    public bool PhotoRequested { get; init; }
    public bool CanTogglePhotoText { get; init; }
    public bool ShowTextWithPhoto { get; init; }
}

public sealed record NativeQuizPhotoAsset(byte[] Bytes, string ContentType);

public sealed record NativeQuizSentenceHint(int ExampleSentenceId, string TargetSentence);

public sealed record NativeQuizSentencePrompt(
    string WordId,
    string TargetLanguageTerm,
    string NativeLanguageGloss,
    string TargetLanguage,
    string InstructionKey = "VocabQuiz_WriteOneSentence",
    string AccessibilityId = NativeAccess.QuizAnswer);

public sealed record NativeQuizLearningDetails(
    LearningStatus Status,
    int TotalAttempts, int CorrectAttempts, float Accuracy,
    float CurrentStreak, int ProductionInStreak, float EffectiveStreak,
    float MasteryScore, DateTime? NextReviewDate, int ReviewInterval,
    float EaseFactor, bool IsDueForReview, string Mode, bool DueOnly,
    string WhyModeKey, int RecognitionDemonstrations);

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
    ImmutableArray<NativeQuizResult> Results = default,
    string? CompleteForm = null,
    string? FeedbackMnemonicText = null,
    string? FeedbackPhotoCandidateUri = null,
    NativeQuizLearningDetails? Details = null)
{
    public ImmutableArray<NativeQuizSentenceHint> SentenceHints { get; init; } = [];
    public bool CanShowSentenceHints { get; init; }
    public bool SentenceHintsExpanded { get; init; }
    public bool CanPlayAnswerAudio { get; init; }
    public ImmutableArray<int> HeardAnswerChoiceIndices { get; init; } = [];
    public DateTimeOffset? FeedbackStartedAt { get; init; }
    public int AutoAdvanceDurationMs { get; init; }
    public ImmutableArray<NativeQuizSentenceGrade> SentenceResults { get; init; } = [];
    public int SentenceSuccessCount { get; init; }
    public bool IsSentenceSubmitting { get; init; }
    public string? SentenceDraft { get; init; }
}

public sealed record NativeQuizSentenceGrade(
    string Sentence, bool UsageCorrect, string UsageExplanation,
    double? Accuracy, double? Fluency, string AccuracyExplanation,
    string FluencyExplanation, string RecommendedSentence, string GrammarNotes,
    bool GradeUnavailable = false)
{
    public string? TargetUsedForm { get; init; }
    public bool HasVerifiedTargetUse { get; init; }
}

public sealed record NativeQuizSentenceCommitReceipt(
    string SubmissionId, int SuccessCount,
    ImmutableArray<NativeQuizSentenceGrade> Results,
    VocabularyProgress? Progress);

/// <summary>The original vocabulary attempt is passed to shared scoring unchanged.</summary>
public sealed record NativeQuizCommitReceipt(string TurnId, VocabularyProgress Progress);
