using Microsoft.SwiftUI;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features;

public static class NativeFeatureViews
{
    public static SwiftView Dashboard(
        NativeDashboardState state,
        bool busy,
        SwiftView? banner,
        Func<string, string> text,
        Action refresh,
        Action generate,
        Action<string> openQuiz)
    {
        var items = new List<SwiftView>
        {
            Views.Text(text(NativeText.Today))
                .WithAccessibilityIdentifier(NativeAccess.Plan)
        };
        if (banner is not null)
            items.Insert(0, banner);
        if (state.HasPendingSync)
            items.Add(Views.Text($"{text("DebugHealth_CoreSync")}: {text("DebugHealth_Pending")}"));

        switch (state.Phase)
        {
            case NativePhase.Loading:
                items.Add(Views.Text(text("Common_Loading")));
                break;
            case NativePhase.Empty:
                items.Add(Views.Text(text(state.ErrorKey ?? NativeText.NoPlan)));
                if (!busy)
                    items.Add(Views.Button(text("Dashboard_GeneratePlan"), generate)
                        .WithAccessibilityIdentifier("native.dashboard.generate"));
                break;
            case NativePhase.Ready:
                items.Add(Views.Text($"{state.CompletedCount}/{state.Cards.Length}"));
                foreach (var card in state.Cards)
                {
                    var name = card.TitleKey is { } key ? text(key) : card.Title;
                    var status = card.IsCompleted ? text(NativeText.Done) : $"{card.EstimatedMinutes} min";
                    if (card.Activity == PlanActivityType.VocabularyReview && !card.IsCompleted)
                    {
                        items.Add(Views.Button($"{name} - {status}", () => openQuiz(card.Id))
                            .WithAccessibilityIdentifier($"{NativeAccess.PlanCard}.{card.Id}"));
                    }
                    else
                    {
                        items.Add(Views.Text($"{name} - {status}")
                            .WithAccessibilityIdentifier($"{NativeAccess.PlanCard}.{card.Id}"));
                    }
                }
                break;
            default:
                items.Add(Views.Text(text(state.ErrorKey ?? NativeText.PlanError)));
                break;
        }
        if (busy)
            items.Add(Views.Text(text("Common_Loading")));
        else
            items.Add(Views.Button(text(NativeText.Retry), refresh)
                .WithAccessibilityIdentifier("native.dashboard.refresh"));
        return Views.List(items.ToArray());
    }

    public static SwiftView Quiz(
        NativeQuizState state,
        string answer,
        bool busy,
        bool canRetry,
        SwiftView? banner,
        Func<string, string> text,
        Action<string> answerChanged,
        Action<string> selectChoice,
        Action playPrompt,
        Action submitText,
        Action<string> correctionChanged,
        string correction,
        Action submitCorrection,
        Action overrideAsCorrect,
        Action next,
        Action resume,
        Action startFresh,
        Action continueRound,
        Action done,
        Action back,
        Action retry,
        Action details)
    {
        var items = new List<SwiftView>
        {
            Views.Text(text(NativeText.Quiz)),
            Views.Button(text("Common_Back"), back)
                .WithAccessibilityIdentifier("native.quiz.back")
        };
        if (banner is not null)
            items.Insert(0, banner);
        if (state.HasPendingSync)
            items.Add(Views.Text($"{text("DebugHealth_CoreSync")}: {text("DebugHealth_Pending")}"));
        if (busy || state.Phase == NativePhase.Loading)
        {
            items.Add(Views.Text(text("Common_Loading")));
            return Views.List(items.ToArray());
        }
        if (state.ErrorKey is not null)
            items.Add(Views.Text(text(state.ErrorKey))
                .WithAccessibilityIdentifier("native.quiz.error"));

        switch (state.Phase)
        {
            case NativePhase.ResumeChoice:
                items.Add(Views.Button(text(NativeText.Resume), resume)
                    .WithAccessibilityIdentifier(NativeAccess.QuizResume));
                items.Add(Views.Button(text(NativeText.StartFresh), startFresh)
                    .WithAccessibilityIdentifier("native.quiz.start-fresh"));
                break;
            case NativePhase.Ready:
                RenderQuestion(items, state, answer, text, answerChanged,
                    selectChoice, playPrompt, submitText);
                break;
            case NativePhase.Feedback:
                RenderFeedback(items, state, correction, text, correctionChanged,
                    submitCorrection, overrideAsCorrect, next, details);
                break;
            case NativePhase.Summary:
                items.Add(Views.Text(text(NativeText.QuizSummary))
                    .WithAccessibilityIdentifier(NativeAccess.QuizSummary));
                items.Add(Views.Text($"{state.CorrectCount}/{state.TotalTurns}"));
                if (state.TotalTurns > 0)
                    items.Add(Views.Button(text("VocabQuiz_ContinueQuiz"), continueRound)
                        .WithAccessibilityIdentifier("native.quiz.continue"));
                items.Add(Views.Button(text(NativeText.Done), done)
                    .WithAccessibilityIdentifier("native.quiz.done"));
                break;
            case NativePhase.Empty:
            case NativePhase.Error:
            case NativePhase.Blocked:
                if (state.ErrorKey is null)
                    items.Add(Views.Text(text(NativeText.QuizUnavailable)));
                if (state.Destination == NativeDestination.QuizSummary)
                {
                    items.Add(Views.Text(text(NativeText.QuizSummary)));
                    items.Add(Views.Text($"{state.CorrectCount}/{state.TotalTurns}"));
                }
                if (canRetry)
                    items.Add(Views.Button(text(NativeText.Retry), retry)
                        .WithAccessibilityIdentifier("native.quiz.retry"));
                break;
        }
        return Views.List(items.ToArray());
    }

