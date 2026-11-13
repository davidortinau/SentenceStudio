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

/// <summary>
/// Source RegisterPage, ForgotPasswordPage and MainLayout sync overlay, as grouped Forms like
/// Sign In (Captain 2026-11-13, the native form pattern).
/// </summary>
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
        var sections = new List<SwiftView> { SignInView.Header(style, text("Register_CreateAccount")) };
        if (inputs.Complete)
        {
            var sent = Views.VStack(
                    Views.Image("envelope.badge")
                        .WithFixedSymbolSize(48)
                        .WithForegroundColor(style.Appearance.SuccessAlertText.Light,
                            style.Appearance.SuccessAlertText.Dark),
                    style.Text(Views.Text(text("Register_CheckYourEmail")),
                            SourceTextRole.Title2, sourceHeading: true)
                        .WithAccessibilityIdentifier("native.register.complete"),
                    style.Text(Views.Text(string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            text("Register_VerificationSent"), inputs.Email)),
                        SourceTextRole.Body2, SourceTextTone.Secondary))
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
            sent.Spacing = SourceUiLayout.ItemGap;
            sections.Add(Views.Section(string.Empty, sent));
            sections.Add(Views.Section(string.Empty, SignInView.SubmitRow(SignInView.Submit(style,
                text("Register_GoToSignIn"), actions.BackToSignIn,
                "native.register.go-to-sign-in", enabled: true))));
            return SignInView.Page(style, sections, "native.register.screen");
        }

        if (inputs.ErrorKey is { } errorKey)
            sections.Add(Views.Section(string.Empty, NativeForm.Notice(style, text(errorKey),
                "native.register.error", error: true)));
        var name = NativeForm.Field(text("Register_DisplayName"), inputs.DisplayName,
            "native.register.display-name", actions.DisplayNameChanged, text("Register_YourName"));
        name.IsEnabled = !busy;
        var email = NativeForm.Field(text(NativeText.Email), inputs.Email,
            "native.register.email", actions.EmailChanged, text("Common_EmailPlaceholder"));
        email.IsEnabled = !busy;
        sections.Add(Views.Section(string.Empty, name, email));

        // The rules and the mismatch are their own row under the two fields: typing refills
        // only that row. Refilling a field's own row re-mounts its SecureField, and iOS clears
        // a secure field that starts a new editing session.
        var hints = Views.VStack();
        hints.Spacing = 2;
        FillPasswordHints(style, hints, inputs.Password, text);
        var mismatch = Views.VStack();
        FillMismatch(style, mismatch, inputs, text);
        var checks = Views.VStack(hints, mismatch).WithFlexibleWidth();
        checks.Spacing = SourceUiLayout.ItemGap;
        sections.Add(Views.Section(string.Empty,
            SignInView.SecretField(style, text(NativeText.Password),
                text("Common_PasswordPlaceholder"), inputs.Password, inputs.ShowPassword,
                actions.PasswordChanged, actions.TogglePassword, "native.register.password",
                "native.register.password.visibility", !busy, text),
            SignInView.SecretField(style, text("Register_ConfirmPassword"),
                text("Register_ConfirmPasswordPlaceholder"), inputs.ConfirmPassword,
                inputs.ShowConfirmPassword, actions.ConfirmPasswordChanged,
                actions.ToggleConfirmPassword, "native.register.confirm-password",
                "native.register.confirm-password.visibility", !busy, text),
            checks));

        var submit = SignInView.Submit(style,
            text(busy ? "Register_CreatingAccount" : "Register_CreateAccount"),
            actions.Submit, "native.register.submit", CanRegister(inputs, busy));
        sections.Add(Views.Section(string.Empty, SignInView.SubmitRow(submit)));
        var back = NativeForm.Action(style, Views.Button(text(NativeText.SignIn), actions.BackToSignIn)
            .WithAccessibilityIdentifier("native.register.sign-in"));
        back.IsEnabled = !busy;
        sections.Add(Views.Section(text("Register_AlreadyHaveAccount"), back));
        actions.RegisterLive(submit, hints, mismatch);
        return SignInView.Page(style, sections, "native.register.screen");
    }

    internal static void UpdateRegisterLive(
        SourceUiStyle style, SwiftView submit, SwiftView hints, SwiftView mismatch,
        RegisterInputs inputs, bool busy, Func<string, string> text)
    {
        submit.IsEnabled = CanRegister(inputs, busy);
        FillPasswordHints(style, hints, inputs.Password, text);
        FillMismatch(style, mismatch, inputs, text);
    }

    /// <summary>
    /// The password rules, always all of them (neutral until a password is typed), so the row
    /// keeps its height while typing: a Form row that changes height is laid out again and
    /// its field can lose focus mid-word.
    /// </summary>
    private static void FillPasswordHints(
        SourceUiStyle style, SwiftView hints, string password, Func<string, string> text)
    {
        hints.Clear();
        foreach (var rule in SentenceStudio.Contracts.Auth.PasswordPolicy.Rules)
            hints.Add(Rule(style,
                string.IsNullOrEmpty(password)
                    ? null
                    : SentenceStudio.Contracts.Auth.PasswordPolicy.IsMet(rule, password),
                text(SentenceStudio.Contracts.Auth.PasswordPolicy.LabelKey(rule)),
                "native.register.rule."
                + SentenceStudio.Contracts.Auth.PasswordPolicy.AutomationSuffix(rule)));
    }

    /// <summary>The mismatch line keeps its space (hidden when the passwords match), for the same reason.</summary>
    private static void FillMismatch(
        SourceUiStyle style, SwiftView slot, RegisterInputs inputs, Func<string, string> text)
    {
        slot.Clear();
        var mismatched = !string.IsNullOrEmpty(inputs.Password)
            && !string.IsNullOrEmpty(inputs.ConfirmPassword)
            && inputs.Password != inputs.ConfirmPassword;
        var line = Views.HStack(
                Views.Image("exclamationmark.triangle"),
                style.Text(Views.Text(text("Register_PasswordsDoNotMatch")),
                    SourceTextRole.Caption1, SourceTextTone.Danger))
            .WithForegroundColor(style.Appearance.DangerAlertText.Light,
                style.Appearance.DangerAlertText.Dark)
            .WithHidden(!mismatched);
        // The automation ID marks a shown mismatch only (the hidden line keeps its space).
        if (mismatched)
            line.WithAccessibilityIdentifier("native.register.mismatch");
        slot.Add(line);
    }

    /// <summary>A rule line: met (green check), not met (red cross) or not yet checked (neutral).</summary>
    private static SwiftView Rule(
        SourceUiStyle style, bool? met, string label, string id)
    {
        var color = met switch
        {
            true => style.Appearance.SuccessAlertText,
            false => style.Appearance.DangerAlertText,
            null => style.Appearance.TertiaryText,
        };
        var row = Views.HStack(
                Views.Image(met switch
                {
                    true => "checkmark.circle",
                    false => "xmark.circle",
                    null => "circle",
                }),
                style.Text(Views.Text(label), SourceTextRole.Caption1, met switch
                {
                    true => SourceTextTone.Success,
                    false => SourceTextTone.Danger,
                    null => SourceTextTone.Secondary,
                }))
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
        var sections = new List<SwiftView> { SignInView.Header(style, text("ForgotPassword_Title")) };
        if (sent)
        {
            sections.Add(Views.Section(string.Empty, NativeForm.Notice(style,
                text("ForgotPassword_ResetSent"), "native.forgot.sent", error: false)));
            sections.Add(Views.Section(string.Empty, SignInView.SubmitRow(SignInView.Submit(style,
                text("ForgotPassword_BackToSignIn"), backToSignIn, "native.forgot.back",
                enabled: true))));
            return SignInView.Page(style, sections, "native.forgot.screen");
        }

        if (errorKey is not null)
            sections.Add(Views.Section(string.Empty, NativeForm.Notice(style, text(errorKey),
                "native.forgot.error", error: true)));
        var field = NativeForm.Field(text(NativeText.Email), email, "native.forgot.email",
            emailChanged, text("Common_EmailPlaceholder"));
        field.IsEnabled = !busy;
        sections.Add(Views.Section(string.Empty,
            NativeForm.Notice(style, text("ForgotPassword_Instructions"),
                "native.forgot.instructions", error: false),
            field));
        var send = SignInView.Submit(style,
            text(busy ? "ForgotPassword_Sending" : "ForgotPassword_SendResetLink"),
            submit, "native.forgot.submit", !busy && !string.IsNullOrWhiteSpace(email));
        registerSubmit(send);
        sections.Add(Views.Section(string.Empty, SignInView.SubmitRow(send)));
        var back = NativeForm.Action(style, Views.Button(text("ForgotPassword_BackToSignIn"),
                backToSignIn)
            .WithAccessibilityIdentifier("native.forgot.back"));
        back.IsEnabled = !busy;
        sections.Add(Views.Section(string.Empty, back));
        return SignInView.Page(style, sections, "native.forgot.screen");
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
        var sections = new List<SwiftView> { SignInView.Header(style, text("MainLayout_SyncingProfile")) };
        if (loading)
        {
            var progress = Views.VStack(
                    Views.IndeterminateProgress(text("MainLayout_Syncing"))
                        .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
                        .WithAccessibilityIdentifier("native.auth.sync.progress"),
                    style.Text(Views.Text(
                            $"{text("MainLayout_SyncingDescription")}\n{text("MainLayout_ThisMayTakeAMoment")}"),
                        SourceTextRole.Body2, SourceTextTone.Secondary))
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
            progress.Spacing = SourceUiLayout.ItemGap;
            sections.Add(Views.Section(string.Empty, progress));
        }
        else
        {
            sections.Add(Views.Section(string.Empty, NativeForm.Notice(style,
                text(state.ErrorKey ?? NativeText.ConnectionError), NativeAccess.AuthError,
                error: true)));
            sections.Add(Views.Section(string.Empty, SignInView.SubmitRow(SignInView.Submit(style,
                text(NativeText.Retry), retry, "native.auth.sync.retry", !busy))));
            var leave = NativeForm.Action(style, Views.Button(text("Nav_Logout"), signOut)
                .WithAccessibilityIdentifier("native.auth.sync.signout"));
            leave.IsEnabled = !busy;
            sections.Add(Views.Section(string.Empty, leave));
        }
        return SignInView.Page(style, sections, "native.auth.sync.screen");
    }
}
