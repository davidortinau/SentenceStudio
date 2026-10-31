using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Features.Authentication;
using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.App;

/// <summary>
/// Source /auth/login, /auth/register and /auth/forgot-password are separate
/// signed-out routes; the MainLayout sync overlay masks post-login readiness.
/// </summary>
internal sealed partial class ApplicationController
{
    internal enum AccountEntry { SignIn, Register, Recovery }

    private AccountEntry _accountEntry = AccountEntry.SignIn;
    private string _registerName = string.Empty;
    private string _registerPassword = string.Empty;
    private string _registerConfirm = string.Empty;
    private bool _registerShowPassword;
    private bool _registerShowConfirm;
    private bool _registrationComplete;
    private string? _accountErrorKey;
    private bool _resetSent;
    private SwiftView? _registerSubmit;
    private SwiftView? _registerHints;
    private SwiftView? _registerMismatch;
    private SwiftView? _recoverySubmit;
    private SourceUiStyle? _accountStyle;

    internal AccountEntry CurrentAccountEntry => _accountEntry;

    internal SwiftView RenderSignIn(SourceUiStyle style)
    {
        var live = _busy && _auth is not null ? _auth.State : _authState;
        if (live is { UserId: not null, Phase: NativePhase.Loading })
            return AccountViews.Readiness(style, live, loading: true, _busy, Text,
                RetryAuthReadiness, () => Run(SignOutAsync));
        if (_authState is { UserId: not null, Phase: NativePhase.PendingSync })
            return AccountViews.Readiness(style, _authState, loading: false, _busy, Text,
                RetryAuthReadiness, () => Run(SignOutAsync));

        _accountStyle = style;
        return _accountEntry switch
        {
            AccountEntry.Register => AccountViews.Register(style, RegisterInputs, _busy, Text,
                new RegisterActions(
                    value => { _registerName = value; UpdateRegisterLive(); },
                    value => { _email = value; UpdateRegisterLive(); },
                    value => { _registerPassword = value; UpdateRegisterLive(); },
                    value => { _registerConfirm = value; UpdateRegisterLive(); },
                    () => { _registerShowPassword = !_registerShowPassword; Publish(); },
                    () => { _registerShowConfirm = !_registerShowConfirm; Publish(); },
                    SubmitRegistration,
                    () => OpenAccountEntry(AccountEntry.SignIn),
                    (submit, hints, mismatch) =>
                    {
                        _registerSubmit = submit;
                        _registerHints = hints;
                        _registerMismatch = mismatch;
                    })),
            AccountEntry.Recovery => AccountViews.ForgotPassword(style, _email, _resetSent,
                _accountErrorKey, _busy, Text,
                value =>
                {
                    _email = value;
                    if (_recoverySubmit is { } send)
                        SourceUiStyle.SetBootstrapLoginAvailability(send, Appearance,
                            !_busy && !string.IsNullOrWhiteSpace(value));
                },
                SubmitRecovery,
                () => OpenAccountEntry(AccountEntry.SignIn),
                send => _recoverySubmit = send),
            _ => RenderSignInForm(style)
        };
    }

    private RegisterInputs RegisterInputs => new(
        _registerName, _email, _registerPassword, _registerConfirm,
        _registerShowPassword, _registerShowConfirm, _registrationComplete,
        _accountErrorKey);

    private void OpenAccountEntry(AccountEntry entry)
    {
        if (_busy || _authState.UserId is not null)
            return;
        _accountEntry = entry;
        _accountErrorKey = null;
        _registrationComplete = false;
        _resetSent = false;
        // Never carry a password between the sign-in, register and recovery routes.
        _password = string.Empty;
        _registerPassword = string.Empty;
        _registerConfirm = string.Empty;
        _registerShowPassword = false;
        _registerShowConfirm = false;
        _showPassword = false;
        _registerSubmit = null;
        _registerHints = null;
        _registerMismatch = null;
        _recoverySubmit = null;
        Publish();
    }

    private void UpdateRegisterLive()
    {
        if (_registerSubmit is { } submit && _registerHints is { } hints
            && _registerMismatch is { } mismatch && _accountStyle is { } style)
            AccountViews.UpdateRegisterLive(style, submit, hints, mismatch,
                RegisterInputs, _busy, Text);
    }

    private void SubmitRegistration() => Run(async () =>
    {
        if (_auth is null || _accountEntry != AccountEntry.Register
            || _authState.UserId is not null
            || !AccountViews.CanRegister(RegisterInputs, busy: false))
            return;
        _accountErrorKey = null;
        var password = _registerPassword;
        var result = await _auth.RegisterAsync(
            _email.Trim(), password, _registerName.Trim(), _lifetime.Token);
        _registerPassword = string.Empty;
        _registerConfirm = string.Empty;
        switch (result)
        {
            case { Outcome: NativeRegistrationOutcome.SignedIn, AuthState: { } signedIn }:
                _authState = signedIn;
                _accountEntry = AccountEntry.SignIn;
                await RouteAfterAuthAsync();
                break;
            case { Outcome: NativeRegistrationOutcome.ConfirmationRequired }:
                _authState = _auth.State;
                _registrationComplete = true;
                break;
            default:
                _authState = result.AuthState ?? _auth.State;
                _accountErrorKey = result.ErrorKey ?? NativeText.UnexpectedError;
                break;
        }
    });

    private void SubmitRecovery() => Run(async () =>
    {
        if (_auth is null || _accountEntry != AccountEntry.Recovery
            || _authState.UserId is not null || string.IsNullOrWhiteSpace(_email))
            return;
        _accountErrorKey = null;
        var result = await _auth.RequestPasswordRecoveryAsync(
            _email.Trim(), _lifetime.Token);
        if (result.Outcome == NativePasswordRecoveryOutcome.Accepted)
            _resetSent = true;
        else
            _accountErrorKey = result.ErrorKey ?? "NativeAuth_RecoveryFailed";
    });

    private void RetryAuthReadiness() => Run(async () =>
    {
        if (_auth is null)
            return;
        _authState = _authState.Phase == NativePhase.PendingSync
            && _authState.UserId is not null
            ? await _auth.RetryReadinessAsync(_lifetime.Token)
            : await _auth.RestoreAsync(_lifetime.Token);
        await RouteAfterAuthAsync();
    });

    private void OnAuthProgress(NativeAuthState state)
    {
        // Only the post-login readiness transition changes what this shell
        // shows mid-operation; every other state arrives with the awaited result.
        if (_busy && state is { UserId: not null, Phase: NativePhase.Loading })
            Publish();
    }
}
