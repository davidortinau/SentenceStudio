using System.Security.Cryptography;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.Application.AppOperations;

public static class ApplicationOperationLimits
{
    public const int MaximumOwnerIdLength = 450;
    public const int MaximumOperationIdLength = 64;
    public const int MaximumCapabilityCodeLength = 96;
    public const int MaximumCapabilityFamilyLength = 64;
    public const int MaximumFingerprintLength = 71;
    public const int MaximumReferenceIdLength = 64;
    public const int MaximumFailureCodeLength = 64;
    public const int MaximumProtectedPlaintextBytes = 1_048_576;
    public const int MaximumProtectedCiphertextBytes = 1_114_112;
    public const int Sha256DigestBytes = 32;
}

public enum ApplicationProtectedContentKind
{
    Unknown = 0,
    CanonicalRequest = 1,
    ProposalPresentation = 2,
    PriorState = 3,
    Receipt = 4,
    ContinuationState = 5
}

public enum ApplicationOperationContentSubjectKind
{
    Unknown = 0,
    Operation = 1,
    Continuation = 2
}

public enum ApplicationOperationEventKind
{
    Unknown = 0,
    Proposed = 1,
    AwaitingProtectedConfirmation = 2,
    ExecutionClaimed = 3,
    LeaseRecovered = 4,
    Executed = 5,
    Rejected = 6,
    Cancelled = 7,
    Expired = 8,
    Failed = 9,
    ReversalLinked = 10,
    Reversed = 11,
    ContinuationCreated = 12,
    ContinuationResumed = 13
}

public enum ApplicationOperationFailureCode
{
    Unknown = 0,
    InvalidProtectedContent = 1,
    InvalidCanonicalRequest = 2,
    CapabilityUnavailable = 3,
    AuthorizationDenied = 4,
    StaleDomainVersion = 5,
    StaleSynchronizationVersion = 6,
    PreEffectHandlerFailure = 7
}

public sealed record ApplicationOperationScope(
    string UserProfileId,
    ApplicationExecutionAuthority Authority)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(UserProfileId)
            || UserProfileId.Length > ApplicationOperationLimits.MaximumOwnerIdLength)
        {
            throw new ApplicationOperationValidationException("A bounded owner identifier is required.");
        }

        ApplicationOperationStateMachine.ValidateAuthority(Authority);
    }
}

public sealed record ApplicationOperationVersion(
    long ApplicationVersion,
    long Fence)
{
    public void Validate()
    {
        if (ApplicationVersion <= 0 || Fence < 0)
        {
            throw new ApplicationOperationValidationException(
                "Application version must be positive and fence must be non-negative.");
        }
    }
}

public sealed record ApplicationOperationSnapshot(
    string OperationId,
    ApplicationOperationScope Scope,
    string CapabilityCode,
    string CapabilityFamily,
    int CapabilityVersion,
    string CapabilityFingerprint,
    ApplicationEffectClass Effect,
    ApplicationConfirmationPolicy Confirmation,
    ApplicationOperationStatus Status,
    string? ParentOperationId,
    ApplicationOperationVersion Version,
    long ExpectedDomainVersion,
    long ExpectedSynchronizationVersion,
    string? LeaseId,
    DateTime? LeaseExpiresAtUtc,
    int AttemptCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime PurgeAfterUtc,
    DateTime? TerminalAtUtc);

public sealed record ApplicationOperationContent(
    ApplicationProtectedContentKind Kind,
    int SchemaVersion,
    ReadOnlyMemory<byte> Plaintext);

