using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.Data.AppOperations;

public sealed class ApplicationOperationRecord
{
    public required string Id { get; set; }
    public required string UserProfileId { get; set; }
    public ApplicationExecutionAuthority Authority { get; set; }
    public required string CapabilityCode { get; set; }
    public required string CapabilityFamily { get; set; }
    public int CapabilityVersion { get; set; }
    public required string CapabilityFingerprint { get; set; }
    public ApplicationEffectClass Effect { get; set; }
    public ApplicationConfirmationPolicy Confirmation { get; set; }
    public ApplicationOperationStatus Status { get; set; }
    public string? ParentOperationId { get; set; }
    public long? ParentApplicationVersion { get; set; }
    public long? ParentFence { get; set; }
    public byte[]? IdempotencyDigest { get; set; }
    public required byte[] CanonicalRequestDigest { get; set; }
    public byte[]? DecisionReferenceDigest { get; set; }
    public ApplicationOperationDecision? Decision { get; set; }
    public long ExpectedDomainVersion { get; set; }
    public long ExpectedSynchronizationVersion { get; set; }
    public long ApplicationVersion { get; set; }
    public long Fence { get; set; }
    public string? LeaseId { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime PurgeAfterUtc { get; set; }
    public DateTime? TerminalAtUtc { get; set; }
    public DateTime? PayloadPurgedAtUtc { get; set; }

    public ApplicationOperationRecord? ParentOperation { get; set; }
    public ICollection<ApplicationOperationRecord> ReversalOperations { get; set; } = [];
    public ICollection<ApplicationProtectedPayloadRecord> ProtectedPayloads { get; set; } = [];
    public ICollection<ApplicationOperationConfirmationRecord> Confirmations { get; set; } = [];
    public ApplicationOperationReceiptRecord? Receipt { get; set; }
    public ICollection<ApplicationOperationContinuationRecord> Continuations { get; set; } = [];
    public ICollection<ApplicationOperationEventRecord> Events { get; set; } = [];
}

public sealed class ApplicationProtectedPayloadRecord
{
    public required string Id { get; set; }
    public required string OperationId { get; set; }
    public required string UserProfileId { get; set; }
    public ApplicationOperationContentSubjectKind SubjectKind { get; set; }
    public required string SubjectId { get; set; }
    public ApplicationProtectedContentKind ContentKind { get; set; }
    public int ProtectionVersion { get; set; }
    public int SchemaVersion { get; set; }
    public required byte[] Ciphertext { get; set; }
    public int PlaintextLength { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime PurgeAfterUtc { get; set; }

    public required ApplicationOperationRecord Operation { get; set; }
}

public sealed class ApplicationOperationConfirmationRecord
{
    public required string Id { get; set; }
    public required string OperationId { get; set; }
    public required string UserProfileId { get; set; }
    public required byte[] ConfirmationDigest { get; set; }
    public required byte[] DecisionReferenceDigest { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public long? ConsumedApplicationVersion { get; set; }
    public long? ConsumedFence { get; set; }

    public required ApplicationOperationRecord Operation { get; set; }
}

public sealed class ApplicationOperationReceiptRecord
{
    public required string Id { get; set; }
    public required string OperationId { get; set; }
    public required string UserProfileId { get; set; }
    public int ReceiptVersion { get; set; }
    public long BeforeDomainVersion { get; set; }
    public long BeforeSynchronizationVersion { get; set; }
    public long AfterDomainVersion { get; set; }
    public long AfterSynchronizationVersion { get; set; }
    public ApplicationReversalAvailability Reversal { get; set; }
    public DateTime? ReversalExpiresAtUtc { get; set; }
    public string? ReversalOperationId { get; set; }
    public DateTime CommittedAtUtc { get; set; }

    public required ApplicationOperationRecord Operation { get; set; }
    public ApplicationOperationRecord? ReversalOperation { get; set; }
}

public sealed class ApplicationOperationContinuationRecord
{
    public required string Id { get; set; }
    public required string OperationId { get; set; }
    public required string UserProfileId { get; set; }
    public ApplicationContinuationWorkflow Workflow { get; set; }
    public int WorkflowVersion { get; set; }
    public ApplicationContinuationState State { get; set; }
    public required byte[] InteractionScopeDigest { get; set; }
    public string? ParentContinuationId { get; set; }
    public bool AllowsAutomaticResume { get; set; }
    public int AutomaticResumeCount { get; set; }
    public long ApplicationVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime PurgeAfterUtc { get; set; }
    public DateTime? ResumedAtUtc { get; set; }

    public required ApplicationOperationRecord Operation { get; set; }
    public ApplicationOperationContinuationRecord? ParentContinuation { get; set; }
    public ICollection<ApplicationOperationContinuationRecord> ChildContinuations { get; set; } = [];
}

public sealed class ApplicationOperationEventRecord
{
    public required string Id { get; set; }
    public required string OperationId { get; set; }
    public required string UserProfileId { get; set; }
    public long Sequence { get; set; }
    public ApplicationOperationEventKind Kind { get; set; }
    public ApplicationOperationStatus? FromStatus { get; set; }
    public ApplicationOperationStatus ToStatus { get; set; }
    public ApplicationOperationFailureCode? FailureCode { get; set; }
    public long ApplicationVersion { get; set; }
    public long Fence { get; set; }
    public DateTime OccurredAtUtc { get; set; }

    public required ApplicationOperationRecord Operation { get; set; }
}
