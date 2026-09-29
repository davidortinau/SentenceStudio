using Android.Content.Res;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Text.Input;
using SentenceStudio.Common;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;
using ComposeButton = AndroidX.Compose.Button;

namespace SentenceStudio.Native.Android;

internal sealed partial class AndroidSliceShell
{
    public ComposableNode Build(IComposer composer)
    {
        _ = _revision.Value;
        var dark = (activity.Resources?.Configuration?.UiMode & UiMode.NightMask)
            == UiMode.NightYes;
        var content = new Column
        {
            Modifier.FillMaxSize().SystemBarsPadding().ImePadding().Padding(20)
                .Semantics(semantics => semantics.TestTagsAsResourceId(true)),
#if DEBUG
            new Text(_preview ? "DEBUG PREVIEW - isolated sample; not signed in." : string.Empty)
            {
                Modifier = Modifier.TestTag("native.debug.preview.banner"),
            },
#endif
            Destination switch
            {
                NativeDestination.Dashboard or NativeDestination.TodaysPlan => RenderDashboard(),
                NativeDestination.VocabularyQuiz or NativeDestination.QuizSummary => RenderQuiz(),
                NativeDestination.Onboarding => RenderOnboardingNotice(),
                _ => RenderSignIn(),
            },
            new Text(Error.Value)
            {
                Modifier = Modifier.TestTag("native.shell.error"),
            },
        };
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxSize(),
        };
        surface.Add(content);
        var theme = new MaterialTheme { surface };
        theme.Dark = dark;
        theme.UseDynamicColor = true;
        return theme;
    }

    private ComposableNode RenderSignIn()
    {
        var state = Auth;
        var keyboard = KeyboardOptionsCompanion.Default;
        var emailKeyboard = keyboard.Copy(
            keyboard.Capitalization, keyboard.AutoCorrectEnabled,
            AndroidX.Compose.KeyboardType.Email,
            AndroidX.Compose.ImeAction.Next, keyboard.PlatformImeOptions,
            keyboard.ShowKeyboardOnFocus, keyboard.HintLocales);
        var passwordKeyboard = keyboard.Copy(
            keyboard.Capitalization, keyboard.AutoCorrectEnabled,
            AndroidX.Compose.KeyboardType.Password,
            AndroidX.Compose.ImeAction.Done, keyboard.PlatformImeOptions,
            keyboard.ShowKeyboardOnFocus, keyboard.HintLocales);

        return new Column
        {
            Modifier.FillMaxWidth().TestTag("native.auth.screen"),
            new Text("SentenceStudio"),
            new Text(Localize(NativeText.SignIn)),
            new OutlinedTextField(Email, singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth().TestTag(NativeAccess.Email)
                    .Semantics(semantics => semantics.ContentDescription(Localize(NativeText.Email))),
                Label = new Text(Localize(NativeText.Email)),
                KeyboardOptions = emailKeyboard,
            },
            new OutlinedTextField(Password, singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth().TestTag(NativeAccess.Password)
                    .Semantics(semantics => semantics.ContentDescription(Localize(NativeText.Password))),
                Label = new Text(Localize(NativeText.Password)),
                VisualTransformation = new PasswordVisualTransformation('\u2022'),
                KeyboardOptions = passwordKeyboard,
                KeyboardActions = KeyboardActionsHelper.Create(
                    onDone: () =>
                    {
                        if (_auth is not null && !Busy.Value)
                            SignIn();
                    }),
            },
            Action(Localize(NativeText.SignIn), NativeAccess.SignIn,
                SignIn, !Busy.Value && state.Phase != NativePhase.Loading
                    && _auth is not null),
#if DEBUG
            Action("Debug preview (isolated sample, no API)", "native.debug.preview",
                OpenDebugPreview, !Busy.Value),
#endif
            state.Phase is NativePhase.PendingSync or NativePhase.Error or NativePhase.Blocked
                ? new Text(Localize(state.ErrorKey ?? NativeText.UnexpectedError))
                {
                    Modifier = Modifier.TestTag(NativeAccess.AuthError),
                }
                : new Text(state.Phase == NativePhase.Loading ? "Restoring session..." : string.Empty),
            state.Phase == NativePhase.PendingSync
                ? Action(Localize(NativeText.Retry), "native.auth.retry", RetryAuth, !Busy.Value)
                : new Text(string.Empty),
        };
    }

    private ComposableNode RenderOnboardingNotice() =>
        new Column
        {
            Modifier.FillMaxWidth().TestTag("native.auth.onboarding"),
            new Text("Complete your account setup in the WebApp before using the native app."),
            Action(Localize("Nav_Logout"), "native.auth.signout", SignOut, !Busy.Value),
        };

    private ComposableNode RenderDashboard()
    {
        var state = Dashboard;
        var cards = state.Cards.IsDefault ? [] : state.Cards;
        var exitLabel = Localize("Nav_Logout");
#if DEBUG
        if (_preview)
            exitLabel = "Exit Debug preview";
#endif
        return new Column
        {
            Modifier.FillMaxSize().TestTag("native.dashboard.screen"),
            new Text(Localize(NativeText.Today)),
            new Text(state.PlanDate?.ToString("D") ?? string.Empty),
            new Text(state.Phase switch
            {
                NativePhase.Loading => "Loading today's plan...",
                NativePhase.Empty => Localize(state.ErrorKey ?? NativeText.NoPlan),
                NativePhase.Error or NativePhase.Blocked =>
                    Localize(state.ErrorKey ?? NativeText.PlanError),
                _ => string.Empty,
            })
            {
                Modifier = Modifier.TestTag("native.dashboard.status"),
            },
            new Text(state.HasPendingSync ? "Local changes have not synced yet." : string.Empty),
            new LazyColumn<NativePlanCard>(cards, RenderPlanCard)
            {
                Modifier = Modifier.FillMaxWidth().Weight(1f).TestTag(NativeAccess.Plan),
                Key = card => card.Id,
            },
            state.Phase == NativePhase.Empty
                ? Action(Localize("Dashboard_GeneratePlan"), "native.dashboard.generate",
                    GeneratePlan, !Busy.Value)
                : Action(Localize(state.Phase is NativePhase.Error or NativePhase.Blocked
                        ? NativeText.Retry : "Common_Refresh"),
                    "native.dashboard.refresh",
                    RefreshPlan, !Busy.Value),
            Action(exitLabel, "native.auth.signout", SignOut, !Busy.Value),
        };
    }

    private ComposableNode RenderPlanCard(NativePlanCard card)
    {
        var title = card.TitleKey is null ? card.Title : Localize(card.TitleKey);
        var details = card.DescriptionKey is null
            ? card.Description : Localize(card.DescriptionKey);
        var progress = card.IsCompleted
            ? Localize("PlanSummary_Completed")
            : $"{card.MinutesSpent} / {card.EstimatedMinutes} min";
        if (card.Activity == PlanActivityType.VocabularyReview && !card.IsCompleted)
        {
            return Action(
                $"{title}\n{details}\n{progress}",
                $"{card.AccessibilityId}.{card.Id}",
                () => OpenQuiz(card.Id),
                !Busy.Value);
        }
        return new Column
        {
            Modifier.FillMaxWidth().Padding(12).TestTag($"{card.AccessibilityId}.{card.Id}")
                .Semantics(semantics => semantics.ContentDescription(
                    $"{title}. {details}. {progress}.")),
            new Text(title),
            new Text(details),
            new Text(progress),
        };
    }

    private ComposableNode RenderQuiz()
    {
        var state = Quiz;
        if (state.Overlay != NativeQuizOverlay.None)
            return RenderOverlay(state);
        if (state.Phase == NativePhase.ResumeChoice)
            return new Column
            {
                Modifier.FillMaxWidth().TestTag(NativeAccess.QuizResume),
                new Text(Localize(NativeText.Quiz)),
                new Text(state.ErrorKey is null
                    ? "Continue your saved quiz or start again?"
                    : Localize(state.ErrorKey)),
                Action(Localize(NativeText.Resume), "native.quiz.resume.continue",
                    ResumeQuiz, !Busy.Value && state.ErrorKey is null),
                Action(Localize(NativeText.StartFresh), "native.quiz.resume.fresh",
                    StartFreshQuiz, !Busy.Value),
                Action(Localize("Common_Back"), "native.quiz.back", HandleBackAction, !Busy.Value),
            };

        if (state.Destination == NativeDestination.QuizSummary
            || state.Phase == NativePhase.Summary)
            return new Column
            {
                Modifier.FillMaxWidth().TestTag(NativeAccess.QuizSummary),
                new Text(Localize(NativeText.QuizSummary)),
                new Text($"{state.CorrectCount} correct of {state.TotalTurns} answers"),
                new Text($"{state.RoundsCompleted} rounds, {state.Elapsed.Minutes} min elapsed"),
                new Text(state.ErrorKey is null ? string.Empty : Localize(state.ErrorKey)),
                Action("Continue quiz", "native.quiz.summary.continue",
                    ContinueRound, !Busy.Value && state.Phase == NativePhase.Summary),
                Action(Localize(NativeText.Done), "native.quiz.summary.done",
                    DoneQuiz, !Busy.Value),
                Action(Localize("Common_Back"), "native.quiz.back", HandleBackAction, !Busy.Value),
            };

        var prompt = state.Prompt;
        if (prompt is null)
            return new Column
            {
                Modifier.FillMaxWidth().TestTag("native.quiz.status"),
                new Text(Localize(NativeText.Quiz)),
                new Text(state.Phase == NativePhase.Loading
                    ? "Loading question..."
                    : Localize(state.ErrorKey ?? NativeText.QuizUnavailable)),
                Action(Localize(NativeText.Retry), "native.quiz.retry",
                    RetryQuizSave, !Busy.Value && state.Phase == NativePhase.Error),
                Action(Localize("Common_Back"), "native.quiz.back", HandleBackAction, !Busy.Value),
            };

        return RenderQuestion(state, prompt);
    }

    private ComposableNode RenderQuestion(NativeQuizState state, NativeQuizPrompt prompt)
    {
        var choices = state.Choices.IsDefault ? [] : state.Choices;
        var isQuestion = state.Phase == NativePhase.Ready;
        return new Column
        {
            Modifier.FillMaxSize().TestTag("native.quiz.screen"),
            new Text($"{Localize(NativeText.Quiz)} - {state.CurrentTurn} / {state.TurnsInRound}"),
            new Text(prompt.ShowWrittenPrompt
                ? prompt.PromptText ?? string.Empty
                : "Listen to the prompt")
            {
                Modifier = Modifier.TestTag(NativeAccess.QuizQuestion)
                    .Semantics(semantics => semantics.ContentDescription(
                        prompt.ShowWrittenPrompt
                            ? $"Question: {prompt.PromptText}"
                            : "Listen to the question")),
            },
            prompt.CanPlayPromptAudio
                ? Action("Play prompt", "native.quiz.audio", PlayPrompt, isQuestion && !Busy.Value)
                : new Text(string.Empty),
            prompt.ShowPhoto
                ? Action("Photo", NativeAccess.QuizPhoto, OpenPhoto, !Busy.Value)
                : new Text(string.Empty),
            choices.Length > 0
                ? new LazyColumn<NativeQuizChoice>(choices, choice =>
                    Action(choice.Text,
                        $"{NativeAccess.QuizChoice}.{choices.IndexOf(choice)}",
                        () => AnswerQuiz(choice.Text),
                        isQuestion && state.CanSubmit && !Busy.Value))
                {
                    Modifier = Modifier.FillMaxWidth().Weight(1f)
                        .TestTag("native.quiz.choices"),
                    Key = choice => choice.Text,
                }
                : new Text(string.Empty),
            state.Mode == "Text" && isQuestion
                ? new OutlinedTextField(Answer, singleLine: true)
                {
                    Modifier = Modifier.FillMaxWidth().TestTag(NativeAccess.QuizAnswer)
                        .Semantics(semantics => semantics.ContentDescription(
                            "Type your answer")),
                    Label = new Text(Localize("Answer")),
                }
                : new Text(string.Empty),
            state.Mode == "Text" && isQuestion
                ? Action(Localize("CheckAnswer"), "native.quiz.answer.submit",
                    SubmitTextAnswer, state.CanSubmit && !Busy.Value
                        && !string.IsNullOrWhiteSpace(Answer.Value))
                : new Text(string.Empty),
            state.Phase is NativePhase.Feedback or NativePhase.Error
                ? new Text(state.FeedbackKey is null
                    ? Localize(state.ErrorKey ?? NativeText.SaveFailed)
                    : $"{Localize(state.FeedbackKey)}. {state.CorrectAnswerAfterGrading}")
                {
                    Modifier = Modifier.TestTag(NativeAccess.QuizFeedback),
                }
                : new Text(string.Empty),
            new Text(state.FeedbackKey is not null && state.ErrorKey is not null
                ? Localize(state.ErrorKey) : string.Empty)
            {
                Modifier = Modifier.TestTag("native.quiz.error"),
            },
            state.RequiresCorrection
                ? new OutlinedTextField(Correction, singleLine: true)
                {
                    Modifier = Modifier.FillMaxWidth().TestTag("native.quiz.correction"),
                    Label = new Text(Localize(NativeText.CorrectionRequired)),
                }
                : new Text(string.Empty),
            state.RequiresCorrection
                ? Action(Localize("VocabQuiz_SubmitAnswer"), "native.quiz.correction.submit",
                    SubmitCorrection, !Busy.Value && !string.IsNullOrWhiteSpace(Correction.Value))
                : new Text(string.Empty),
            state.Phase == NativePhase.Feedback && !state.RequiresCorrection
                ? Action(Localize(NativeText.QuizNext), "native.quiz.next",
                    NextQuestion, !Busy.Value)
                : new Text(string.Empty),
            state.Phase == NativePhase.Error
                ? Action(Localize(NativeText.Retry), "native.quiz.retry",
                    RetryQuizSave, !Busy.Value)
                : new Text(string.Empty),
            Action(Localize("Common_Back"), "native.quiz.back", HandleBackAction, !Busy.Value),
        };
    }

    private ComposableNode RenderOverlay(NativeQuizState state) =>
        new Column
        {
            Modifier.FillMaxSize().TestTag(state.Overlay == NativeQuizOverlay.FullscreenPhoto
                ? NativeAccess.QuizPhoto : NativeAccess.QuizDetails),
            new Text(state.Overlay == NativeQuizOverlay.FullscreenPhoto
                ? "Photo viewer" : "Learning details"),
            new Text("This detail is not available from the current shared quiz state."),
            Action("Close", "native.quiz.overlay.close", DismissOverlay, !Busy.Value),
        };

    private void HandleBackAction() => HandleBack();

    private static ComposableNode Action(
        string label, string automationId, Action command, bool enabled)
    {
        var button = new ComposeButton(onClick: command, enabled: enabled)
        {
            Modifier.FillMaxWidth().TestTag(automationId)
                .Semantics(semantics => semantics.ContentDescription(label)),
            new Text(label),
        };
        return button;
    }

    private static string Localize(string key) =>
        LocalizationManager.Instance.GetString(key);
}
