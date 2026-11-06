using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Theme;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Settings;

internal static class SettingsView
{
    internal static SwiftView OwnerUnavailable(
        SourceUiStyle style, Func<string, string> text, SwiftView? footer = null)
    {
        var message = style.Alert(style.Text(
                Views.Text(text("NativeSettings_OwnerUnavailable")),
                SourceTextRole.Body2), success: false)
            .WithAccessibilityIdentifier("native.settings.owner-unavailable");
        var content = footer is null ? Views.VStack(message) : Views.VStack(message, footer);
        content.Spacing = SourceUiLayout.SectionGap;
        return style.Page(Views.Scroll(
                content.WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth)))
            .WithAccessibilityIdentifier("native.settings.screen"));
    }

    /// <summary>The Settings tab before its owner's settings are read.</summary>
    internal static SwiftView Loading(SourceUiStyle style, Func<string, string> text) =>
        style.Page(Views.Scroll(
                Views.VStack(Views.IndeterminateProgress(text("Common_Loading"))
                        .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center))
                    .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth))
                    .WithFlexibleWidth())
            .WithAccessibilityIdentifier("native.settings.screen"));

    /// <summary>
    /// The first row of the Settings tab, as iOS Settings starts with your name: the
    /// learner's name and languages (in the UI language); it opens the Profile page.
    /// Before the profile is read it shows "Profile" alone.
    /// </summary>
    internal static SwiftView ProfileRow(SourceUiStyle style, Func<string, string> text,
        (string Name, string NativeLanguage, string TargetLanguage)? summary, Action openProfile)
    {
        var appearance = style.Appearance;
        var profileTitle = text("Nav_Profile");
        var name = summary is { Name.Length: > 0 } known ? known.Name : profileTitle;
        var languages = summary is { } profile
            ? LanguagePair(profile.NativeLanguage, profile.TargetLanguage, text) : null;
        var labels = languages is null
            ? Views.VStack(style.Text(Views.Text(name), SourceTextRole.Title3))
            : Views.VStack(
                style.Text(Views.Text(name), SourceTextRole.Title3),
                style.Text(Views.Text(languages), SourceTextRole.Body2, SourceTextTone.Secondary));
        labels.WithFlexibleWidth();
        labels.Spacing = 2;
        var spoken = new List<string> { name };
        if (languages is not null)
            spoken.Add(languages);
        if (!string.Equals(name, profileTitle, StringComparison.Ordinal))
            spoken.Add(profileTitle);
        var row = Views.HStack(
                Views.Image("person.crop.circle.fill")
                    .WithFixedSymbolSize(40)
                    .WithForegroundColor(appearance.Primary.Light, appearance.Primary.Dark),
                labels,
                Views.Image("chevron.right")
                    .WithForegroundColor(appearance.SecondaryText.Light, appearance.SecondaryText.Dark))
            .WithPadding(SourceUiLayout.CardInset)
            .WithFlexibleWidth();
        row.Spacing = 12;
        return Views.Button(style.BorderedCard(row), openProfile)
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithAccessibilityLabel(string.Join(", ", spoken))
            .WithAccessibilityIdentifier("native.settings.profile");
    }

    /// <summary>
    /// "Native → Target" with each language named in the UI language (the native heads'
    /// five supported names; any other is shown as stored). One language alone is shown
    /// alone; none gives null.
    /// </summary>
    internal static string? LanguagePair(string native, string target, Func<string, string> text)
    {
        string? Name(string language) => string.IsNullOrWhiteSpace(language) ? null
            : NativeHowDoYouSayText.LanguageKey(language) is { } key ? text(key) : language;
        return (Name(native), Name(target)) switch
        {
            ({ } from, { } to) => $"{from} → {to}",
            ({ } only, null) => only,
            (null, { } only) => only,
            _ => null
        };
    }

    /// <summary>The last row of the Settings tab (the Dashboard More menu's Sign out).</summary>
    internal static SwiftView SignOut(SourceUiStyle style, string title, bool enabled, Action signOut)
    {
        var button = style.SecondaryButton(title, signOut, fullWidth: true)
            .WithFlexibleWidth()
            .WithAccessibilityLabel(title)
            .WithAccessibilityIdentifier("native.settings.sign-out");
        // The destructive row reads in the danger color, as iOS Settings' Sign Out does.
        button.Children[0].WithForegroundColor(style.Appearance.Danger.Light,
            style.Appearance.Danger.Dark);
        button.IsEnabled = enabled;
        return button;
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
        string? voiceErrorKey,
        SwiftView? coach = null,
        SwiftView? header = null,
        SwiftView? footer = null)
    {
        var sections = new List<SwiftView>();
        if (header is not null)
            sections.Add(header);
        sections.AddRange(
        [
            AppearanceSettingsView.Section(style, selection, text,
                selectTheme, selectMode, setFontScale),
            QuizSettingsView.Section(style, quiz, text, selectVoiceLanguage,
                quizChanged, saveQuiz, voiceLoading, voiceErrorKey)
        ]);
        if (feedbackKey is not null)
            sections.Add(style.Text(Views.Text(text(feedbackKey)),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.settings.quiz.feedback"));
        // Source Settings.razor: the Learning Coach and memory cards follow the study settings.
        if (coach is not null)
            sections.Add(coach);
        if (footer is not null)
            sections.Add(footer);
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
