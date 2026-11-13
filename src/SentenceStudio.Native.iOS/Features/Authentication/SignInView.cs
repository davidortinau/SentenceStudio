using Microsoft.SwiftUI;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Authentication;

/// <summary>
/// Sign In as a grouped Form (Captain 2026-11-13, the native form pattern): the app mark and
/// heading sit on the page above the rows; Email and Password are fields beside their labels,
/// Remember Me a switch; Sign In is the filled button under them; Forgot Password and Create
/// One are action rows.
/// </summary>
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
        Action forgotPassword,
        Action createAccount,
        Action<SwiftView> registerSubmit)
    {
        var sections = new List<SwiftView> { Header(style, text(NativeText.SignIn)) };
        var notices = new List<SwiftView>();
        if (state.ErrorKey is { } errorKey)
            notices.Add(NativeForm.Notice(style, text(errorKey), NativeAccess.AuthError, error: true));
        if (!busy && state.Phase == NativePhase.Loading)
            notices.Add(NativeForm.Notice(style, text("Common_Loading"), "native.auth.loading",
                error: false));
        if (notices.Count > 0)
            sections.Add(Views.Section(string.Empty, notices.ToArray()));

        var enabled = canAuthenticate && !busy;
        var emailField = NativeForm.Field(text(NativeText.Email), email, NativeAccess.Email,
            emailChanged, text("Common_EmailPlaceholder"));
        emailField.IsEnabled = enabled;
        var passwordField = SecretField(style, text(NativeText.Password),
            text("Common_PasswordPlaceholder"), password, showPassword, passwordChanged,
            togglePassword, NativeAccess.Password, NativeAccess.PasswordVisibility, enabled, text);
        var remember = NativeForm.Switch(style, text(NativeText.RememberMe), rememberMe,
            rememberChanged, NativeAccess.RememberMe);
        remember.IsEnabled = enabled;
        sections.Add(Views.Section(string.Empty, emailField, passwordField, remember));

        var canSubmit = !busy && canAuthenticate
            && !string.IsNullOrWhiteSpace(email)
            && !string.IsNullOrWhiteSpace(password);
        var submit = Submit(style, text(busy || state.Phase == NativePhase.Loading
                ? "Login_SigningIn" : NativeText.SignIn), signIn, NativeAccess.SignIn, canSubmit);
        registerSubmit(submit);
        sections.Add(Views.Section(string.Empty, SubmitRow(submit)));
        // A failed form sign-in is corrected and resubmitted with Sign In.
        // Only an offline stored session gets a separate, labelled restore (its own Section:
        // the filled button's row has no background).
        if (!busy && state is { Phase: NativePhase.PendingSync, UserId: null })
            sections.Add(Views.Section(string.Empty, NativeForm.Action(style,
                text("NativeAuth_RestoreSessionRetry"), "arrow.clockwise", retry,
                "native.auth.retry")));

        var forgot = NativeForm.Action(style, Views.Button(text("Login_ForgotPassword"),
                forgotPassword)
            .WithAccessibilityIdentifier("native.auth.forgot"));
        forgot.IsEnabled = !busy;
        var create = NativeForm.Action(style, Views.Button(text("Login_CreateOne"), createAccount)
            .WithAccessibilityIdentifier("native.auth.register"));
        create.IsEnabled = !busy;
        sections.Add(Views.Section(string.Empty, forgot));
        sections.Add(Views.Section(text("Login_NoAccountCreateOne"), create));
        return Page(style, sections, "native.auth.screen");
    }

    /// <summary>The app mark over the page heading, on the page above the rows (no row behind).</summary>
    internal static SwiftView Header(SourceUiStyle style, string heading)
    {
        var header = Views.VStack(
                Brand(style),
                style.Text(Views.Text(heading), SourceTextRole.LoginHeading,
                        sourceHeading: true)
                    .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                    .WithAccessibilityIdentifier("native.auth.heading"))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithListRowBackgroundHidden();
        header.Spacing = SourceUiLayout.ItemGap;
        return Views.Section(string.Empty, header);
    }

    internal static SwiftView Brand(SourceUiStyle style) =>
        Views.HStack(
            Views.Image("translate")
                .WithForegroundColor(style.Appearance.Text.Light,
                    style.Appearance.Text.Dark),
            style.Text(Views.Text("SentenceStudio"),
                SourceTextRole.Brand,
                sourceHeading: true))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier("native.auth.brand");

    /// <summary>An account page: the grouped Form (full width, as iOS Settings is).</summary>
    internal static SwiftView Page(SourceUiStyle style, IEnumerable<SwiftView> sections,
        string screenId) =>
        NativeForm.Page(style, sections, screenId, screenId);

    /// <summary>The page's filled action (Sign In, Create Account, Send Reset Link).</summary>
    internal static SwiftView Submit(SourceUiStyle style, string title, Action action, string id,
        bool enabled)
    {
        var submit = NativeForm.Prominent(style, title, action, id);
        submit.IsEnabled = enabled;
        return submit;
    }

    /// <summary>The filled action centered in its own row, on the page (no row behind it).</summary>
    internal static SwiftView SubmitRow(SwiftView submit) =>
        Views.HStack(submit)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
            .WithListRowBackgroundHidden();

    /// <summary>
    /// A password field beside its label, with the show/hide button at the row's end (its own
    /// target, so tapping the field does not toggle it).
    /// </summary>
    internal static SwiftView SecretField(SourceUiStyle style, string label, string placeholder,
        string value, bool show, Action<string> changed, Action toggle, string id,
        string visibilityId, bool enabled, Func<string, string> text)
    {
        var input = (show
                ? Views.TextField(placeholder, value, changed)
                : Views.SecureField(placeholder, value, changed))
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(id);
        input.IsEnabled = enabled;
        var visibility = Views.Button(Views.Image(show ? "eye.slash" : "eye"), toggle)
            .WithButtonStyle(SwiftButtonStyle.Plain)
            .WithForegroundColor(style.Appearance.Primary.Light, style.Appearance.Primary.Dark)
            .WithAccessibilityLabel(text(show ? NativeText.HidePassword : NativeText.ShowPassword))
            .WithAccessibilityIdentifier(visibilityId);
        visibility.IsEnabled = enabled;
        var row = Views.HStack(input, visibility);
        row.Spacing = SourceUiLayout.ItemGap;
        return Views.LabeledContent(label, row);
    }
}
