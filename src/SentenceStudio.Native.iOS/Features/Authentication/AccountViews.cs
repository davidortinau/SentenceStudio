using Microsoft.SwiftUI;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Authentication;

internal sealed record RegisterInputs(
    string DisplayName, string Email, string Password, string ConfirmPassword,
    bool ShowPassword, bool ShowConfirmPassword, bool Complete, string? ErrorKey);

internal sealed record RegisterActions(
    Action<string> DisplayNameChanged,
    Action<string> EmailChanged,
    Action<string> PasswordChanged,
    Action<string> ConfirmPasswordChanged,
    Action TogglePassword,
    Action ToggleConfirmPassword,
    Action Submit,
    Action BackToSignIn,
    Action<SwiftView, SwiftView, SwiftView> RegisterLive);

/// <summary>Source RegisterPage, ForgotPasswordPage and MainLayout sync overlay.</summary>
internal static class AccountViews
{
    internal static bool IsPasswordValid(string password) =>
        SentenceStudio.Contracts.Auth.PasswordPolicy.IsSatisfied(password);

    internal static bool CanRegister(RegisterInputs inputs, bool busy) =>
        !busy && !string.IsNullOrWhiteSpace(inputs.DisplayName)
        && !string.IsNullOrWhiteSpace(inputs.Email)
        && IsPasswordValid(inputs.Password)
        && inputs.Password == inputs.ConfirmPassword;

