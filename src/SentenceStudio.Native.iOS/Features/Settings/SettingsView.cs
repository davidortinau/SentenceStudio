using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native.Features.Settings;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Settings;

/// <summary>
/// The Settings tab as iOS Settings is laid out (Captain 2026-11-15): a sidebar of pages (the
/// profile row first, Appearance, Voice &amp; Quiz, Learning mode, the Learning Coach, then
/// Sign Out) beside the chosen page's grouped Form (the native form pattern, 2026-11-13). In a
/// compact width the sidebar is the list and a page is pushed over it.
/// </summary>
internal static class SettingsView
{
    /// <summary>A settings page when its owner's settings cannot be read (Sign Out stays in the sidebar).</summary>
    internal static SwiftView OwnerUnavailable(SourceUiStyle style, Func<string, string> text) =>
        Form(style, [Views.Section(string.Empty, NativeForm.Notice(style,
            text("NativeSettings_OwnerUnavailable"), "native.settings.owner-unavailable",
            error: true))]);

    /// <summary>A settings page before its owner's settings are read.</summary>
    internal static SwiftView Loading(SourceUiStyle style, Func<string, string> text) =>
        Form(style, [Views.Section(string.Empty,
            Views.IndeterminateProgress(text("Common_Loading")))]);

    /// <summary>
    /// The Settings sidebar: the profile row, the settings pages, the Learning Coach (when Sam
    /// is offered) and About. Sign Out is at the bottom of Profile. The open page's row is marked (see <see cref="Row"/>).
    /// </summary>
    internal static SwiftView Sidebar(SourceUiStyle style, Func<string, string> text,
        (string Name, string NativeLanguage, string TargetLanguage)? summary,
        NativeSettingsPage? selected, bool coachShown, Action<NativeSettingsPage> select,
        SwiftView? previewExit = null)
    {
        SwiftView PageRow(NativeSettingsPage page, string symbol, string id) =>
            Row(style, symbol, text(TitleKey(page)), selected == page, () => select(page), id);
        var sections = new List<SwiftView>
        {
            Views.Section(string.Empty, ProfileRow(style, text, summary,
                selected == NativeSettingsPage.Profile, () => select(NativeSettingsPage.Profile))),
            Views.Section(string.Empty,
                PageRow(NativeSettingsPage.Appearance, "paintbrush", "native.settings.row.appearance"),
                PageRow(NativeSettingsPage.VoiceQuiz, "speaker.wave.2", "native.settings.row.voice-quiz"),
                PageRow(NativeSettingsPage.LearningMode, "slider.horizontal.3",
                    "native.settings.row.learning-mode")),
        };
        if (coachShown)
            sections.Add(Views.Section(string.Empty, PageRow(NativeSettingsPage.Coach,
                "bubble.left.and.bubble.right", "native.settings.row.coach")));
        sections.Add(Views.Section(string.Empty,
            PageRow(NativeSettingsPage.About, "info.circle", "native.settings.row.about")));
        if (previewExit is not null)
            sections.Add(Views.Section(string.Empty, previewExit));
        return NativeForm.Page(style, sections, "native.settings.sidebar.form",
            "native.settings.sidebar");
    }

    /// <summary>
    /// The About page (source Settings.razor About card): the app's version, and What's New,
    /// which opens the release notes in a sheet. A note says when there are none or they could
    /// not be read.
    /// </summary>
    internal static SwiftView About(SourceUiStyle style, Func<string, string> text, string version,
        bool loading, string? noticeKey, Action openWhatsNew)
    {
        var whatsNew = NativeForm.Action(style, text("Settings_WhatsNew"), "megaphone", openWhatsNew,
            "native.settings.about.whats-new");
        whatsNew.IsEnabled = !loading;
        var rows = new List<SwiftView> { whatsNew };
        if (loading)
            rows.Add(Views.IndeterminateProgress(text("Common_Loading"))
                .WithAccessibilityIdentifier("native.settings.about.loading"));
        if (noticeKey is not null)
            rows.Add(NativeForm.Notice(style, text(noticeKey), "native.settings.about.notice",
                error: noticeKey != "Settings_NoReleaseNotes"));
        return Form(style,
        [
            Views.Section(string.Empty, style.Text(Views.Text(string.Format(
                        CultureInfo.CurrentCulture, text("Settings_VersionLabel"), version)),
                    SourceTextRole.Body1)
                .WithAccessibilityIdentifier("native.settings.about.version")),
            Views.Section(string.Empty, rows.ToArray()),
        ]);
    }

