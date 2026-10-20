using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Quiz;

internal static class QuizFeedbackView
{
    internal static void Add(
        List<SwiftView> items,
        NativeQuizState state,
        SourceUiStyle style,
        Func<string, string> text,
        Action overrideAsCorrect,
        Action next)
    {
        if (state.RequiresCorrection)
        {
            var overrideLabel = Views.HStack(
                Views.Image("checkmark"),
                Views.Text(text(NativeText.QuizOverride)));
            overrideLabel.Spacing = 4;
            items.Add(style.SuccessOutlineSmallButton(
                    overrideLabel, overrideAsCorrect)
                .WithAccessibilityLabel(text(NativeText.QuizOverride))
                .WithAccessibilityIdentifier("native.quiz.override"));
        }
        else
        {
            var nextButton = style.QuizPrimaryButton(
                    text(NativeText.QuizNext), "chevron.right", next)
                .WithAccessibilityIdentifier("native.quiz.next");
            items.Add(Views.HStack(nextButton)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center));
        }
    }}
