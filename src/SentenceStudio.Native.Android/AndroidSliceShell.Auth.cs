using AndroidX.Compose;
using AndroidX.Compose.UI.Text.Input;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

internal sealed partial class AndroidSliceShell
{
    private void SubmitRegistration() => Execute(async cancellationToken =>
    {
        if (_auth is null || _accountEntry != AccountEntry.Register
            || Auth.UserId is not null
            || Auth.Phase is NativePhase.Loading or NativePhase.PendingSync
                or NativePhase.Blocked
            || !AndroidRegistrationPolicy.CanSubmit(
                DisplayName.Value.Text, Email.Value, Password.Value,
                ConfirmPassword.Value))
            return;

        var result = await _auth.RegisterAsync(
            Email.Value.Trim(), Password.Value, DisplayName.Value.Text!.Trim(),
            cancellationToken);
        if (result is {
            Outcome: NativeRegistrationOutcome.SignedIn,
            AuthState: { } signedIn
        })
        {
            Publish(() =>
            {
                Password.Value = string.Empty;
                ConfirmPassword.Value = string.Empty;
            });
            await HandleAuthAsync(signedIn, cancellationToken);
        }
        else if (result.Outcome == NativeRegistrationOutcome.ConfirmationRequired)
            Publish(() =>
            {
                _registrationComplete = true;
                Password.Value = string.Empty;
                ConfirmPassword.Value = string.Empty;
                Error.Value = string.Empty;
            });
        else
            ShowError(Localize(result.ErrorKey ?? NativeText.UnexpectedError));
    });

    private void SendResetLink() => Execute(async cancellationToken =>
    {
        if (_auth is null || _accountEntry != AccountEntry.Recovery
            || Auth.UserId is not null
            || Auth.Phase is NativePhase.Loading or NativePhase.PendingSync
                or NativePhase.Blocked
            || string.IsNullOrWhiteSpace(Email.Value))
            return;

        var result = await _auth.RequestPasswordRecoveryAsync(
            Email.Value.Trim(), cancellationToken);
        if (result.Outcome == NativePasswordRecoveryOutcome.Accepted)
            Publish(() =>
            {
                _resetSent = true;
                Error.Value = string.Empty;
            });
        else
            ShowError(Localize(result.ErrorKey ?? "NativeAuth_RecoveryFailed"));
    });

    private ComposableNode RenderAuthentication(AndroidVisualPalette palette)
    {
        if (Auth is { Phase: NativePhase.Loading or NativePhase.PendingSync,
                UserId: not null })
            return RenderAuthReadiness(palette);

        return _accountEntry switch
        {
            AccountEntry.Register => RenderRegistration(palette),
            AccountEntry.Recovery => RenderRecovery(palette),
            _ => RenderSignIn(palette)
        };
    }

    private ComposableNode RenderAuthReadiness(AndroidVisualPalette palette)
    {
        var contents = new List<ComposableNode>
        {
            Heading(Localize(Auth.Phase == NativePhase.Loading
                ? "MainLayout_SyncingProfile" : "DebugHealth_Pending"),
                palette, 22)
        };
        if (Auth.Phase == NativePhase.PendingSync)
        {
            contents.Add(StatusAlert(
                Localize(Auth.ErrorKey ?? NativeText.ConnectionError),
                palette, error: true, automationId: NativeAccess.AuthError));
            contents.Add(Action(Localize(NativeText.Retry),
                "native.auth.sync.retry", RetryAuth, !Busy.Value));
            contents.Add(Action(Localize("Nav_Logout"),
                "native.auth.sync.signout", SignOut, !Busy.Value, primary: false));
        }
        else
        {
            contents.Add(new CircularProgressIndicator
            {
                Modifier = Modifier.Size(40)
                    .TestTag("native.auth.sync.progress")
                    .Semantics(semantics => semantics.ContentDescription(
                        Localize("MainLayout_Syncing")))
            });
            contents.Add(Body(Localize("Common_Loading"), palette, secondary: true));
        }
        return new Column(Arrangement.Center)
        {
            Modifier.FillMaxSize().Padding(AndroidVisualTheme.PagePadding)
                .TestTag("native.auth.sync.screen"),
            Card(palette, null, contents.ToArray())
        };
    }

