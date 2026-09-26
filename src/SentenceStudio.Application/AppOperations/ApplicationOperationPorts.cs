namespace SentenceStudio.Application.AppOperations;

public interface IApplicationOperationContentProtector
{
    byte[] Protect(
        ApplicationOperationProtectionContext context,
        ReadOnlyMemory<byte> plaintext);

    byte[] Unprotect(
        ApplicationOperationProtectionContext context,
        ReadOnlyMemory<byte> ciphertext,
        int expectedPlaintextLength);
}

public interface IApplicationOperationHandler
{
    string CapabilityCode { get; }

    int CapabilityVersion { get; }

    /// <summary>
    /// Applies only changes tracked by the same scoped data context used by the operation store.
    /// Implementations must not save independently or perform an external effect.
    /// </summary>
    Task<ApplicationOperationHandlerResult> ExecuteAsync(
        ApplicationOperationHandlerContext context,
        CancellationToken cancellationToken);
}

public interface IApplicationOperationStore
{
    Task<ApplicationOperationSnapshot?> FindAsync(
        ApplicationOperationScope scope,
        string operationId,
        CancellationToken cancellationToken);

    Task<ApplicationOperationTransitionResult> CreateAsync(
        ApplicationOperationCreateRequest request,
        CancellationToken cancellationToken);

    Task<ApplicationOperationTransitionResult> TransitionAsync(
        ApplicationOperationTransitionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Invokes the handler and commits its tracked domain changes, receipt, event, and optional
    /// continuation in one store-owned transaction.
    /// </summary>
    Task<ApplicationOperationExecutionResult> ExecuteAsync(
        ApplicationOperationExecutionRequest request,
        IApplicationOperationHandler handler,
        CancellationToken cancellationToken);

    Task<ApplicationOperationReceiptSnapshot?> FindReceiptAsync(
        ApplicationOperationScope scope,
        string operationId,
        bool isReplay,
        CancellationToken cancellationToken);

    Task<ApplicationOperationContinuationSnapshot?> FindContinuationAsync(
        ApplicationOperationScope scope,
        string continuationId,
        CancellationToken cancellationToken);

    Task<ApplicationOperationContinuationSnapshot> ResumeContinuationAsync(
        ApplicationOperationScope scope,
        string continuationId,
        long expectedApplicationVersion,
        DateTime resumedAtUtc,
        CancellationToken cancellationToken);
}

public interface IApplicationOperationExporter
{
    Task<ApplicationOperationOwnerExport> ExportOwnerAsync(
        string userProfileId,
        CancellationToken cancellationToken);
}

public interface IApplicationOperationDeleter
{
    Task<int> DeleteOwnerAsync(
        string userProfileId,
        CancellationToken cancellationToken);
}

public interface IApplicationOperationRetention
{
    Task<ApplicationOperationRetentionResult> ApplyAsync(
        DateTime nowUtc,
        int maximumOperations,
        CancellationToken cancellationToken);
}

public sealed record ApplicationOperationExportRow(
    string OperationId,
    string UserProfileId,
    string Authority,
    string CapabilityCode,
    string CapabilityFamily,
    int CapabilityVersion,
    string CapabilityFingerprint,
    string Effect,
    string Confirmation,
    string Status,
    string? Decision,
    string? ParentOperationId,
    long? ParentApplicationVersion,
    long ExpectedDomainVersion,
    long ExpectedSynchronizationVersion,
    long ApplicationVersion,
    int AttemptCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime PurgeAfterUtc,
    DateTime? TerminalAtUtc,
    DateTime? PayloadPurgedAtUtc);

public sealed record ApplicationProtectedPayloadExportRow(
    string PayloadId,
    string OperationId,
    string SubjectKind,
    string SubjectId,
    string ContentKind,
    int SchemaVersion,
    DateTime CreatedAtUtc,
    DateTime PurgeAfterUtc,
    string OmissionReason);

public sealed record ApplicationOperationConfirmationExportRow(
    string ConfirmationReferenceId,
    string OperationId,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? ConsumedAtUtc,
    DateTime? RevokedAtUtc,
    long? ConsumedApplicationVersion,
    bool IsCurrent);

public sealed record ApplicationOperationReceiptExportRow(
    string ReceiptId,
    string OperationId,
    int ReceiptVersion,
    long BeforeDomainVersion,
    long BeforeSynchronizationVersion,
    long AfterDomainVersion,
    long AfterSynchronizationVersion,
    string Reversal,
    DateTime? ReversalExpiresAtUtc,
    string? ReversalOperationId,
    DateTime CommittedAtUtc);

public sealed record ApplicationOperationContinuationExportRow(
    string ContinuationId,
    string OperationId,
    string Workflow,
    int WorkflowVersion,
    string State,
    string? ParentContinuationId,
    bool AllowsAutomaticResume,
    int AutomaticResumeCount,
    long ApplicationVersion,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime PurgeAfterUtc,
    DateTime? ResumedAtUtc);

public sealed record ApplicationOperationEventExportRow(
    string EventId,
    string OperationId,
    long Sequence,
    string Kind,
    string? FromStatus,
    string ToStatus,
    string? FailureCode,
    long ApplicationVersion,
    DateTime OccurredAtUtc);

public sealed record ApplicationOperationAuthorityExportCoverage(
    string Authority,
    bool Included,
    string? OmissionReason);

public sealed record ApplicationOperationOwnerExport(
    string UserProfileId,
    IReadOnlyList<ApplicationOperationAuthorityExportCoverage> AuthorityCoverage,
    string ProtectedPayloadDisposition,
    IReadOnlyList<ApplicationOperationExportRow> Operations,
    IReadOnlyList<ApplicationProtectedPayloadExportRow> ProtectedPayloads,
    IReadOnlyList<ApplicationOperationConfirmationExportRow> Confirmations,
    IReadOnlyList<ApplicationOperationReceiptExportRow> Receipts,
    IReadOnlyList<ApplicationOperationContinuationExportRow> Continuations,
    IReadOnlyList<ApplicationOperationEventExportRow> Events);

public sealed record ApplicationOperationRetentionResult(
    int ExpiredOperations,
    int PurgedOperations);