    /// <summary>
    /// What's New (source WhatsNewModal): the release's title, version and date, then each
    /// section of the notes as a list Section whose rows are a bold headline over its text.
    /// </summary>
    internal static SwiftView WhatsNew(SourceUiStyle style, Func<string, string> text,
        NativeReleaseNotes? notes)
    {
        if (notes is null)
            return Loading(style, text);
        var date = DateOnly.TryParse(notes.Date, CultureInfo.InvariantCulture, out var day)
            ? day.ToString("D", CultureInfo.CurrentCulture) : notes.Date;
        var header = Views.VStack(
                style.Text(Views.Text(notes.Title), SourceTextRole.Title3),
                style.Text(Views.Text(string.Format(CultureInfo.CurrentCulture,
                        text("Settings_VersionLabel"), notes.Version)
                    + (date.Length > 0 ? " · " + date : string.Empty)),
                    SourceTextRole.Body2, SourceTextTone.Secondary))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.whats-new.header");
        header.Spacing = 4;
        var sections = new List<SwiftView> { Views.Section(string.Empty, header) };
        foreach (var section in notes.Sections)
            sections.Add(Views.Section(section.Title, section.Items.Select(item =>
            {
                if (item.Headline is null)
                    return style.Text(Views.Text(item.Text), SourceTextRole.Body1);
                var lines = Views.VStack(
                        style.Text(Views.Text(item.Headline), SourceTextRole.Body1,
                            strongElement: true),
                        style.Text(Views.Text(item.Text), SourceTextRole.Body2,
                            SourceTextTone.Secondary))
                    .WithFlexibleWidth();
                lines.Spacing = 2;
                return lines;
            }).ToArray()));
        return NativeForm.Page(style, sections, "native.whats-new.form", "native.whats-new");
    }

