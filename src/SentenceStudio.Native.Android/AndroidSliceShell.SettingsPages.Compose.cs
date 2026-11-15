using System.Globalization;
using AndroidX.Compose;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Native.Features.Profile;
using SentenceStudio.Native.Features.Settings;
using SentenceStudio.Services;
using ComposeScrollState = AndroidX.Compose.ScrollState;

namespace SentenceStudio.Native.Android;

/// <summary>
/// The Settings tab as a Material list-detail layout (Captain 2026-11-15, iOS Settings split
/// view): from the medium window width (600 dp, an unfolded foldable, a tablet) the list of
/// pages sits beside the chosen page, and Profile opens first. On a phone the list is the page;
/// a row opens its page over it and Back closes it. Pages: Profile, Appearance, Voice and Quiz,
/// Learning mode, the Learning Coach, About (the version and What's New).
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private NativeSettingsPage? _settingsPage;
    // The page opened by itself beside the list (not chosen): it closes when the panes join.
    private bool _settingsPageAutomatic;
    private NativeReleaseNotes? _whatsNew;
    private bool _whatsNewPresented;
    private bool _whatsNewLoading;
    private string? _whatsNewNoticeKey;

    /// <summary>Two panes from the Material medium window class (600 dp).</summary>
    private bool IsSettingsTwoPane => WindowWidthDp >= 600;

    /// <summary>Profile is saving: the page cannot change, as Back waits.</summary>
    private bool IsSettingsPageBlocked => _profile?.State.IsSaving == true;

    private ComposableNode RenderSettings(AndroidVisualPalette palette) =>
        new Composed(composer =>
        {
            // The list keeps its scroll with the owner's settings; a page starts at its top.
            var listScroll = composer.Remember(() => new ComposeScrollState(), _settings);
            var pageScroll = composer.Remember(() => new ComposeScrollState(),
                _settingsPage?.ToString() ?? string.Empty);
            _whatsNewScroll = composer.Remember(() => new ComposeScrollState());
            if (IsSettingsTwoPane && _settingsPage is null)
                // Beside the list a page is always open: Profile first, as on iOS. Keyed on Busy,
                // so a refusal while an action runs is tried again once it ends.
                composer.Effect($"settings.default.{Busy.Value}", _ =>
                {
                    Publish(SelectDefaultSettingsPage);
                    return Task.CompletedTask;
                });
            if (!IsSettingsTwoPane && _settingsPageAutomatic && _settingsPage is not null)
                // A phone rotated back, or a foldable folded: the page nobody chose closes, so
                // the list shows (a chosen page stays open over it).
                composer.Effect("settings.automatic.close", _ =>
                {
                    Publish(() =>
                    {
                        if (_settingsPageAutomatic && !IsSettingsTwoPane)
                            CloseSettingsPageNow();
                    });
                    return Task.CompletedTask;
                });
            if (IsSettingsTwoPane)
                return new Row
                {
                    Modifier.FillMaxSize().TestTag("native.settings.split"),
                    new Column
                    {
                        Modifier.Width(360).FillMaxHeight(),
                        ScreenHeader(Localize("Settings_Title"), palette),
                        SettingsList(palette, listScroll, twoPane: true),
                    },
                    new VerticalDivider { Color = palette.Border },
                    new Column
                    {
                        Modifier.Weight(1f).FillMaxHeight(),
                        ScreenHeader(_settingsPage is { } open
                            ? Localize(SettingsTitleKey(open)) : string.Empty, palette),
                        SettingsDetail(palette, pageScroll),
                    },
                };
            if (_settingsPage is { } page)
                return new Column
                {
                    Modifier.FillMaxSize().TestTag("native.settings.page"),
                    ScreenHeader(Localize(SettingsTitleKey(page)), palette, back: true,
                        backAutomationId: "native.settings.back"),
                    SettingsDetail(palette, pageScroll),
                };
            return new Column
            {
                Modifier.FillMaxSize(),
                ScreenHeader(Localize("Settings_Title"), palette),
                SettingsList(palette, listScroll, twoPane: false),
            };
        });

    private static string SettingsTitleKey(NativeSettingsPage page) => page switch
    {
        NativeSettingsPage.Profile => "Profile_PageTitle",
        NativeSettingsPage.Appearance => "Settings_AppearanceSection",
        NativeSettingsPage.VoiceQuiz => "Settings_VoiceQuizSection",
        NativeSettingsPage.LearningMode => "LearningMode_Title",
        NativeSettingsPage.Coach => "Coach_SettingsSection",
        NativeSettingsPage.About => "Settings_AboutSection",
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, "Unrecognized Settings page."),
    };

    /// <summary>The list of pages: the profile row, the settings, the coach and About.</summary>
    private ComposableNode SettingsList(AndroidVisualPalette palette, ComposeScrollState scroll, bool twoPane)
    {
        var viewport = new Column(Arrangement.SpacedBy(16))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f).VerticalScroll(scroll)
                .Padding(AndroidVisualTheme.PagePadding)
                .TestTag("native.settings.screen"),
        };
        if (!_preview)
            viewport.Add(SettingsProfileRow(palette,
                selected: twoPane && _settingsPage == NativeSettingsPage.Profile));
        ComposableNode PageRow(NativeSettingsPage page, Icon icon, string id) =>
            SettingsRow(page, icon, id, selected: twoPane && _settingsPage == page, palette);
        viewport.Add(SettingsGroup(palette,
            PageRow(NativeSettingsPage.Appearance,
                new Icon(Icons.Default.Create, null), "native.settings.row.appearance"),
            PageRow(NativeSettingsPage.VoiceQuiz,
                new Icon(Resource.Drawable.ic_volume_up, null), "native.settings.row.voice-quiz"),
            PageRow(NativeSettingsPage.LearningMode,
                new Icon(Resource.Drawable.ic_tune, null), "native.settings.row.learning-mode")));
        if (IsSamSettingsShown)
            viewport.Add(SettingsGroup(palette, PageRow(NativeSettingsPage.Coach,
                new Icon(Resource.Drawable.ic_auto_awesome, null), "native.settings.row.coach")));
        viewport.Add(SettingsGroup(palette, PageRow(NativeSettingsPage.About,
            new Icon(Icons.Default.Info, null), "native.settings.row.about")));
        if (!string.IsNullOrWhiteSpace(Error.Value))
            viewport.Add(StatusAlert(Error.Value, palette, error: true,
                automationId: "native.settings.error"));
        // Sign out is at the bottom of Profile (Captain 2026-11-15); the Debug preview has no
        // Profile, so its exit stays here.
        if (_preview)
            viewport.Add(SettingsSignOut(palette));
        return viewport;
    }

    /// <summary>A card of rows separated by dividers (the Library list's look).</summary>
    private static ComposableNode SettingsGroup(AndroidVisualPalette palette, params ComposableNode[] rows)
    {
        var column = new Column { Modifier.FillMaxWidth() };
        for (var index = 0; index < rows.Length; index++)
        {
            if (index > 0)
                column.Add(new HorizontalDivider { Color = palette.Border });
            column.Add(rows[index]);
        }
        var card = new Surface
        {
            Modifier = Modifier.FillMaxWidth(),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        card.Add(column);
        return card;
    }

    /// <summary>
    /// A page row: icon, title and a chevron. The open page's row beside it (two panes only) is
    /// marked: its title in the accent color, bold, a check in place of the chevron (not color
    /// alone), and selected for TalkBack.
    /// </summary>
    private ComposableNode SettingsRow(NativeSettingsPage page, Icon icon, string id, bool selected,
        AndroidVisualPalette palette)
    {
        var title = Localize(SettingsTitleKey(page));
        icon.Modifier = Modifier.Size(22);
        icon.Tint = palette.Primary;
        return new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(id)
                .Clickable(() => Publish(() => SelectSettingsPage(page)))
                .Padding(horizontal: AndroidVisualTheme.CardPadding, vertical: 12)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(title);
                    semantics.Role(SemanticsRole.Button);
                    semantics.Selected(selected);
                }),
            icon,
            new Text(title)
            {
                Modifier = Modifier.Weight(1f),
                Color = selected ? palette.Primary : palette.Text,
                FontSize = new Sp(15),
                FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal,
                MaxLines = 1,
                Overflow = TextOverflow.Ellipsis,
            },
            SettingsRowTrailing(selected, palette),
        };
    }

    private static ComposableNode SettingsRowTrailing(bool selected, AndroidVisualPalette palette) =>
        selected
            ? new Icon(Icons.Default.Check, null) { Modifier = Modifier.Size(20), Tint = palette.Primary }
            : new Icon(Icons.AutoMirrored.Default.KeyboardArrowRight, null)
            {
                Modifier = Modifier.Size(20),
                Tint = palette.SecondaryText,
            };

    /// <summary>The open page (beside the list, or over it on a phone).</summary>
    private ComposableNode SettingsDetail(AndroidVisualPalette palette, ComposeScrollState scroll)
    {
        if (_settingsPage == NativeSettingsPage.Profile)
            return new Box { Modifier.FillMaxWidth().Weight(1f), RenderProfile(palette) };
        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f).VerticalScroll(scroll)
                .Padding(AndroidVisualTheme.PagePadding)
                .TestTag("native.settings.detail"),
        };
        if (_settingsPage is not { } page)
            return viewport;
        var state = _settings?.State;
        var quiz = state is { OwnerBlocked: false, Quiz: { } draft } ? draft : null;
        switch (page)
        {
            case NativeSettingsPage.About:
                viewport.Add(SettingsAbout(palette));
                break;
            case NativeSettingsPage.Coach:
                viewport.Add(RenderSamSettings(palette) ?? Caption(Localize("Coach_SettingsUnavailable"),
                    palette, "native.sam.settings.unavailable"));
                break;
            case var _ when state is null || quiz is null:
                viewport.Add(StatusAlert(Localize("NativeSettings_OwnerUnavailable"), palette,
                    error: true, automationId: "native.settings.owner-unavailable"));
                break;
            case NativeSettingsPage.LearningMode:
                viewport.Add(RenderLearningModeSection(palette) ?? Body(Localize("Common_Loading"),
                    palette, secondary: true, modifier: Modifier.TestTag("native.settings.learning-mode.loading")));
                break;
            case NativeSettingsPage.Appearance:
                viewport.Add(RenderAppearanceSection(palette));
                break;
            case NativeSettingsPage.VoiceQuiz:
                viewport.Add(RenderQuizSettingsSection(state, quiz, palette));
                if (state.FeedbackKey is { } feedback)
                    viewport.Add(Body(Localize(feedback), palette, secondary: true,
                        modifier: Modifier.TestTag("native.settings.quiz.feedback")));
                break;
        }
        return viewport;
    }

    // ---- Page selection ----

    /// <summary>
    /// A list row: the page opens. Leaving Profile closes it unless it has edits not saved yet
    /// (then it stays open and shows them again on return; one tap must not lose them). Nothing
    /// changes while an action runs or Profile saves, or when Profile cannot open.
    /// </summary>
    private void SelectSettingsPage(NativeSettingsPage page)
    {
        // Choosing Profile again while it could not open tries again.
        if (Busy.Value || IsSettingsPageBlocked
            || _settingsPage == page && !(page == NativeSettingsPage.Profile && _profile is null))
            return;
        var previous = _settingsPage;
        if (previous == NativeSettingsPage.Profile)
            CloseSettingsProfile();
        _whatsNewPresented = false;
        _moreMenu = null;
        _settingsPage = page;
        _settingsPageAutomatic = false;
        // An untrusted owner still opens the page: it says Profile is unavailable (as before).
        if (page == NativeSettingsPage.Profile)
            OpenSettingsProfile();
    }

    /// <summary>Beside the list with no page open: Profile, or Appearance when Profile cannot open.</summary>
    private void SelectDefaultSettingsPage()
    {
        if (_settingsPage is not null || _tab != ShellTab.Settings || !IsSettingsTwoPane)
            return;
        // The preview has no profile row, so it opens on Appearance.
        SelectSettingsPage(_preview ? NativeSettingsPage.Appearance : NativeSettingsPage.Profile);
        _settingsPageAutomatic = _settingsPage is not null;
    }

    /// <summary>On a phone, Back (or re-tapping the tab) closes the page; nothing while Profile saves.</summary>
    private void CloseSettingsPage() => Publish(CloseSettingsPageNow);

    private void CloseSettingsPageNow()
    {
        if (_settingsPage is null || IsSettingsPageBlocked)
            return;
        if (_settingsPage == NativeSettingsPage.Profile)
            CloseSettingsProfile();
        _whatsNewPresented = false;
        _moreMenu = null;
        _settingsPage = null;
        _settingsPageAutomatic = false;
    }

    /// <summary>System Back on the Settings tab: the innermost layer of the open page first.</summary>
    private bool HandleSettingsBack()
    {
        if (_tab != ShellTab.Settings || Destination != NativeDestination.Dashboard
            || IsMoreScreenOpen || _settingsPage is null)
            return false;
        if (_moreMenu is not null)
        {
            Publish(() => _moreMenu = null);
            return true;
        }
        if (_whatsNewPresented)
        {
            Publish(() => _whatsNewPresented = false);
            return true;
        }
        if (_settingsPage == NativeSettingsPage.Profile
            && _profile?.State.ShowDeleteConfirmation == true)
        {
            _profile.CancelDelete();
            return true;
        }
        // Beside the list Back leaves the tab (as at any tab root); on a phone it closes the page.
        if (IsSettingsTwoPane)
            return false;
        if (!IsSettingsPageBlocked)
            CloseSettingsPage();
        return true;
    }

    /// <summary>Opens the owner's profile for the Profile page; false when the owner cannot be trusted.</summary>
    private bool OpenSettingsProfile()
    {
        if (_profile is { IsOpen: true })
            return true;
        var owner = MoreOwner();
        if (owner is null || _scope is null)
        {
            global::Android.Util.Log.Warn("SentenceStudio.Native",
                "Profile refused an absent or untrusted owner.");
            return false;
        }
        ReleaseSettingsProfile();
        var provider = _scope.ServiceProvider;
        var profile = new ProfileCoordinator(
            provider.GetRequiredService<UserProfileRepository>(),
            provider.GetRequiredService<DeviceUserScopeProvider>(),
            provider.GetRequiredService<ILogger<ProfileCoordinator>>(),
            provider.GetRequiredService<INativeProfileRemoteStore>());
        profile.Changed += OnMoreChanged;
        _profile = profile;
        RunMore(() => profile.OpenAsync(owner, _lifetime.Token));
        return true;
    }

    /// <summary>Leaving Profile: closed unless it holds unsaved edits; the profile row reads it again.</summary>
    private void CloseSettingsProfile()
    {
        if (_profile is not { } profile || profile.HasUnsavedChanges)
            return;
        ReleaseSettingsProfile();
        _ = RefreshProfileSummaryAsync();
    }

    private void ReleaseSettingsProfile()
    {
        if (_profile is not { } profile)
            return;
        _profile = null;
        profile.Changed -= OnMoreChanged;
        profile.Dispose();
    }

    /// <summary>Owner change, sign-out or disposal: no page stays open; the notes may stay (not owner data).</summary>
    private void ReleaseSettingsPages()
    {
        ReleaseSettingsProfile();
        _settingsPage = null;
        _settingsPageAutomatic = false;
        _whatsNewPresented = false;
    }

    // ---- About ----

    /// <summary>The app's version (src/SentenceStudio.Version.props); empty when unreadable. Read once.</summary>
    private string AppVersion => _appVersion ??= ReadAppVersion();
    private string? _appVersion;

    private string ReadAppVersion()
    {
        try
        {
            long noFlags = 0;
            return activity.PackageManager?.GetPackageInfo(activity.PackageName!,
                global::Android.Content.PM.PackageManager.PackageInfoFlags.Of(noFlags))?.VersionName
                ?? string.Empty;
        }
        catch (global::Android.Content.PM.PackageManager.NameNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>About (source Settings.razor About card): the version and What's New.</summary>
    private ComposableNode SettingsAbout(AndroidVisualPalette palette)
    {
        var rows = new List<ComposableNode>
        {
            Body(string.Format(CultureInfo.CurrentCulture, Localize("Settings_VersionLabel"), AppVersion),
                palette, modifier: Modifier.TestTag("native.settings.about.version")),
            Action(Localize("Settings_WhatsNew"), "native.settings.about.whats-new", OpenWhatsNew,
                enabled: !_whatsNewLoading, primary: false),
        };
        if (_whatsNewLoading)
            rows.Add(new LinearProgressIndicator
            {
                Modifier = Modifier.FillMaxWidth().TestTag("native.settings.about.loading"),
            });
        if (_whatsNewNoticeKey is { } notice)
            rows.Add(StatusAlert(Localize(notice), palette,
                error: notice != "Settings_NoReleaseNotes", automationId: "native.settings.about.notice"));
        var column = new Column(Arrangement.SpacedBy(12))
        {
            Modifier.FillMaxWidth(),
            Card(palette, "native.settings.about", rows.ToArray()),
        };
        if (_whatsNewPresented && _whatsNew is { } notes)
            column.Add(WhatsNewSheet(notes, palette));
        return column;
    }

    /// <summary>What's New: reads the notes once, then shows them; says so when there are none.</summary>
    private void OpenWhatsNew() => Publish(() =>
    {
        if (_whatsNewLoading || _scope is null)
            return;
        if (_whatsNew is not null)
        {
            _whatsNewPresented = true;
            return;
        }
        _whatsNewLoading = true;
        _whatsNewNoticeKey = null;
        var provider = _scope.ServiceProvider;
        var version = AppVersion;
        _ = Task.Run(async () =>
        {
            NativeReleaseNotes? notes = null;
            string? notice = null;
            try
            {
                notes = await NativeReleaseNotes.ForAppVersionAsync(
                    provider.GetRequiredService<ReleaseNotesService>(), version);
                if (notes is null)
                    notice = "Settings_NoReleaseNotes";
            }
            catch (Exception ex)
            {
                provider.GetService<ILogger<AndroidSliceShell>>()?.LogWarning(ex,
                    "The release notes could not be read.");
                notice = "NativeSettings_ReleaseNotesFailed";
            }
            Publish(() =>
            {
                _whatsNewLoading = false;
                _whatsNew = notes;
                _whatsNewNoticeKey = notice;
                // Shown only if About is still in front (the person may have moved on).
                _whatsNewPresented = notes is not null && _tab == ShellTab.Settings
                    && _settingsPage == NativeSettingsPage.About;
            });
        });
    });

    /// <summary>
    /// What's New (source WhatsNewModal) as a Material bottom sheet: the release's title, version
    /// and date, each section of the notes as a heading over list items (a bold headline over its
    /// text), and Got it.
    /// </summary>
    private ComposableNode WhatsNewSheet(NativeReleaseNotes notes, AndroidVisualPalette palette)
    {
        var maxHeight = (int)Math.Round((activity.Resources?.Configuration?.ScreenHeightDp ?? 640) * 0.85);
        var date = DateOnly.TryParse(notes.Date, CultureInfo.InvariantCulture, out var day)
            ? day.ToString("D", CultureInfo.CurrentCulture) : notes.Date;
        var sheet = new ModalBottomSheet(onDismissRequest: () => Publish(() => _whatsNewPresented = false))
        {
            Modifier = PopupWindowSemantics(Modifier.TestTag("native.whats-new")),
        };
        var content = new Column(Arrangement.SpacedBy(8))
        {
            Modifier = Modifier.FillMaxWidth().HeightIn(max: maxHeight)
                .VerticalScroll(_whatsNewScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding,
                    vertical: AndroidVisualTheme.CardPadding)
                .TestTag("native.whats-new.content"),
        };
        content.Add(Heading(Localize("WhatsNew_Title"), palette, 22,
            Modifier.Semantics(semantics => semantics.Heading())));
        content.Add(Heading(notes.Title, palette, 18, Modifier.TestTag("native.whats-new.header")));
        content.Add(Body(string.Format(CultureInfo.CurrentCulture, Localize("Settings_VersionLabel"), notes.Version)
            + (date.Length > 0 ? " · " + date : string.Empty), palette, secondary: true));
        foreach (var section in notes.Sections)
        {
            if (section.Title.Length > 0)
                content.Add(Heading(section.Title, palette, 16,
                    Modifier.Padding(top: 8).Semantics(semantics => semantics.Heading())));
            foreach (var item in section.Items)
                content.Add(new ListItem
                {
                    Headline = new Text(item.Headline ?? item.Text)
                    {
                        FontWeight = item.Headline is null ? FontWeight.Normal : FontWeight.SemiBold,
                    },
                    Supporting = item.Headline is null ? null : new Text(item.Text),
                });
        }
        content.Add(Action(Localize("WhatsNew_GotIt"), "native.whats-new.done",
            () => Publish(() => _whatsNewPresented = false), enabled: true));
        sheet.Add(content);
        return sheet;
    }

    // Remembered by the Settings composition (as the quiz details sheet's scroll).
    private ComposeScrollState _whatsNewScroll = null!;

    /// <summary>Settings offers the Learning Coach (a signed-in owner with Sam, not the preview).</summary>
    private bool IsSamSettingsShown => !_preview && _sam is not null && HasDashboardOwner(Auth.UserId);
}
