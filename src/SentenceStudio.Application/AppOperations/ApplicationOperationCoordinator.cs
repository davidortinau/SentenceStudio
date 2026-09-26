using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.Application.AppOperations;

public sealed class ApplicationOperationCoordinator(
    IApplicationOperationStore store,
    IApplicationOperationContentProtector protector)
{
    public async Task<ApplicationOperationTransitionResult> ProposeAsync(
        ApplicationOperationProposalCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateProposal(command);

        var canonicalRequest = command.Contents.Single(content =>
            content.Kind == ApplicationProtectedContentKind.CanonicalRequest);
        var protectedContents = command.Contents
            .Select(content => Protect(command, content))
            .ToArray();

        var request = new ApplicationOperationCreateRequest(
            command.OperationId,
            command.Scope,
            command.CapabilityCode,
            command.CapabilityFamily,
            command.CapabilityVersion,
            command.CapabilityFingerprint,
            command.Effect,
            command.Confirmation,
            command.ParentOperationId,
            command.ParentApplicationVersion,
            command.ParentFence,
            command.IdempotencyMaterial is { } material
                ? ApplicationOperationDigest.Compute(material)
                : null,
            ApplicationOperationDigest.Compute(canonicalRequest.Plaintext),
            command.ExpectedDomainVersion,
            command.ExpectedSynchronizationVersion,
            command.CreatedAtUtc,
            command.ExpiresAtUtc,
            command.PurgeAfterUtc,
            protectedContents);

        return await store.CreateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplicationOperationTransitionResult> DecideAsync(
        ApplicationOperationDecisionCommand command,
        CancellationToken cancellationToken = default)
    {
        command.Scope.Validate();
        if (!Enum.IsDefined(command.Decision)
            || command.Decision == ApplicationOperationDecision.Unknown)
        {
            throw new ApplicationOperationValidationException(
                "A known operation decision is required.");
        }

        var operation = await store.FindAsync(
                command.Scope,
                command.OperationId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ApplicationOperationConflictException("The operation is unavailable.");
        var decisionReferenceDigest =
            ApplicationOperationDigest.Compute(command.DecisionReferenceMaterial);

        if (ApplicationOperationStateMachine.IsTerminal(operation.Status))
        {
            return await store.TransitionAsync(
                    new ApplicationOperationTransitionRequest(
                        command.OperationId,
                        command.Scope,
                        operation.Version,
                        operation.Status,
                        decisionReferenceDigest,
                        command.DecidedAtUtc,
                        null,
                        null,
                        null,
                        null,
                        EffectStarted: false,
                        command.Decision),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (command.DecidedAtUtc >= operation.ExpiresAtUtc)
        {
            return await store.TransitionAsync(
                    new ApplicationOperationTransitionRequest(
                        command.OperationId,
                        command.Scope,
                        operation.Version,
                        ApplicationOperationStatus.Expired,
                        decisionReferenceDigest,
                        command.DecidedAtUtc,
                        null,
                        null,
                        null,
                        null,
                        EffectStarted: false,
                        command.Decision),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        ApplicationOperationStatus target;
        if (operation.Status == ApplicationOperationStatus.AwaitingProtectedConfirmation
            && command.Decision == ApplicationOperationDecision.Accept
            && operation.Confirmation == ApplicationConfirmationPolicy.ProtectedConfirmation)
        {
            target = ApplicationOperationStatus.AwaitingProtectedConfirmation;
        }
        else try
        {
            target = ApplicationOperationStateMachine.ResolveDecisionTarget(
                operation.Status,
                command.Decision,
                operation.Confirmation);
        }
        catch (ApplicationOperationConflictException)
        {
            target = operation.Status;
        }

        var lease = target == ApplicationOperationStatus.Executing
            ? CreateLease(command)
            : null;

        ApplicationOperationConfirmationIssue? confirmationIssue = null;
        ApplicationOperationConfirmationUse? confirmationUse = null;
        if (target == ApplicationOperationStatus.AwaitingProtectedConfirmation)
        {
            if (string.IsNullOrWhiteSpace(command.ConfirmationReferenceId)
                || command.ConfirmationMaterial is not { } confirmationMaterial)
            {
                throw new ApplicationOperationValidationException(
                    "Protected confirmation issuance requires a reference and digest source.");
            }

            confirmationIssue = new ApplicationOperationConfirmationIssue(
                command.ConfirmationReferenceId,
                ApplicationOperationDigest.Compute(confirmationMaterial),
                command.DecidedAtUtc,
                operation.ExpiresAtUtc);
        }
        else if (command.Decision == ApplicationOperationDecision.Confirm
                 && string.IsNullOrWhiteSpace(command.ConfirmationReferenceId) == false
                 && command.ConfirmationMaterial is { } confirmationMaterial)
        {
            confirmationUse = new ApplicationOperationConfirmationUse(
                command.ConfirmationReferenceId!,
                ApplicationOperationDigest.Compute(confirmationMaterial),
                command.DecidedAtUtc);
        }

        return await store.TransitionAsync(
                new ApplicationOperationTransitionRequest(
                    command.OperationId,
                    command.Scope,
                    operation.Version,
                    target,
                    decisionReferenceDigest,
                    command.DecidedAtUtc,
                    lease,
                    confirmationIssue,
                    confirmationUse,
                    null,
                    EffectStarted: false,
                    command.Decision),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ApplicationOperationExecutionResult> ExecuteAsync(
        ApplicationOperationExecutionRequest request,
        IApplicationOperationHandler handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        request.Scope.Validate();
        request.ExpectedVersion.Validate();

        var operation = await store.FindAsync(
                request.Scope,
                request.OperationId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ApplicationOperationConflictException("The operation is unavailable.");

        if (operation.Status == ApplicationOperationStatus.Executed)
        {
            var receipt = await store.FindReceiptAsync(
                    request.Scope,
                    request.OperationId,
                    isReplay: true,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new ApplicationOperationConflictException(
                    "An executed operation has no durable receipt.");

            return new ApplicationOperationExecutionResult(operation, receipt);
        }

        if (!string.Equals(handler.CapabilityCode, operation.CapabilityCode, StringComparison.Ordinal)
            || handler.CapabilityVersion != operation.CapabilityVersion)
        {
            throw new ApplicationOperationConflictException(
                "The handler does not match the operation capability identity.");
        }

        return await store.ExecuteAsync(request, handler, cancellationToken).ConfigureAwait(false);
    }

    private ApplicationProtectedContent Protect(
        ApplicationOperationProposalCommand command,
        ApplicationOperationContent content)
    {
        if (content.SchemaVersion <= 0
            || content.Plaintext.IsEmpty
            || content.Plaintext.Length > ApplicationOperationLimits.MaximumProtectedPlaintextBytes)
        {
            throw new ApplicationOperationValidationException(
                "Protected operation content requires a positive schema and bounded non-empty content.");
        }

        var context = new ApplicationOperationProtectionContext(
            command.Scope,
            ApplicationOperationContentSubjectKind.Operation,
            command.OperationId,
            content.Kind,
            command.CapabilityCode,
            command.CapabilityVersion);
        var ciphertext = protector.Protect(context, content.Plaintext);
        if (ciphertext.Length > ApplicationOperationLimits.MaximumProtectedCiphertextBytes)
        {
            throw new ApplicationOperationProtectionException("Protected operation content is too large.");
        }

        return new ApplicationProtectedContent(
            Guid.NewGuid().ToString("N"),
            command.OperationId,
            ApplicationOperationContentSubjectKind.Operation,
            command.OperationId,
            content.Kind,
            ProtectionVersion: 1,
            content.SchemaVersion,
            ciphertext,
            content.Plaintext.Length,
            command.CreatedAtUtc,
            command.PurgeAfterUtc);
    }

    private static ApplicationOperationLeaseRequest CreateLease(
        ApplicationOperationDecisionCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.LeaseId)
            || command.LeaseId.Length > ApplicationOperationLimits.MaximumReferenceIdLength
            || command.LeaseExpiresAtUtc is not { } leaseExpiresAtUtc
            || leaseExpiresAtUtc <= command.DecidedAtUtc)
        {
            throw new ApplicationOperationValidationException(
                "Execution requires a bounded lease identifier and future expiry.");
        }

        return new ApplicationOperationLeaseRequest(
            command.LeaseId,
            leaseExpiresAtUtc,
            RecoverExpiredLease: false);
    }

    private static void ValidateProposal(ApplicationOperationProposalCommand command)
    {
        command.Scope.Validate();
        if (string.IsNullOrWhiteSpace(command.OperationId)
            || command.OperationId.Length > ApplicationOperationLimits.MaximumOperationIdLength
            || string.IsNullOrWhiteSpace(command.CapabilityCode)
            || command.CapabilityCode.Length > ApplicationOperationLimits.MaximumCapabilityCodeLength
            || string.IsNullOrWhiteSpace(command.CapabilityFamily)
            || command.CapabilityFamily.Length > ApplicationOperationLimits.MaximumCapabilityFamilyLength
            || command.CapabilityVersion <= 0
            || !ApplicationOperationFingerprint.IsCanonical(command.CapabilityFingerprint))
        {
            throw new ApplicationOperationValidationException(
                "The operation and capability identity must be complete and bounded.");
        }

        if (command.Effect is not ApplicationEffectClass.Write
            and not ApplicationEffectClass.Launch
            and not ApplicationEffectClass.Composite)
        {
            throw new ApplicationOperationValidationException(
                "Only consequential in-database application effects use the operation ledger.");
        }

        if (command.Effect == ApplicationEffectClass.Composite
            && command.IdempotencyMaterial is null)
        {
            throw new ApplicationOperationValidationException(
                "A composite operation requires keyed idempotency.");
        }

        if (command.Confirmation is not ApplicationConfirmationPolicy.Gesture
            and not ApplicationConfirmationPolicy.Accept
            and not ApplicationConfirmationPolicy.ProtectedConfirmation)
        {
            throw new ApplicationOperationValidationException(
                "A consequential operation requires an approved confirmation policy.");
        }

        if (command.ExpectedDomainVersion < 0
            || command.ExpectedSynchronizationVersion < 0
            || command.ExpiresAtUtc <= command.CreatedAtUtc
            || command.PurgeAfterUtc < command.CreatedAtUtc)
        {
            throw new ApplicationOperationValidationException(
                "Operation versions and lifecycle timestamps are invalid.");
        }

        if (command.ParentOperationId is null
            != (command.ParentApplicationVersion is null || command.ParentFence is null))
        {
            throw new ApplicationOperationValidationException(
                "A reversal parent requires its expected application version and fence.");
        }

        if (command.ParentApplicationVersion is <= 0 || command.ParentFence is < 0)
        {
            throw new ApplicationOperationValidationException(
                "Parent application version and fence are invalid.");
        }

        if (command.Contents.Count == 0
            || command.Contents.Count(content =>
                content.Kind == ApplicationProtectedContentKind.CanonicalRequest) != 1
            || command.Contents.Select(content => content.Kind).Distinct().Count()
                != command.Contents.Count)
        {
            throw new ApplicationOperationValidationException(
                "An operation requires one canonical request and no duplicate protected content kinds.");
        }
    }
}
