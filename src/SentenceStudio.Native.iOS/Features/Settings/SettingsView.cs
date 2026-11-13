using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native.Features.Settings;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Settings;

/// <summary>
/// The Settings tab (Captain 2026-11-13, the native form pattern): one grouped Form, as iOS
/// Settings is. The profile row first, Appearance, Voice &amp; Quiz and the prompt modalities,
/// the Learning Coach and what Sam remembers, then Sign Out.
/// </summary>
internal static class SettingsView
{
    internal static SwiftView OwnerUnavailable(
        SourceUiStyle style, Func<string, string> text, SwiftView? footer = null)
    {
        var sections = new List<SwiftView>
        {
            Views.Section(string.Empty, NativeForm.Notice(style,
                text("NativeSettings_OwnerUnavailable"), "native.settings.owner-unavailable",
                error: true))
        };
        if (footer is not null)
            sections.Add(Views.Section(string.Empty, footer));
        return Form(style, sections);
    }

    /// <summary>The Settings tab before its owner's settings are read.</summary>
    internal static SwiftView Loading(SourceUiStyle style, Func<string, string> text) =>
        Form(style, [Views.Section(string.Empty,
            Views.IndeterminateProgress(text("Common_Loading")))]);

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
            ? NativeProfileSummaryText.LanguagePair(profile.NativeLanguage, profile.TargetLanguage, text) : null;
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
            .WithFlexibleWidth();
        row.Spacing = 12;
        // A Form row that opens Profile, as iOS Settings' first row opens your account.
        return Views.Button(row, openProfile)
            .WithFlexibleWidth()
            .WithAccessibilityLabel(string.Join(", ", spoken))
            .WithAccessibilityIdentifier("native.settings.profile");
    }

    /// <summary>The last row of the Settings tab (the Dashboard More menu's Sign out).</summary>
    internal static SwiftView SignOut(SourceUiStyle style, string title, bool enabled, Action signOut)
    {
        // The destructive row reads in the danger color, as iOS Settings' Sign Out does.
        var button = Views.Button(Views.Text(title)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center),
                signOut)
            .WithButtonRole(SwiftButtonRole.Destructive)
            .WithForegroundColor(style.Appearance.Danger.Light, style.Appearance.Danger.Dark)
            .WithAccessibilityLabel(title)
            .WithAccessibilityIdentifier("native.settings.sign-out");
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
        IEnumerable<SwiftView>? coach = null,
        SwiftView? header = null,
        SwiftView? footer = null)
    {
        var sections = new List<SwiftView>();
        if (header is not null)
            sections.Add(Views.Section(string.Empty, header));
        sections.Add(AppearanceSettingsView.Section(style, selection, text,
            selectTheme, selectMode, setFontScale));
        sections.AddRange(QuizSettingsView.Sections(style, quiz, text, selectVoiceLanguage,
            quizChanged, saveQuiz, voiceLoading, voiceErrorKey));
        if (feedbackKey is not null)
            sections.Add(Views.Section(string.Empty, style.Text(Views.Text(text(feedbackKey)),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.settings.quiz.feedback")));
        // Source Settings.razor: the Learning Coach and memory cards follow the study settings.
        if (coach is not null)
            sections.AddRange(coach);
        if (footer is not null)
            sections.Add(Views.Section(string.Empty, footer));
        return Form(style, sections);
    }

    /// <summary>The Settings Form; ApplicationScene.UpdateSettings swaps its Sections in place.</summary>
    private static SwiftView Form(SourceUiStyle style, IEnumerable<SwiftView> sections) =>
        NativeForm.Page(style, sections, "native.settings.form", "native.settings.screen");
}