    private ComposableNode RenderRegistration(AndroidVisualPalette palette)
    {
        var contents = new List<ComposableNode>
        {
            Heading(Localize("Register_CreateAccount"), palette, 22)
        };
        if (Busy.Value)
            contents.Add(new CircularProgressIndicator
            {
                Modifier = Modifier.Size(24).TestTag("native.auth.register.progress")
            });
        if (_registrationComplete)
        {
            contents.Add(Body(Localize("NativeAuth_RegistrationAccepted"), palette));
            contents.Add(Action(Localize("Register_GoToSignIn"),
                "native.auth.register.signin", ReturnToSignIn, !Busy.Value));
            return AuthCardViewport("native.auth.register.screen", contents, palette);
        }

        if (!string.IsNullOrWhiteSpace(Error.Value))
            contents.Add(StatusAlert(Error.Value, palette, error: true,
                automationId: NativeAccess.AuthError));
        var keyboard = KeyboardOptionsCompanion.Default;
        var emailKeyboard = keyboard.Copy(
            keyboard.Capitalization, keyboard.AutoCorrectEnabled,
            AndroidX.Compose.KeyboardType.Email,
            AndroidX.Compose.ImeAction.Next, keyboard.PlatformImeOptions,
            keyboard.ShowKeyboardOnFocus, keyboard.HintLocales);
        var passwordKeyboard = keyboard.Copy(
            keyboard.Capitalization, keyboard.AutoCorrectEnabled,
            AndroidX.Compose.KeyboardType.Password,
            AndroidX.Compose.ImeAction.Done, keyboard.PlatformImeOptions,
            keyboard.ShowKeyboardOnFocus, keyboard.HintLocales);
        var canEdit = !Busy.Value && Auth.UserId is null
            && Auth.Phase is not (NativePhase.Loading or NativePhase.PendingSync
                or NativePhase.Blocked);
        var canSubmit = canEdit && AndroidRegistrationPolicy.CanSubmit(
            DisplayName.Value.Text, Email.Value, Password.Value,
            ConfirmPassword.Value);

        contents.Add(new OutlinedTextField(
            DisplayName, enabled: canEdit, singleLine: true)
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.auth.register.name")
                .Semantics(semantics => semantics.ContentDescription(
                    Localize("Register_DisplayName"))),
            Label = new Text(Localize("Register_DisplayName")),
            Placeholder = new Text(Localize("Register_YourName"))
        });
        contents.Add(new OutlinedTextField(
            Email, enabled: canEdit, singleLine: true)
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.auth.register.email")
                .Semantics(semantics => semantics.ContentDescription(
                    Localize(NativeText.Email))),
            Label = new Text(Localize(NativeText.Email)),
            Placeholder = new Text(Localize("Common_EmailPlaceholder")),
            KeyboardOptions = emailKeyboard
        });
        contents.Add(PasswordEntry(
            Password, ShowPassword, Localize(NativeText.Password),
            Localize("Common_PasswordPlaceholder"),
            "native.auth.register.password", passwordKeyboard, canEdit));
        if (!string.IsNullOrEmpty(Password.Value))
        {
            if (Password.Value.Length < 8)
                contents.Add(Body(Localize("Register_AtLeast8Chars"),
                    palette, secondary: true));
            if (!Password.Value.Any(char.IsUpper))
                contents.Add(Body(Localize("Register_OneUppercase"),
                    palette, secondary: true));
            if (!Password.Value.Any(char.IsDigit))
                contents.Add(Body(Localize("Register_OneNumber"),
                    palette, secondary: true));
        }
        contents.Add(PasswordEntry(
            ConfirmPassword, ShowConfirmPassword,
            Localize("Register_ConfirmPassword"),
            Localize("Register_ConfirmPasswordPlaceholder"),
            "native.auth.register.confirm", passwordKeyboard, canEdit,
            submitOnDone: true));
        if (!string.IsNullOrEmpty(Password.Value)
            && !string.IsNullOrEmpty(ConfirmPassword.Value)
            && Password.Value != ConfirmPassword.Value)
            contents.Add(StatusAlert(
                Localize("Register_PasswordsDoNotMatch"),
                palette, error: true));
        contents.Add(Action(Localize(Busy.Value
                ? "Register_CreatingAccount" : "Register_CreateAccount"),
            "native.auth.register.submit", SubmitRegistration, canSubmit));
        contents.Add(Action(Localize("Register_GoToSignIn"),
            "native.auth.register.back", ReturnToSignIn, !Busy.Value,
            primary: false));
        return AuthCardViewport("native.auth.register.screen", contents, palette);
    }

    private ComposableNode RenderRecovery(AndroidVisualPalette palette)
    {
        var contents = new List<ComposableNode>
        {
            Heading(Localize("ForgotPassword_Title"), palette, 22)
        };
        if (Busy.Value)
            contents.Add(new CircularProgressIndicator
            {
                Modifier = Modifier.Size(24).TestTag("native.auth.recovery.progress")
            });
        if (_resetSent)
            contents.Add(StatusAlert(
                Localize("ForgotPassword_ResetSent"), palette, success: true));
        else
        {
            if (!string.IsNullOrWhiteSpace(Error.Value))
                contents.Add(StatusAlert(Error.Value, palette, error: true,
                    automationId: NativeAccess.AuthError));
            contents.Add(Body(Localize("ForgotPassword_Instructions"),
                palette, secondary: true));
            var keyboard = KeyboardOptionsCompanion.Default;
            var emailKeyboard = keyboard.Copy(
                keyboard.Capitalization, keyboard.AutoCorrectEnabled,
                AndroidX.Compose.KeyboardType.Email,
                AndroidX.Compose.ImeAction.Done, keyboard.PlatformImeOptions,
                keyboard.ShowKeyboardOnFocus, keyboard.HintLocales);
            var canEdit = !Busy.Value && Auth.UserId is null
                && Auth.Phase is not (NativePhase.Loading or NativePhase.PendingSync
                    or NativePhase.Blocked);
            var canSubmit = canEdit && !string.IsNullOrWhiteSpace(Email.Value);
            contents.Add(new OutlinedTextField(
                Email, enabled: canEdit, singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth()
                    .TestTag("native.auth.recovery.email")
                    .Semantics(semantics => semantics.ContentDescription(
                        Localize(NativeText.Email))),
                Label = new Text(Localize(NativeText.Email)),
                Placeholder = new Text(Localize("Common_EmailPlaceholder")),
                KeyboardOptions = emailKeyboard,
                KeyboardActions = KeyboardActionsHelper.Create(onDone: () =>
                {
                    if (canSubmit)
                        SendResetLink();
                })
            });
            contents.Add(Action(Localize(Busy.Value
                    ? "ForgotPassword_Sending" : "ForgotPassword_SendResetLink"),
                "native.auth.recovery.send", SendResetLink, canSubmit));
        }
        contents.Add(Action(Localize("ForgotPassword_BackToSignIn"),
            "native.auth.recovery.back", ReturnToSignIn, !Busy.Value,
            primary: false));
        return AuthCardViewport("native.auth.recovery.screen", contents, palette);
    }

    private ComposableNode PasswordEntry(
        MutableState<string> value,
        MutableState<bool> visible,
        string label, string placeholder, string tag,
        AndroidX.Compose.Foundation.Text.KeyboardOptions options,
        bool enabled, bool submitOnDone = false)
    {
        var buttonLabel = Localize(visible.Value
            ? NativeText.HidePassword : NativeText.ShowPassword);
        var visibility = new TextButton(
            () => visible.Value = !visible.Value, enabled: enabled)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .WidthIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(tag + ".visibility")
                .Semantics(semantics => semantics.ContentDescription(buttonLabel))
        };
        visibility.Add(new Text(Localize(visible.Value
            ? NativeText.HidePasswordShort : NativeText.ShowPasswordShort)));
        return new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            new OutlinedTextField(value, enabled: enabled, singleLine: true)
            {
                Modifier = Modifier.Weight(1f).TestTag(tag)
                    .Semantics(semantics => semantics.ContentDescription(label)),
                Label = new Text(label),
                Placeholder = new Text(placeholder),
                VisualTransformation = visible.Value
                    ? VisualTransformation.Companion.None
                    : new PasswordVisualTransformation('\u2022'),
                KeyboardOptions = options,
                KeyboardActions = KeyboardActionsHelper.Create(onDone: () =>
                {
                    if (submitOnDone && enabled
                        && AndroidRegistrationPolicy.CanSubmit(
                            DisplayName.Value.Text, Email.Value, Password.Value,
                            ConfirmPassword.Value))
                        SubmitRegistration();
                })
            },
            visibility
        };
    }

    private ComposableNode AuthCardViewport(
        string tag, IEnumerable<ComposableNode> contents, AndroidVisualPalette palette)
    {
        var body = new Column(Arrangement.SpacedBy(16))
        {
            Modifier = Modifier.FillMaxWidth().VerticalScroll(_signInScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 24)
        };
        var brand = Heading("SentenceStudio", palette, 28, Modifier.FillMaxWidth());
        brand.Align = TextAlign.Center;
        brand.Color = palette.Primary;
        body.Add(brand);
        body.Add(Card(palette, null, contents.ToArray()));
        return new Column(Arrangement.Center)
        {
            Modifier.FillMaxWidth().Weight(1f).TestTag(tag),
            body
        };
    }
}
