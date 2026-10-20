using Microsoft.SwiftUI;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Authentication;

internal static class SignInView
{
    internal static SwiftView Page(
        SourceUiStyle style,
        NativeAuthState state,
        string email,
        string password,
        bool rememberMe,
        bool showPassword,
        bool busy,
        bool canAuthenticate,
        Func<string, string> text,
        Action<string> emailChanged,
        Action<string> passwordChanged,
        Action<bool> rememberChanged,
        Action togglePassword,
        Action signIn,
        Action retry,
        Action<SwiftView> registerSubmit)
    {
        var content = new List<SwiftView>
        {
            Views.HStack(
                Views.Image("translate")
                    .WithForegroundColor(style.Appearance.Text.Light,
                        style.Appearance.Text.Dark),
                style.Text(Views.Text("SentenceStudio"),
                    SourceTextRole.Brand,
                    sourceHeading: true))
                .WithFlexibleWidth(440, SwiftHorizontalAlignment.Center)
                .WithAccessibilityIdentifier("native.auth.brand")
        };
        var form = new List<SwiftView>
        {
            style.Text(Views.Text(text(NativeText.SignIn)),
                    SourceTextRole.LoginHeading, SourceTextTone.PlainCard,
                    sourceHeading: true)
                .WithFlexibleWidth(440, SwiftHorizontalAlignment.Center)
                .WithAccessibilityIdentifier("native.auth.heading"),
        };
        if (state.ErrorKey is { } errorKey)
            form.Add(style.Alert(style.Text(Views.Text(text(errorKey)),
                    SourceTextRole.DefaultBody), success: false)
                .WithAccessibilityIdentifier(NativeAccess.AuthError));
        if (!busy && state.Phase == NativePhase.Loading)
            form.Add(style.Text(Views.Text(text("Common_Loading")),
                    SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.auth.loading"));

        var emailInput = style.TextInput(
                Views.TextField(text("Common_EmailPlaceholder"), email, emailChanged))
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(NativeAccess.Email);
        emailInput.IsEnabled = canAuthenticate && !busy;
        var emailField = Views.VStack(
            style.Text(Views.Text(text(NativeText.Email)),
                SourceTextRole.FormLabel, SourceTextTone.PlainCard),
            emailInput);
        emailField.Spacing = SourceUiLayout.ItemGap;
        form.Add(emailField);
        var passwordInput = (showPassword
                ? Views.TextField(text("Common_PasswordPlaceholder"),
                    password, passwordChanged)
                : Views.SecureField(text("Common_PasswordPlaceholder"),
                    password, passwordChanged));
        style.TextInput(passwordInput)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(NativeAccess.Password);
        passwordInput.IsEnabled = canAuthenticate && !busy;
        var visibility = style.OutlineButton(
                Views.Image(showPassword ? "eye.slash" : "eye"),
                togglePassword)
            .WithAccessibilityLabel(text(showPassword
                ? NativeText.HidePassword : NativeText.ShowPassword))
            .WithAccessibilityIdentifier(NativeAccess.PasswordVisibility);
        visibility.IsEnabled = canAuthenticate && !busy;
        var passwordRow = Views.HStack(passwordInput, visibility)
            .WithFlexibleWidth();
        passwordRow.Spacing = 0;
        var passwordField = Views.VStack(
            style.Text(Views.Text(text(NativeText.Password)),
                SourceTextRole.FormLabel, SourceTextTone.PlainCard),
            passwordRow);
        passwordField.Spacing = SourceUiLayout.ItemGap;
        form.Add(passwordField);
        var remember = Views.Toggle(
                text(NativeText.RememberMe), rememberMe, rememberChanged)
            .WithTintColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithForegroundColor(style.Appearance.PlainCardText.Light,
                style.Appearance.PlainCardText.Dark)
            .WithAccessibilityIdentifier(NativeAccess.RememberMe);
        remember.IsEnabled = canAuthenticate && !busy;
        form.Add(remember);
        var canSubmit = !busy && canAuthenticate
            && !string.IsNullOrWhiteSpace(email)
            && !string.IsNullOrWhiteSpace(password);
        var submit = style.BootstrapLoginButton(
                text(busy || state.Phase == NativePhase.Loading
                    ? "Login_SigningIn" : NativeText.SignIn), signIn, canSubmit)
            .WithAccessibilityIdentifier(NativeAccess.SignIn);
        registerSubmit(submit);
        form.Add(submit);
        if (!busy && state.Phase is NativePhase.PendingSync or NativePhase.Error)
            form.Add(style.SecondaryButton(text(NativeText.Retry), retry)
                .WithAccessibilityIdentifier("native.auth.retry"));
        var formBody = Views.VStack(form.ToArray())
            .WithPadding(SourceUiLayout.LargeCardInset)
            .WithFlexibleWidth(440, SwiftHorizontalAlignment.Center)
            .WithRoundedSurface(style.Appearance.PlainCardSurface.Light,
                style.Appearance.PlainCardSurface.Dark,
                style.Appearance.BootstrapRadius)
            .WithShadow("#000000", "#000000", 4, 0.075, 2);
        formBody.Spacing = SourceUiLayout.FormGap;
        content.Add(formBody);
        var page = Views.Scroll(content.ToArray())
            .WithCenterWhenShort()
            .WithPadding(SourceUiLayout.AuthOuterInset);
        page.Spacing = SourceUiLayout.LargeCardInset;
        return style.Page(page)
            .WithAccessibilityIdentifier("native.auth.screen");
    }
}
