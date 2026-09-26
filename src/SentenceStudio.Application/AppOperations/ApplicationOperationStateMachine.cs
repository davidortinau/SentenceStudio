using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.Application.AppOperations;

public static class ApplicationOperationStateMachine
{
    public static void ValidateAuthority(ApplicationExecutionAuthority authority)
    {
        if (authority is not ApplicationExecutionAuthority.Server
            and not ApplicationExecutionAuthority.NativeLocal)
        {
            throw new ApplicationOperationValidationException(
                "Application operations require Server or NativeLocal execution authority.");
        }
    }

    public static ApplicationOperationStatus ResolveDecisionTarget(
        ApplicationOperationStatus current,
        ApplicationOperationDecision decision,
        ApplicationConfirmationPolicy confirmation)
    {
        if (!Enum.IsDefined(decision) || decision == ApplicationOperationDecision.Unknown)
        {
            throw new ApplicationOperationValidationException("A known operation decision is required.");
        }

        return (current, decision) switch
        {
            (ApplicationOperationStatus.Proposed, ApplicationOperationDecision.Accept)
                when confirmation == ApplicationConfirmationPolicy.ProtectedConfirmation
                => ApplicationOperationStatus.AwaitingProtectedConfirmation,
            (ApplicationOperationStatus.Proposed, ApplicationOperationDecision.Accept)
                when confirmation is ApplicationConfirmationPolicy.Accept
                    or ApplicationConfirmationPolicy.Gesture
                => ApplicationOperationStatus.Executing,
            (ApplicationOperationStatus.Proposed, ApplicationOperationDecision.Reject)
                => ApplicationOperationStatus.Rejected,
            (ApplicationOperationStatus.Proposed, ApplicationOperationDecision.Cancel)
                => ApplicationOperationStatus.Cancelled,
            (ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationDecision.Confirm)
                => ApplicationOperationStatus.Executing,
            (ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationDecision.Reject)
                => ApplicationOperationStatus.Rejected,
            (ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationDecision.Cancel)
                => ApplicationOperationStatus.Cancelled,
            _ => throw new ApplicationOperationConflictException(
                $"Decision {decision} is not valid while the operation is {current}.")
        };
    }

    public static void ValidateTransition(
        ApplicationOperationStatus current,
        ApplicationOperationStatus target,
        bool effectStarted)
    {
        var valid = (current, target) switch
        {
            (ApplicationOperationStatus.Proposed, ApplicationOperationStatus.Rejected) => true,
            (ApplicationOperationStatus.Proposed, ApplicationOperationStatus.Cancelled) => true,
            (ApplicationOperationStatus.Proposed, ApplicationOperationStatus.Expired) => true,
            (ApplicationOperationStatus.Proposed, ApplicationOperationStatus.Executing) => true,
            (ApplicationOperationStatus.Proposed, ApplicationOperationStatus.AwaitingProtectedConfirmation) => true,
            (ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationStatus.Executing) => true,
            (ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationStatus.Rejected) => true,
            (ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationStatus.Cancelled) => true,
            (ApplicationOperationStatus.AwaitingProtectedConfirmation, ApplicationOperationStatus.Expired) => true,
            (ApplicationOperationStatus.Executing, ApplicationOperationStatus.Executing) => true,
            (ApplicationOperationStatus.Executing, ApplicationOperationStatus.Executed) => true,
            (ApplicationOperationStatus.Executing, ApplicationOperationStatus.Failed) when !effectStarted => true,
            (ApplicationOperationStatus.Executed, ApplicationOperationStatus.Reversed) => true,
            _ => false
        };

        if (!valid)
        {
            throw new ApplicationOperationConflictException(
                $"Transition from {current} to {target} is not permitted.");
        }
    }

    public static bool IsTerminal(ApplicationOperationStatus status) =>
        status is ApplicationOperationStatus.Executed
            or ApplicationOperationStatus.Rejected
            or ApplicationOperationStatus.Cancelled
            or ApplicationOperationStatus.Expired
            or ApplicationOperationStatus.Failed
            or ApplicationOperationStatus.Reversed;
}
