using System.ComponentModel;

namespace SentenceStudio.Contracts.AppOperation;

public sealed class ApplicationLimitation
{
    [Description("The closed reason the requested capability or operation could not proceed.")]
    public required ApplicationLimitationReason Reason { get; init; }

    [Description("Whether and under what condition a fresh attempt may be safe.")]
    public required ApplicationRetryClassification Retry { get; init; }

    [Description("The earliest safe retry time when Retry is RetryAfterDelay.")]
    public DateTime? RetryAfterUtc { get; init; }

    [Description("Approved capability alternatives that are narrower or otherwise safe.")]
    public IReadOnlyList<ApplicationCapabilityReference> AlternativeCapabilities { get; init; } = [];

    [Description("Closed safe actions the client may offer. Arbitrary routes are not permitted.")]
    public IReadOnlyList<ClientAction> AlternativeActions { get; init; } = [];

    [Description("A bounded safe explanation with no raw exception or protected implementation detail.")]
    public required string SafeSummary { get; init; }
}

public sealed class ApplicationContinuation
{
    [Description("The opaque server-assigned continuation reference.")]
    public required string ContinuationId { get; init; }

    [Description("The closed workflow that owns the continuation.")]
    public required ApplicationContinuationWorkflow Workflow { get; init; }

    [Description("The positive append-only version of the continuation workflow.")]
    public required int WorkflowVersion { get; init; }

    [Description("The current continuation state. Unknown is not resumable.")]
    public required ApplicationContinuationState State { get; init; }

    [Description("The closed decisions the authenticated application surface may currently offer.")]
    public IReadOnlyList<ApplicationOperationDecision> AllowedNextDecisions { get; init; } = [];

    [Description("The instant after which this continuation cannot be resumed.")]
    public required DateTime ExpiresAtUtc { get; init; }

    [Description("A bounded safe summary of the unresolved workflow, with no navigation or execution target.")]
    public required string SafeSummary { get; init; }
}
