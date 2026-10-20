using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Profile;

internal sealed record ProfileFormActions(
    Func<string, bool> NameChanged,
    Func<string, bool> EmailChanged,
    Func<string, bool> NativeLanguageChanged,
    Func<string, bool> TargetLanguageChanged,
    Func<string, bool> DisplayLanguageChanged,
    Func<int, bool> SessionMinutesChanged,
    Func<string, bool> TargetCefrLevelChanged,
    Action Retry,
    Action RequestDelete);

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
                : style.Alert(style.Text(Views.Text(text(status)),
                    SourceTextRole.Body2), success: false);
            message.WithAccessibilityIdentifier("native.profile.status");
            var unavailable = new List<SwiftView> { message };
            if (state.Phase is ProfilePhase.Missing or ProfilePhase.LoadError)
                unavailable.Add(style.SecondaryButton(
                        text(NativeText.Retry), actions.Retry)
                    .WithAccessibilityIdentifier("native.profile.retry"));
            return Page(style, unavailable);
        }

        var draft = state.Draft;
        var personal = Card(style, "native.profile.personal", [
            Heading(style, text("Profile_PersonalInformation")),
            Field(style, text("Profile_Name"), text("Profile_NamePlaceholder"),
                draft.Name, "native.profile.name", actions.NameChanged),
            Field(style, text("Profile_Email"), text("Profile_EmailPlaceholder"),
                draft.Email, "native.profile.email", actions.EmailChanged)
        ]);

        var languages = NativeOnboardingOptions.NativeLanguages
            .Select(language => new SwiftPickerOption(language, language)).ToArray();
        var settings = Card(style, "native.profile.languages", [
            Heading(style, text("Profile_LanguageSettings")),
            Choice(style, text("Profile_NativeLanguage"), languages,
                draft.NativeLanguage, "native.profile.language.native",
                actions.NativeLanguageChanged),
            Choice(style, text("Profile_TargetLanguage"), languages,
                draft.TargetLanguage, "native.profile.language.target",
                actions.TargetLanguageChanged),
            Choice(style, text("Profile_DisplayLanguage"),
                [
                    new SwiftPickerOption("English",
                        text("Profile_DisplayLanguage_English")),
                    new SwiftPickerOption("Korean",
                        text("Profile_DisplayLanguage_Korean"))
                ], draft.DisplayLanguage, "native.profile.language.display",
                actions.DisplayLanguageChanged)
        ]);

        var preferences = Card(style, "native.profile.preferences", [
            Heading(style, text("Profile_LearningPreferences")),
            Preference(style, text("Profile_SessionDuration"),
                NativeOnboardingOptions.SessionMinutes,
                draft.PreferredSessionMinutes,
                minute => minute.ToString(CultureInfo.CurrentCulture),
                minute => $"native.profile.minutes.{minute}",
                actions.SessionMinutesChanged),
            Preference(style, text("Profile_TargetCEFRLevel"),
                NativeOnboardingOptions.CefrLevels,
                draft.TargetCefrLevel, level => level,
                level => $"native.profile.level.{level}",
                actions.TargetCefrLevelChanged)
        ]);

        var save = style.PrimaryButton(text("Profile_SaveProfile"),
                static () => { }, fullWidth: true)
            .WithAccessibilityIdentifier("native.profile.save");
        SourceUiStyle.SetCustomButtonAvailability(save, style.Appearance,
            SourceButtonRole.Primary, enabled: false);

        var password = Card(style, "native.profile.password", [
            Views.HStack(
                Views.Image("key"),
                Heading(style, text("Profile_ChangePassword"))),
            PasswordField(style, text("Profile_CurrentPassword"),
                text("Profile_CurrentPasswordPlaceholder"),
                "native.profile.password.current"),
            PasswordField(style, text("Profile_NewPassword"),
                text("Profile_NewPasswordPlaceholder"),
                "native.profile.password.new"),
            PasswordField(style, text("Profile_ConfirmNewPassword"),
                text("Profile_ConfirmNewPasswordPlaceholder"),
                "native.profile.password.confirm"),
            DisabledPasswordAction(style, text),
            style.Text(Views.Text(text("NativeProfile_AccountActionUnavailable")),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.profile.password.unavailable")
        ]);

        var danger = DangerCard(style, text, actions.RequestDelete);
        return Page(style, [
            personal, settings, preferences,
            save,
            style.Text(Views.Text(text("NativeProfile_SaveUnavailable")),
                    SourceTextRole.Caption1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.profile.save.unavailable"),
            password, danger
        ]);
    }

    private static SwiftView Page(SourceUiStyle style, IEnumerable<SwiftView> sections)
    {
        var content = Views.VStack(sections.ToArray())
            .WithPadding(SourceUiLayout.PageGutter(style.ViewportWidth),
                SourceUiLayout.PageGutter(style.ViewportWidth),
                48, SourceUiLayout.PageGutter(style.ViewportWidth))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.profile.scroll.content");
        content.Spacing = SourceUiLayout.FormGap;
        return style.Page(Views.Scroll(content)
                .WithAccessibilityIdentifier("native.profile.scroll"))
            .WithAccessibilityIdentifier("native.profile.screen");
    }

    private static SwiftView Heading(SourceUiStyle style, string title) =>
        style.Text(Views.Text(title), SourceTextRole.Title3, sourceHeading: true);

    private static SwiftView Card(
        SourceUiStyle style, string id, IReadOnlyList<SwiftView> children)
    {
        var content = Views.VStack(children.ToArray())
            .WithPadding(SourceUiLayout.LargeCardInset)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(id);
        content.Spacing = SourceUiLayout.FormGap;
        return style.Card(content);
    }

    private static SwiftView Field(
        SourceUiStyle style, string label, string placeholder,
        string value, string id, Func<string, bool> changed)
    {
        var input = style.TextInput(Views.TextField(placeholder, value,
                next => changed(next)))
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(id);
        var field = Views.VStack(
            style.Text(Views.Text(label), SourceTextRole.Body2,
                SourceTextTone.Secondary), input);
        field.Spacing = SourceUiLayout.ItemGap;
        return field.WithFlexibleWidth();
    }

    private static SwiftView Choice(
        SourceUiStyle style, string label, SwiftPickerOption[] options,
        string selected, string id, Func<string, bool> changed)
    {
        var picker = style.Input(Views.Picker(
                    string.Empty, options, selected, next => changed(next))
                .WithPickerStyle(SwiftPickerStyle.Menu)
                .WithAccessibilityLabel(label)
                .WithFlexibleWidth())
            .WithAccessibilityIdentifier(id);
        var field = Views.VStack(
            style.Text(Views.Text(label), SourceTextRole.Body2,
                SourceTextTone.Secondary), picker);
        field.Spacing = SourceUiLayout.ItemGap;
        return field.WithFlexibleWidth();
    }

    private static SwiftView Preference<T>(
        SourceUiStyle style, string label, IReadOnlyList<T> options,
        T selected, Func<T, string> display, Func<T, string> id,
        Func<T, bool> changed) where T : notnull
    {
        var phone = style.ViewportWidth < 768;
        var buttons = new SwiftView[options.Count];
        for (var index = 0; index < options.Count; index++)
        {
            var value = options[index];
            var isSelected = EqualityComparer<T>.Default.Equals(selected, value);
            buttons[index] = (isSelected
                    ? style.PrimaryButton(display(value), () => Select(value),
                        fullWidth: phone)
                    : style.SecondaryButton(display(value), () => Select(value),
                        fullWidth: phone))
                .WithAccessibilitySelected(isSelected)
                .WithAccessibilityIdentifier(id(value));
        }

        void Select(T value)
        {
            if (!changed(value))
                return;
            for (var index = 0; index < buttons.Length; index++)
            {
                var active = EqualityComparer<T>.Default.Equals(options[index], value);
                SourceUiStyle.SetCustomButtonAvailability(
                    buttons[index], style.Appearance,
                    active ? SourceButtonRole.Primary : SourceButtonRole.FilledSecondary,
                    enabled: true);
                buttons[index].WithAccessibilitySelected(active);
            }
        }

        var choices = phone ? Views.VStack(buttons) : Views.HStack(buttons);
        choices.Spacing = SourceUiLayout.ItemGap;
        choices.WithFlexibleWidth();
        var field = Views.VStack(
            style.Text(Views.Text(label), SourceTextRole.Body2,
                SourceTextTone.Secondary), choices);
        field.Spacing = SourceUiLayout.ItemGap;
        return field.WithFlexibleWidth();
    }

    private static SwiftView PasswordField(
        SourceUiStyle style, string label, string placeholder, string id)
    {
        var input = style.TextInput(Views.SecureField(placeholder))
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(id);
        input.IsEnabled = false;
        var field = Views.VStack(
            style.Text(Views.Text(label), SourceTextRole.Body2,
                SourceTextTone.Secondary), input);
        field.Spacing = SourceUiLayout.ItemGap;
        return field.WithFlexibleWidth();
    }

    private static SwiftView DisabledPasswordAction(
        SourceUiStyle style, Func<string, string> text)
    {
        var change = style.PrimaryButton(text("Profile_ChangePassword"),
                static () => { }, fullWidth: true)
            .WithAccessibilityIdentifier("native.profile.password.change");
        SourceUiStyle.SetCustomButtonAvailability(change, style.Appearance,
            SourceButtonRole.Primary, enabled: false);
        return change;
    }

    private static SwiftView DangerCard(
        SourceUiStyle style, Func<string, string> text, Action requestDelete)
    {
        var danger = style.Appearance.Danger;
        var label = style.Text(Views.Text(text("Profile_DeleteMyAccount")),
                SourceTextRole.Button)
            .WithForegroundColor("#FFFFFF", "#FFFFFF")
            .WithPadding(10, 16, 10, 16)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight);
        var delete = Views.Button(label, requestDelete)
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithButtonAppearance(danger.Light, danger.Dark,
                danger.Light, danger.Dark, style.Appearance.BootstrapRadius)
            .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.profile.delete.request");
        var content = Views.VStack(
                style.Text(Views.Text(text("Profile_DangerZone")),
                        SourceTextRole.Title3, SourceTextTone.Danger,
                        sourceHeading: true),
                style.Text(Views.Text(text("Profile_DangerZoneDescription")),
                    SourceTextRole.Body2, SourceTextTone.Secondary),
                delete,
                style.Text(Views.Text(text("NativeProfile_AccountActionUnavailable")),
                        SourceTextRole.Caption1, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier("native.profile.delete.unavailable"))
            .WithPadding(SourceUiLayout.LargeCardInset)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier("native.profile.danger");
        content.Spacing = SourceUiLayout.FormGap;
        return content.WithRoundedSurface(style.Appearance.CardSurface.Light,
            style.Appearance.CardSurface.Dark, style.Appearance.BootstrapRadius,
            danger.Light, danger.Dark, 1);
    }
}
