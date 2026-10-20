using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Quiz;

internal static class QuizSentenceView
{
    internal static void Add(
        List<SwiftView> items, NativeQuizState state, string input, bool busy,
        SourceUiStyle style, Func<string, string> text,
        Action<string> changed, Action submit, Action cancel, Action next)
    {
        var sentence = state.Sentence!;
        var results = state.SentenceResults.IsDefault
            ? ImmutableArray<NativeQuizSentenceGrade>.Empty
            : state.SentenceResults;
        var graded = !results.IsEmpty;
        var allGradingUnavailable = graded
            && results.All(result => result.GradeUnavailable);
        var headerAction = allGradingUnavailable ? null
            : graded
                ? style.SecondaryButton(text("VocabQuiz_ContinueQuiz"), next)
                : style.SecondaryButton(text("Common_Cancel"), cancel);
        if (headerAction is not null)
            headerAction.IsEnabled = !busy && !state.IsSentenceSubmitting
                && (!graded || state.Phase == NativePhase.Ready);
        var heading = Views.VStack(
                style.Text(Views.Text(text("VocabQuiz_SentenceShortcut")),
                    SourceTextRole.Caption1, SourceTextTone.Primary),
                style.Text(Views.Text(sentence.TargetLanguageTerm),
                        SourceTextRole.Title2, SourceTextTone.Primary)
                    .WithAccessibilityIdentifier(
                        "native.quiz.sentence.target-term"),
                style.Text(Views.Text(sentence.NativeLanguageGloss),
                    SourceTextRole.Body2, SourceTextTone.Secondary))
            .WithFlexibleWidth();
        var header = headerAction is null
            ? Views.HStack(heading)
            : Views.HStack(heading, headerAction);
        header.WithAdaptiveStack(368);
        var instruction = string.Format(CultureInfo.CurrentCulture,
            text(sentence.InstructionKey), sentence.TargetLanguageTerm);
        var grade = style.PrimaryButton(
                text(state.IsSentenceSubmitting || busy
                    ? "Common_Loading"
                    : allGradingUnavailable
                        ? NativeText.Retry : "VocabQuiz_GradeSentences"), submit)
            .WithAccessibilityIdentifier("native.quiz.sentence.grade");
        grade.IsEnabled = !busy && !state.IsSentenceSubmitting
            && !string.IsNullOrWhiteSpace(input);
        var placeholder = string.Format(CultureInfo.CurrentCulture,
            text("VocabQuiz_SentencePlaceholder"),
            string.IsNullOrWhiteSpace(sentence.TargetLanguageTerm)
                ? "the word" : sentence.TargetLanguageTerm);
        var inputView = style.Input(Views.TextEditor(input, value =>
            {
                changed(value);
                grade.IsEnabled = !busy && !state.IsSentenceSubmitting
                    && !string.IsNullOrWhiteSpace(value);
            })
            .WithTextEditorPlaceholder(placeholder)
            .WithPlaceholderColor(style.Appearance.TertiaryText.Light,
                style.Appearance.TertiaryText.Dark))
            .WithMinimumHeight(140)
            .WithAccessibilityLabel(instruction)
            .WithAccessibilityIdentifier("native.quiz.sentence.input");
        inputView.IsEnabled = !busy && !state.IsSentenceSubmitting;
        SwiftView gradeAction = state.IsSentenceSubmitting || busy
            ? Views.HStack(
                Views.IndeterminateProgress(text("Common_Loading"))
                    .WithAccessibilityIdentifier(
                        "native.quiz.sentence.grade.spinner"),
                grade)
            : grade;
        var body = Views.VStack(
            header,
            style.Text(Views.Text(instruction), SourceTextRole.Body1),
            style.Text(Views.Text(text("VocabQuiz_OnlyMeaningful")),
                SourceTextRole.Body2, SourceTextTone.Secondary),
            inputView,
            style.Text(Views.Text(text("VocabQuiz_SeparateMultiple")),
                SourceTextRole.Caption1, SourceTextTone.Secondary),
            gradeAction).WithPadding(SourceUiLayout.LargeCardInset);
        body.Spacing = SourceUiLayout.PageGap;
        items.Add(style.Card(body, maxWidth: 720)
            .WithAccessibilityIdentifier("native.quiz.sentence.screen"));

        if (results.IsEmpty)
            return;
        items.Add(style.Alert(
                style.Text(Views.Text(string.Format(
                    CultureInfo.CurrentCulture,
                    text("NativeVocabQuiz_SentenceResults"),
                    state.SentenceSuccessCount, results.Length,
                    sentence.TargetLanguageTerm)), SourceTextRole.Body2),
                success: state.SentenceSuccessCount > 0)
            .WithAccessibilityIdentifier("native.quiz.sentence.summary"));
        for (var index = 0; index < results.Length; index++)
        {
            var result = results[index];
            var hasVerifiedTargetUse = result.HasVerifiedTargetUse
                && result.TargetUsedForm is { Length: > 0 }
                && !result.GradeUnavailable;
            var usageExplanation = hasVerifiedTargetUse
                ? result.UsageExplanation
                : string.Format(CultureInfo.CurrentCulture,
                    text(NativeText.SentenceTargetUnconfirmed),
                    sentence.TargetLanguageTerm);
            var gradeBody = new List<SwiftView>
            {
                style.Text(Views.Text(text(result.GradeUnavailable
                    ? NativeText.SentenceGradeUnavailable
                    : result.UsageCorrect
                        ? "VocabQuiz_CountsTowardMastery" : "VocabQuiz_NeedsAnotherTry")),
                    SourceTextRole.Body2Strong, result.UsageCorrect
                        ? SourceTextTone.Success : SourceTextTone.Secondary),
                style.Text(Views.Text(result.Sentence), SourceTextRole.Body1),
            };
            if (!result.GradeUnavailable)
                gradeBody.Add(style.Text(Views.Text(
                    $"{text("VocabQuiz_TargetWordFeedback")} {usageExplanation}"),
                    SourceTextRole.Body2, SourceTextTone.Secondary));
            if (hasVerifiedTargetUse)
            {
                if (result.Accuracy is { } accuracy
                    && result.Fluency is { } fluency)
                    gradeBody.Add(style.Text(Views.Text(
                        $"{text("Common_Accuracy")}: {accuracy:F0}% · " +
                        $"{text("Writing_Fluency")}: {fluency:F0}%"),
                        SourceTextRole.Caption1, SourceTextTone.Secondary));
                if (!string.IsNullOrWhiteSpace(result.RecommendedSentence))
                    gradeBody.Add(style.Text(Views.Text(
                        $"{text("Common_Suggested")} {result.RecommendedSentence}"),
                        SourceTextRole.Body2, SourceTextTone.Secondary));
                foreach (var note in new[] { result.AccuracyExplanation,
                             result.FluencyExplanation, result.GrammarNotes }
                         .Where(note => !string.IsNullOrWhiteSpace(note)))
                    gradeBody.Add(style.Text(Views.Text(note),
                        SourceTextRole.Caption1, SourceTextTone.Secondary));
            }
            var resultCard = Views.VStack(gradeBody.ToArray())
                .WithPadding(SourceUiLayout.CardInset);
            resultCard.Spacing = SourceUiLayout.ItemGap;
            items.Add(style.Card(resultCard)
                .WithAccessibilityIdentifier($"native.quiz.sentence.result.{index}"));
        }
    }

}