    /// <summary>A page's title: its sidebar row and the detail column's navigation title.</summary>
    internal static string TitleKey(NativeSettingsPage page) => page switch
    {
        NativeSettingsPage.Profile => "Profile_PageTitle",
        NativeSettingsPage.Appearance => "Settings_AppearanceSection",
        NativeSettingsPage.VoiceQuiz => "Settings_VoiceQuizSection",
        NativeSettingsPage.LearningMode => "LearningMode_Title",
        NativeSettingsPage.Coach => "Coach_SettingsSection",
        NativeSettingsPage.About => "Settings_AboutSection",
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, "Unrecognized Settings page."),
    };

    /// <summary>
    /// A sidebar row: symbol, title and a chevron. The open page's row (beside the list only:
    /// in a compact width nothing stays chosen) shows its title and symbol in the accent color,
    /// bold, and a checkmark in place of the chevron (not color alone), and is selected for
    /// VoiceOver, as the Library's sidebar marks its open place.
    /// </summary>
    private static SwiftView Row(SourceUiStyle style, string symbol, string title, bool selected,
        Action choose, string id)
    {
        var appearance = style.Appearance;
        var row = Views.HStack(
                Views.Image(symbol)
                    .WithFixedSymbolSize(20)
                    .WithForegroundColor(appearance.Primary.Light, appearance.Primary.Dark),
                style.Text(Views.Text(title), SourceTextRole.Body1,
                        selected ? SourceTextTone.Primary : SourceTextTone.Body,
                        strongElement: selected)
                    .WithLineLimit(1)
                    .WithFlexibleWidth(),
                Trailing(style, selected))
            .WithFlexibleWidth();
        row.Spacing = 12;
        return Views.Button(row, choose)
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithAccessibilitySelected(selected)
            .WithAccessibilityLabel(title)
            .WithAccessibilityIdentifier(id);
    }

    private static SwiftView Trailing(SourceUiStyle style, bool selected)
    {
        var appearance = style.Appearance;
        return selected
            ? Views.Image("checkmark")
                .WithFixedSymbolSize(15)
                .WithForegroundColor(appearance.Primary.Light, appearance.Primary.Dark)
            : Views.Image("chevron.right")
                .WithFixedSymbolSize(13)
                .WithForegroundColor(appearance.SecondaryText.Light, appearance.SecondaryText.Dark);
    }

    /// <summary>
    /// The first row of the Settings sidebar, as iOS Settings starts with your name: the
    /// learner's name and languages (in the UI language); it opens the Profile page.
    /// Before the profile is read it shows "Profile" alone.
    /// </summary>
    private static SwiftView ProfileRow(SourceUiStyle style, Func<string, string> text,
        (string Name, string NativeLanguage, string TargetLanguage)? summary, bool selected,
        Action openProfile)
    {
        var appearance = style.Appearance;
        var profileTitle = text("Nav_Profile");
        var name = summary is { Name.Length: > 0 } known ? known.Name : profileTitle;
        var languages = summary is { } profile
            ? NativeProfileSummaryText.LanguagePair(profile.NativeLanguage, profile.TargetLanguage, text) : null;
        var tone = selected ? SourceTextTone.Primary : SourceTextTone.Body;
        var labels = languages is null
            ? Views.VStack(style.Text(Views.Text(name), SourceTextRole.Title3, tone,
                strongElement: selected))
            : Views.VStack(
                style.Text(Views.Text(name), SourceTextRole.Title3, tone, strongElement: selected),
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
                Trailing(style, selected))
            .WithFlexibleWidth();
        row.Spacing = 12;
        // A sidebar row that opens Profile, as iOS Settings' first row opens your account.
        return Views.Button(row, openProfile)
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithFlexibleWidth()
            .WithAccessibilitySelected(selected)
            .WithAccessibilityLabel(string.Join(", ", spoken))
            .WithAccessibilityIdentifier("native.settings.profile");
    }

    /// <summary>The last row of the Profile page (the Dashboard More menu's Sign out).</summary>
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

    /// <summary>
    /// A settings page's Form (Profile is its own page, ProfileView): the Sections it held in
    /// the single Settings Form, in the same order.
    /// </summary>
    internal static SwiftView Page(
        SourceUiStyle style,
        NativeSettingsPage page,
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
        SwiftView? learning = null)
    {
        var sections = new List<SwiftView>();
        switch (page)
        {
            case NativeSettingsPage.Appearance:
                sections.Add(AppearanceSettingsView.Section(style, selection, text,
                    selectTheme, selectMode, setFontScale));
                break;
            case NativeSettingsPage.VoiceQuiz:
                sections.AddRange(QuizSettingsView.Sections(style, quiz, text, selectVoiceLanguage,
                    quizChanged, saveQuiz, voiceLoading, voiceErrorKey));
                if (feedbackKey is not null)
                    sections.Add(Views.Section(string.Empty, style.Text(Views.Text(text(feedbackKey)),
                            SourceTextRole.Body2, SourceTextTone.Secondary)
                        .WithAccessibilityIdentifier("native.settings.quiz.feedback")));
                break;
            case NativeSettingsPage.LearningMode when learning is not null:
                sections.Add(learning);
                break;
            // Source Settings.razor: the Learning Coach and what Sam remembers.
            case NativeSettingsPage.Coach when coach is not null:
                sections.AddRange(coach);
                break;
            default:
                // Learning mode not read yet, or no Learning Coach to show.
                return Loading(style, text);
        }
        return Form(style, sections);
    }

    /// <summary>
    /// Learning mode (docs/known-words-plan.md, source Settings.razor): Learner-led or
    /// Practice-led, with what the chosen one means under it. A change is saved at once; the
    /// picker is off while it saves. Null until the mode has been read.
    /// </summary>
    internal static SwiftView? LearningMode(SourceUiStyle style, LearningModeState state,
        Func<string, string> text, Action<SentenceStudio.Shared.Models.LearningMode> choose)
    {
        if (state.Mode is not { } mode)
            return state.ErrorKey is { } loadError
                ? Views.Section(string.Empty, NativeForm.Notice(style, text(loadError),
                    "native.settings.learning-mode.error", error: true))
                : null;
        var picker = Views.Picker(text("LearningMode_Title"),
                [
                    new SwiftPickerOption(nameof(SentenceStudio.Shared.Models.LearningMode.LearnerLed),
                        text("LearningMode_LearnerLed")),
                    new SwiftPickerOption(nameof(SentenceStudio.Shared.Models.LearningMode.PracticeLed),
                        text("LearningMode_PracticeLed")),
                ],
                mode.ToString(),
                value =>
                {
                    if (Enum.TryParse<SentenceStudio.Shared.Models.LearningMode>(value, out var chosen))
                        choose(chosen);
                })
            .WithPickerStyle(SwiftPickerStyle.Segmented)
            .WithAccessibilityIdentifier("native.settings.learning-mode");
        picker.IsEnabled = !state.Saving;
        var rows = new List<SwiftView>
        {
            picker,
            style.Text(Views.Text(text(mode == SentenceStudio.Shared.Models.LearningMode.LearnerLed
                        ? "LearningMode_LearnerLedDetail" : "LearningMode_PracticeLedDetail")),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.settings.learning-mode.detail")
        };
        if (state.Saving)
            rows.Add(Views.IndeterminateProgress(text("LearningMode_Title"))
                .WithAccessibilityIdentifier("native.settings.learning-mode.saving"));
        if (state.ErrorKey is { } error)
            rows.Add(NativeForm.Notice(style, text(error), "native.settings.learning-mode.error",
                error: true));
        // Untitled: the Learning mode page's title bar names it.
        return Views.Section(string.Empty, rows.ToArray());
    }

    /// <summary>A settings page's Form; ApplicationScene.UpdateSettings swaps its Sections in place.</summary>
    private static SwiftView Form(SourceUiStyle style, IEnumerable<SwiftView> sections) =>
        NativeForm.Page(style, sections, "native.settings.form", "native.settings.screen");
}
