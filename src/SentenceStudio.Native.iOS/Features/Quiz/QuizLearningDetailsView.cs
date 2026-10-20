using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Shared.Models;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Quiz;

internal static class QuizLearningDetailsView
{
    internal static SwiftView Page(
        NativeQuizLearningDetails details,
        SourceUiStyle style,
        Func<string, string> text,
        Action close)
    {
        var status = text(StatusKey(details.Status));
        var fields = new List<SwiftView>
        {
            Views.HStack(
                Views.Image("info.circle"),
                style.Text(Views.Text(text("VocabQuiz_LearningDetails")),
                        SourceTextRole.Title3, sourceHeading: true)
                    .WithFlexibleWidth()
                    .WithAccessibilityIdentifier("native.quiz.details.heading"),
                Views.Button(Views.Image("xmark"), close)
                    .WithAccessibilityLabel(text("Common_Close"))
                    .WithAccessibilityIdentifier("native.quiz.details.close")),
            style.Text(Views.Text(status), SourceTextRole.BadgeSmall)
                .WithForegroundColor(
                    details.Status == LearningStatus.Known
                        ? "#FFFFFF" : style.Appearance.OnBootstrapSecondary.Light,
                    details.Status == LearningStatus.Known
                        ? "#FFFFFF" : style.Appearance.OnBootstrapSecondary.Dark)
                .WithPadding(5, 10, 5, 10)
                .WithRoundedSurface(
                    details.Status == LearningStatus.Known
                        ? style.Appearance.Success.Light
                        : style.Appearance.BootstrapSecondary.Light,
                    details.Status == LearningStatus.Known
                        ? style.Appearance.Success.Dark
                        : style.Appearance.BootstrapSecondary.Dark, 12)
                .WithAccessibilityIdentifier("native.quiz.details.status"),
            Views.LazyVGrid(3,
                Value("NativeVocabQuiz_Details_TotalAttempts",
                details.TotalAttempts.ToString(CultureInfo.CurrentCulture),
                "native.quiz.details.total-attempts", style, text),
                Value("NativeVocabQuiz_Details_CorrectAttempts",
                details.CorrectAttempts.ToString(CultureInfo.CurrentCulture),
                "native.quiz.details.correct-attempts", style, text),
                Value("Accuracy",
                details.Accuracy.ToString("P0", CultureInfo.CurrentCulture),
                "native.quiz.details.accuracy", style, text),
                Value("NativeVocabQuiz_Details_CurrentStreak",
                details.CurrentStreak.ToString("F1", CultureInfo.CurrentCulture),
                "native.quiz.details.current-streak", style, text),
                Value("NativeVocabQuiz_Details_ProductionInStreak",
                $"{details.ProductionInStreak} / 2",
                "native.quiz.details.production", style, text),
                Value("NativeVocabQuiz_Details_EffectiveStreak",
                details.EffectiveStreak.ToString("F1", CultureInfo.CurrentCulture),
                "native.quiz.details.effective-streak", style, text),
                Value("NativeVocabQuiz_Details_MasteryScore",
                details.MasteryScore.ToString("P0", CultureInfo.CurrentCulture),
                "native.quiz.details.mastery", style, text))
                .WithAccessibilityIdentifier("native.quiz.details.metrics"),
            Views.VStack(Views.Divider()
                    .WithAccessibilityIdentifier("native.quiz.details.srs.divider"))
                .WithFlexibleWidth(),
            style.Text(Views.Text(text("NativeVocabQuiz_Details_SrsSchedule")),
                SourceTextRole.Body2Strong),
            Views.LazyVGrid(2,
                Value("VocabWordEdit_NextReviewLabel",
                details.NextReviewDate?.ToString("d", CultureInfo.CurrentCulture)
                    ?? text("NotScheduled"),
                "native.quiz.details.next-review", style, text),
                Value("NativeVocabQuiz_Details_ReviewInterval",
                string.Format(CultureInfo.CurrentCulture,
                    text("NativeVocabQuiz_Details_Days"), details.ReviewInterval),
                "native.quiz.details.interval", style, text),
                Value("NativeVocabQuiz_Details_EaseFactor",
                details.EaseFactor.ToString("F2", CultureInfo.CurrentCulture),
                "native.quiz.details.ease", style, text),
                Value("NativeVocabQuiz_Details_IsDueForReview",
                text(details.IsDueForReview ? "Yes" : "No"),
                "native.quiz.details.due", style, text)),
            Views.VStack(Views.Divider()
                    .WithAccessibilityIdentifier("native.quiz.details.context.divider"))
                .WithFlexibleWidth(),
            style.Text(Views.Text(text("NativeVocabQuiz_Details_QuizContext")),
                SourceTextRole.Body2Strong),
            Views.LazyVGrid(2,
                Value("NativeVocabQuiz_Details_QuizMode",
                text(details.Mode == "Text"
                    ? "NativeVocabQuiz_Details_TextMode"
                    : "NativeVocabQuiz_Details_MultipleChoiceMode"),
                "native.quiz.details.mode", style, text),
                Value("NativeVocabQuiz_Details_DueOnly",
                text(details.DueOnly ? "Yes" : "No"),
                "native.quiz.details.due-only", style, text)),
            Value("NativeVocabQuiz_Details_WhyMode",
                text(details.WhyModeKey),
                "native.quiz.details.why-mode", style, text),
        };
        if (details.WhyModeKey == "NativeVocabQuiz_ModeFocusRecognition")
            fields.Add(Value("NativeVocabQuiz_Details_RecognitionDemonstrations",
                $"{details.RecognitionDemonstrations}/3",
                "native.quiz.details.recognition-demonstrations", style, text));
        return style.GroupedForm(Views.Form(fields.ToArray()))
            .WithAccessibilityIdentifier("native.quiz.details.screen");
    }

    private static SwiftView Value(
        string key, string value, string id,
        SourceUiStyle style,
        Func<string, string> text) =>
        Views.VStack(
            style.Text(Views.Text(text(key)),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier($"{id}.label"),
            style.Text(Views.Text(value), SourceTextRole.Body2Strong)
                .WithAccessibilityIdentifier($"{id}.value"))
        .WithAccessibilityIdentifier(id);

    private static string StatusKey(LearningStatus status) => status switch
    {
        LearningStatus.Known => "VocabWordEdit_StatusKnown",
        LearningStatus.Familiar => "VocabWordEdit_StatusFamiliar",
        LearningStatus.Learning => "VocabWordEdit_StatusLearning",
        _ => "VocabWordEdit_StatusUnknown"
    };
}