public sealed record ApplicationOperationProtectionContext(
    ApplicationOperationScope Scope,
    ApplicationOperationContentSubjectKind SubjectKind,
    string SubjectId,
    ApplicationProtectedContentKind ContentKind,
    string CapabilityCode,
    int CapabilityVersion)
{
    public void Validate()
    {
        Scope.Validate();

        if (!Enum.IsDefined(SubjectKind)
            || SubjectKind == ApplicationOperationContentSubjectKind.Unknown)
        {
            throw new ApplicationOperationValidationException("A known protection subject is required.");
        }

        if (string.IsNullOrWhiteSpace(SubjectId)
            || SubjectId.Length > ApplicationOperationLimits.MaximumOperationIdLength)
        {
            throw new ApplicationOperationValidationException("A bounded protection subject identifier is required.");
        }

        if (!Enum.IsDefined(ContentKind) || ContentKind == ApplicationProtectedContentKind.Unknown)
        {
            throw new ApplicationOperationValidationException("A known protected content kind is required.");
        }

        if (string.IsNullOrWhiteSpace(CapabilityCode)
            || CapabilityCode.Length > ApplicationOperationLimits.MaximumCapabilityCodeLength
            || CapabilityVersion <= 0)
        {
            throw new ApplicationOperationValidationException(
                "A bounded capability code and positive version are required.");
        }
    }
}

public sealed record ApplicationProtectedContent(
    string PayloadId,
    string OperationId,
    ApplicationOperationContentSubjectKind SubjectKind,
    string SubjectId,
    ApplicationProtectedContentKind Kind,
    int ProtectionVersion,
    int SchemaVersion,
    byte[] Ciphertext,
    int PlaintextLength,
    DateTime CreatedAtUtc,
    DateTime PurgeAfterUtc);

public sealed record ApplicationOperationCreateRequest(
    string OperationId,
    ApplicationOperationScope Scope,
    string CapabilityCode,
    string CapabilityFamily,
    int CapabilityVersion,
    string CapabilityFingerprint,
    ApplicationEffectClass Effect,
    ApplicationConfirmationPolicy Confirmation,
    string? ParentOperationId,
    long? ParentApplicationVersion,
    long? ParentFence,
    byte[]? IdempotencyDigest,
    byte[] CanonicalRequestDigest,
    long ExpectedDomainVersion,
    long ExpectedSynchronizationVersion,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime PurgeAfterUtc,
    IReadOnlyList<ApplicationProtectedContent> ProtectedContents);

public sealed record ApplicationOperationProposalCommand(
    string OperationId,
    ApplicationOperationScope Scope,
    string CapabilityCode,
    string CapabilityFamily,
    int CapabilityVersion,
    string CapabilityFingerprint,
    ApplicationEffectClass Effect,
    ApplicationConfirmationPolicy Confirmation,
    string? ParentOperationId,
    long? ParentApplicationVersion,
    long? ParentFence,
    ReadOnlyMemory<byte>? IdempotencyMaterial,
    long ExpectedDomainVersion,
    long ExpectedSynchronizationVersion,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime PurgeAfterUtc,
    IReadOnlyList<ApplicationOperationContent> Contents);

public sealed record ApplicationOperationLeaseRequest(
    string LeaseId,
    DateTime LeaseExpiresAtUtc,
    bool RecoverExpiredLease);

public sealed record ApplicationOperationConfirmationIssue(
    string ConfirmationReferenceId,
    byte[] ConfirmationDigest,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);

public sealed record ApplicationOperationConfirmationUse(
    string ConfirmationReferenceId,
    byte[] ConfirmationDigest,
    DateTime ConsumedAtUtc);

public sealed record ApplicationOperationTransitionRequest(
    string OperationId,
    ApplicationOperationScope Scope,
    ApplicationOperationVersion ExpectedVersion,
    ApplicationOperationStatus TargetStatus,
    byte[] DecisionReferenceDigest,
    DateTime TransitionedAtUtc,
    ApplicationOperationLeaseRequest? Lease,
    ApplicationOperationConfirmationIssue? ConfirmationIssue,
    ApplicationOperationConfirmationUse? ConfirmationUse,
    ApplicationOperationFailureCode? FailureCode,
    bool EffectStarted,
    ApplicationOperationDecision Decision = ApplicationOperationDecision.Unknown);