    private static void RenderQuestion(
        List<SwiftView> items,
        NativeQuizState state,
        string answer,
        Func<string, string> text,
        Action<string> answerChanged,
        Action<string> selectChoice,
        Action playPrompt,
        Action submitText)
    {
        items.Add(Views.Text($"{state.CurrentTurn}/{state.TurnsInRound}"));
        if (state.Prompt is { } prompt)
        {
            if (prompt.ShowWrittenPrompt && !string.IsNullOrWhiteSpace(prompt.PromptText))
                items.Add(Views.Text(prompt.PromptText)
                    .WithAccessibilityIdentifier(NativeAccess.QuizQuestion));
            if (prompt.CanPlayPromptAudio)
                items.Add(Views.Button(text("VocabQuiz_ListenAndAnswer"), playPrompt)
                    .WithAccessibilityIdentifier("native.quiz.play-prompt"));
        }
        if (!state.CanSubmit)
            items.Add(Views.Text(text(NativeText.QuizUnavailable)));
        else if (state.Mode == "MultipleChoice")
        {
            for (var index = 0; index < state.Choices.Length; index++)
            {
                var choice = state.Choices[index];
                items.Add(Views.Button(choice.Text, () => selectChoice(choice.Text))
                    .WithAccessibilityIdentifier($"{NativeAccess.QuizChoice}.{index}"));
            }
        }
        else if (state.Mode == "Text")
        {
            items.Add(Views.TextField(text(NativeText.AnswerRequired), answer, answerChanged)
                .WithAccessibilityIdentifier(NativeAccess.QuizAnswer));
            items.Add(Views.Button(text("VocabQuiz_SubmitAnswer"), submitText)
                .WithAccessibilityIdentifier("native.quiz.submit"));
        }
    }

    private static void RenderFeedback(
        List<SwiftView> items,
        NativeQuizState state,
        string correction,
        Func<string, string> text,
        Action<string> correctionChanged,
        Action submitCorrection,
        Action overrideAsCorrect,
        Action next,
        Action details)
    {
        var prompt = state.Prompt;
        if (prompt is { ShowWrittenPrompt: true }
            && !string.IsNullOrWhiteSpace(prompt.PromptText))
            items.Add(Views.Text(prompt.PromptText)
                .WithAccessibilityIdentifier(NativeAccess.QuizQuestion));
        items.Add(Views.Text(text(state.FeedbackKey ?? NativeText.QuizIncorrect))
            .WithAccessibilityIdentifier(NativeAccess.QuizFeedback));
        if (state.CorrectAnswerAfterGrading is { } correct)
            items.Add(Views.Text(correct));
        items.Add(Views.Button(text("VocabQuiz_LearningDetails"), details)
            .WithAccessibilityIdentifier(NativeAccess.QuizDetails));
        if (state.RequiresCorrection)
        {
            items.Add(Views.TextField(text(NativeText.CorrectionRequired), correction, correctionChanged)
                .WithAccessibilityIdentifier($"{NativeAccess.QuizAnswer}.correction"));
            items.Add(Views.Button(text("Common_Submit"), submitCorrection)
                .WithAccessibilityIdentifier("native.quiz.correction"));
            items.Add(Views.Button(text("VocabQuiz_IWasCorrect"), overrideAsCorrect)
                .WithAccessibilityIdentifier("native.quiz.override"));
        }
        else
        {
            items.Add(Views.Button(text(NativeText.QuizNext), next)
                .WithAccessibilityIdentifier("native.quiz.next"));
        }
    }
}
