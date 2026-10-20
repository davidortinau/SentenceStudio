using Microsoft.Extensions.Logging;
using SentenceStudio.Services;

namespace SentenceStudio.Native;

public sealed partial class NativeAuthPresenter
{
    public async Task<NativeRegistrationResult> RegisterAsync(
        string email, string password, string displayName,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State.UserId is not null || auth.IsSignedIn)
                return new(NativeRegistrationOutcome.Unavailable, "Register_NotAvailable");
            if (string.IsNullOrWhiteSpace(email)
                || string.IsNullOrWhiteSpace(password)
                || string.IsNullOrWhiteSpace(displayName))
                return new(NativeRegistrationOutcome.Rejected, "Register_Failed");
            if (!BeginAuthentication())
            {
                var blocked = RefuseChangedOwner();
                return new(NativeRegistrationOutcome.Failed,
                    NativeText.InvalidCredentials, blocked);
            }

            using var authentication = await scope.EnterAuthenticationAsync(cancellationToken);
            if (_transition is not { } transition || !scope.IsCurrent(transition))
            {
                var blocked = RefuseChangedOwner();
                return new(NativeRegistrationOutcome.Failed,
                    NativeText.InvalidCredentials, blocked);
            }
            SetState(new(NativePhase.Loading, NativeDestination.SignIn));
            var response = await auth.RegisterWithOutcomeAsync(
                email.Trim(), password, displayName.Trim(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!scope.IsCurrent(transition))
            {
                var blocked = RefuseChangedOwner();
                return new(NativeRegistrationOutcome.Failed,
                    NativeText.InvalidCredentials, blocked);
            }

            if (response.Outcome == AuthRegistrationOutcome.SignedIn)
            {
                if (response.Session is null)
                {
                    var missing = SetState(new(NativePhase.Error, NativeDestination.SignIn,
                        ErrorKey: NativeText.UnexpectedError));
                    return new(NativeRegistrationOutcome.Failed,
                        NativeText.UnexpectedError, missing);
                }
                var state = await EnterAuthenticatedAsync(
                    response.Session, cancellationToken);
                return state.Phase is NativePhase.Ready or NativePhase.PendingSync
                    && state.UserId is not null
                    ? new(NativeRegistrationOutcome.SignedIn, AuthState: state)
                    : new(NativeRegistrationOutcome.Failed,
                        state.ErrorKey ?? NativeText.UnexpectedError, state);
            }

            SetState(new(NativePhase.Ready, NativeDestination.SignIn));
            return response.Outcome switch
            {
                AuthRegistrationOutcome.ConfirmationRequired =>
                    new(NativeRegistrationOutcome.ConfirmationRequired,
                        "NativeAuth_RegistrationAccepted"),
                AuthRegistrationOutcome.TransportFailure =>
                    new(NativeRegistrationOutcome.TransportFailure,
                        NativeText.ConnectionError),
                AuthRegistrationOutcome.Unavailable =>
                    new(NativeRegistrationOutcome.Unavailable,
                        "Register_NotAvailable"),
                _ => new(NativeRegistrationOutcome.Rejected, "Register_Failed")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_state is { Phase: NativePhase.Loading, UserId: null })
                SetState(new(NativePhase.Ready, NativeDestination.SignIn));
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Native registration did not complete ({FailureType}).",
                ex.GetType().Name);
            var failed = SetState(new(NativePhase.Error, NativeDestination.SignIn,
                ErrorKey: NativeText.UnexpectedError));
            return new(NativeRegistrationOutcome.Failed,
                NativeText.UnexpectedError, failed);
        }
        finally
        {
            if (State.UserId is null
                || State.Phase is not (NativePhase.Ready or NativePhase.PendingSync))
                ClearOwnedScope();
            _gate.Release();
        }
    }

    public async Task<NativePasswordRecoveryResult> RequestPasswordRecoveryAsync(
        string email, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State.UserId is not null || auth.IsSignedIn)
                return new(NativePasswordRecoveryOutcome.Unavailable,
                    "NativeAuth_RecoveryUnavailable");
            if (string.IsNullOrWhiteSpace(email))
                return new(NativePasswordRecoveryOutcome.Rejected,
                    "NativeAuth_RecoveryFailed");

            var response = await auth.RequestPasswordRecoveryAsync(
                email.Trim(), cancellationToken);
            return response.Outcome switch
            {
                AuthPasswordRecoveryOutcome.Accepted =>
                    new(NativePasswordRecoveryOutcome.Accepted,
                        "ForgotPassword_ResetSent"),
                AuthPasswordRecoveryOutcome.TransportFailure =>
                    new(NativePasswordRecoveryOutcome.TransportFailure,
                        NativeText.ConnectionError),
                AuthPasswordRecoveryOutcome.Unavailable =>
                    new(NativePasswordRecoveryOutcome.Unavailable,
                        "NativeAuth_RecoveryUnavailable"),
                _ => new(NativePasswordRecoveryOutcome.Rejected,
                    "NativeAuth_RecoveryFailed")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Native password recovery did not complete ({FailureType}).",
                ex.GetType().Name);
            return new(NativePasswordRecoveryOutcome.Rejected,
                "NativeAuth_RecoveryFailed");
        }
        finally
        {
            _gate.Release();
        }
    }
}