public sealed record ApplicationOperationDecisionCommand(
    string OperationId,
    ApplicationOperationScope Scope,
    ApplicationOperationDecision Decision,
    ReadOnlyMemory<byte> DecisionReferenceMaterial,
    string? ConfirmationReferenceId,
    ReadOnlyMemory<byte>? ConfirmationMaterial,
    string? LeaseId,
    DateTime? LeaseExpiresAtUtc,
    DateTime DecidedAtUtc);

public sealed record ApplicationOperationTransitionResult(
    ApplicationOperationSnapshot Operation,
    bool IsReplay);

public sealed record ApplicationStateVersions(
    long DomainVersion,
    long SynchronizationVersion);

public sealed record ApplicationOperationHandlerContext(
    ApplicationOperationSnapshot Operation,
    ReadOnlyMemory<byte> CanonicalRequest);

public sealed record ApplicationOperationContinuationWrite(
    string ContinuationId,
    ApplicationContinuationWorkflow Workflow,
    int WorkflowVersion,
    byte[] InteractionScopeDigest,
    string? ParentContinuationId,
    bool AllowsAutomaticResume,
    int StateSchemaVersion,
    ReadOnlyMemory<byte> ProtectedStateSource,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime PurgeAfterUtc);

public sealed record ApplicationOperationHandlerResult(
    ApplicationStateVersions BeforeVersion,
    ApplicationStateVersions AfterVersion,
    int ReceiptSchemaVersion,
    ReadOnlyMemory<byte> ReceiptSource,
    ApplicationReversalAvailability Reversal,
    DateTime? ReversalExpiresAtUtc,
    ApplicationOperationContinuationWrite? Continuation);

public sealed record ApplicationOperationExecutionRequest(
    string OperationId,
    ApplicationOperationScope Scope,
    ApplicationOperationVersion ExpectedVersion,
    string LeaseId,
    DateTime ExecutedAtUtc);

public sealed record ApplicationOperationReceiptSnapshot(
    string ReceiptId,
    string OperationId,
    ApplicationOperationScope Scope,
    int ReceiptVersion,
    ApplicationStateVersions BeforeVersion,
    ApplicationStateVersions AfterVersion,
    ApplicationReversalAvailability Reversal,
    DateTime? ReversalExpiresAtUtc,
    string? ReversalOperationId,
    DateTime CommittedAtUtc,
    bool IsReplay,
    byte[] ReceiptContent);

public sealed record ApplicationOperationExecutionResult(
    ApplicationOperationSnapshot Operation,
    ApplicationOperationReceiptSnapshot Receipt);

public sealed record ApplicationOperationContinuationSnapshot(
    string ContinuationId,
    string OperationId,
    ApplicationOperationScope Scope,
    ApplicationContinuationWorkflow Workflow,
    int WorkflowVersion,
    ApplicationContinuationState State,
    byte[] InteractionScopeDigest,
    string? ParentContinuationId,
    bool AllowsAutomaticResume,
    int AutomaticResumeCount,
    long ApplicationVersion,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime PurgeAfterUtc,
    DateTime? ResumedAtUtc);

public static class ApplicationOperationDigest
{
    public static byte[] Compute(ReadOnlyMemory<byte> material)
    {
        if (material.IsEmpty)
        {
            throw new ApplicationOperationValidationException("Digest source material is required.");
        }

        return SHA256.HashData(material.Span);
    }
}

public static class ApplicationOperationFingerprint
{
    public static bool IsCanonical(string? value) =>
        value is not null
        && value.Length == ApplicationOperationLimits.MaximumFingerprintLength
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).ToArray().All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class ApplicationOperationValidationException(string message)
    : InvalidOperationException(message);

public sealed class ApplicationOperationConflictException(
    string message,
    Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class ApplicationOperationProtectionException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
