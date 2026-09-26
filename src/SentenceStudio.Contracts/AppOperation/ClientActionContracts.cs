using System.ComponentModel;

namespace SentenceStudio.Contracts.AppOperation;

public sealed class LaunchActivityClientAction
{
    [Description("The closed activity the authenticated client may launch.")]
    public required ApplicationActivityKind Activity { get; init; }

    [Description("The opaque server-authored reference to validated prepared activity state.")]
    public required string LaunchReferenceId { get; init; }
}

public sealed class RefreshDomainViewClientAction
{
    [Description("The closed application domain view whose authoritative data should be refreshed.")]
    public required ApplicationDomainViewKind View { get; init; }
}

public sealed class ReloadLearnerContextClientAction
{
    [Description("The closed section of learner context the authenticated client should reload.")]
    public required ApplicationLearnerContextKind Context { get; init; }
}

public sealed class ClientAction
{
    [Description("The closed action kind. Unknown is inert and must not be dispatched.")]
    public required ClientActionKind Kind { get; init; }

    [Description("Payload used only when Kind is LaunchActivity.")]
    public LaunchActivityClientAction? LaunchActivity { get; init; }

    [Description("Payload used only when Kind is RefreshDomainView.")]
    public RefreshDomainViewClientAction? RefreshDomainView { get; init; }

    [Description("Payload used only when Kind is ReloadLearnerContext.")]
    public ReloadLearnerContextClientAction? ReloadLearnerContext { get; init; }

    /// <summary>
    /// A client action is valid only when exactly the payload selected by its closed kind is present.
    /// Unknown values and mismatched payloads remain inert.
    /// </summary>
    public bool IsValid()
    {
        if (!Enum.IsDefined(Kind) || Kind == ClientActionKind.Unknown)
        {
            return false;
        }

        return Kind switch
        {
            ClientActionKind.None => PayloadCount() == 0,
            ClientActionKind.LaunchActivity => PayloadCount() == 1
                && LaunchActivity is not null
                && Enum.IsDefined(LaunchActivity.Activity)
                && LaunchActivity.Activity != ApplicationActivityKind.Unknown
                && !string.IsNullOrWhiteSpace(LaunchActivity.LaunchReferenceId),
            ClientActionKind.RefreshDomainView => PayloadCount() == 1
                && RefreshDomainView is not null
                && Enum.IsDefined(RefreshDomainView.View)
                && RefreshDomainView.View != ApplicationDomainViewKind.Unknown,
            ClientActionKind.ReloadLearnerContext => PayloadCount() == 1
                && ReloadLearnerContext is not null
                && Enum.IsDefined(ReloadLearnerContext.Context)
                && ReloadLearnerContext.Context != ApplicationLearnerContextKind.Unknown,
            _ => false
        };
    }

    private int PayloadCount() =>
        (LaunchActivity is null ? 0 : 1)
        + (RefreshDomainView is null ? 0 : 1)
        + (ReloadLearnerContext is null ? 0 : 1);
}
