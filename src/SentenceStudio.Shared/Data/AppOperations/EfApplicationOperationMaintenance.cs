using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.Data.AppOperations;

public sealed class EfApplicationOperationMaintenance(ApplicationDbContext dbContext)
    : IApplicationOperationExporter, IApplicationOperationDeleter, IApplicationOperationRetention
{
    public async Task<ApplicationOperationOwnerExport> ExportOwnerAsync(
        string userProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            return EmptyExport();
        }

        var operations = await dbContext.ApplicationOperations
            .AsNoTracking()
            .Where(operation => operation.UserProfileId == userProfileId)
            .OrderBy(operation => operation.CreatedAtUtc)
            .Select(operation => new ApplicationOperationExportRow(
                operation.Id,
                operation.UserProfileId,
                operation.Authority.ToString(),
                operation.CapabilityCode,
                operation.CapabilityFamily,
                operation.CapabilityVersion,
                operation.CapabilityFingerprint,
                operation.Effect.ToString(),
                operation.Confirmation.ToString(),
                operation.Status.ToString(),
                operation.Decision == null ? null : operation.Decision.ToString(),
                operation.ParentOperationId,
                operation.ParentApplicationVersion,
                operation.ExpectedDomainVersion,
                operation.ExpectedSynchronizationVersion,
                operation.ApplicationVersion,
                operation.AttemptCount,
                operation.CreatedAtUtc,
                operation.UpdatedAtUtc,
                operation.ExpiresAtUtc,
                operation.PurgeAfterUtc,
                operation.TerminalAtUtc,
                operation.PayloadPurgedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var payloads = await dbContext.ApplicationProtectedPayloads
            .AsNoTracking()
            .Where(payload => payload.UserProfileId == userProfileId)
            .OrderBy(payload => payload.CreatedAtUtc)
            .Select(payload => new ApplicationProtectedPayloadExportRow(
                payload.Id,
                payload.OperationId,
                payload.SubjectKind.ToString(),
                payload.SubjectId,
                payload.ContentKind.ToString(),
                payload.SchemaVersion,
                payload.CreatedAtUtc,
                payload.PurgeAfterUtc,
                "Protected content omitted because recipient-bound export re-encryption is not configured."))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var confirmations = await dbContext.ApplicationOperationConfirmations
            .AsNoTracking()
            .Where(confirmation => confirmation.UserProfileId == userProfileId)
            .OrderBy(confirmation => confirmation.CreatedAtUtc)
            .Select(confirmation => new ApplicationOperationConfirmationExportRow(
                confirmation.Id,
                confirmation.OperationId,
                confirmation.CreatedAtUtc,
                confirmation.ExpiresAtUtc,
                confirmation.ConsumedAtUtc,
                confirmation.RevokedAtUtc,
                confirmation.ConsumedApplicationVersion,
                confirmation.ConsumedAtUtc == null && confirmation.RevokedAtUtc == null))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var receipts = await dbContext.ApplicationOperationReceipts
            .AsNoTracking()
            .Where(receipt => receipt.UserProfileId == userProfileId)
            .OrderBy(receipt => receipt.CommittedAtUtc)
            .Select(receipt => new ApplicationOperationReceiptExportRow(
                receipt.Id,
                receipt.OperationId,
                receipt.ReceiptVersion,
                receipt.BeforeDomainVersion,
                receipt.BeforeSynchronizationVersion,
                receipt.AfterDomainVersion,
                receipt.AfterSynchronizationVersion,
                receipt.Reversal.ToString(),
                receipt.ReversalExpiresAtUtc,
                receipt.ReversalOperationId,
                receipt.CommittedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var continuations = await dbContext.ApplicationOperationContinuations
            .AsNoTracking()
            .Where(continuation => continuation.UserProfileId == userProfileId)
            .OrderBy(continuation => continuation.CreatedAtUtc)
            .Select(continuation => new ApplicationOperationContinuationExportRow(
                continuation.Id,
                continuation.OperationId,
                continuation.Workflow.ToString(),
                continuation.WorkflowVersion,
                continuation.State.ToString(),
                continuation.ParentContinuationId,
                continuation.AllowsAutomaticResume,
                continuation.AutomaticResumeCount,
                continuation.ApplicationVersion,
                continuation.CreatedAtUtc,
                continuation.UpdatedAtUtc,
                continuation.ExpiresAtUtc,
                continuation.PurgeAfterUtc,
                continuation.ResumedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var events = await dbContext.ApplicationOperationEvents
            .AsNoTracking()
            .Where(operationEvent => operationEvent.UserProfileId == userProfileId)
            .OrderBy(operationEvent => operationEvent.OccurredAtUtc)
            .ThenBy(operationEvent => operationEvent.Sequence)
            .Select(operationEvent => new ApplicationOperationEventExportRow(
                operationEvent.Id,
                operationEvent.OperationId,
                operationEvent.Sequence,
                operationEvent.Kind.ToString(),
                operationEvent.FromStatus == null ? null : operationEvent.FromStatus.ToString(),
                operationEvent.ToStatus.ToString(),
                operationEvent.FailureCode == null ? null : operationEvent.FailureCode.ToString(),
                operationEvent.ApplicationVersion,
                operationEvent.OccurredAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var authorityCoverage = Enum.GetValues<ApplicationExecutionAuthority>()
            .Where(authority => authority is ApplicationExecutionAuthority.Server
                or ApplicationExecutionAuthority.NativeLocal)
            .Select(authority =>
            {
                var included = operations.Any(operation =>
                    string.Equals(operation.Authority, authority.ToString(), StringComparison.Ordinal));
                return new ApplicationOperationAuthorityExportCoverage(
                    authority.ToString(),
                    included,
                    included
                        ? null
                        : "No rows for this authority were present in the current store; other stores were not queried.");
            })
            .ToArray();

        return new ApplicationOperationOwnerExport(
            userProfileId,
            authorityCoverage,
            "Protected payloads are omitted because recipient-bound export re-encryption is not configured.",
            operations,
            payloads,
            confirmations,
            receipts,
            continuations,
            events);
    }

    public async Task<int> DeleteOwnerAsync(
        string userProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            return 0;
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
                () => ExecuteTransactionalAttemptAsync(
                    () => DeleteOwnerAttemptAsync(userProfileId, cancellationToken),
                    cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task<int> DeleteOwnerAttemptAsync(
        string userProfileId,
        CancellationToken cancellationToken)
    {
        var operations = await dbContext.ApplicationOperations
            .Where(operation => operation.UserProfileId == userProfileId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (operations.Count == 0)
        {
            return 0;
        }

        await RemoveDependentsAsync(
                operations.Select(operation => operation.Id).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        dbContext.ApplicationOperations.RemoveRange(operations);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return operations.Count;
    }

    public async Task<ApplicationOperationRetentionResult> ApplyAsync(
        DateTime nowUtc,
        int maximumOperations,
        CancellationToken cancellationToken)
    {
        if (maximumOperations <= 0)
        {
            throw new ApplicationOperationValidationException(
                "Retention requires a positive operation batch size.");
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
                () => ExecuteTransactionalAttemptAsync(
                    () => ApplyAttemptAsync(nowUtc, maximumOperations, cancellationToken),
                    cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task<ApplicationOperationRetentionResult> ApplyAttemptAsync(
        DateTime nowUtc,
        int maximumOperations,
        CancellationToken cancellationToken)
    {
        var expired = await dbContext.ApplicationOperations
            .Where(operation =>
                (operation.Status == ApplicationOperationStatus.Proposed
                    || operation.Status == ApplicationOperationStatus.AwaitingProtectedConfirmation)
                && operation.ExpiresAtUtc <= nowUtc)
            .OrderBy(operation => operation.ExpiresAtUtc)
            .Take(maximumOperations)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var operation in expired)
        {
            var fromStatus = operation.Status;
            operation.Status = ApplicationOperationStatus.Expired;
            operation.ApplicationVersion++;
            operation.UpdatedAtUtc = nowUtc;
            operation.TerminalAtUtc = nowUtc;
            operation.LeaseId = null;
            operation.LeaseExpiresAtUtc = null;
            var sequence = await NextSequenceAsync(operation.Id, cancellationToken).ConfigureAwait(false);
            dbContext.ApplicationOperationEvents.Add(new ApplicationOperationEventRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                OperationId = operation.Id,
                UserProfileId = operation.UserProfileId,
                Sequence = sequence,
                Kind = ApplicationOperationEventKind.Expired,
                FromStatus = fromStatus,
                ToStatus = ApplicationOperationStatus.Expired,
                ApplicationVersion = operation.ApplicationVersion,
                Fence = operation.Fence,
                OccurredAtUtc = nowUtc,
                Operation = operation
            });
        }

        if (expired.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // Retention is staged: each dependent observes its own deadline, active
        // continuations retain their state, and the operation replay tombstone remains.
        var retentionCandidates = await dbContext.ApplicationOperations
            .Where(operation =>
                (operation.Status == ApplicationOperationStatus.Executed
                    || operation.Status == ApplicationOperationStatus.Rejected
                    || operation.Status == ApplicationOperationStatus.Cancelled
                    || operation.Status == ApplicationOperationStatus.Expired
                    || operation.Status == ApplicationOperationStatus.Failed
                    || operation.Status == ApplicationOperationStatus.Reversed)
                && operation.PayloadPurgedAtUtc == null
                && (operation.PurgeAfterUtc <= nowUtc
                    || operation.ProtectedPayloads.Any(payload => payload.PurgeAfterUtc <= nowUtc)
                    || operation.Confirmations.Any(confirmation => confirmation.ExpiresAtUtc <= nowUtc)
                    || operation.Continuations.Any(continuation =>
                        continuation.ExpiresAtUtc <= nowUtc
                        || continuation.PurgeAfterUtc <= nowUtc)))
            .Include(operation => operation.ProtectedPayloads)
            .Include(operation => operation.Confirmations)
            .Include(operation => operation.Receipt)
            .Include(operation => operation.Continuations)
            .AsSplitQuery()
            .OrderBy(operation => operation.PurgeAfterUtc)
            .ThenBy(operation => operation.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var continuationLinks = await dbContext.ApplicationOperationContinuations
            .AsNoTracking()
            .Where(continuation => continuation.ParentContinuationId != null)
            .Select(continuation => new ContinuationLink(
                continuation.Id,
                continuation.ParentContinuationId!))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var continuations = retentionCandidates
            .SelectMany(operation => operation.Continuations)
            .ToArray();
        var expiringContinuations = continuations
            .Where(continuation =>
                IsPendingContinuation(continuation)
                && continuation.ExpiresAtUtc <= nowUtc)
            .OrderBy(continuation => continuation.ExpiresAtUtc)
            .ThenBy(continuation => continuation.Id)
            .Take(maximumOperations)
            .ToArray();
        foreach (var continuation in expiringContinuations)
        {
            continuation.State = ApplicationContinuationState.Expired;
            continuation.ApplicationVersion++;
            continuation.UpdatedAtUtc = nowUtc;
        }

        var removedPayloadIds = retentionCandidates
            .SelectMany(operation => operation.ProtectedPayloads)
            .Where(payload =>
                payload.PurgeAfterUtc <= nowUtc
                && !IsRequiredByActiveContinuation(payload, continuations, nowUtc))
            .OrderBy(payload => payload.PurgeAfterUtc)
            .ThenBy(payload => payload.Id)
            .Take(maximumOperations)
            .Select(payload => payload.Id)
            .ToHashSet(StringComparer.Ordinal);
        dbContext.ApplicationProtectedPayloads.RemoveRange(
            retentionCandidates
                .SelectMany(operation => operation.ProtectedPayloads)
                .Where(payload => removedPayloadIds.Contains(payload.Id)));

        var removedConfirmationIds = retentionCandidates
            .SelectMany(operation => operation.Confirmations)
            .Where(confirmation => confirmation.ExpiresAtUtc <= nowUtc)
            .OrderBy(confirmation => confirmation.ExpiresAtUtc)
            .ThenBy(confirmation => confirmation.Id)
            .Take(maximumOperations)
            .Select(confirmation => confirmation.Id)
            .ToHashSet(StringComparer.Ordinal);
        dbContext.ApplicationOperationConfirmations.RemoveRange(
            retentionCandidates
                .SelectMany(operation => operation.Confirmations)
                .Where(confirmation => removedConfirmationIds.Contains(confirmation.Id)));

        var removedContinuationIds = new HashSet<string>(StringComparer.Ordinal);
        while (removedContinuationIds.Count < maximumOperations)
        {
            var continuation = continuations
                .Where(item =>
                    !removedContinuationIds.Contains(item.Id)
                    && item.PurgeAfterUtc <= nowUtc
                    && !IsActiveContinuation(item, nowUtc)
                    && !continuationLinks.Any(link =>
                        link.Parent == item.Id
                        && !removedContinuationIds.Contains(link.Child))
                    && !HasRetainedContinuationPayload(
                        item,
                        retentionCandidates,
                        removedPayloadIds))
                .OrderBy(item => item.PurgeAfterUtc)
                .ThenBy(item => item.Id)
                .FirstOrDefault();
            if (continuation is null)
            {
                break;
            }

            removedContinuationIds.Add(continuation.Id);
            dbContext.ApplicationOperationContinuations.Remove(continuation);
        }

        var removedReceiptIds = retentionCandidates
            .Where(operation =>
                operation.Receipt is not null
                && operation.PurgeAfterUtc <= nowUtc
                && !operation.Continuations.Any(continuation =>
                    !removedContinuationIds.Contains(continuation.Id)
                    && IsActiveContinuation(continuation, nowUtc))
                && !(operation.Receipt.Reversal == ApplicationReversalAvailability.Available
                    && operation.Receipt.ReversalExpiresAtUtc > nowUtc))
            .OrderBy(operation => operation.PurgeAfterUtc)
            .ThenBy(operation => operation.Id)
            .Take(maximumOperations)
            .Select(operation => operation.Receipt!.Id)
            .ToHashSet(StringComparer.Ordinal);
        dbContext.ApplicationOperationReceipts.RemoveRange(
            retentionCandidates
                .Where(operation =>
                    operation.Receipt is not null
                    && removedReceiptIds.Contains(operation.Receipt.Id))
                .Select(operation => operation.Receipt!));

        var purgedOperations = retentionCandidates
            .Where(operation =>
                operation.PurgeAfterUtc <= nowUtc
                && operation.TerminalAtUtc <= nowUtc
                && !operation.ProtectedPayloads.Any(payload =>
                    !removedPayloadIds.Contains(payload.Id))
                && !operation.Confirmations.Any(confirmation =>
                    !removedConfirmationIds.Contains(confirmation.Id))
                && !operation.Continuations.Any(continuation =>
                    !removedContinuationIds.Contains(continuation.Id))
                && (operation.Receipt is null
                    || removedReceiptIds.Contains(operation.Receipt.Id)))
            .OrderBy(operation => operation.PurgeAfterUtc)
            .ThenBy(operation => operation.Id)
            .Take(maximumOperations)
            .ToArray();
        foreach (var operation in purgedOperations)
        {
            operation.PayloadPurgedAtUtc = nowUtc;
        }

        if (expiringContinuations.Length > 0
            || removedPayloadIds.Count > 0
            || removedConfirmationIds.Count > 0
            || removedContinuationIds.Count > 0
            || removedReceiptIds.Count > 0
            || purgedOperations.Length > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ApplicationOperationRetentionResult(expired.Count, purgedOperations.Length);
    }

    private async Task<T> ExecuteTransactionalAttemptAsync<T>(
        Func<Task<T>> attempt,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var result = await attempt().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Cleanup must preserve the original failure for the execution strategy.
                }
            }

            dbContext.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task RemoveDependentsAsync(
        IReadOnlyCollection<string> operationIds,
        CancellationToken cancellationToken)
    {
        var payloads = await dbContext.ApplicationProtectedPayloads
            .Where(item => operationIds.Contains(item.OperationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var confirmations = await dbContext.ApplicationOperationConfirmations
            .Where(item => operationIds.Contains(item.OperationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var continuations = await dbContext.ApplicationOperationContinuations
            .Where(item => operationIds.Contains(item.OperationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var receipts = await dbContext.ApplicationOperationReceipts
            .Where(item => operationIds.Contains(item.OperationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        dbContext.ApplicationProtectedPayloads.RemoveRange(payloads);
        dbContext.ApplicationOperationConfirmations.RemoveRange(confirmations);
        dbContext.ApplicationOperationContinuations.RemoveRange(continuations);
        dbContext.ApplicationOperationReceipts.RemoveRange(receipts);
    }

    private static bool IsActiveContinuation(
        ApplicationOperationContinuationRecord continuation,
        DateTime nowUtc) =>
        IsPendingContinuation(continuation)
        && continuation.ExpiresAtUtc > nowUtc;

    private static bool IsPendingContinuation(
        ApplicationOperationContinuationRecord continuation) =>
        continuation.State is ApplicationContinuationState.AwaitingDecision
            or ApplicationContinuationState.ReadyToResume;

    private static bool IsRequiredByActiveContinuation(
        ApplicationProtectedPayloadRecord payload,
        IReadOnlyCollection<ApplicationOperationContinuationRecord> continuations,
        DateTime nowUtc)
    {
        if (payload.SubjectKind != ApplicationOperationContentSubjectKind.Continuation)
        {
            return false;
        }

        return continuations.Any(continuation =>
            continuation.Id == payload.SubjectId
            && continuation.OperationId == payload.OperationId
            && continuation.UserProfileId == payload.UserProfileId
            && IsActiveContinuation(continuation, nowUtc));
    }

    private static bool HasRetainedContinuationPayload(
        ApplicationOperationContinuationRecord continuation,
        IReadOnlyCollection<ApplicationOperationRecord> operations,
        IReadOnlySet<string> removedPayloadIds) =>
        operations
            .Where(operation =>
                operation.Id == continuation.OperationId
                && operation.UserProfileId == continuation.UserProfileId)
            .SelectMany(operation => operation.ProtectedPayloads)
            .Any(payload =>
                payload.SubjectKind == ApplicationOperationContentSubjectKind.Continuation
                && payload.SubjectId == continuation.Id
                && !removedPayloadIds.Contains(payload.Id));

    private sealed record ContinuationLink(string Child, string Parent);

    private async Task<long> NextSequenceAsync(
        string operationId,
        CancellationToken cancellationToken) =>
        (await dbContext.ApplicationOperationEvents
            .Where(item => item.OperationId == operationId)
            .Select(item => (long?)item.Sequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0) + 1;

    private static ApplicationOperationOwnerExport EmptyExport() =>
        new(
            string.Empty,
            [
                new(
                    ApplicationExecutionAuthority.Server.ToString(),
                    false,
                    "Empty owner scope; no authority store was queried."),
                new(
                    ApplicationExecutionAuthority.NativeLocal.ToString(),
                    false,
                    "Empty owner scope; no authority store was queried.")
            ],
            "Protected payloads are omitted because recipient-bound export re-encryption is not configured.",
            [],
            [],
            [],
            [],
            [],
            []);
}
