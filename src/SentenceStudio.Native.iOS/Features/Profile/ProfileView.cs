using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Profile;

internal sealed record ProfileFormActions(
    Func<string, bool> NameChanged,
    Func<string, bool> NativeLanguageChanged,
    Func<string, bool> TargetLanguageChanged,
    Func<string, bool> DisplayLanguageChanged,
    Func<int, bool> SessionMinutesChanged,
    Func<string, bool> TargetCefrLevelChanged,
    Action Retry,
    Action RequestDelete,
    Action Save);

/// <summary>
/// Profile (Captain 2026-11-13, the native form pattern of departure 84): a grouped Form.
/// Personal Information holds Name beside its label and the read-only email as a value row;
/// the languages and the learning preferences are system menu Pickers; Save is the toolbar's
/// action; Change Password and Danger Zone keep their unavailable controls, disabled, with the
/// reason.
/// </summary>
internal static class ProfileView
{
    internal static SwiftView Page(
        ProfileState state, SourceUiStyle style,
        Func<string, string> text, ProfileFormActions actions)
    {
        if (state.Phase != ProfilePhase.Ready)
        {
            var status = state.Phase switch
            {
                ProfilePhase.Missing => "NativeProfile_NotFound",
                ProfilePhase.Blocked => "NativeProfile_OwnerUnavailable",
                ProfilePhase.LoadError => "Profile_LoadFailed",
                _ => "Common_Loading"
            };
            var message = state.Phase == ProfilePhase.Loading
                ? Views.IndeterminateProgress(text(status))
                    .WithAccessibilityIdentifier("native.profile.status")
                : NativeForm.Notice(style, text(status), "native.profile.status", error: true);
            var unavailable = new List<SwiftView> { message };
            if (state.Phase is ProfilePhase.Missing or ProfilePhase.LoadError)
                unavailable.Add(NativeForm.Action(style, Views.Button(
                        text(NativeText.Retry), actions.Retry)
                    .WithAccessibilityIdentifier("native.profile.retry")));
            return Page(style, [Views.Section(string.Empty, unavailable.ToArray())]);
        }

        var draft = state.Draft;
        var editable = !state.IsSaving;
        var sections = new List<SwiftView>();
        if (SaveStatus(state, style, text) is { } saveStatus)
            sections.Add(Views.Section(string.Empty, saveStatus));

        // Save sends only the editable fields; email is shown read-only so an edit
        // is never silently dropped.
        var name = NativeForm.Field(text("Profile_Name"), draft.Name, "native.profile.name",
            next => actions.NameChanged(next), text("Profile_NamePlaceholder"));
        name.IsEnabled = editable;
        sections.Add(Views.Section(text("Profile_PersonalInformation"),
            name,
            NativeForm.Value(text("Profile_Email"), draft.Email, "native.profile.email"),
            style.Text(Views.Text(text("NativeProfile_EmailReadOnly")),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.profile.email.readonly")));

        var languages = NativeOnboardingOptions.NativeLanguages
            .Select(language => new SwiftPickerOption(language, language)).ToArray();
        sections.Add(Views.Section(text("Profile_LanguageSettings"),
            Choice(style, text("Profile_NativeLanguage"), languages,
                draft.NativeLanguage, "native.profile.language.native",
                actions.NativeLanguageChanged, editable),
            Choice(style, text("Profile_TargetLanguage"), languages,
                draft.TargetLanguage, "native.profile.language.target",
                actions.TargetLanguageChanged, editable),
            Choice(style, text("Profile_DisplayLanguage"),
                [
                    new SwiftPickerOption("English",
                        text("Profile_DisplayLanguage_English")),
                    new SwiftPickerOption("Korean",
                        text("Profile_DisplayLanguage_Korean"))
                ], draft.DisplayLanguage, "native.profile.language.display",
                actions.DisplayLanguageChanged, editable)));

        var minutes = NativeOnboardingOptions.SessionMinutes;
        var levels = NativeOnboardingOptions.CefrLevels;
        sections.Add(Views.Section(text("Profile_LearningPreferences"),
            Choice(style, text("Profile_SessionDuration"),
                minutes.Select(minute => new SwiftPickerOption(
                    minute.ToString(CultureInfo.InvariantCulture),
                    minute.ToString(CultureInfo.CurrentCulture))).ToArray(),
                draft.PreferredSessionMinutes.ToString(CultureInfo.InvariantCulture),
                "native.profile.minutes",
                next => int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var minute) && actions.SessionMinutesChanged(minute), editable),
            Choice(style, text("Profile_TargetCEFRLevel"),
                levels.Select(level => new SwiftPickerOption(level, level)).ToArray(),
                draft.TargetCefrLevel, "native.profile.level",
                actions.TargetCefrLevelChanged, editable)));

        // Change Password and Delete My Account are not available on this device yet; their
        // controls stay, disabled, with the reason.
        sections.Add(Views.Section(text("Profile_ChangePassword"),
            PasswordField(text("Profile_CurrentPassword"),
                text("Profile_CurrentPasswordPlaceholder"), "native.profile.password.current"),
            PasswordField(text("Profile_NewPassword"),
                text("Profile_NewPasswordPlaceholder"), "native.profile.password.new"),
            PasswordField(text("Profile_ConfirmNewPassword"),
                text("Profile_ConfirmNewPasswordPlaceholder"), "native.profile.password.confirm"),
            Disabled(NativeForm.Action(style, text("Profile_ChangePassword"), "key",
                static () => { }, "native.profile.password.change")),
            style.Text(Views.Text(text("NativeProfile_AccountActionUnavailable")),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.profile.password.unavailable")));

        sections.Add(Views.Section(text("Profile_DangerZone"),
            style.Text(Views.Text(text("Profile_DangerZoneDescription")),
                SourceTextRole.Body2, SourceTextTone.Secondary),
            Views.Button(Views.Label(text("Profile_DeleteMyAccount"), "trash"),
                    actions.RequestDelete)
                .WithButtonRole(SwiftButtonRole.Destructive)
                // Explicit: the Form's text ink would otherwise replace the role's red.
                .WithTintColor(style.Appearance.Danger.Light, style.Appearance.Danger.Dark)
                .WithForegroundColor(style.Appearance.Danger.Light, style.Appearance.Danger.Dark)
                .WithAccessibilityIdentifier("native.profile.delete.request"),
            style.Text(Views.Text(text("NativeProfile_AccountActionUnavailable")),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.profile.delete.unavailable")));

        // Captain 2026-11-10 (every editor): one Save, the toolbar's action, as plain text.
        var save = Views.Button(text("Common_Save"), actions.Save)
            .WithAccessibilityLabel(text("Profile_SaveProfile"))
            .WithAccessibilityIdentifier("native.profile.save");
        save.IsEnabled = state.CanSave;
        return Views.Toolbar(Page(style, sections), save);
    }

    /// <summary>The last save attempt: progress, the source "Profile saved" success, or an honest error.</summary>
    private static SwiftView? SaveStatus(
        ProfileState state, SourceUiStyle style, Func<string, string> text) =>
        state.SaveStatus switch
        {
            ProfileSaveStatus.Saving => Views.IndeterminateProgress(text("NativeProfile_Saving"))
                .WithAccessibilityIdentifier("native.profile.save.status"),
            ProfileSaveStatus.Saved or ProfileSaveStatus.Failed
                when state.SaveMessageKey is { } key => NativeForm.Notice(style, text(key),
                    "native.profile.save.status",
                    error: state.SaveStatus == ProfileSaveStatus.Failed),
            _ => null
        };

    private static SwiftView Page(SourceUiStyle style, IEnumerable<SwiftView> sections) =>
        NativeForm.Page(style, sections, "native.profile.scroll", "native.profile.screen");

    /// <summary>A choice row (NativeForm.Choice); disabled while a save runs.</summary>
    private static SwiftView Choice(
        SourceUiStyle style, string label, SwiftPickerOption[] options,
        string selected, string id, Func<string, bool> changed, bool enabled)
    {
        var picker = NativeForm.Choice(style, label, options, selected, id, changed);
        picker.IsEnabled = enabled;
        return picker;
    }

    /// <summary>An unavailable password field: its label beside the empty, disabled field.</summary>
    private static SwiftView PasswordField(string label, string placeholder, string id) =>
        Disabled(NativeForm.Field(label, string.Empty, id, static _ => { }, placeholder,
            secure: true));

    private static SwiftView Disabled(SwiftView view)
    {
        view.IsEnabled = false;
        return view;
    }
}
