using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.Data.AppOperations;

public sealed class EfApplicationOperationStore(
    ApplicationDbContext dbContext,
    IApplicationOperationContentProtector protector)
    : IApplicationOperationStore
{
    public async Task<ApplicationOperationSnapshot?> FindAsync(
        ApplicationOperationScope scope,
        string operationId,
        CancellationToken cancellationToken)
    {
        scope.Validate();
        ValidateOperationId(operationId);

        var operation = await OwnerOperations(scope)
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken)
            .ConfigureAwait(false);
        return operation is null ? null : ToSnapshot(operation);
    }

    public async Task<ApplicationOperationTransitionResult> CreateAsync(
        ApplicationOperationCreateRequest request,
        CancellationToken cancellationToken)
    {
        ValidateCreateRequest(request);
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
                () => ExecuteWithCleanTrackerAsync(
                    () => CreateAttemptAsync(request, cancellationToken)))
            .ConfigureAwait(false);
    }

    private async Task<ApplicationOperationTransitionResult> CreateAttemptAsync(
        ApplicationOperationCreateRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var existingById = await OwnerOperations(request.Scope)
            .SingleOrDefaultAsync(operation => operation.Id == request.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (existingById is not null)
        {
            EnsureSameCreation(existingById, request);
            return new ApplicationOperationTransitionResult(ToSnapshot(existingById), IsReplay: true);
        }

        if (request.IdempotencyDigest is { } idempotencyDigest)
        {
            var existingByDigest = await OwnerOperations(request.Scope)
                .SingleOrDefaultAsync(
                    operation =>
                        operation.IdempotencyDigest != null
                        && operation.IdempotencyDigest.SequenceEqual(idempotencyDigest),
                    cancellationToken)
                .ConfigureAwait(false);
            if (existingByDigest is not null)
            {
                EnsureSameCreation(existingByDigest, request);
                return new ApplicationOperationTransitionResult(
                    ToSnapshot(existingByDigest),
                    IsReplay: true);
            }
        }

        ApplicationOperationRecord? parent = null;
        ApplicationOperationReceiptRecord? parentReceipt = null;
        if (request.ParentOperationId is not null)
        {
            parent = await OwnerOperations(request.Scope)
                .SingleOrDefaultAsync(
                    operation => operation.Id == request.ParentOperationId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw Conflict();
            parentReceipt = await dbContext.ApplicationOperationReceipts
                .SingleOrDefaultAsync(
                    receipt =>
                        receipt.UserProfileId == request.Scope.UserProfileId
                        && receipt.OperationId == parent.Id,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw Conflict();

            if (parent.Status != ApplicationOperationStatus.Executed
                || parent.ApplicationVersion != request.ParentApplicationVersion
                || parent.Fence != request.ParentFence
                || parentReceipt.Reversal != ApplicationReversalAvailability.Available
                || parentReceipt.ReversalExpiresAtUtc is not { } reversalExpiresAtUtc
                || reversalExpiresAtUtc <= request.CreatedAtUtc
                || parentReceipt.ReversalOperationId is not null)
            {
                throw Conflict();
            }

            parent.ApplicationVersion++;
            parent.UpdatedAtUtc = request.CreatedAtUtc;
            parentReceipt.ReversalOperationId = request.OperationId;
            await AddEventAsync(
                    parent,
                    ApplicationOperationEventKind.ReversalLinked,
                    parent.Status,
                    parent.Status,
                    null,
                    request.CreatedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var operation = new ApplicationOperationRecord
        {
            Id = request.OperationId,
            UserProfileId = request.Scope.UserProfileId,
            Authority = request.Scope.Authority,
            CapabilityCode = request.CapabilityCode,
            CapabilityFamily = request.CapabilityFamily,
            CapabilityVersion = request.CapabilityVersion,
            CapabilityFingerprint = request.CapabilityFingerprint,
            Effect = request.Effect,
            Confirmation = request.Confirmation,
            Status = ApplicationOperationStatus.Proposed,
            ParentOperationId = request.ParentOperationId,
            ParentApplicationVersion = request.ParentApplicationVersion,
            ParentFence = request.ParentFence,
            IdempotencyDigest = request.IdempotencyDigest?.ToArray(),
            CanonicalRequestDigest = request.CanonicalRequestDigest.ToArray(),
            ExpectedDomainVersion = request.ExpectedDomainVersion,
            ExpectedSynchronizationVersion = request.ExpectedSynchronizationVersion,
            ApplicationVersion = 1,
            Fence = 0,
            AttemptCount = 0,
            CreatedAtUtc = request.CreatedAtUtc,
            UpdatedAtUtc = request.CreatedAtUtc,
            ExpiresAtUtc = request.ExpiresAtUtc,
            PurgeAfterUtc = request.PurgeAfterUtc
        };

        dbContext.ApplicationOperations.Add(operation);
        foreach (var content in request.ProtectedContents)
        {
            dbContext.ApplicationProtectedPayloads.Add(new ApplicationProtectedPayloadRecord
            {
                Id = content.PayloadId,
                OperationId = operation.Id,
                UserProfileId = operation.UserProfileId,
                SubjectKind = content.SubjectKind,
                SubjectId = content.SubjectId,
                ContentKind = content.Kind,
                ProtectionVersion = content.ProtectionVersion,
                SchemaVersion = content.SchemaVersion,
                Ciphertext = content.Ciphertext.ToArray(),
                PlaintextLength = content.PlaintextLength,
                CreatedAtUtc = content.CreatedAtUtc,
                PurgeAfterUtc = content.PurgeAfterUtc,
                Operation = operation
            });
        }

        dbContext.ApplicationOperationEvents.Add(CreateEvent(
            operation,
            sequence: 1,
            ApplicationOperationEventKind.Proposed,
            fromStatus: null,
            ApplicationOperationStatus.Proposed,
            failureCode: null,
            request.CreatedAtUtc));

        try
        {
            await SaveAndCommitAsync(transaction, cancellationToken).ConfigureAwait(false);
            return new ApplicationOperationTransitionResult(ToSnapshot(operation), IsReplay: false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            ResetFailedUnitOfWork();
            var replay = await FindCreationReplayAsync(request, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                EnsureSameCreation(replay, request);
                return new ApplicationOperationTransitionResult(ToSnapshot(replay), IsReplay: true);
            }

            throw;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            ResetFailedUnitOfWork();
            throw;
        }
    }

    public async Task<ApplicationOperationTransitionResult> TransitionAsync(
        ApplicationOperationTransitionRequest request,
        CancellationToken cancellationToken)
    {
        request.Scope.Validate();
        request.ExpectedVersion.Validate();
        ValidateDigest(request.DecisionReferenceDigest, nameof(request.DecisionReferenceDigest));
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
                () => ExecuteWithCleanTrackerAsync(
                    () => TransitionAttemptAsync(request, cancellationToken)))
            .ConfigureAwait(false);
    }

    private async Task<ApplicationOperationTransitionResult> TransitionAttemptAsync(
        ApplicationOperationTransitionRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var operation = await OwnerOperations(request.Scope)
            .SingleOrDefaultAsync(item => item.Id == request.OperationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Conflict();

        var decision = ValidateDeclaredDecision(request.Decision);
        var isTerminal = ApplicationOperationStateMachine.IsTerminal(operation.Status);
        if (decision != ApplicationOperationDecision.Unknown
            && DigestsEqual(operation.DecisionReferenceDigest, request.DecisionReferenceDigest))
        {
            if (operation.Decision != decision)
            {
                if (isTerminal || request.TargetStatus != ApplicationOperationStatus.Expired)
                {
                    throw Conflict();
                }
            }
            else if (isTerminal || request.TargetStatus == operation.Status)
            {
                await ValidateDecisionReplayAsync(
                        operation,
                        decision,
                        request.DecisionReferenceDigest,
                        request.ConfirmationIssue,
                        request.ConfirmationUse,
                        cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ApplicationOperationTransitionResult(ToSnapshot(operation), IsReplay: true);
            }
        }

        var internalTransition = ResolveInternalTransitionKind(operation, request, decision);
        string? stableEventId = null;
        if (internalTransition is { } transitionKind)
        {
            stableEventId = CreateInternalTransitionEventId(request, transitionKind);
            if (await IsCommittedInternalTransitionAsync(
                    operation,
                    request,
                    transitionKind,
                    stableEventId,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ApplicationOperationTransitionResult(ToSnapshot(operation), IsReplay: true);
            }
        }

        EnsureVersion(operation, request.ExpectedVersion);
        if (request.TransitionedAtUtc < operation.UpdatedAtUtc
            || request.TargetStatus == ApplicationOperationStatus.Expired
                && request.TransitionedAtUtc < operation.ExpiresAtUtc
            || request.TargetStatus != ApplicationOperationStatus.Expired
                && operation.Status is ApplicationOperationStatus.Proposed
                    or ApplicationOperationStatus.AwaitingProtectedConfirmation
                && request.TransitionedAtUtc >= operation.ExpiresAtUtc)
        {
            throw Conflict();
        }

        var isConfirmationRotation =
            operation.Status == ApplicationOperationStatus.AwaitingProtectedConfirmation
            && request.TargetStatus == ApplicationOperationStatus.AwaitingProtectedConfirmation
            && decision == ApplicationOperationDecision.Accept;
        if (!isConfirmationRotation)
        {
            ApplicationOperationStateMachine.ValidateTransition(
                operation.Status,
                request.TargetStatus,
                request.EffectStarted);
        }
        ValidateTransitionCeremony(operation, request, decision, isConfirmationRotation);
        if (request.TargetStatus == ApplicationOperationStatus.Reversed)
        {
            throw new ApplicationOperationValidationException(
                "A reversal can be settled only by executing its fenced child operation.");
        }

        if (request.TargetStatus == ApplicationOperationStatus.Failed)
        {
            if (request.FailureCode is null or ApplicationOperationFailureCode.Unknown)
            {
                throw new ApplicationOperationValidationException(
                    "A pre-effect failure requires a controlled failure code.");
            }
        }
        else if (request.FailureCode is not null)
        {
            throw new ApplicationOperationValidationException(
                "Only a failed transition may carry a failure code.");
        }

        if (request.TargetStatus == ApplicationOperationStatus.Executing)
        {
            ApplyExecutionLease(operation, request);
        }
        else if (request.Lease is not null)
        {
            throw new ApplicationOperationValidationException(
                "Only an executing transition may carry a lease.");
        }

        if (request.ConfirmationIssue is { } confirmationIssue)
        {
            if (request.TargetStatus != ApplicationOperationStatus.AwaitingProtectedConfirmation
                || operation.Status is not ApplicationOperationStatus.Proposed
                    and not ApplicationOperationStatus.AwaitingProtectedConfirmation)
            {
                throw new ApplicationOperationValidationException(
                    "A confirmation can be issued only while entering protected confirmation.");
            }

            await AddConfirmationAsync(
                    operation,
                    confirmationIssue,
                    request.DecisionReferenceDigest,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.ConfirmationUse is { } confirmationUse)
        {
            if (operation.Status != ApplicationOperationStatus.AwaitingProtectedConfirmation
                || request.TargetStatus != ApplicationOperationStatus.Executing)
            {
                throw new ApplicationOperationValidationException(
                    "A confirmation can be consumed only while entering execution.");
            }

            await ConsumeConfirmationAsync(
                    operation,
                    confirmationUse,
                    request.TransitionedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var fromStatus = operation.Status;
        operation.Status = request.TargetStatus;
        if (decision != ApplicationOperationDecision.Unknown)
        {
            operation.DecisionReferenceDigest = request.DecisionReferenceDigest.ToArray();
            operation.Decision = decision;
        }
        operation.ApplicationVersion++;
        operation.UpdatedAtUtc = request.TransitionedAtUtc;
        if (ApplicationOperationStateMachine.IsTerminal(request.TargetStatus))
        {
            operation.TerminalAtUtc = request.TransitionedAtUtc;
            operation.LeaseId = null;
            operation.LeaseExpiresAtUtc = null;
        }

        await AddEventAsync(
                operation,
                ResolveEventKind(fromStatus, request.TargetStatus, request.Lease),
                fromStatus,
                request.TargetStatus,
                request.FailureCode,
                request.TransitionedAtUtc,
                cancellationToken,
                stableEventId)
            .ConfigureAwait(false);

        try
        {
            await SaveAndCommitAsync(transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ApplicationOperationConflictException(
                "The operation changed while the transition was being settled.",
                exception);
        }

        return new ApplicationOperationTransitionResult(ToSnapshot(operation), IsReplay: false);
    }

    public async Task<ApplicationOperationExecutionResult> ExecuteAsync(
        ApplicationOperationExecutionRequest request,
        IApplicationOperationHandler handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        request.Scope.Validate();
        request.ExpectedVersion.Validate();
        ValidateOperationId(request.OperationId);
        ValidateReferenceId(request.LeaseId, nameof(request.LeaseId));
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
                () => ExecuteWithCleanTrackerAsync(
                    () => ExecuteAttemptAsync(request, handler, cancellationToken)))
            .ConfigureAwait(false);
    }

    private async Task<ApplicationOperationExecutionResult> ExecuteAttemptAsync(
        ApplicationOperationExecutionRequest request,
        IApplicationOperationHandler handler,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var operation = await OwnerOperations(request.Scope)
            .SingleOrDefaultAsync(item => item.Id == request.OperationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Conflict();

        if (operation.Status == ApplicationOperationStatus.Executed)
        {
            var replay = await FindReceiptWithinTransactionAsync(
                    operation,
                    isReplay: true,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new ApplicationOperationConflictException(
                    "An executed operation has no durable receipt.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ApplicationOperationExecutionResult(ToSnapshot(operation), replay);
        }

        EnsureVersion(operation, request.ExpectedVersion);
        if (operation.Status != ApplicationOperationStatus.Executing
            || !string.Equals(operation.LeaseId, request.LeaseId, StringComparison.Ordinal)
            || operation.LeaseExpiresAtUtc is not { } leaseExpiresAtUtc
            || leaseExpiresAtUtc <= request.ExecutedAtUtc
            || !string.Equals(handler.CapabilityCode, operation.CapabilityCode, StringComparison.Ordinal)
            || handler.CapabilityVersion != operation.CapabilityVersion)
        {
            throw Conflict();
        }

        var canonicalPayload = await dbContext.ApplicationProtectedPayloads
            .AsNoTracking()
            .SingleOrDefaultAsync(
                payload =>
                    payload.UserProfileId == operation.UserProfileId
                    && payload.OperationId == operation.Id
                    && payload.SubjectKind == ApplicationOperationContentSubjectKind.Operation
                    && payload.SubjectId == operation.Id
                    && payload.ContentKind == ApplicationProtectedContentKind.CanonicalRequest,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ApplicationOperationProtectionException(
                "The canonical operation request is unavailable.");

        var canonicalRequest = protector.Unprotect(
            CreateProtectionContext(
                operation,
                ApplicationOperationContentSubjectKind.Operation,
                operation.Id,
                ApplicationProtectedContentKind.CanonicalRequest),
            canonicalPayload.Ciphertext,
            canonicalPayload.PlaintextLength);

        var handlerResult = await handler.ExecuteAsync(
                new ApplicationOperationHandlerContext(ToSnapshot(operation), canonicalRequest),
                cancellationToken)
            .ConfigureAwait(false);
        ValidateHandlerResult(operation, handlerResult);

        var receiptCiphertext = protector.Protect(
            CreateProtectionContext(
                operation,
                ApplicationOperationContentSubjectKind.Operation,
                operation.Id,
                ApplicationProtectedContentKind.Receipt),
            handlerResult.ReceiptSource);
        ValidateCiphertext(receiptCiphertext);

        var receipt = new ApplicationOperationReceiptRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            OperationId = operation.Id,
            UserProfileId = operation.UserProfileId,
            ReceiptVersion = handlerResult.ReceiptSchemaVersion,
            BeforeDomainVersion = handlerResult.BeforeVersion.DomainVersion,
            BeforeSynchronizationVersion = handlerResult.BeforeVersion.SynchronizationVersion,
            AfterDomainVersion = handlerResult.AfterVersion.DomainVersion,
            AfterSynchronizationVersion = handlerResult.AfterVersion.SynchronizationVersion,
            Reversal = handlerResult.Reversal,
            ReversalExpiresAtUtc = handlerResult.ReversalExpiresAtUtc,
            CommittedAtUtc = request.ExecutedAtUtc,
            Operation = operation
        };
        dbContext.ApplicationOperationReceipts.Add(receipt);
        dbContext.ApplicationProtectedPayloads.Add(new ApplicationProtectedPayloadRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            OperationId = operation.Id,
            UserProfileId = operation.UserProfileId,
            SubjectKind = ApplicationOperationContentSubjectKind.Operation,
            SubjectId = operation.Id,
            ContentKind = ApplicationProtectedContentKind.Receipt,
            ProtectionVersion = 1,
            SchemaVersion = handlerResult.ReceiptSchemaVersion,
            Ciphertext = receiptCiphertext,
            PlaintextLength = handlerResult.ReceiptSource.Length,
            CreatedAtUtc = request.ExecutedAtUtc,
            PurgeAfterUtc = operation.PurgeAfterUtc,
            Operation = operation
        });

        if (handlerResult.Continuation is { } continuation)
        {
            await AddContinuationAsync(
                    operation,
                    continuation,
                    request.ExecutedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var fromStatus = operation.Status;
        operation.Status = ApplicationOperationStatus.Executed;
        operation.ApplicationVersion++;
        operation.UpdatedAtUtc = request.ExecutedAtUtc;
        operation.TerminalAtUtc = request.ExecutedAtUtc;
        operation.LeaseId = null;
        operation.LeaseExpiresAtUtc = null;
        await AddEventAsync(
                operation,
                ApplicationOperationEventKind.Executed,
                fromStatus,
                operation.Status,
                null,
                request.ExecutedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);

        if (operation.ParentOperationId is not null)
        {
            await SettleReversalParentAsync(operation, request.ExecutedAtUtc, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await SaveAndCommitAsync(transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ApplicationOperationConflictException(
                "The operation changed while the effect was being settled.",
                exception);
        }

        return new ApplicationOperationExecutionResult(
            ToSnapshot(operation),
            ToReceiptSnapshot(
                operation,
                receipt,
                handlerResult.ReceiptSource.ToArray(),
                isReplay: false));
    }

    public async Task<ApplicationOperationReceiptSnapshot?> FindReceiptAsync(
        ApplicationOperationScope scope,
        string operationId,
        bool isReplay,
        CancellationToken cancellationToken)
    {
        scope.Validate();
        ValidateOperationId(operationId);
        var operation = await OwnerOperations(scope)
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken)
            .ConfigureAwait(false);
        return operation is null
            ? null
            : await FindReceiptWithinTransactionAsync(operation, isReplay, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<ApplicationOperationContinuationSnapshot?> FindContinuationAsync(
        ApplicationOperationScope scope,
        string continuationId,
        CancellationToken cancellationToken)
    {
        scope.Validate();
        ValidateReferenceId(continuationId, nameof(continuationId));
        var continuation = await dbContext.ApplicationOperationContinuations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.UserProfileId == scope.UserProfileId
                    && item.Operation.Authority == scope.Authority
                    && item.Id == continuationId,
                cancellationToken)
            .ConfigureAwait(false);
        return continuation is null ? null : ToContinuationSnapshot(continuation, scope.Authority);
    }

    public async Task<ApplicationOperationContinuationSnapshot> ResumeContinuationAsync(
        ApplicationOperationScope scope,
        string continuationId,
        long expectedApplicationVersion,
        DateTime resumedAtUtc,
        CancellationToken cancellationToken)
    {
        scope.Validate();
        ValidateReferenceId(continuationId, nameof(continuationId));
        if (expectedApplicationVersion <= 0)
        {
            throw new ApplicationOperationValidationException(
                "A positive continuation application version is required.");
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
                () => ExecuteWithCleanTrackerAsync(
                    () => ResumeContinuationAttemptAsync(
                        scope,
                        continuationId,
                        expectedApplicationVersion,
                        resumedAtUtc,
                        cancellationToken)))
            .ConfigureAwait(false);
    }

    private async Task<ApplicationOperationContinuationSnapshot> ResumeContinuationAttemptAsync(
        ApplicationOperationScope scope,
        string continuationId,
        long expectedApplicationVersion,
        DateTime resumedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var continuation = await dbContext.ApplicationOperationContinuations
            .Include(item => item.Operation)
            .SingleOrDefaultAsync(
                item =>
                    item.UserProfileId == scope.UserProfileId
                    && item.Operation.Authority == scope.Authority
                    && item.Id == continuationId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Conflict();

        var stableEventId = CreateContinuationResumeEventId(
            scope,
            continuationId,
            expectedApplicationVersion,
            resumedAtUtc);
        if (await IsCommittedContinuationResumeAsync(
                continuation,
                expectedApplicationVersion,
                resumedAtUtc,
                stableEventId,
                cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToContinuationSnapshot(continuation, scope.Authority);
        }

        if (continuation.ApplicationVersion != expectedApplicationVersion
            || continuation.State != ApplicationContinuationState.ReadyToResume
            || continuation.ExpiresAtUtc <= resumedAtUtc
            || !continuation.AllowsAutomaticResume
            || continuation.AutomaticResumeCount != 0
            || continuation.ParentContinuationId is not null)
        {
            throw Conflict();
        }

        continuation.AutomaticResumeCount = 1;
        continuation.State = ApplicationContinuationState.Completed;
        continuation.ResumedAtUtc = resumedAtUtc;
        continuation.UpdatedAtUtc = resumedAtUtc;
        continuation.ApplicationVersion++;
        await AddEventAsync(
                continuation.Operation,
                ApplicationOperationEventKind.ContinuationResumed,
                continuation.Operation.Status,
                continuation.Operation.Status,
                null,
                resumedAtUtc,
                cancellationToken,
                stableEventId)
            .ConfigureAwait(false);

        try
        {
            await SaveAndCommitAsync(transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ApplicationOperationConflictException(
                "The continuation changed while it was being resumed.",
                exception);
        }

        return ToContinuationSnapshot(continuation, scope.Authority);
    }

    private IQueryable<ApplicationOperationRecord> OwnerOperations(ApplicationOperationScope scope) =>
        dbContext.ApplicationOperations.Where(operation =>
            operation.UserProfileId == scope.UserProfileId
            && operation.Authority == scope.Authority);

    private async Task<ApplicationOperationReceiptSnapshot?> FindReceiptWithinTransactionAsync(
        ApplicationOperationRecord operation,
        bool isReplay,
        CancellationToken cancellationToken)
    {
        var receipt = await dbContext.ApplicationOperationReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.UserProfileId == operation.UserProfileId
                    && item.OperationId == operation.Id,
                cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null)
        {
            return null;
        }

        var payload = await dbContext.ApplicationProtectedPayloads
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.UserProfileId == operation.UserProfileId
                    && item.OperationId == operation.Id
                    && item.SubjectKind == ApplicationOperationContentSubjectKind.Operation
                    && item.SubjectId == operation.Id
                    && item.ContentKind == ApplicationProtectedContentKind.Receipt,
                cancellationToken)
            .ConfigureAwait(false);
        if (payload is null)
        {
            throw new ApplicationOperationProtectionException(
                "The durable receipt content is unavailable.");
        }

        var receiptContent = protector.Unprotect(
            CreateProtectionContext(
                operation,
                ApplicationOperationContentSubjectKind.Operation,
                operation.Id,
                ApplicationProtectedContentKind.Receipt),
            payload.Ciphertext,
            payload.PlaintextLength);

        return ToReceiptSnapshot(operation, receipt, receiptContent, isReplay);
    }

    private async Task AddContinuationAsync(
        ApplicationOperationRecord operation,
        ApplicationOperationContinuationWrite continuation,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ValidateReferenceId(continuation.ContinuationId, nameof(continuation.ContinuationId));
        ValidateDigest(continuation.InteractionScopeDigest, nameof(continuation.InteractionScopeDigest));
        if (!Enum.IsDefined(continuation.Workflow)
            || continuation.Workflow == ApplicationContinuationWorkflow.Unknown
            || continuation.WorkflowVersion <= 0
            || continuation.StateSchemaVersion <= 0
            || continuation.ProtectedStateSource.IsEmpty
            || continuation.ProtectedStateSource.Length
                > ApplicationOperationLimits.MaximumProtectedPlaintextBytes
            || continuation.ExpiresAtUtc <= continuation.CreatedAtUtc
            || continuation.PurgeAfterUtc < continuation.ExpiresAtUtc
            || continuation.AllowsAutomaticResume
                && continuation.Workflow != ApplicationContinuationWorkflow.PostReceiptResume
            || continuation.ParentContinuationId is not null
                && continuation.AllowsAutomaticResume)
        {
            throw new ApplicationOperationValidationException("The continuation is invalid.");
        }

        if (string.Equals(
                continuation.ParentContinuationId,
                continuation.ContinuationId,
                StringComparison.Ordinal))
        {
            throw new ApplicationOperationValidationException(
                "A continuation cannot be its own parent.");
        }

        if (continuation.ParentContinuationId is { } parentContinuationId)
        {
            var parent = await dbContext.ApplicationOperationContinuations
                .Include(item => item.Operation)
                .SingleOrDefaultAsync(
                    item =>
                        item.Id == parentContinuationId
                        && item.UserProfileId == operation.UserProfileId
                        && item.Operation.Authority == operation.Authority,
                    cancellationToken)
                .ConfigureAwait(false);
            if (parent is null
                || parent.ParentContinuationId is not null
                || parent.AllowsAutomaticResume)
            {
                throw new ApplicationOperationValidationException(
                    "A continuation parent must be a same-scope non-resumable root.");
            }
        }

        var state = continuation.Workflow == ApplicationContinuationWorkflow.PostReceiptResume
            ? ApplicationContinuationState.ReadyToResume
            : ApplicationContinuationState.AwaitingDecision;
        var ciphertext = protector.Protect(
            CreateProtectionContext(
                operation,
                ApplicationOperationContentSubjectKind.Continuation,
                continuation.ContinuationId,
                ApplicationProtectedContentKind.ContinuationState),
            continuation.ProtectedStateSource);
        ValidateCiphertext(ciphertext);

        var record = new ApplicationOperationContinuationRecord
        {
            Id = continuation.ContinuationId,
            OperationId = operation.Id,
            UserProfileId = operation.UserProfileId,
            Workflow = continuation.Workflow,
            WorkflowVersion = continuation.WorkflowVersion,
            State = state,
            InteractionScopeDigest = continuation.InteractionScopeDigest.ToArray(),
            ParentContinuationId = continuation.ParentContinuationId,
            AllowsAutomaticResume = continuation.AllowsAutomaticResume,
            AutomaticResumeCount = 0,
            ApplicationVersion = 1,
            CreatedAtUtc = continuation.CreatedAtUtc,
            UpdatedAtUtc = continuation.CreatedAtUtc,
            ExpiresAtUtc = continuation.ExpiresAtUtc,
            PurgeAfterUtc = continuation.PurgeAfterUtc,
            Operation = operation
        };
        dbContext.ApplicationOperationContinuations.Add(record);
        dbContext.ApplicationProtectedPayloads.Add(new ApplicationProtectedPayloadRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            OperationId = operation.Id,
            UserProfileId = operation.UserProfileId,
            SubjectKind = ApplicationOperationContentSubjectKind.Continuation,
            SubjectId = continuation.ContinuationId,
            ContentKind = ApplicationProtectedContentKind.ContinuationState,
            ProtectionVersion = 1,
            SchemaVersion = continuation.StateSchemaVersion,
            Ciphertext = ciphertext,
            PlaintextLength = continuation.ProtectedStateSource.Length,
            CreatedAtUtc = nowUtc,
            PurgeAfterUtc = continuation.PurgeAfterUtc,
            Operation = operation
        });
        await AddEventAsync(
                operation,
                ApplicationOperationEventKind.ContinuationCreated,
                operation.Status,
                operation.Status,
                null,
                nowUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SettleReversalParentAsync(
        ApplicationOperationRecord reversal,
        DateTime settledAtUtc,
        CancellationToken cancellationToken)
    {
        var parent = await dbContext.ApplicationOperations
            .SingleOrDefaultAsync(
                operation =>
                    operation.UserProfileId == reversal.UserProfileId
                    && operation.Authority == reversal.Authority
                    && operation.Id == reversal.ParentOperationId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Conflict();
        var parentReceipt = await dbContext.ApplicationOperationReceipts
            .SingleOrDefaultAsync(
                receipt =>
                    receipt.UserProfileId == reversal.UserProfileId
                    && receipt.OperationId == parent.Id,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Conflict();

        if (parent.Status != ApplicationOperationStatus.Executed
            || parent.ApplicationVersion != reversal.ParentApplicationVersion + 1
            || parent.Fence != reversal.ParentFence
            || parentReceipt.Reversal != ApplicationReversalAvailability.Available
            || parentReceipt.ReversalOperationId != reversal.Id
            || parentReceipt.ReversalExpiresAtUtc is not { } reversalExpiresAtUtc
            || reversalExpiresAtUtc <= settledAtUtc)
        {
            throw Conflict();
        }

        var fromStatus = parent.Status;
        parent.Status = ApplicationOperationStatus.Reversed;
        parent.ApplicationVersion++;
        parent.UpdatedAtUtc = settledAtUtc;
        parent.TerminalAtUtc = settledAtUtc;
        parentReceipt.Reversal = ApplicationReversalAvailability.Completed;
        await AddEventAsync(
                parent,
                ApplicationOperationEventKind.Reversed,
                fromStatus,
                parent.Status,
                null,
                settledAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task AddConfirmationAsync(
        ApplicationOperationRecord operation,
        ApplicationOperationConfirmationIssue confirmation,
        byte[] decisionReferenceDigest,
        CancellationToken cancellationToken)
    {
        ValidateReferenceId(confirmation.ConfirmationReferenceId, nameof(confirmation.ConfirmationReferenceId));
        ValidateDigest(confirmation.ConfirmationDigest, nameof(confirmation.ConfirmationDigest));
        if (confirmation.ExpiresAtUtc <= confirmation.CreatedAtUtc
            || confirmation.ExpiresAtUtc > operation.ExpiresAtUtc)
        {
            throw new ApplicationOperationValidationException(
                "Confirmation lifecycle is outside the operation lifecycle.");
        }

        var current = await dbContext.ApplicationOperationConfirmations
            .Where(item =>
                item.OperationId == operation.Id
                && item.UserProfileId == operation.UserProfileId
                && item.ConsumedAtUtc == null
                && item.RevokedAtUtc == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var prior in current)
        {
            prior.RevokedAtUtc = confirmation.CreatedAtUtc;
            prior.ConsumedAtUtc = prior.ExpiresAtUtc < confirmation.CreatedAtUtc
                ? prior.ExpiresAtUtc
                : confirmation.CreatedAtUtc;
            prior.ConsumedApplicationVersion = operation.ApplicationVersion + 1;
            prior.ConsumedFence = operation.Fence;
        }

        dbContext.ApplicationOperationConfirmations.Add(new ApplicationOperationConfirmationRecord
        {
            Id = confirmation.ConfirmationReferenceId,
            OperationId = operation.Id,
            UserProfileId = operation.UserProfileId,
            ConfirmationDigest = confirmation.ConfirmationDigest.ToArray(),
            DecisionReferenceDigest = decisionReferenceDigest.ToArray(),
            CreatedAtUtc = confirmation.CreatedAtUtc,
            ExpiresAtUtc = confirmation.ExpiresAtUtc,
            Operation = operation
        });
    }

    private async Task ConsumeConfirmationAsync(
        ApplicationOperationRecord operation,
        ApplicationOperationConfirmationUse confirmationUse,
        DateTime transitionedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateReferenceId(confirmationUse.ConfirmationReferenceId, nameof(confirmationUse.ConfirmationReferenceId));
        ValidateDigest(confirmationUse.ConfirmationDigest, nameof(confirmationUse.ConfirmationDigest));
        var confirmation = await dbContext.ApplicationOperationConfirmations
            .SingleOrDefaultAsync(
                item =>
                    item.UserProfileId == operation.UserProfileId
                    && item.OperationId == operation.Id
                    && item.Id == confirmationUse.ConfirmationReferenceId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Conflict();

        if (confirmation.ConsumedAtUtc is not null
            || confirmation.RevokedAtUtc is not null
            || confirmation.ExpiresAtUtc <= transitionedAtUtc
            || confirmationUse.ConsumedAtUtc != transitionedAtUtc
            || !CryptographicOperations.FixedTimeEquals(
                confirmation.ConfirmationDigest,
                confirmationUse.ConfirmationDigest))
        {
            throw Conflict();
        }

        confirmation.ConsumedAtUtc = transitionedAtUtc;
        confirmation.ConsumedApplicationVersion = operation.ApplicationVersion + 1;
        confirmation.ConsumedFence = operation.Fence;
    }

    private async Task ValidateDecisionReplayAsync(
        ApplicationOperationRecord operation,
        ApplicationOperationDecision decision,
        byte[] decisionReferenceDigest,
        ApplicationOperationConfirmationIssue? confirmationIssue,
        ApplicationOperationConfirmationUse? confirmationUse,
        CancellationToken cancellationToken)
    {
        if (ApplicationOperationStateMachine.IsTerminal(operation.Status))
        {
            return;
        }

        if (decision == ApplicationOperationDecision.Confirm)
        {
            if (confirmationIssue is not null || confirmationUse is null)
            {
                throw Conflict();
            }

            var confirmation = await dbContext.ApplicationOperationConfirmations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item =>
                        item.OperationId == operation.Id
                        && item.UserProfileId == operation.UserProfileId
                        && item.Id == confirmationUse.ConfirmationReferenceId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw Conflict();
            if (confirmation.RevokedAtUtc is not null
                || confirmation.ConsumedAtUtc is null
                || !CryptographicOperations.FixedTimeEquals(
                    confirmation.ConfirmationDigest,
                    confirmationUse.ConfirmationDigest))
            {
                throw Conflict();
            }

            return;
        }

        if (decision == ApplicationOperationDecision.Accept
            && operation.Confirmation == ApplicationConfirmationPolicy.ProtectedConfirmation)
        {
            if (confirmationIssue is null || confirmationUse is not null)
            {
                throw Conflict();
            }

            var confirmation = await dbContext.ApplicationOperationConfirmations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item =>
                        item.OperationId == operation.Id
                        && item.UserProfileId == operation.UserProfileId
                        && item.Id == confirmationIssue.ConfirmationReferenceId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw Conflict();
            if (!CryptographicOperations.FixedTimeEquals(
                    confirmation.DecisionReferenceDigest,
                    decisionReferenceDigest)
                || !CryptographicOperations.FixedTimeEquals(
                    confirmation.ConfirmationDigest,
                    confirmationIssue.ConfirmationDigest))
            {
                throw Conflict();
            }

            return;
        }

        if (confirmationIssue is not null || confirmationUse is not null)
        {
            throw Conflict();
        }
    }

    private static ApplicationOperationDecision ValidateDeclaredDecision(
        ApplicationOperationDecision declared)
    {
        if (!Enum.IsDefined(declared))
        {
            throw new ApplicationOperationValidationException(
                "A known operation decision is required.");
        }

        return declared;
    }

    private static void ValidateTransitionCeremony(
        ApplicationOperationRecord operation,
        ApplicationOperationTransitionRequest request,
        ApplicationOperationDecision decision,
        bool isConfirmationRotation)
    {
        var valid = (operation.Status, request.TargetStatus, decision) switch
        {
            (ApplicationOperationStatus.Proposed, ApplicationOperationStatus.Executing,
                ApplicationOperationDecision.Accept) =>
                operation.Confirmation is ApplicationConfirmationPolicy.Accept
                    or ApplicationConfirmationPolicy.Gesture
                && request.Lease is not null
                && request.ConfirmationIssue is null
                && request.ConfirmationUse is null,
            (ApplicationOperationStatus.Proposed,
                ApplicationOperationStatus.AwaitingProtectedConfirmation,
                ApplicationOperationDecision.Accept) =>
                operation.Confirmation == ApplicationConfirmationPolicy.ProtectedConfirmation
                && request.Lease is null
                && request.ConfirmationIssue is not null
                && request.ConfirmationUse is null,
            (ApplicationOperationStatus.AwaitingProtectedConfirmation,
                ApplicationOperationStatus.AwaitingProtectedConfirmation,
                ApplicationOperationDecision.Accept) when isConfirmationRotation =>
                operation.Confirmation == ApplicationConfirmationPolicy.ProtectedConfirmation
                && request.Lease is null
                && request.ConfirmationIssue is not null
                && request.ConfirmationUse is null,
            (ApplicationOperationStatus.AwaitingProtectedConfirmation,
                ApplicationOperationStatus.Executing,
                ApplicationOperationDecision.Confirm) =>
                operation.Confirmation == ApplicationConfirmationPolicy.ProtectedConfirmation
                && request.Lease is not null
                && request.ConfirmationIssue is null
                && request.ConfirmationUse is not null,
            (ApplicationOperationStatus.Proposed, ApplicationOperationStatus.Rejected,
                ApplicationOperationDecision.Reject)
                or (ApplicationOperationStatus.Proposed, ApplicationOperationStatus.Cancelled,
                    ApplicationOperationDecision.Cancel)
                or (ApplicationOperationStatus.AwaitingProtectedConfirmation,
                    ApplicationOperationStatus.Rejected, ApplicationOperationDecision.Reject)
                or (ApplicationOperationStatus.AwaitingProtectedConfirmation,
                    ApplicationOperationStatus.Cancelled, ApplicationOperationDecision.Cancel) =>
                request.Lease is null
                && request.ConfirmationIssue is null
                && request.ConfirmationUse is null,
            (_, ApplicationOperationStatus.Expired, _) =>
                request.Lease is null
                && request.ConfirmationIssue is null
                && request.ConfirmationUse is null,
            (ApplicationOperationStatus.Executing, ApplicationOperationStatus.Executing,
                ApplicationOperationDecision.Unknown)
                or (ApplicationOperationStatus.Executing, ApplicationOperationStatus.Failed,
                    ApplicationOperationDecision.Unknown) =>
                request.ConfirmationIssue is null
                && request.ConfirmationUse is null,
            _ => false
        };

        if (!valid)
        {
            throw new ApplicationOperationValidationException(
                "The transition does not satisfy the operation confirmation policy.");
        }
    }

    private static void ApplyExecutionLease(
        ApplicationOperationRecord operation,
        ApplicationOperationTransitionRequest request)
    {
        var lease = request.Lease
            ?? throw new ApplicationOperationValidationException(
                "An execution transition requires a lease.");
        ValidateReferenceId(lease.LeaseId, nameof(lease.LeaseId));
        if (lease.LeaseExpiresAtUtc <= request.TransitionedAtUtc)
        {
            throw new ApplicationOperationValidationException("The execution lease must expire in the future.");
        }

        if (operation.Status == ApplicationOperationStatus.Executing)
        {
            if (!lease.RecoverExpiredLease
                || operation.LeaseExpiresAtUtc is not { } oldExpiry
                || oldExpiry > request.TransitionedAtUtc)
            {
                throw Conflict();
            }

            operation.Fence++;
        }
        else if (lease.RecoverExpiredLease)
        {
            throw new ApplicationOperationValidationException(
                "Only an expired executing lease can be recovered.");
        }

        operation.LeaseId = lease.LeaseId;
        operation.LeaseExpiresAtUtc = lease.LeaseExpiresAtUtc;
        operation.AttemptCount++;
    }

    private async Task AddEventAsync(
        ApplicationOperationRecord operation,
        ApplicationOperationEventKind kind,
        ApplicationOperationStatus? fromStatus,
        ApplicationOperationStatus toStatus,
        ApplicationOperationFailureCode? failureCode,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken,
        string? eventId = null)
    {
        var sequence = await dbContext.ApplicationOperationEvents
            .Where(item => item.OperationId == operation.Id)
            .Select(item => (long?)item.Sequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;
        sequence += dbContext.ChangeTracker
            .Entries<ApplicationOperationEventRecord>()
            .Count(entry =>
                entry.State == EntityState.Added
                && entry.Entity.OperationId == operation.Id);

        dbContext.ApplicationOperationEvents.Add(CreateEvent(
            operation,
            sequence + 1,
            kind,
            fromStatus,
            toStatus,
            failureCode,
            occurredAtUtc,
            eventId));
    }

    private static ApplicationOperationEventRecord CreateEvent(
        ApplicationOperationRecord operation,
        long sequence,
        ApplicationOperationEventKind kind,
        ApplicationOperationStatus? fromStatus,
        ApplicationOperationStatus toStatus,
        ApplicationOperationFailureCode? failureCode,
        DateTime occurredAtUtc,
        string? eventId = null) =>
        new()
        {
            Id = eventId ?? Guid.NewGuid().ToString("N"),
            OperationId = operation.Id,
            UserProfileId = operation.UserProfileId,
            Sequence = sequence,
            Kind = kind,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            FailureCode = failureCode,
            ApplicationVersion = operation.ApplicationVersion,
            Fence = operation.Fence,
            OccurredAtUtc = occurredAtUtc,
            Operation = operation
        };

    private async Task SaveAndCommitAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureVersion(
        ApplicationOperationRecord operation,
        ApplicationOperationVersion expected)
    {
        if (operation.ApplicationVersion != expected.ApplicationVersion
            || operation.Fence != expected.Fence)
        {
            throw Conflict();
        }
    }

    private async Task<bool> IsCommittedInternalTransitionAsync(
        ApplicationOperationRecord operation,
        ApplicationOperationTransitionRequest request,
        InternalTransitionKind transitionKind,
        string stableEventId,
        CancellationToken cancellationToken)
    {
        var expectedFence = request.ExpectedVersion.Fence
            + (transitionKind == InternalTransitionKind.LeaseRecovery ? 1 : 0);
        var expectedApplicationVersion = request.ExpectedVersion.ApplicationVersion + 1;
        if (operation.Status != request.TargetStatus
            || operation.ApplicationVersion != expectedApplicationVersion
            || operation.Fence != expectedFence
            || !DatabaseTimesEqual(operation.UpdatedAtUtc, request.TransitionedAtUtc)
            || ApplicationOperationStateMachine.IsTerminal(request.TargetStatus)
                && !DatabaseTimesEqual(operation.TerminalAtUtc, request.TransitionedAtUtc))
        {
            return false;
        }

        if (transitionKind == InternalTransitionKind.LeaseRecovery)
        {
            if (request.Lease is not { } lease
                || !string.Equals(operation.LeaseId, lease.LeaseId, StringComparison.Ordinal)
                || !DatabaseTimesEqual(operation.LeaseExpiresAtUtc, lease.LeaseExpiresAtUtc))
            {
                return false;
            }
        }
        else if (operation.LeaseId is not null || operation.LeaseExpiresAtUtc is not null)
        {
            return false;
        }

        var expectedKind = ResolveInternalEventKind(transitionKind);
        var committedEvent = await dbContext.ApplicationOperationEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                operationEvent =>
                    operationEvent.Id == stableEventId
                    && operationEvent.OperationId == operation.Id
                    && operationEvent.UserProfileId == request.Scope.UserProfileId,
                cancellationToken)
            .ConfigureAwait(false);
        return committedEvent is not null
            && committedEvent.Kind == expectedKind
            && committedEvent.ToStatus == request.TargetStatus
            && committedEvent.FailureCode == request.FailureCode
            && committedEvent.ApplicationVersion == expectedApplicationVersion
            && committedEvent.Fence == expectedFence
            && DatabaseTimesEqual(committedEvent.OccurredAtUtc, request.TransitionedAtUtc)
            && (transitionKind switch
            {
                InternalTransitionKind.LeaseRecovery or InternalTransitionKind.PreEffectFailure =>
                    committedEvent.FromStatus == ApplicationOperationStatus.Executing,
                InternalTransitionKind.Expiration =>
                    committedEvent.FromStatus is ApplicationOperationStatus.Proposed
                        or ApplicationOperationStatus.AwaitingProtectedConfirmation
                        or ApplicationOperationStatus.Executing,
                _ => false
            });
    }

    private async Task<bool> IsCommittedContinuationResumeAsync(
        ApplicationOperationContinuationRecord continuation,
        long expectedApplicationVersion,
        DateTime resumedAtUtc,
        string stableEventId,
        CancellationToken cancellationToken)
    {
        if (continuation.ApplicationVersion != expectedApplicationVersion + 1
            || continuation.State != ApplicationContinuationState.Completed
            || !continuation.AllowsAutomaticResume
            || continuation.AutomaticResumeCount != 1
            || continuation.ParentContinuationId is not null
            || !DatabaseTimesEqual(continuation.ResumedAtUtc, resumedAtUtc)
            || !DatabaseTimesEqual(continuation.UpdatedAtUtc, resumedAtUtc)
            || continuation.ExpiresAtUtc <= resumedAtUtc)
        {
            return false;
        }

        var committedEvent = await dbContext.ApplicationOperationEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                operationEvent =>
                    operationEvent.Id == stableEventId
                    && operationEvent.OperationId == continuation.OperationId
                    && operationEvent.UserProfileId == continuation.UserProfileId,
                cancellationToken)
            .ConfigureAwait(false);
        return committedEvent is not null
            && committedEvent.Kind == ApplicationOperationEventKind.ContinuationResumed
            && committedEvent.FromStatus == continuation.Operation.Status
            && committedEvent.ToStatus == continuation.Operation.Status
            && committedEvent.FailureCode is null
            && committedEvent.ApplicationVersion == continuation.Operation.ApplicationVersion
            && committedEvent.Fence == continuation.Operation.Fence
            && DatabaseTimesEqual(committedEvent.OccurredAtUtc, resumedAtUtc);
    }

    private static InternalTransitionKind? ResolveInternalTransitionKind(
        ApplicationOperationRecord operation,
        ApplicationOperationTransitionRequest request,
        ApplicationOperationDecision decision)
    {
        if (decision != ApplicationOperationDecision.Unknown)
        {
            return null;
        }

        if (operation.Status == ApplicationOperationStatus.Executing
            && request.TargetStatus == ApplicationOperationStatus.Executing
            && request.Lease is { RecoverExpiredLease: true }
            && request.ConfirmationIssue is null
            && request.ConfirmationUse is null
            && request.FailureCode is null
            && !request.EffectStarted
            && IsExactLeaseRecoveryAttemptOrReplay(operation, request))
        {
            return InternalTransitionKind.LeaseRecovery;
        }

        if (request.TargetStatus == ApplicationOperationStatus.Failed
            && request.Lease is null
            && request.ConfirmationIssue is null
            && request.ConfirmationUse is null
            && request.FailureCode is { } failureCode
            && Enum.IsDefined(failureCode)
            && failureCode != ApplicationOperationFailureCode.Unknown
            && !request.EffectStarted
            && IsExactPreEffectFailureAttemptOrReplay(operation, request))
        {
            return InternalTransitionKind.PreEffectFailure;
        }

        if (request.TargetStatus == ApplicationOperationStatus.Expired
            && request.Lease is null
            && request.ConfirmationIssue is null
            && request.ConfirmationUse is null
            && request.FailureCode is null
            && !request.EffectStarted
            && IsExactExpirationAttemptOrReplay(operation, request))
        {
            return InternalTransitionKind.Expiration;
        }

        throw Conflict();
    }

    private static bool IsExactLeaseRecoveryAttemptOrReplay(
        ApplicationOperationRecord operation,
        ApplicationOperationTransitionRequest request)
    {
        var lease = request.Lease!;
        var isAttempt =
            HasExpectedVersion(operation, request.ExpectedVersion)
            && IsValidReferenceId(lease.LeaseId)
            && operation.LeaseId is not null
            && operation.LeaseExpiresAtUtc is { } previousLeaseExpiry
            && previousLeaseExpiry <= request.TransitionedAtUtc
            && lease.LeaseExpiresAtUtc > request.TransitionedAtUtc;
        var isReplay =
            operation.ApplicationVersion == request.ExpectedVersion.ApplicationVersion + 1
            && operation.Fence == request.ExpectedVersion.Fence + 1
            && DatabaseTimesEqual(operation.UpdatedAtUtc, request.TransitionedAtUtc)
            && string.Equals(operation.LeaseId, lease.LeaseId, StringComparison.Ordinal)
            && DatabaseTimesEqual(operation.LeaseExpiresAtUtc, lease.LeaseExpiresAtUtc);
        return isAttempt || isReplay;
    }

    private static bool IsExactPreEffectFailureAttemptOrReplay(
        ApplicationOperationRecord operation,
        ApplicationOperationTransitionRequest request)
    {
        var isAttempt =
            operation.Status == ApplicationOperationStatus.Executing
            && HasExpectedVersion(operation, request.ExpectedVersion)
            && operation.LeaseId is not null
            && operation.LeaseExpiresAtUtc is not null;
        var isReplay =
            operation.Status == ApplicationOperationStatus.Failed
            && operation.ApplicationVersion == request.ExpectedVersion.ApplicationVersion + 1
            && operation.Fence == request.ExpectedVersion.Fence
            && DatabaseTimesEqual(operation.UpdatedAtUtc, request.TransitionedAtUtc)
            && DatabaseTimesEqual(operation.TerminalAtUtc, request.TransitionedAtUtc);
        return isAttempt || isReplay;
    }

    private static bool IsExactExpirationAttemptOrReplay(
        ApplicationOperationRecord operation,
        ApplicationOperationTransitionRequest request)
    {
        var isAttempt =
            operation.Status is ApplicationOperationStatus.Proposed
                or ApplicationOperationStatus.AwaitingProtectedConfirmation
            && HasExpectedVersion(operation, request.ExpectedVersion)
            && request.TransitionedAtUtc >= operation.ExpiresAtUtc;
        var isReplay =
            operation.Status == ApplicationOperationStatus.Expired
            && operation.ApplicationVersion == request.ExpectedVersion.ApplicationVersion + 1
            && operation.Fence == request.ExpectedVersion.Fence
            && DatabaseTimesEqual(operation.UpdatedAtUtc, request.TransitionedAtUtc)
            && DatabaseTimesEqual(operation.TerminalAtUtc, request.TransitionedAtUtc);
        return isAttempt || isReplay;
    }

    private static bool HasExpectedVersion(
        ApplicationOperationRecord operation,
        ApplicationOperationVersion expected) =>
        operation.ApplicationVersion == expected.ApplicationVersion
        && operation.Fence == expected.Fence;

    private static bool IsValidReferenceId(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= ApplicationOperationLimits.MaximumReferenceIdLength;

    private static ApplicationOperationEventKind ResolveInternalEventKind(
        InternalTransitionKind transitionKind) =>
        transitionKind switch
        {
            InternalTransitionKind.LeaseRecovery => ApplicationOperationEventKind.LeaseRecovered,
            InternalTransitionKind.PreEffectFailure => ApplicationOperationEventKind.Failed,
            InternalTransitionKind.Expiration => ApplicationOperationEventKind.Expired,
            _ => throw new ArgumentOutOfRangeException(nameof(transitionKind), transitionKind, null)
        };

    private static string CreateInternalTransitionEventId(
        ApplicationOperationTransitionRequest request,
        InternalTransitionKind transitionKind) =>
        CreateStableEventId(writer =>
        {
            writer.Write("application-operation-internal-transition-v1");
            writer.Write((int)transitionKind);
            writer.Write(request.OperationId);
            writer.Write(request.Scope.UserProfileId);
            writer.Write((int)request.Scope.Authority);
            writer.Write(request.ExpectedVersion.ApplicationVersion);
            writer.Write(request.ExpectedVersion.Fence);
            writer.Write((int)request.TargetStatus);
            writer.Write(request.DecisionReferenceDigest.Length);
            writer.Write(request.DecisionReferenceDigest);
            writer.Write(request.TransitionedAtUtc.Ticks);
            writer.Write(request.Lease is not null);
            if (request.Lease is { } lease)
            {
                writer.Write(lease.LeaseId);
                writer.Write(lease.LeaseExpiresAtUtc.Ticks);
                writer.Write(lease.RecoverExpiredLease);
            }

            writer.Write(request.FailureCode is not null);
            if (request.FailureCode is { } failureCode)
            {
                writer.Write((int)failureCode);
            }

            writer.Write(request.EffectStarted);
        });

    private static string CreateContinuationResumeEventId(
        ApplicationOperationScope scope,
        string continuationId,
        long expectedApplicationVersion,
        DateTime resumedAtUtc) =>
        CreateStableEventId(writer =>
        {
            writer.Write("application-operation-continuation-resume-v1");
            writer.Write(scope.UserProfileId);
            writer.Write((int)scope.Authority);
            writer.Write(continuationId);
            writer.Write(expectedApplicationVersion);
            writer.Write(resumedAtUtc.Ticks);
        });

    private static string CreateStableEventId(Action<BinaryWriter> writeIdentity)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writeIdentity(writer);
        }

        var digest = SHA256.HashData(stream.ToArray());
        return $"i_{Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }

    private static bool DatabaseTimesEqual(DateTime? left, DateTime? right) =>
        left is null || right is null
            ? left is null && right is null
            : left.Value.Ticks / 10 == right.Value.Ticks / 10;

    private async Task<ApplicationOperationRecord?> FindCreationReplayAsync(
        ApplicationOperationCreateRequest request,
        CancellationToken cancellationToken)
    {
        var byId = await OwnerOperations(request.Scope)
            .AsNoTracking()
            .SingleOrDefaultAsync(
                operation => operation.Id == request.OperationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (byId is not null || request.IdempotencyDigest is not { } idempotencyDigest)
        {
            return byId;
        }

        return await OwnerOperations(request.Scope)
            .AsNoTracking()
            .SingleOrDefaultAsync(
                operation =>
                    operation.IdempotencyDigest != null
                    && operation.IdempotencyDigest.SequenceEqual(idempotencyDigest),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void ResetFailedUnitOfWork() => dbContext.ChangeTracker.Clear();

    private async Task<T> ExecuteWithCleanTrackerAsync<T>(Func<Task<T>> attempt)
    {
        ResetFailedUnitOfWork();
        try
        {
            return await attempt().ConfigureAwait(false);
        }
        catch
        {
            ResetFailedUnitOfWork();
            throw;
        }
    }

    private static void EnsureSameCreation(
        ApplicationOperationRecord operation,
        ApplicationOperationCreateRequest request)
    {
        if (!string.Equals(operation.UserProfileId, request.Scope.UserProfileId, StringComparison.Ordinal)
            || operation.Authority != request.Scope.Authority
            || !string.Equals(operation.CapabilityCode, request.CapabilityCode, StringComparison.Ordinal)
            || !string.Equals(operation.CapabilityFamily, request.CapabilityFamily, StringComparison.Ordinal)
            || operation.CapabilityVersion != request.CapabilityVersion
            || !string.Equals(
                operation.CapabilityFingerprint,
                request.CapabilityFingerprint,
                StringComparison.Ordinal)
            || operation.Effect != request.Effect
            || operation.Confirmation != request.Confirmation
            || !string.Equals(operation.ParentOperationId, request.ParentOperationId, StringComparison.Ordinal)
            || operation.ParentApplicationVersion != request.ParentApplicationVersion
            || operation.ParentFence != request.ParentFence
            || operation.ExpectedDomainVersion != request.ExpectedDomainVersion
            || operation.ExpectedSynchronizationVersion != request.ExpectedSynchronizationVersion
            || !DigestsEqual(operation.IdempotencyDigest, request.IdempotencyDigest)
            || !DigestsEqual(operation.CanonicalRequestDigest, request.CanonicalRequestDigest))
        {
            throw Conflict();
        }
    }

    private static bool DigestsEqual(byte[]? left, byte[]? right) =>
        left is null || right is null
            ? left is null && right is null
            : CryptographicOperations.FixedTimeEquals(left, right);

    private static void ValidateCreateRequest(ApplicationOperationCreateRequest request)
    {
        request.Scope.Validate();
        ValidateOperationId(request.OperationId);
        ValidateDigest(request.CanonicalRequestDigest, nameof(request.CanonicalRequestDigest));
        if (string.IsNullOrWhiteSpace(request.CapabilityCode)
            || request.CapabilityCode.Length > ApplicationOperationLimits.MaximumCapabilityCodeLength
            || string.IsNullOrWhiteSpace(request.CapabilityFamily)
            || request.CapabilityFamily.Length > ApplicationOperationLimits.MaximumCapabilityFamilyLength
            || request.CapabilityVersion <= 0
            || !ApplicationOperationFingerprint.IsCanonical(request.CapabilityFingerprint)
            || request.Effect is not ApplicationEffectClass.Write
                and not ApplicationEffectClass.Launch
                and not ApplicationEffectClass.Composite
            || request.Confirmation is not ApplicationConfirmationPolicy.Gesture
                and not ApplicationConfirmationPolicy.Accept
                and not ApplicationConfirmationPolicy.ProtectedConfirmation
            || request.ExpectedDomainVersion < 0
            || request.ExpectedSynchronizationVersion < 0
            || request.ExpiresAtUtc <= request.CreatedAtUtc
            || request.PurgeAfterUtc < request.CreatedAtUtc
            || request.ParentOperationId is null
                != (request.ParentApplicationVersion is null || request.ParentFence is null)
            || request.ParentApplicationVersion is <= 0
            || request.ParentFence is < 0
            || request.Effect == ApplicationEffectClass.Composite
                && request.IdempotencyDigest is null)
        {
            throw new ApplicationOperationValidationException(
                "The operation metadata is invalid.");
        }

        if (request.ProtectedContents.Count == 0
            || request.ProtectedContents.Count(content =>
                content.Kind == ApplicationProtectedContentKind.CanonicalRequest) != 1
            || request.ProtectedContents.Select(content => content.Kind).Distinct().Count()
                != request.ProtectedContents.Count
            || request.ProtectedContents.Any(content =>
                content.OperationId != request.OperationId
                || content.SubjectKind != ApplicationOperationContentSubjectKind.Operation
                || content.SubjectId != request.OperationId
                || content.ProtectionVersion != 1
                || content.SchemaVersion <= 0
                || content.Ciphertext.Length == 0
                || content.Ciphertext.Length
                    > ApplicationOperationLimits.MaximumProtectedCiphertextBytes
                || content.PlaintextLength <= 0
                || content.PlaintextLength
                    > ApplicationOperationLimits.MaximumProtectedPlaintextBytes
                || content.CreatedAtUtc != request.CreatedAtUtc
                || content.PurgeAfterUtc != request.PurgeAfterUtc))
        {
            throw new ApplicationOperationValidationException(
                "A protected canonical request is required.");
        }

        if (request.IdempotencyDigest is { } digest)
        {
            ValidateDigest(digest, nameof(request.IdempotencyDigest));
        }
    }

    private static void ValidateHandlerResult(
        ApplicationOperationRecord operation,
        ApplicationOperationHandlerResult result)
    {
        if (result.BeforeVersion.DomainVersion < 0
            || result.BeforeVersion.SynchronizationVersion < 0
            || result.AfterVersion.DomainVersion < result.BeforeVersion.DomainVersion
            || result.AfterVersion.SynchronizationVersion < result.BeforeVersion.SynchronizationVersion
            || operation.ExpectedDomainVersion != 0
                && result.BeforeVersion.DomainVersion != operation.ExpectedDomainVersion
            || operation.ExpectedSynchronizationVersion != 0
                && result.BeforeVersion.SynchronizationVersion
                    != operation.ExpectedSynchronizationVersion
            || result.ReceiptSchemaVersion <= 0
            || result.ReceiptSource.IsEmpty
            || result.ReceiptSource.Length > ApplicationOperationLimits.MaximumProtectedPlaintextBytes
            || result.Reversal is ApplicationReversalAvailability.Unknown
            || result.Reversal == ApplicationReversalAvailability.Available
                && result.ReversalExpiresAtUtc is null
            || result.Reversal != ApplicationReversalAvailability.Available
                && result.ReversalExpiresAtUtc is not null)
        {
            throw new ApplicationOperationValidationException(
                "The in-database handler returned an invalid settlement.");
        }
    }

    private static ApplicationOperationEventKind ResolveEventKind(
        ApplicationOperationStatus current,
        ApplicationOperationStatus target,
        ApplicationOperationLeaseRequest? lease) =>
        target switch
        {
            ApplicationOperationStatus.AwaitingProtectedConfirmation =>
                ApplicationOperationEventKind.AwaitingProtectedConfirmation,
            ApplicationOperationStatus.Executing when current == ApplicationOperationStatus.Executing
                && lease?.RecoverExpiredLease == true =>
                ApplicationOperationEventKind.LeaseRecovered,
            ApplicationOperationStatus.Executing => ApplicationOperationEventKind.ExecutionClaimed,
            ApplicationOperationStatus.Rejected => ApplicationOperationEventKind.Rejected,
            ApplicationOperationStatus.Cancelled => ApplicationOperationEventKind.Cancelled,
            ApplicationOperationStatus.Expired => ApplicationOperationEventKind.Expired,
            ApplicationOperationStatus.Failed => ApplicationOperationEventKind.Failed,
            _ => throw new ApplicationOperationValidationException(
                "The transition has no content-free event kind.")
        };

    private static ApplicationOperationProtectionContext CreateProtectionContext(
        ApplicationOperationRecord operation,
        ApplicationOperationContentSubjectKind subjectKind,
        string subjectId,
        ApplicationProtectedContentKind contentKind) =>
        new(
            new ApplicationOperationScope(operation.UserProfileId, operation.Authority),
            subjectKind,
            subjectId,
            contentKind,
            operation.CapabilityCode,
            operation.CapabilityVersion);

    private static ApplicationOperationSnapshot ToSnapshot(ApplicationOperationRecord operation) =>
        new(
            operation.Id,
            new ApplicationOperationScope(operation.UserProfileId, operation.Authority),
            operation.CapabilityCode,
            operation.CapabilityFamily,
            operation.CapabilityVersion,
            operation.CapabilityFingerprint,
            operation.Effect,
            operation.Confirmation,
            operation.Status,
            operation.ParentOperationId,
            new ApplicationOperationVersion(operation.ApplicationVersion, operation.Fence),
            operation.ExpectedDomainVersion,
            operation.ExpectedSynchronizationVersion,
            operation.LeaseId,
            operation.LeaseExpiresAtUtc,
            operation.AttemptCount,
            operation.CreatedAtUtc,
            operation.UpdatedAtUtc,
            operation.ExpiresAtUtc,
            operation.PurgeAfterUtc,
            operation.TerminalAtUtc);

    private static ApplicationOperationReceiptSnapshot ToReceiptSnapshot(
        ApplicationOperationRecord operation,
        ApplicationOperationReceiptRecord receipt,
        byte[] receiptContent,
        bool isReplay) =>
        new(
            receipt.Id,
            receipt.OperationId,
            new ApplicationOperationScope(operation.UserProfileId, operation.Authority),
            receipt.ReceiptVersion,
            new ApplicationStateVersions(
                receipt.BeforeDomainVersion,
                receipt.BeforeSynchronizationVersion),
            new ApplicationStateVersions(
                receipt.AfterDomainVersion,
                receipt.AfterSynchronizationVersion),
            receipt.Reversal,
            receipt.ReversalExpiresAtUtc,
            receipt.ReversalOperationId,
            receipt.CommittedAtUtc,
            isReplay,
            receiptContent);

    private static ApplicationOperationContinuationSnapshot ToContinuationSnapshot(
        ApplicationOperationContinuationRecord continuation,
        ApplicationExecutionAuthority authority) =>
        new(
            continuation.Id,
            continuation.OperationId,
            new ApplicationOperationScope(continuation.UserProfileId, authority),
            continuation.Workflow,
            continuation.WorkflowVersion,
            continuation.State,
            continuation.InteractionScopeDigest.ToArray(),
            continuation.ParentContinuationId,
            continuation.AllowsAutomaticResume,
            continuation.AutomaticResumeCount,
            continuation.ApplicationVersion,
            continuation.CreatedAtUtc,
            continuation.UpdatedAtUtc,
            continuation.ExpiresAtUtc,
            continuation.PurgeAfterUtc,
            continuation.ResumedAtUtc);

    private static void ValidateOperationId(string operationId) =>
        ValidateReferenceId(operationId, nameof(operationId));

    private static void ValidateReferenceId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > ApplicationOperationLimits.MaximumReferenceIdLength)
        {
            throw new ApplicationOperationValidationException($"{name} is required and must be bounded.");
        }
    }

    private static void ValidateDigest(byte[] digest, string name)
    {
        if (digest.Length != ApplicationOperationLimits.Sha256DigestBytes)
        {
            throw new ApplicationOperationValidationException($"{name} must be a SHA-256 digest.");
        }
    }

    private static void ValidateCiphertext(byte[] ciphertext)
    {
        if (ciphertext.Length == 0
            || ciphertext.Length > ApplicationOperationLimits.MaximumProtectedCiphertextBytes)
        {
            throw new ApplicationOperationProtectionException("Protected operation content is invalid.");
        }
    }

    private static ApplicationOperationConflictException Conflict() =>
        new("The operation is unavailable or no longer matches the supplied authority and version.");

    private enum InternalTransitionKind
    {
        LeaseRecovery,
        PreEffectFailure,
        Expiration
    }
}
