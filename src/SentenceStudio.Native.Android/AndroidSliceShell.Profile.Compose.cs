using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.UI.Text.Input;
using SentenceStudio.Native.Features.Profile;
using ComposeButton = AndroidX.Compose.Button;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Profile (source Profile.razor, iOS ProfileView) on the shared ProfileCoordinator.
/// As on iOS, edits are drafts: Save, password change and account deletion stay
/// disabled with their explanation until a safe server update exists, so nothing
/// here changes the account.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private ComposableNode RenderProfile(AndroidVisualPalette palette) =>
        new Composed(composer =>
        {
            var scroll = composer.Remember(() => new AndroidX.Compose.ScrollState());
            return ProfilePage(palette, scroll);
        });

    private ComposableNode ProfilePage(AndroidVisualPalette palette, AndroidX.Compose.ScrollState scroll)
    {
        var viewport = new Column(Arrangement.SpacedBy(16))
        {
            Modifier = Modifier.FillMaxSize().VerticalScroll(scroll)
                .Padding(AndroidVisualTheme.PagePadding, AndroidVisualTheme.PagePadding,
                    AndroidVisualTheme.PagePadding, 48)
                .TestTag("native.profile.scroll"),
        };
        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.profile.screen"),
            ScreenHeader(Localize("Profile_PageTitle"), palette, back: true,
                backAutomationId: "native.profile.back"),
            viewport,
        };
        var profile = _profile;
        var state = profile?.State;
        if (profile is null || state is null || state.Phase != ProfilePhase.Ready)
        {
            var phase = profile is null ? ProfilePhase.Blocked : state!.Phase;
            if (phase is ProfilePhase.Loading or ProfilePhase.Closed)
                viewport.Add(new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
                {
                    Modifier.FillMaxWidth().TestTag("native.profile.status"),
                    new CircularProgressIndicator { Modifier = Modifier.Size(24) },
                    Body(Localize("Common_Loading"), palette, secondary: true),
                });
            else
                viewport.Add(StatusAlert(Localize(phase switch
                {
                    ProfilePhase.Missing => "NativeProfile_NotFound",
                    ProfilePhase.LoadError => "Profile_LoadFailed",
                    _ => "NativeProfile_OwnerUnavailable",
                }), palette, error: true, automationId: "native.profile.status"));
            if (phase is ProfilePhase.Missing or ProfilePhase.LoadError && profile is not null)
                viewport.Add(Action(Localize(NativeText.Retry), "native.profile.retry",
                    () => RunMore(() => profile.ReloadAsync(_lifetime.Token)), true,
                    primary: false));
            return page;
        }

        var draft = state.Draft;
        viewport.Add(Card(palette, "native.profile.personal",
            ProfileHeading(Localize("Profile_PersonalInformation"), palette),
            ProfileField(Localize("Profile_Name"), Localize("Profile_NamePlaceholder"),
                draft.Name, "native.profile.name", profile.SetName),
            ProfileField(Localize("Profile_Email"), Localize("Profile_EmailPlaceholder"),
                draft.Email, "native.profile.email", profile.SetEmail)));

        var languages = NativeOnboardingOptions.NativeLanguages
            .Select(language => (language, language)).ToArray();
        viewport.Add(Card(palette, "native.profile.languages",
            ProfileHeading(Localize("Profile_LanguageSettings"), palette),
            MoreDropdown("profile.native", "native.profile.language.native",
                Localize("Profile_NativeLanguage"), languages, draft.NativeLanguage,
                value => ProfileEdit(profile.SetNativeLanguage(value)), true, palette),
            MoreDropdown("profile.target", "native.profile.language.target",
                Localize("Profile_TargetLanguage"), languages, draft.TargetLanguage,
                value => ProfileEdit(profile.SetTargetLanguage(value)), true, palette),
            MoreDropdown("profile.display", "native.profile.language.display",
                Localize("Profile_DisplayLanguage"),
                [
                    ("English", Localize("Profile_DisplayLanguage_English")),
                    ("Korean", Localize("Profile_DisplayLanguage_Korean")),
                ], draft.DisplayLanguage,
                value => ProfileEdit(profile.SetDisplayLanguage(value)), true, palette)));

        viewport.Add(Card(palette, "native.profile.preferences",
            ProfileHeading(Localize("Profile_LearningPreferences"), palette),
            ProfileChoices(Localize("Profile_SessionDuration"),
                NativeOnboardingOptions.SessionMinutes, draft.PreferredSessionMinutes,
                minute => minute.ToString(CultureInfo.CurrentCulture),
                minute => $"native.profile.minutes.{minute}",
                minute => ProfileEdit(profile.SetSessionMinutes(minute)), palette),
            ProfileChoices(Localize("Profile_TargetCEFRLevel"),
                NativeOnboardingOptions.CefrLevels, draft.TargetCefrLevel,
                level => level, level => $"native.profile.level.{level}",
                level => ProfileEdit(profile.SetTargetCefrLevel(level)), palette)));

        viewport.Add(Action(Localize("Profile_SaveProfile"), "native.profile.save",
            static () => { }, enabled: false));
        viewport.Add(Caption(Localize("NativeProfile_SaveUnavailable"), palette,
            "native.profile.save.unavailable"));

        viewport.Add(Card(palette, "native.profile.password",
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                new Icon(Icons.Default.Lock, Localize("Profile_ChangePassword"))
                {
                    Modifier = Modifier.Size(22),
                    Tint = palette.Text,
                },
                ProfileHeading(Localize("Profile_ChangePassword"), palette),
            },
            DisabledPasswordField(Localize("Profile_CurrentPassword"),
                Localize("Profile_CurrentPasswordPlaceholder"), "native.profile.password.current"),
            DisabledPasswordField(Localize("Profile_NewPassword"),
                Localize("Profile_NewPasswordPlaceholder"), "native.profile.password.new"),
            DisabledPasswordField(Localize("Profile_ConfirmNewPassword"),
                Localize("Profile_ConfirmNewPasswordPlaceholder"), "native.profile.password.confirm"),
            Action(Localize("Profile_ChangePassword"), "native.profile.password.change",
                static () => { }, enabled: false),
            Caption(Localize("NativeProfile_AccountActionUnavailable"), palette,
                "native.profile.password.unavailable")));

        viewport.Add(ProfileDangerCard(profile, palette));
        if (state.ShowDeleteConfirmation)
            page.Add(ProfileDeleteDialog(profile));
        return page;
    }

    private void ProfileEdit(bool applied)
    {
        if (applied)
            RenderMoreNow();
    }

    private static ComposableNode ProfileHeading(string title, AndroidVisualPalette palette) =>
        Heading(title, palette, 20, Modifier.Semantics(semantics => semantics.Heading()));

    private ComposableNode ProfileField(string label, string placeholder, string value,
        string tag, Func<string, bool> changed) =>
        new OutlinedTextField(value, text => ProfileEdit(changed(text)), singleLine: true)
        {
            Modifier = Modifier.FillMaxWidth().TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(label)),
            Label = new Text(label),
            Placeholder = new Text(placeholder),
        };

    private static ComposableNode DisabledPasswordField(string label, string placeholder, string tag) =>
        new OutlinedTextField(string.Empty, static _ => { }, enabled: false, singleLine: true)
        {
            Modifier = Modifier.FillMaxWidth().TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(label)),
            Label = new Text(label),
            Placeholder = new Text(placeholder),
            VisualTransformation = new PasswordVisualTransformation('•'),
        };

    /// <summary>Source button group: the selected option is primary, the rest secondary; phones stack.</summary>
    private ComposableNode ProfileChoices<T>(string label, IReadOnlyList<T> options, T selected,
        Func<T, string> display, Func<T, string> tag, Action<T> changed,
        AndroidVisualPalette palette) where T : notnull
    {
        var phone = WindowWidthDp < 768;
        var buttons = new List<ComposableNode>();
        foreach (var option in options)
        {
            var value = option;
            var isSelected = EqualityComparer<T>.Default.Equals(selected, value);
            var text = display(value);
            var modifier = (phone ? Modifier.FillMaxWidth() : Modifier.Weight(1f))
                .HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(tag(value))
                .Semantics(semantics =>
                {
                    semantics.ContentDescription($"{label}: {text}");
                    semantics.Selected(isSelected);
                });
            if (isSelected)
            {
                var primary = new ComposeButton(() => changed(value), enabled: true)
                {
                    Modifier = modifier,
                    Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                };
                primary.Add(new Text(text));
                buttons.Add(primary);
            }
            else
            {
                var secondary = new OutlinedButton(() => changed(value), enabled: true)
                {
                    Modifier = modifier,
                    Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                };
                secondary.Add(new Text(text));
                buttons.Add(secondary);
            }
        }
        ComposableNode group;
        if (phone)
        {
            var column = new Column(Arrangement.SpacedBy(8)) { Modifier.FillMaxWidth() };
            foreach (var button in buttons)
                column.Add(button);
            group = column;
        }
        else
        {
            var row = new Row(Arrangement.SpacedBy(8)) { Modifier.FillMaxWidth() };
            foreach (var button in buttons)
                row.Add(button);
            group = row;
        }
        return new Column(Arrangement.SpacedBy(6))
        {
            Modifier.FillMaxWidth(),
            Caption(label, palette),
            group,
        };
    }

    private ComposableNode ProfileDangerCard(ProfileCoordinator profile, AndroidVisualPalette palette)
    {
        var title = Heading(Localize("Profile_DangerZone"), palette, 20,
            Modifier.Semantics(semantics => semantics.Heading()));
        title.Color = palette.Danger;
        var delete = DangerAction(Localize("Profile_DeleteMyAccount"),
            "native.profile.delete.request", profile.RequestDelete, true, palette);
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().Padding(bottom: 12)
                .Border(1, palette.Danger, AndroidVisualTheme.CardRadius)
                .TestTag("native.profile.danger"),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        surface.Add(new Column(Arrangement.SpacedBy(10))
        {
            Modifier.FillMaxWidth().Padding(AndroidVisualTheme.CardPadding),
            title,
            Body(Localize("Profile_DangerZoneDescription"), palette, secondary: true),
            delete,
            Caption(Localize("NativeProfile_AccountActionUnavailable"), palette,
                "native.profile.delete.unavailable"),
        });
        return surface;
    }

    /// <summary>Source btn-danger: a full-width destructive button (6.5:1 label contrast).</summary>
    private static ComposableNode DangerAction(string label, string tag, Action command,
        bool enabled, AndroidVisualPalette palette) =>
        new Composed(composer =>
        {
            var button = new ComposeButton(command, enabled: enabled)
            {
                Modifier = Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .TestTag(tag).Semantics(semantics => semantics.ContentDescription(label)),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
                Colors = composer.ButtonColors(
                    containerColor: palette.DangerContainer,
                    contentColor: palette.OnDanger,
                    disabledContainerColor: palette.Border,
                    disabledContentColor: palette.SecondaryText),
            };
            button.Add(new Text(label));
            return button;
        });

    /// <summary>Source confirmation modal; Delete stays disabled (no account deletion here).</summary>
    private ComposableNode ProfileDeleteDialog(ProfileCoordinator profile)
    {
        var confirm = new TextButton(static () => { }, enabled: false)
        {
            Modifier = Modifier.TestTag("native.profile.delete.confirm"),
        };
        confirm.Add(new Text(Localize("Profile_Delete")));
        var cancel = new TextButton(profile.CancelDelete, enabled: true)
        {
            Modifier = Modifier.TestTag("native.profile.delete.cancel"),
        };
        cancel.Add(new Text(Localize("Profile_Cancel")));
        return new AndroidX.Compose.AlertDialog(profile.CancelDelete)
        {
            Modifier = PopupWindowSemantics(Modifier.TestTag("native.profile.delete.confirmation")),
            Title = new Text(Localize("Profile_ConfirmDelete")),
            Text = new Text(Localize("Profile_DeleteConfirmationMessage")),
            ConfirmButton = confirm,
            DismissButton = cancel,
        };
    }
}