    internal static SwiftView Register(
        SourceUiStyle style, RegisterInputs inputs, bool busy,
        Func<string, string> text, RegisterActions actions)
    {
        var content = new List<SwiftView> { SignInView.Brand(style) };
        var form = new List<SwiftView> { Heading(style, text("Register_CreateAccount")) };
        if (inputs.Complete)
        {
            form.Add(Views.Image("envelope.badge")
                .WithFixedSymbolSize(48)
                .WithForegroundColor(style.Appearance.SuccessAlertText.Light,
                    style.Appearance.SuccessAlertText.Dark)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center));
            form.Add(style.Text(Views.Text(text("Register_CheckYourEmail")),
                    SourceTextRole.Title2, SourceTextTone.PlainCard, sourceHeading: true)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithAccessibilityIdentifier("native.register.complete"));
            form.Add(style.Text(Views.Text(string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        text("Register_VerificationSent"), inputs.Email)),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center));
            form.Add(style.BootstrapLoginButton(text("Register_GoToSignIn"),
                    actions.BackToSignIn, enabled: true)
                .WithAccessibilityIdentifier("native.register.go-to-sign-in"));
            content.Add(SignInView.Card(style, form));
            return SignInView.AuthPage(style, content, "native.register.screen");
        }

        if (inputs.ErrorKey is { } errorKey)
            form.Add(style.Alert(style.Text(Views.Text(text(errorKey)),
                    SourceTextRole.DefaultBody), success: false)
                .WithAccessibilityIdentifier("native.register.error"));
        form.Add(Field(style, text("Register_DisplayName"),
            Input(style, Views.TextField(text("Register_YourName"),
                inputs.DisplayName, actions.DisplayNameChanged),
                "native.register.display-name", busy)));
        form.Add(Field(style, text(NativeText.Email),
            Input(style, Views.TextField(text("Common_EmailPlaceholder"),
                inputs.Email, actions.EmailChanged),
                "native.register.email", busy)));

        var hints = Views.VStack();
        hints.Spacing = 2;
        FillPasswordHints(style, hints, inputs.Password, text);
        form.Add(Field(style, text(NativeText.Password),
            SecretRow(style, text("Common_PasswordPlaceholder"), inputs.Password,
                inputs.ShowPassword, actions.PasswordChanged, actions.TogglePassword,
                "native.register.password", busy, text),
            hints));
        var mismatch = Views.VStack();
        FillMismatch(style, mismatch, inputs, text);
        form.Add(Field(style, text("Register_ConfirmPassword"),
            SecretRow(style, text("Register_ConfirmPasswordPlaceholder"),
                inputs.ConfirmPassword, inputs.ShowConfirmPassword,
                actions.ConfirmPasswordChanged, actions.ToggleConfirmPassword,
                "native.register.confirm-password", busy, text),
            mismatch));

        var submit = style.BootstrapLoginButton(
                text(busy ? "Register_CreatingAccount" : "Register_CreateAccount"),
                actions.Submit, CanRegister(inputs, busy))
            .WithAccessibilityIdentifier("native.register.submit");
        form.Add(submit);
        form.Add(Views.Divider());
        var back = SignInView.Link(style, text(NativeText.SignIn), actions.BackToSignIn,
            "native.register.sign-in");
        back.IsEnabled = !busy;
        var backRow = Views.HStack(
            style.Text(Views.Text(text("Register_AlreadyHaveAccount")),
                SourceTextRole.DefaultBody, SourceTextTone.PlainCard),
            back);
        backRow.Spacing = 4;
        form.Add(backRow.WithAdaptiveStack(320)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center));
        content.Add(SignInView.Card(style, form));
        // Typing must not rebuild the fields (that would drop focus), so the
        // hint/mismatch containers and the submit button update in place.
        actions.RegisterLive(submit, hints, mismatch);
        return SignInView.AuthPage(style, content, "native.register.screen");
    }

    internal static void UpdateRegisterLive(
        SourceUiStyle style, SwiftView submit, SwiftView hints, SwiftView mismatch,
        RegisterInputs inputs, bool busy, Func<string, string> text)
    {
        SourceUiStyle.SetBootstrapLoginAvailability(submit, style.Appearance,
            CanRegister(inputs, busy));
        FillPasswordHints(style, hints, inputs.Password, text);
        FillMismatch(style, mismatch, inputs, text);
    }

    private static void FillPasswordHints(
        SourceUiStyle style, SwiftView hints, string password, Func<string, string> text)
    {
        hints.Clear();
        if (string.IsNullOrEmpty(password))
            return;
        foreach (var rule in SentenceStudio.Contracts.Auth.PasswordPolicy.Rules)
            hints.Add(Rule(style,
                SentenceStudio.Contracts.Auth.PasswordPolicy.IsMet(rule, password),
                text(SentenceStudio.Contracts.Auth.PasswordPolicy.LabelKey(rule)),
                "native.register.rule."
                + SentenceStudio.Contracts.Auth.PasswordPolicy.AutomationSuffix(rule)));
    }

    private static void FillMismatch(
        SourceUiStyle style, SwiftView slot, RegisterInputs inputs, Func<string, string> text)
    {
        slot.Clear();
        if (!string.IsNullOrEmpty(inputs.Password)
            && !string.IsNullOrEmpty(inputs.ConfirmPassword)
            && inputs.Password != inputs.ConfirmPassword)
            slot.Add(Views.HStack(
                    Views.Image("exclamationmark.triangle"),
                    style.Text(Views.Text(text("Register_PasswordsDoNotMatch")),
                        SourceTextRole.Caption1, SourceTextTone.Danger))
                .WithForegroundColor(style.Appearance.DangerAlertText.Light,
                    style.Appearance.DangerAlertText.Dark)
                .WithAccessibilityIdentifier("native.register.mismatch"));
    }

    private static SwiftView Rule(
        SourceUiStyle style, bool met, string label, string id)
    {
        var color = met ? style.Appearance.SuccessAlertText : style.Appearance.DangerAlertText;
        var row = Views.HStack(
                Views.Image(met ? "checkmark.circle" : "xmark.circle"),
                style.Text(Views.Text(label), SourceTextRole.Caption1,
                    met ? SourceTextTone.Success : SourceTextTone.Danger))
            .WithForegroundColor(color.Light, color.Dark)
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(id);
        row.Spacing = 4;
        return row;
    }

    internal static SwiftView ForgotPassword(
        SourceUiStyle style, string email, bool sent, string? errorKey, bool busy,
        Func<string, string> text, Action<string> emailChanged, Action submit,
        Action backToSignIn, Action<SwiftView> registerSubmit)
    {
        var content = new List<SwiftView> { SignInView.Brand(style) };
        var form = new List<SwiftView> { Heading(style, text("ForgotPassword_Title")) };
        if (sent)
        {
            form.Add(style.Alert(style.Text(Views.Text(text("ForgotPassword_ResetSent")),
                    SourceTextRole.DefaultBody), success: true)
                .WithAccessibilityIdentifier("native.forgot.sent"));
            form.Add(style.BootstrapLoginButton(text("ForgotPassword_BackToSignIn"),
                    backToSignIn, enabled: true)
                .WithAccessibilityIdentifier("native.forgot.back"));
            content.Add(SignInView.Card(style, form));
            return SignInView.AuthPage(style, content, "native.forgot.screen");
        }

        if (errorKey is not null)
            form.Add(style.Alert(style.Text(Views.Text(text(errorKey)),
                    SourceTextRole.DefaultBody), success: false)
                .WithAccessibilityIdentifier("native.forgot.error"));
        form.Add(style.Text(Views.Text(text("ForgotPassword_Instructions")),
            SourceTextRole.DefaultBody, SourceTextTone.Secondary));
        form.Add(Field(style, text(NativeText.Email),
            Input(style, Views.TextField(text("Common_EmailPlaceholder"),
                email, emailChanged), "native.forgot.email", busy)));
        var send = style.BootstrapLoginButton(
                text(busy ? "ForgotPassword_Sending" : "ForgotPassword_SendResetLink"),
                submit, !busy && !string.IsNullOrWhiteSpace(email))
            .WithAccessibilityIdentifier("native.forgot.submit");
        registerSubmit(send);
        form.Add(send);
        form.Add(Views.Divider());
        var back = SignInView.Link(style, text("ForgotPassword_BackToSignIn"),
            backToSignIn, "native.forgot.back");
        back.IsEnabled = !busy;
        form.Add(back.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center));
        content.Add(SignInView.Card(style, form));
        return SignInView.AuthPage(style, content, "native.forgot.screen");
    }

    /// <summary>
    /// Source MainLayout masks the shell with a sync overlay while the post-login
    /// route waits for the owner's initial sync. A pending (offline/unconfirmed)
    /// result keeps the owner signed in with an honest retry and sign-out.
    /// </summary>
    internal static SwiftView Readiness(
        SourceUiStyle style, NativeAuthState state, bool loading, bool busy,
        Func<string, string> text, Action retry, Action signOut)
    {
        var items = new List<SwiftView>();
        if (loading)
        {
            items.Add(Views.IndeterminateProgress(text("MainLayout_Syncing"))
                .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                .WithAccessibilityIdentifier("native.auth.sync.progress"));
            items.Add(style.Text(Views.Text(text("MainLayout_SyncingProfile")),
                    SourceTextRole.Title2, SourceTextTone.PlainCard, sourceHeading: true)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center));
            items.Add(style.Text(Views.Text(
                    $"{text("MainLayout_SyncingDescription")}\n{text("MainLayout_ThisMayTakeAMoment")}"),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center));
        }
        else
        {
            items.Add(Heading(style, text("MainLayout_SyncingProfile")));
            items.Add(style.Alert(style.Text(Views.Text(
                        text(state.ErrorKey ?? NativeText.ConnectionError)),
                    SourceTextRole.DefaultBody), success: false)
                .WithAccessibilityIdentifier(NativeAccess.AuthError));
            var again = style.BootstrapLoginButton(text(NativeText.Retry), retry, !busy)
                .WithAccessibilityIdentifier("native.auth.sync.retry");
            items.Add(again);
            var leave = style.SecondaryButton(text("Nav_Logout"), signOut, fullWidth: true)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier("native.auth.sync.signout");
            leave.IsEnabled = !busy;
            items.Add(leave);
        }
        var content = new List<SwiftView> { SignInView.Brand(style), SignInView.Card(style, items) };
        return SignInView.AuthPage(style, content, "native.auth.sync.screen");
    }

    private static SwiftView Heading(SourceUiStyle style, string title) =>
        style.Text(Views.Text(title), SourceTextRole.LoginHeading, SourceTextTone.PlainCard,
                sourceHeading: true)
            .WithFlexibleWidth(440, SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier("native.auth.heading");

    private static SwiftView Field(SourceUiStyle style, string label, params SwiftView[] input)
    {
        var field = Views.VStack([
            style.Text(Views.Text(label), SourceTextRole.FormLabel, SourceTextTone.PlainCard),
            .. input]);
        field.Spacing = SourceUiLayout.ItemGap;
        return field;
    }

    private static SwiftView Input(SourceUiStyle style, SwiftView field, string id, bool busy)
    {
        style.TextInput(field).WithFlexibleWidth().WithAccessibilityIdentifier(id);
        field.IsEnabled = !busy;
        return field;
    }

    private static SwiftView SecretRow(
        SourceUiStyle style, string placeholder, string value, bool show,
        Action<string> changed, Action toggle, string id, bool busy,
        Func<string, string> text)
    {
        var input = show
            ? Views.TextField(placeholder, value, changed)
            : Views.SecureField(placeholder, value, changed);
        Input(style, input, id, busy);
        var visibility = style.OutlineButton(
                Views.Image(show ? "eye.slash" : "eye"), toggle)
            .WithAccessibilityLabel(text(show
                ? NativeText.HidePassword : NativeText.ShowPassword))
            .WithAccessibilityIdentifier(id + ".visibility");
        visibility.IsEnabled = !busy;
        var row = Views.HStack(input, visibility).WithFlexibleWidth();
        row.Spacing = 0;
        return row;
    }
}
