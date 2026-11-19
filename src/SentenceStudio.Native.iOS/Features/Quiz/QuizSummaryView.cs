using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Quiz;

internal static class QuizSummaryView
{
    /// <summary>The text-scaled width from which Continue and Done share one row.</summary>
    private const double SideBySideActionsWidth = 568;

    /// <summary>
    /// Adds the summary to <paramref name="items"/> and returns the footer that holds
    /// Continue and Done, or null when there is none.
    /// </summary>
    internal static SwiftView? Add(
        List<SwiftView> items, NativeQuizState state, bool canRetry,
        SourceUiStyle style, Func<string, string> text,
        Action continueRound, Action done, Action retry)
    {
        items.Add(Views.HStack(
                Views.Image("book.closed"),
                style.Text(Views.Text(text(NativeText.QuizSummary)),
                        SourceTextRole.Title1, sourceHeading: true)
                    .WithAccessibilityIdentifier("native.quiz.summary.title"))
            .WithFlexibleWidth(500, SwiftHorizontalAlignment.Center));
        var summary = new List<SwiftView>
        {
            Views.HStack(
                Statistic(state.CorrectCount, text(NativeText.QuizCorrect),
                    "correct", style, success: true),
                Statistic(state.TotalTurns, text("VocabQuiz_Total"),
                    "total", style),
                Statistic(state.WordsMastered, text("VocabQuiz_Mastered"),
                    "mastered", style, success: true),
                Statistic(state.RoundsCompleted, text("VocabQuiz_Rounds"),
                    "rounds", style)),
        };
        var results = state.Results.IsDefault ? [] : state.Results;
        for (var index = 0; index < results.Length; index++)
        {
            var result = results[index];
            var id = $"{NativeAccess.QuizSummaryResult}.{index}";
            var status = result.WasCorrect
                ? result.ReadyToRotateOut
                    ? "VocabQuiz_Mastered" : NativeText.QuizCorrect
                : NativeText.QuizIncorrect;
            var icon = Views.Image(result.WasCorrect
                    ? result.ReadyToRotateOut
                        ? "checkmark.circle.fill" : "checkmark.circle"
                    : "xmark.circle")
                .WithForegroundColor(
                    result.WasCorrect
                        ? style.Appearance.Success.Light
                        : style.Appearance.Danger.Light,
                    result.WasCorrect
                        ? style.Appearance.Success.Dark
                        : style.Appearance.Danger.Dark)
                .WithAccessibilityLabel(text(status))
                .WithAccessibilityIdentifier($"{id}.status");
            summary.Add(Views.HStack(
                icon,
                style.Text(Views.Text(result.TargetLanguageTerm),
                        SourceTextRole.Body1Bold, SourceTextTone.Primary)
                    .WithAccessibilityIdentifier($"{id}.word"),
                style.Text(Views.Text("→"), SourceTextRole.Body1,
                    SourceTextTone.Secondary),
                style.Text(Views.Text(result.NativeLanguageTerm),
                        SourceTextRole.Body1)
                    .WithAccessibilityIdentifier($"{id}.meaning"))
                .WithPadding(4, 0, 4, 0)
                .WithAccessibilityIdentifier(id));
        }
        var summaryCard = Views.VStack(summary.ToArray())
            .WithPadding(SourceUiLayout.LargeCardInset);
        summaryCard.Spacing = SourceUiLayout.ItemGap;
        items.Add(style.Card(summaryCard, maxWidth: 500)
            .WithAccessibilityIdentifier(NativeAccess.QuizSummary));
        if (state.Phase == NativePhase.Summary)
        {
            // Issue 225 (Captain, 2026-11-15): Continue and Done stay on screen below the
            // scrolling results, so a landscape phone reaches them without scrolling.
            var actions = new List<SwiftView>();
            if (state.TotalTurns > 0)
                actions.Add(style.PrimaryButton(
                        text("VocabQuiz_ContinueQuiz"), continueRound)
                    .WithAccessibilityIdentifier("native.quiz.continue"));
            actions.Add(style.SecondaryButton(
                    text(NativeText.Done), done)
                .WithAccessibilityIdentifier("native.quiz.done"));
            // Side by side only where both fit at the current text size (landscape phone,
            // iPad); otherwise stacked, as in the source.
            var sideBySide = NativeAdaptiveLayout.EffectiveWidth(
                style.ViewportWidth, style.TextScale) >= SideBySideActionsWidth;
            var row = (sideBySide ? Views.HStack(actions.ToArray())
                    : Views.VStack(actions.ToArray()))
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
            row.Spacing = SourceUiLayout.ItemGap;
            return Views.VStack(
                    Views.Divider().WithAccessibilityIdentifier(
                        "native.quiz.summary.footer.divider"),
                    // DockedBar sets its content's width leading; the row inside stays centered.
                    style.DockedBar(Views.VStack(row)))
                .WithAccessibilityIdentifier("native.quiz.summary.footer");
        }
        if (canRetry)
            items.Add(Views.Button(text(NativeText.Retry), retry)
                .WithAccessibilityIdentifier("native.quiz.retry"));
        return null;
    }

    private static SwiftView Statistic(
        int count, string label, string name, SourceUiStyle style,
        bool success = false) =>
        Views.VStack(
            style.Text(Views.Text(count.ToString(CultureInfo.CurrentCulture)),
                    SourceTextRole.Title2,
                    success ? SourceTextTone.Success : SourceTextTone.Body)
                .WithAccessibilityIdentifier($"native.quiz.summary.stat.{name}.value"),
            style.Text(Views.Text(label), SourceTextRole.Small,
                    SourceTextTone.Secondary)
                .WithAccessibilityIdentifier($"native.quiz.summary.stat.{name}.label"));

}
