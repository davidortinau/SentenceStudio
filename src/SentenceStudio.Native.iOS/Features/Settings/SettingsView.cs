using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Theme;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Settings;

internal static class SettingsView
{
    internal static SwiftView OwnerUnavailable(
        SourceUiStyle style, Func<string, string> text)
    {
        var message = style.Alert(style.Text(
                Views.Text(text("NativeSettings_OwnerUnavailable")),
                SourceTextRole.Body2), success: false)
            .WithAccessibilityIdentifier("native.settings.owner-unavailable");
        return style.Page(Views.Scroll(
                Views.VStack(message)
                    .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth)))
            .WithAccessibilityIdentifier("native.settings.screen"));
    }

    internal static SwiftView Page(
        SourceUiStyle style,
        AppearanceSelection selection,
        QuizSettingsDraft quiz,
        Func<string, string> text,
        Action<string> selectTheme,
        Action<ThemeMode> selectMode,
        Action<double> setFontScale,
        Action<string> selectVoiceLanguage,
        Action<Func<QuizSettingsDraft, QuizSettingsDraft>> quizChanged,
        Action saveQuiz,
        string? feedbackKey,
        bool voiceLoading,
        string? voiceErrorKey)
    {
        var sections = new List<SwiftView>
        {
            AppearanceSettingsView.Section(style, selection, text,
                selectTheme, selectMode, setFontScale),
            QuizSettingsView.Section(style, quiz, text, selectVoiceLanguage,
                quizChanged, saveQuiz, voiceLoading, voiceErrorKey)
        };
        if (feedbackKey is not null)
            sections.Add(style.Text(Views.Text(text(feedbackKey)),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.settings.quiz.feedback"));
        var content = Views.VStack(sections.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.settings.scroll.content");
        content.Spacing = SourceUiLayout.SectionGap;
        var page = Views.Scroll(content)
            .WithAccessibilityIdentifier("native.settings.screen");
        return style.Page(page);
    }
}
