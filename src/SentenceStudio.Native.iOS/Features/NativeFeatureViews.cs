using System.Globalization;
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
        bool insightExpanded,
        Action toggleInsight)
    {
        var items = new List<SwiftView>();
        if (banner is not null)
            items.Add(banner);
        items.Add(Views.Button(text("Common_Refresh"), refresh)
            .WithAccessibilityIdentifier("native.dashboard.refresh"));
        items.Add(Views.Text(text(NativeText.Today))
            .WithAccessibilityIdentifier("native.dashboard.mode.today"));
        if (state.HasPendingSync)
            items.Add(Views.Text($"{text("DebugHealth_CoreSync")}: {text("DebugHealth_Pending")}")
                .WithAccessibilityIdentifier("native.plan.pending"));

        switch (state.Phase)
        {
            case NativePhase.Loading:
                items.Add(Views.Text(text("Common_Loading"))
                    .WithAccessibilityIdentifier("native.dashboard.status"));
                break;
            case NativePhase.Empty:
                var empty = new List<SwiftView>
                {
                    Views.Text(text(state.ErrorKey ?? NativeText.NoPlan))
                        .WithAccessibilityIdentifier("native.dashboard.status"),
                };
                if (!busy)
                    empty.Add(Views.Button(text("Dashboard_GeneratePlan"), generate)
                        .WithAccessibilityIdentifier("native.dashboard.generate"));
                items.Add(Views.VStack(empty.ToArray())
                    .WithAccessibilityIdentifier("native.plan.empty"));
                break;
            case NativePhase.Ready:
                if (state.CurrentStreak > 0)
                {
                    var streak = string.Format(CultureInfo.CurrentCulture,
                        text("Dashboard_DayStreak"), state.CurrentStreak);
                    if (state.LongestStreak > state.CurrentStreak)
                        streak += "  " + string.Format(CultureInfo.CurrentCulture,
                            text("Dashboard_BestStreak"), state.LongestStreak);
                    items.Add(Views.Text(streak)
                        .WithAccessibilityIdentifier("native.plan.streak"));
                }
                items.Add(Views.VStack(
                    Views.HStack(
                        Views.Text(text("Dashboard_TodaysProgress"))
                            .WithAccessibilityIdentifier("native.plan.progress.title"),
                        Views.Text(string.Format(CultureInfo.CurrentCulture,
                            text("Dashboard_ProgressCounts"),
                            state.CompletedCount, state.TotalCount,
                            state.MinutesSpent, state.EstimatedTotalMinutes))
                            .WithAccessibilityIdentifier("native.plan.progress.count"))
                        .WithAccessibilityIdentifier("native.plan.progress.row"))
                    .WithAccessibilityIdentifier("native.plan.progress"));
                if (state.Narrative is { } narrative)
                {
                    var insight = new List<SwiftView>
                    {
                        Views.Button(text("Dashboard_TodaysInsight"), toggleInsight)
                            .WithAccessibilityIdentifier("native.plan.insight.toggle")
                    };
                    if (insightExpanded)
                    {
                        if (!string.IsNullOrWhiteSpace(narrative.Story))
                            insight.Add(Views.Text(narrative.Story)
                                .WithAccessibilityIdentifier("native.plan.insight.story"));
                        foreach (var resource in narrative.Resources)
                        {
                            if (string.IsNullOrWhiteSpace(resource.Title))
                                continue;
                            insight.Add(Views.Text(resource.Title)
                                .WithAccessibilityIdentifier("native.plan.insight.resource.title"));
                            if (!string.IsNullOrWhiteSpace(resource.SelectionReason))
                                insight.Add(Views.Text(resource.SelectionReason)
                                    .WithAccessibilityIdentifier(
                                        "native.plan.insight.resource.reason"));
                        }
                        if (narrative.VocabInsight is { } vocab)
                        {
                            insight.Add(Views.Text(string.Join(" · ",
                            [
                                $"{vocab.NewCount} {text("Dashboard_InsightNew")}",
                                $"{vocab.ReviewCount} {text("Dashboard_InsightReview")}",
                                $"{vocab.TotalDue} {text("Dashboard_InsightTotalDue")}",
                            ])).WithAccessibilityIdentifier("native.plan.insight.vocab"));
                            if (vocab.AverageMastery > 0)
                                insight.Add(Views.Text(string.Format(CultureInfo.CurrentCulture,
                                    text("Dashboard_InsightMastery"),
                                    (vocab.AverageMastery * 100).ToString("F0",
                                        CultureInfo.CurrentCulture))));
                            var struggling = vocab.StrugglingCategories
                                .Where(tag => tag.TotalAttempts > 0
                                    && tag.AverageAccuracy < 0.6f)
                                .Select(tag => $"{tag.Tag} ({tag.AverageAccuracy:P0})").ToArray();
                            if (struggling.Length > 0)
                                insight.Add(Views.Text($"{text("Dashboard_StrugglingWith")} " +
                                    string.Join(", ", struggling)));
                            if (!string.IsNullOrWhiteSpace(vocab.PatternInsight))
                                insight.Add(Views.Text(vocab.PatternInsight)
                                    .WithAccessibilityIdentifier("native.plan.insight.pattern"));
                            if (vocab.SampleStrugglingWords.Count > 0)
                                insight.Add(Views.Text(string.Format(CultureInfo.CurrentCulture,
                                    text(struggling.Length > 0
                                        ? "Dashboard_WordsToReview" : "Dashboard_WordsToDiscover"),
                                    string.Join(", ", vocab.SampleStrugglingWords))));
                        }
                        if (narrative.FocusAreas.Count > 0)
                        {
                            insight.Add(Views.Text(text("Dashboard_FocusAreas"))
                                .WithAccessibilityIdentifier("native.plan.insight.focus.title"));
                            foreach (var area in narrative.FocusAreas)
                                insight.Add(Views.Text($"\u2022 {area}"));
                        }
                    }
                    items.Add(Views.VStack(insight.ToArray())
                        .WithAccessibilityIdentifier("native.plan.insight"));
                }
                else if (!string.IsNullOrWhiteSpace(state.Rationale))
                {
                    var rationale = new List<SwiftView>();
                    var names = state.Cards.Select(card => card.ResourceTitle)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.Ordinal).ToArray();
                    if (names.Length > 0)
                        rationale.Add(Views.Text(string.Join(", ", names)));
                    rationale.Add(Views.Text(state.Rationale));
                    items.Add(Views.VStack(rationale.ToArray())
                        .WithAccessibilityIdentifier("native.plan.rationale"));
                }

                foreach (var card in state.Cards)
                {
                    var name = card.TitleKey is { } key ? text(key) : card.Title;
                    var minutes = card.MinutesSpent > 0
                        ? string.Format(CultureInfo.CurrentCulture,
                            text("Dashboard_SpentOfEstimate"),
                            card.MinutesSpent, card.EstimatedMinutes)
                        : string.Format(CultureInfo.CurrentCulture,
                            text("Dashboard_EstimateMinutes"), card.EstimatedMinutes);
                    var metadata = card.ResourceTitle is { Length: > 0 }
                        ? $"{card.ResourceTitle} \u00b7 {minutes}" : minutes;
                    if (card.VocabDueCount > 0)
                        metadata += " \u00b7 " + string.Format(CultureInfo.CurrentCulture,
                            text("Dashboard_VocabDueCount"), card.VocabDueCount.Value);
                    var cardContent = new List<SwiftView>
                    {
                        Views.Text(name).WithAccessibilityIdentifier("native.plan.card.title"),
                    };
                    cardContent.Add(Views.Text(metadata)
                        .WithAccessibilityIdentifier("native.plan.card.metadata"));
                    if (card.IsCompleted)
                        cardContent.Add(Views.Text(text("PlanSummary_Completed"))
                            .WithAccessibilityIdentifier("native.plan.card.completed"));
                    items.Add(Views.VStack(cardContent.ToArray())
                        .WithAccessibilityIdentifier($"native.plan.card.container.{card.Id}"));
                }
                break;
            default:
                items.Add(Views.Text(text(state.ErrorKey ?? NativeText.PlanError))
                    .WithAccessibilityIdentifier("native.dashboard.status"));
                break;
        }
        if (state.VocabSummary is { } vocabulary)
            AddVocabularySummary(items, vocabulary, text);
        if (busy)
            items.Add(Views.Text(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.dashboard.busy"));
        return Views.Scroll(items.ToArray());
    }

    private static void AddVocabularySummary(
        List<SwiftView> items, VocabProgressSummary summary, Func<string, string> text)
    {
        items.Add(Views.Text(text("Dashboard_VocabularyHeader"))
            .WithAccessibilityIdentifier("native.dashboard.vocab.title"));
        items.Add(Views.HStack(
            VocabularyTile(summary.New, text("Dashboard_VocabNew"), "new"),
            VocabularyTile(summary.Learning, text("Dashboard_VocabLearning"), "learning")));
        items.Add(Views.HStack(
            VocabularyTile(summary.Familiar, text("Dashboard_VocabFamiliar"), "familiar"),
            VocabularyTile(summary.Review, text("Dashboard_VocabReview"), "review")));
        items.Add(Views.HStack(
            VocabularyTile(summary.Known, text("Dashboard_VocabKnown"), "known"),
            Views.Text(string.Empty)));

        var total = summary.New + summary.Learning + summary.Familiar
            + summary.Review + summary.Known;
        if (total > 0)
            items.Add(Views.HStack(
                Views.Text($"{text("Dashboard_TotalWordsLabel")} " +
                    total.ToString(CultureInfo.CurrentCulture))
                    .WithAccessibilityIdentifier("native.dashboard.vocab.total"),
                Views.Text($"{text("Dashboard_WeeklyAccuracyLabel")} " +
                    $"{Math.Round(summary.SuccessRate7d * 100).ToString(CultureInfo.CurrentCulture)}%")
                    .WithAccessibilityIdentifier("native.dashboard.vocab.accuracy"))
                .WithAccessibilityIdentifier("native.dashboard.vocab.summary"));
    }

    private static SwiftView VocabularyTile(int count, string label, string name) =>
        Views.VStack(
            Views.Text(count.ToString(CultureInfo.CurrentCulture))
                .WithAccessibilityIdentifier($"native.dashboard.vocab.stat.{name}.value"),
            Views.Text(label)
                .WithAccessibilityIdentifier($"native.dashboard.vocab.stat.{name}.label"))
        .WithAccessibilityIdentifier($"native.dashboard.vocab.stat.{name}");

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
        Action retry)
    {
        var items = new List<SwiftView>();
        if (banner is not null)
            items.Insert(0, banner);
        if (state.HasPendingSync)
            items.Add(Views.Text($"{text("DebugHealth_CoreSync")}: {text("DebugHealth_Pending")}")
                .WithAccessibilityIdentifier("native.quiz.pending"));
        if (state.Phase == NativePhase.Loading
            || busy && state.Mode != "MultipleChoice")
        {
            items.Add(Views.Text(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.quiz.status"));
            return Views.Scroll(items.ToArray());
        }
        if (state.ErrorKey is not null)
            items.Add(Views.Text(text(state.ErrorKey))
                .WithAccessibilityIdentifier("native.quiz.error"));

        switch (state.Phase)
        {
            case NativePhase.ResumeChoice:
                var resumeContent = new List<SwiftView>
                {
                    Views.Text(text("VocabQuiz_ResumePrompt")),
                };
                if (state.ErrorKey is null)
                    resumeContent.Add(Views.Button(text(NativeText.Resume), resume)
                        .WithAccessibilityIdentifier("native.quiz.resume.continue"));
                resumeContent.Add(Views.Button(text(NativeText.StartFresh), startFresh)
                    .WithAccessibilityIdentifier("native.quiz.start-fresh"));
                items.Add(Views.VStack(resumeContent.ToArray())
                    .WithAccessibilityIdentifier(NativeAccess.QuizResume));
                break;
            case NativePhase.Ready:
            case NativePhase.Feedback:
                if (state.Prompt?.ShowPhoto == true)
                {
                    items.Add(Views.Text(text(NativeText.QuizUnavailable))
                        .WithAccessibilityIdentifier("native.quiz.status"));
                    break;
                }
                RenderQuestion(items, state, answer, text, answerChanged,
                    selectChoice, playPrompt, submitText);
                if (state.Phase == NativePhase.Feedback)
                    RenderFeedback(items, state, correction, text, correctionChanged,
                        submitCorrection, overrideAsCorrect, next);
                break;
            case NativePhase.Summary:
            case NativePhase.Error when state.Destination == NativeDestination.QuizSummary:
                items.Add(Views.VStack(
                    Views.Text(text(NativeText.QuizSummary))
                        .WithAccessibilityIdentifier("native.quiz.summary.title"),
                    Views.HStack(
                        Statistic(state.CorrectCount, text(NativeText.QuizCorrect), "correct"),
                        Statistic(state.TotalTurns, text("VocabQuiz_Total"), "total"),
                        Statistic(state.WordsMastered, text("VocabQuiz_Mastered"), "mastered"),
                        Statistic(state.RoundsCompleted, text("VocabQuiz_Rounds"), "rounds")))
                    .WithAccessibilityIdentifier(NativeAccess.QuizSummary));
                var results = state.Results.IsDefault ? [] : state.Results;
                for (var index = 0; index < results.Length; index++)
                {
                    var result = results[index];
                    var id = $"{NativeAccess.QuizSummaryResult}.{index}";
                    items.Add(Views.VStack(
                        Views.Text(result.TargetLanguageTerm)
                            .WithAccessibilityIdentifier($"{id}.word"),
                        Views.Text(result.NativeLanguageTerm)
                            .WithAccessibilityIdentifier($"{id}.meaning"),
                        Views.Text(text(result.WasCorrect
                                ? NativeText.QuizCorrect : NativeText.QuizIncorrect))
                            .WithAccessibilityIdentifier($"{id}.status"))
                        .WithAccessibilityIdentifier(id));
                }
                if (state.Phase == NativePhase.Summary)
                {
                    if (state.TotalTurns > 0)
                        items.Add(Views.Button(text("VocabQuiz_ContinueQuiz"), continueRound)
                            .WithAccessibilityIdentifier("native.quiz.continue"));
                    items.Add(Views.Button(text(NativeText.Done), done)
                        .WithAccessibilityIdentifier("native.quiz.done"));
                }
                else if (canRetry)
                    items.Add(Views.Button(text(NativeText.Retry), retry)
                        .WithAccessibilityIdentifier("native.quiz.retry"));
                break;
            case NativePhase.Empty:
            case NativePhase.Error:
            case NativePhase.Blocked:
                if (state.ErrorKey is null)
                    items.Add(Views.Text(text(NativeText.QuizUnavailable)));
                if (state.Destination == NativeDestination.QuizSummary)
                {
                    items.Add(Views.VStack(
                        Views.Text(text(NativeText.QuizSummary)),
                        Views.Text($"{state.CorrectCount}/{state.TotalTurns}"))
                        .WithAccessibilityIdentifier(NativeAccess.QuizSummary));
                }
                if (canRetry)
                    items.Add(Views.Button(text(NativeText.Retry), retry)
                        .WithAccessibilityIdentifier("native.quiz.retry"));
                break;
        }
        return Views.Scroll(items.ToArray());
    }

    private static SwiftView Statistic(int count, string label, string name) =>
        Views.VStack(
            Views.Text(count.ToString(CultureInfo.CurrentCulture))
                .WithAccessibilityIdentifier($"native.quiz.summary.stat.{name}.value"),
            Views.Text(label)
                .WithAccessibilityIdentifier($"native.quiz.summary.stat.{name}.label"));

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
        if (state.Prompt is { } prompt)
        {
            if (prompt.ShowWrittenPrompt && !string.IsNullOrWhiteSpace(prompt.PromptText))
                items.Add(Views.Text(prompt.PromptText)
                    .WithAccessibilityIdentifier(NativeAccess.QuizQuestion));
            else
                items.Add(Views.Text(text("VocabQuiz_ListenAndAnswer"))
                    .WithAccessibilityIdentifier(NativeAccess.QuizQuestion));
            if (prompt.CanPlayPromptAudio)
                items.Add(Views.Button(text("VocabQuiz_ListenAndAnswer"), playPrompt)
                    .WithAccessibilityIdentifier("native.quiz.play-prompt"));
        }
        if (!state.CanSubmit && state.Phase != NativePhase.Feedback)
            items.Add(Views.Text(text(NativeText.QuizUnavailable)));
        else if (state.Mode == "MultipleChoice")
        {
            for (var index = 0; index < state.Choices.Length; index++)
            {
                var choice = state.Choices[index];
                var id = $"{NativeAccess.QuizChoice}.{index}";
                items.Add(Views.Button(choice.Text, () => selectChoice(choice.Text))
                    .WithAccessibilityIdentifier(id));
            }
        }
        else if (state.Mode == "Text")
        {
            var displayedAnswer = state.Phase == NativePhase.Feedback
                ? state.SelectedAnswer ?? throw new InvalidOperationException(
                    "A graded text answer must retain the submitted response.")
                : answer;
            items.Add(Views.Text(text("VocabQuiz_TypeTranslation")));
            items.Add(Views.TextField(text(NativeText.AnswerRequired), displayedAnswer, answerChanged)
                .WithAccessibilityIdentifier(NativeAccess.QuizAnswer));
            if (state.Phase == NativePhase.Ready)
                items.Add(Views.Button(text("VocabQuiz_SubmitAnswer"), submitText)
                    .WithAccessibilityIdentifier("native.quiz.submit"));
        }
        if (state.Phase == NativePhase.Feedback)
        {
            if (state.CorrectAnswerAfterGrading is { } correct)
                items.Add(Views.Text(correct)
                    .WithAccessibilityIdentifier("native.quiz.correct-answer"));
            items.Add(Views.Text(text(state.FeedbackKey ?? NativeText.QuizIncorrect))
                .WithAccessibilityIdentifier(NativeAccess.QuizFeedback));
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
        Action next)
    {
        if (state.RequiresCorrection)
        {
            items.Add(Views.TextField(text(NativeText.CorrectionRequired), correction, correctionChanged)
                .WithAccessibilityIdentifier($"{NativeAccess.QuizAnswer}.correction"));
            items.Add(Views.Button(text("Common_Submit"), submitCorrection)
                .WithAccessibilityIdentifier("native.quiz.correction"));
            items.Add(Views.Button(text(NativeText.QuizOverride), overrideAsCorrect)
                .WithAccessibilityIdentifier("native.quiz.override"));
        }
        else
        {
            items.Add(Views.Button(text(NativeText.QuizNext), next)
                .WithAccessibilityIdentifier("native.quiz.next"));
        }
    }
}
