using System.ComponentModel;

namespace SentenceStudio.Contracts.AppOperation;

public sealed class ApplicationOperationProposal
{
    [Description("The opaque server-assigned application operation reference.")]
    public required string OperationId { get; init; }

    [Description("The capability and contract version whose canonical request is held by the trusted operation ledger.")]
    public required ApplicationCapabilityReference Capability { get; init; }

    [Description("The proposal state. Only Proposed may expose one of the allowed decisions.")]
    public required ApplicationOperationStatus Status { get; init; }

    [Description("The effect class of the proposed outcome.")]
    public required ApplicationEffectClass Effect { get; init; }

    [Description("The confirmation ceremony required for this proposal.")]
    public required ApplicationConfirmationPolicy Confirmation { get; init; }

    [Description("The closed decisions the authenticated application surface may currently offer.")]
    public IReadOnlyList<ApplicationOperationDecision> AllowedDecisions { get; init; } = [];

    [Description("A bounded safe summary of the inert proposal. It is not proof that an effect occurred.")]
    public required string SafeSummary { get; init; }

    [Description("Bounded safe preview lines for the authenticated application surface.")]
    public IReadOnlyList<string> SafePreview { get; init; } = [];

    [Description("The instant after which the proposal cannot be decided.")]
    public required DateTime ExpiresAtUtc { get; init; }
}

public sealed class ApplicationOperationDecisionReference
{
    [Description("The opaque operation being decided. Its protected canonical request remains in the trusted ledger.")]
    public required string OperationId { get; init; }

    [Description("The authenticated application surface's opaque idempotency reference for this decision.")]
    public required string DecisionReferenceId { get; init; }

    [Description("The closed learner decision made on the authenticated application surface.")]
    public required ApplicationOperationDecision Decision { get; init; }
}

public sealed class ApplicationOperationConfirmationReference
{
    [Description("The opaque operation awaiting protected confirmation.")]
    public required string OperationId { get; init; }

    [Description("The opaque server-issued confirmation reference. This is not confirmation material or a credential.")]
    public required string ConfirmationReferenceId { get; init; }

    [Description("The authenticated application surface's opaque idempotency reference for the confirmation decision.")]
    public required string DecisionReferenceId { get; init; }
}

public sealed class ApplicationStateVersion
{
    [Description("The authoritative domain version. Zero means no version claim.")]
    public long DomainVersion { get; init; }

    [Description("The authoritative synchronization version. Zero means no version claim.")]
    public long SynchronizationVersion { get; init; }
}

public sealed class ApplicationOperationReceipt
{
    [Description("The opaque durable receipt reference.")]
    public required string ReceiptId { get; init; }

    [Description("The opaque operation whose committed outcome this receipt proves.")]
    public required string OperationId { get; init; }

    [Description("The capability and contract version that produced the committed outcome.")]
    public required ApplicationCapabilityReference Capability { get; init; }

    [Description("The terminal operation state. Only Executed proves the described effect occurred.")]
    public required ApplicationOperationStatus Status { get; init; }

    [Description("The authoritative versions immediately before the committed effect.")]
    public required ApplicationStateVersion BeforeVersion { get; init; }

    [Description("The authoritative versions immediately after the committed effect.")]
    public required ApplicationStateVersion AfterVersion { get; init; }

    [Description("True when this response replays the original durable receipt without repeating the effect.")]
    public bool IsReplay { get; init; }

    [Description("Whether reversal is currently available, unavailable, expired, or already completed.")]
    public required ApplicationReversalAvailability Reversal { get; init; }

    [Description("When an available reversal expires. Empty when no reversal is available.")]
    public DateTime? ReversalExpiresAtUtc { get; init; }

    [Description("Closed server-authored client actions resulting from this receipt.")]
    public IReadOnlyList<ClientAction> ClientActions { get; init; } = [];

    [Description("A bounded safe summary of the outcome proven by this receipt.")]
    public required string SafeSummary { get; init; }

    [Description("The instant the durable outcome was committed.")]
    public required DateTime CommittedAtUtc { get; init; }
}
