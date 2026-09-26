using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>Issues and atomically decides owner-bound Coach vocabulary proposals.</summary>
public sealed class CoachVocabularySetApplicationService(
    ApplicationDbContext db,
    IUserScopeProvider userScope,
    ApplicationOperationCoordinator coordinator,
    IApplicationOperationStore operationStore,
    IApplicationOperationContentProtector protector,
    ICoachValidationDataSource validationData,
    CoachDueItemLeakValidator leakValidator,
    TimeProvider timeProvider,
    ILogger<CoachVocabularySetApplicationService> logger)
{
    private readonly CoachVocabularySetEmbargoValidator _embargo =
        new(validationData, leakValidator);
    private readonly Action? _handlerInvocationObserver;
    private const int RequiredTermCount = 10;
    private static readonly TimeSpan ConcurrentExecutionPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan ConcurrentExecutionWaitTimeout = TimeSpan.FromSeconds(5);
    private const string CapabilityCode = "vocabulary.set.create";
    private const string CapabilityFamily = "vocabulary-management";
    private const int CapabilityVersion = 1;
    private static readonly string CapabilityFingerprint =
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{CapabilityCode}@{CapabilityVersion}"))).ToLowerInvariant()}";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal CoachVocabularySetApplicationService(
        ApplicationDbContext db,
        IUserScopeProvider userScope,
        ApplicationOperationCoordinator coordinator,
        IApplicationOperationStore operationStore,
        IApplicationOperationContentProtector protector,
        ICoachValidationDataSource validationData,
        CoachDueItemLeakValidator leakValidator,
        TimeProvider timeProvider,
        ILogger<CoachVocabularySetApplicationService> logger,
        Action handlerInvocationObserver)
        : this(
            db,
            userScope,
            coordinator,
            operationStore,
            protector,
            validationData,
            leakValidator,
            timeProvider,
            logger)
    {
        _handlerInvocationObserver = handlerInvocationObserver;
    }

    public async Task<CoachVocabularySetProposal?> IssueAsync(
        CoachVocabularySetProposal generated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generated);
        if (!TryScope(out var scope))
        {
            return null;
        }

        var proposal = new CoachVocabularySetProposal
        {
            ProposalId = Guid.NewGuid().ToString("N"),
            Topic = generated.Topic.Trim(),
            Title = generated.Title.Trim(),
            TargetLanguageTag = generated.TargetLanguageTag.Trim(),
            Terms = generated.Terms.Select(pair => new CoachVocabularyTermDto
            {
                TargetTerm = pair.TargetTerm.Trim(),
                NativeTerm = pair.NativeTerm.Trim()
            }).ToArray()
        };
        Validate(proposal);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var canonical = JsonSerializer.SerializeToUtf8Bytes(proposal, JsonOptions);
        await coordinator.ProposeAsync(
            new ApplicationOperationProposalCommand(
                proposal.ProposalId,
                scope,
                CapabilityCode,
                CapabilityFamily,
                CapabilityVersion,
                CapabilityFingerprint,
                ApplicationEffectClass.Write,
                ApplicationConfirmationPolicy.Accept,
                ParentOperationId: null,
                ParentApplicationVersion: null,
                ParentFence: null,
                IdempotencyMaterial: null,
                ExpectedDomainVersion: 0,
                ExpectedSynchronizationVersion: 0,
                now,
                now.AddMinutes(15),
                now.AddDays(30),
                [new ApplicationOperationContent(
                    ApplicationProtectedContentKind.CanonicalRequest,
                    SchemaVersion: 1,
                    canonical)]),
            cancellationToken).ConfigureAwait(false);

        return proposal;
    }

    public async Task<CoachVocabularySetApprovalResponse?> ApproveAsync(
        ApproveCoachVocabularySetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ProposalReference)
            || request.Decision is not ApplicationOperationDecision.Accept
                and not ApplicationOperationDecision.Reject)
        {
            throw new ArgumentException("A proposal reference and accept or reject decision are required.");
        }

        if (!TryScope(out var scope))
        {
            return null;
        }

        string? ownedLeaseId = null;
        long? concurrentWaitStartedAt = null;
        while (true)
        {
            var operation = await operationStore.FindAsync(
                    scope,
                    request.ProposalReference,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new ApplicationOperationConflictException("The vocabulary proposal is unavailable.");

            if (operation.CapabilityCode != CapabilityCode
                || operation.CapabilityVersion != CapabilityVersion)
            {
                throw new ApplicationOperationConflictException("The vocabulary proposal is unavailable.");
            }

            if (operation.Status == ApplicationOperationStatus.Executed)
            {
                if (request.Decision != ApplicationOperationDecision.Accept)
                {
                    throw new ApplicationOperationConflictException("The vocabulary proposal was already accepted.");
                }

                return await ReadReceiptAsync(scope, operation.OperationId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (operation.Status is ApplicationOperationStatus.Rejected
                or ApplicationOperationStatus.Cancelled
                or ApplicationOperationStatus.Expired
                or ApplicationOperationStatus.Failed
                or ApplicationOperationStatus.Reversed)
            {
                if (operation.Status == ApplicationOperationStatus.Rejected
                    && request.Decision == ApplicationOperationDecision.Reject)
                {
                    return Rejected(operation.OperationId);
                }

                throw new ApplicationOperationConflictException("The vocabulary proposal is no longer available.");
            }

            if (operation.Status == ApplicationOperationStatus.Proposed
                && request.Decision == ApplicationOperationDecision.Accept)
            {
                var canonicalProposal = await ReadCanonicalProposalAsync(
                    scope,
                    operation,
                    cancellationToken).ConfigureAwait(false);
                if (canonicalProposal is null
                    || !await _embargo.IsSafeAsync(
                            scope.UserProfileId,
                            canonicalProposal,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    var now = timeProvider.GetUtcNow().UtcDateTime;
                    await coordinator.DecideAsync(
                        new ApplicationOperationDecisionCommand(
                            operation.OperationId,
                            scope,
                            ApplicationOperationDecision.Reject,
                            Encoding.UTF8.GetBytes($"{operation.OperationId}:embargo"),
                            ConfirmationReferenceId: null,
                            ConfirmationMaterial: null,
                            LeaseId: null,
                            LeaseExpiresAtUtc: null,
                            now),
                        cancellationToken).ConfigureAwait(false);
                    throw new ApplicationOperationConflictException(
                        "The vocabulary proposal is no longer available.");
                }
            }

            try
            {
                if (operation.Status == ApplicationOperationStatus.Proposed)
                {
                    var now = timeProvider.GetUtcNow().UtcDateTime;
                    var decision = await coordinator.DecideAsync(
                        new ApplicationOperationDecisionCommand(
                            operation.OperationId,
                            scope,
                            request.Decision,
                            Encoding.UTF8.GetBytes(
                                $"{operation.OperationId}:{request.Decision}"),
                            ConfirmationReferenceId: null,
                            ConfirmationMaterial: null,
                            request.Decision == ApplicationOperationDecision.Accept
                                ? Guid.NewGuid().ToString("N")
                                : null,
                            request.Decision == ApplicationOperationDecision.Accept
                                ? now.AddMinutes(1)
                                : null,
                            now),
                        cancellationToken).ConfigureAwait(false);
                    operation = decision.Operation;
                    if (!decision.IsReplay)
                    {
                        ownedLeaseId = operation.LeaseId;
                    }
                }

                if (operation.Status == ApplicationOperationStatus.Executing)
                {
                    if (request.Decision != ApplicationOperationDecision.Accept)
                    {
                        throw new ApplicationOperationConflictException(
                            "The vocabulary proposal was already accepted.");
                    }

                    var now = timeProvider.GetUtcNow().UtcDateTime;
                    if (operation.LeaseExpiresAtUtc is not { } leaseExpiresAtUtc
                        || now >= operation.ExpiresAtUtc)
                    {
                        throw new ApplicationOperationConflictException(
                            "The vocabulary proposal is no longer available.");
                    }

                    if (leaseExpiresAtUtc <= now)
                    {
                        var recoveredLeaseId = Guid.NewGuid().ToString("N");
                        var recovery = await operationStore.TransitionAsync(
                            new ApplicationOperationTransitionRequest(
                               operation.OperationId,
                               scope,
                               operation.Version,
                               ApplicationOperationStatus.Executing,
                               ApplicationOperationDigest.Compute(
                                   Encoding.UTF8.GetBytes(
                                       $"{operation.OperationId}:lease-recovery")),
                               now,
                               new ApplicationOperationLeaseRequest(
                                   recoveredLeaseId,
                                   now.AddMinutes(1),
                                   RecoverExpiredLease: true),
                               ConfirmationIssue: null,
                               ConfirmationUse: null,
                               FailureCode: null,
                               EffectStarted: false),
                            cancellationToken).ConfigureAwait(false);
                        operation = recovery.Operation;
                        if (!recovery.IsReplay)
                        {
                            ownedLeaseId = recoveredLeaseId;
                        }
                    }

                    if (operation.Status == ApplicationOperationStatus.Executing
                        && !string.Equals(
                        operation.LeaseId,
                        ownedLeaseId,
                        StringComparison.Ordinal))
                    {
                        concurrentWaitStartedAt ??= Stopwatch.GetTimestamp();
                        await WaitForConcurrentExecutionAsync(
                            concurrentWaitStartedAt.Value,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                if (request.Decision == ApplicationOperationDecision.Reject)
                {
                    if (operation.Status != ApplicationOperationStatus.Rejected)
                    {
                        throw new ApplicationOperationConflictException(
                            "The vocabulary proposal was already accepted.");
                    }

                    return Rejected(operation.OperationId);
                }

                if (operation.Status == ApplicationOperationStatus.Executing
                    && operation.LeaseId is { } leaseId)
                {
                    var canonicalProposal = await ReadCanonicalProposalAsync(
                        scope,
                        operation,
                        cancellationToken).ConfigureAwait(false);
                    if (canonicalProposal is null
                        || !await _embargo.IsSafeAsync(
                                scope.UserProfileId,
                                canonicalProposal,
                                cancellationToken)
                            .ConfigureAwait(false))
                    {
                        var now = timeProvider.GetUtcNow().UtcDateTime;
                        await operationStore.TransitionAsync(
                            new ApplicationOperationTransitionRequest(
                                operation.OperationId,
                                scope,
                                operation.Version,
                                ApplicationOperationStatus.Failed,
                                ApplicationOperationDigest.Compute(
                                    Encoding.UTF8.GetBytes(
                                        $"{operation.OperationId}:embargo-pre-execution")),
                                now,
                                Lease: null,
                                ConfirmationIssue: null,
                                ConfirmationUse: null,
                                FailureCode: ApplicationOperationFailureCode.PreEffectHandlerFailure,
                                EffectStarted: false),
                            cancellationToken).ConfigureAwait(false);
                        throw new ApplicationOperationConflictException(
                            "The vocabulary proposal is no longer available.");
                    }

                    var executed = await coordinator.ExecuteAsync(
                        new ApplicationOperationExecutionRequest(
                            operation.OperationId,
                            scope,
                            operation.Version,
                            leaseId,
                            timeProvider.GetUtcNow().UtcDateTime),
                        new VocabularySetHandler(db, _handlerInvocationObserver),
                        cancellationToken).ConfigureAwait(false);
                    return DeserializeReceipt(executed.Receipt.ReceiptContent);
                }
            }
            catch (ApplicationOperationConflictException)
            {
                concurrentWaitStartedAt ??= Stopwatch.GetTimestamp();
                await WaitForConcurrentExecutionAsync(
                    concurrentWaitStartedAt.Value,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                concurrentWaitStartedAt ??= Stopwatch.GetTimestamp();
                await WaitForConcurrentExecutionAsync(
                    concurrentWaitStartedAt.Value,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task WaitForConcurrentExecutionAsync(
        long waitStartedAt,
        CancellationToken cancellationToken)
    {
        if (Stopwatch.GetElapsedTime(waitStartedAt) >= ConcurrentExecutionWaitTimeout)
        {
            throw new CoachVocabularySetInProgressException();
        }

        await Task.Delay(ConcurrentExecutionPollInterval, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CoachVocabularySetApprovalResponse> ReadReceiptAsync(
        ApplicationOperationScope scope,
        string operationId,
        CancellationToken cancellationToken)
    {
        var receipt = await operationStore.FindReceiptAsync(
                scope,
                operationId,
                isReplay: true,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ApplicationOperationConflictException(
                "The accepted vocabulary proposal has no durable receipt.");
        return DeserializeReceipt(receipt.ReceiptContent);
    }

    internal async Task<CoachVocabularySetClientProjection> ProjectForClientAsync(
        CoachVocabularySetProposal persistedProposal,
        CancellationToken cancellationToken = default)
    {
        if (!TryScope(out var scope))
        {
            return CoachVocabularySetClientProjection.None;
        }

        var operation = await operationStore.FindAsync(
            scope,
            persistedProposal.ProposalId,
            cancellationToken).ConfigureAwait(false);
        if (operation is null
            || operation.CapabilityCode != CapabilityCode
            || operation.CapabilityVersion != CapabilityVersion
            || operation.Status != ApplicationOperationStatus.Proposed
            || operation.ExpiresAtUtc <= timeProvider.GetUtcNow().UtcDateTime)
        {
            return CoachVocabularySetClientProjection.None;
        }

        var canonical = await ReadCanonicalProposalAsync(scope, operation, cancellationToken)
            .ConfigureAwait(false);
        if (canonical is null
            || !string.Equals(
                canonical.ProposalId,
                persistedProposal.ProposalId,
                StringComparison.Ordinal))
        {
            return CoachVocabularySetClientProjection.Withheld;
        }

        return await _embargo.IsSafeAsync(
                scope.UserProfileId,
                canonical,
                cancellationToken)
            .ConfigureAwait(false)
            ? new CoachVocabularySetClientProjection(canonical, false)
            : CoachVocabularySetClientProjection.Withheld;
    }

    private async Task<CoachVocabularySetProposal?> ReadCanonicalProposalAsync(
        ApplicationOperationScope scope,
        ApplicationOperationSnapshot operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await db.ApplicationProtectedPayloads
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.OperationId == operation.OperationId
                        && item.UserProfileId == scope.UserProfileId
                        && item.SubjectKind == ApplicationOperationContentSubjectKind.Operation
                        && item.SubjectId == operation.OperationId
                        && item.ContentKind == ApplicationProtectedContentKind.CanonicalRequest,
                    cancellationToken)
                .ConfigureAwait(false);
            if (payload is null || payload.SchemaVersion != 1)
            {
                return null;
            }

            var plaintext = protector.Unprotect(
                new ApplicationOperationProtectionContext(
                    scope,
                    ApplicationOperationContentSubjectKind.Operation,
                    operation.OperationId,
                    ApplicationProtectedContentKind.CanonicalRequest,
                    operation.CapabilityCode,
                    operation.CapabilityVersion),
                payload.Ciphertext,
                payload.PlaintextLength);
            try
            {
                return JsonSerializer.Deserialize<CoachVocabularySetProposal>(
                    plaintext,
                    JsonOptions);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception exception) when (
            exception is JsonException
            or ApplicationOperationProtectionException)
        {
            logger.LogWarning(
                exception,
                "Coach vocabulary proposal {ProposalId} could not be projected",
                operation.OperationId);
            return null;
        }
    }

    private bool TryScope(out ApplicationOperationScope scope)
    {
        if (userScope.TryGetUserProfileId(out var userId)
            && !string.IsNullOrWhiteSpace(userId))
        {
            scope = new ApplicationOperationScope(userId, ApplicationExecutionAuthority.Server);
            return true;
        }

        logger.LogWarning(
            "Coach vocabulary proposal decision called with no active userId — returning no data");
        scope = null!;
        return false;
    }

    private static CoachVocabularySetApprovalResponse Rejected(string proposalId) => new()
    {
        ProposalId = proposalId,
        Decision = ApplicationOperationDecision.Reject
    };

    private static CoachVocabularySetApprovalResponse DeserializeReceipt(byte[] content) =>
        JsonSerializer.Deserialize<CoachVocabularySetApprovalResponse>(content, JsonOptions)
        ?? throw new ApplicationOperationConflictException("The vocabulary receipt is invalid.");

    internal static void Validate(CoachVocabularySetProposal proposal)
    {
        if (string.IsNullOrWhiteSpace(proposal.ProposalId)
            || string.IsNullOrWhiteSpace(proposal.Topic)
            || string.IsNullOrWhiteSpace(proposal.Title)
            || string.IsNullOrWhiteSpace(proposal.TargetLanguageTag)
            || proposal.Terms.Count != RequiredTermCount)
        {
            throw new ArgumentException(
                "A vocabulary proposal needs identity, languages, a topic, a title, and exactly ten terms.");
        }

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in proposal.Terms)
        {
            if (string.IsNullOrWhiteSpace(pair.TargetTerm)
                || string.IsNullOrWhiteSpace(pair.NativeTerm)
                || !targets.Add(pair.TargetTerm.Trim()))
            {
                throw new ArgumentException(
                    "Every vocabulary pair must be complete and target terms must be unique.");
            }
        }

    }

    private sealed class VocabularySetHandler(
        ApplicationDbContext db,
        Action? invocationObserver) : IApplicationOperationHandler
    {
        public string CapabilityCode => CoachVocabularySetApplicationService.CapabilityCode;
        public int CapabilityVersion => CoachVocabularySetApplicationService.CapabilityVersion;

        public async Task<ApplicationOperationHandlerResult> ExecuteAsync(
            ApplicationOperationHandlerContext context,
            CancellationToken cancellationToken)
        {
            invocationObserver?.Invoke();
            var proposal = JsonSerializer.Deserialize<CoachVocabularySetProposal>(
                    context.CanonicalRequest.Span,
                    JsonOptions)
                ?? throw new ApplicationOperationValidationException(
                    "The canonical vocabulary proposal is invalid.");
            Validate(proposal);

            var language = await db.UserProfiles
                .Where(profile => profile.Id == context.Operation.Scope.UserProfileId)
                .Select(profile => profile.TargetLanguage)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? proposal.TargetLanguageTag;
            var now = context.Operation.UpdatedAtUtc;
            var resource = new LearningResource
            {
                Id = Guid.NewGuid().ToString(),
                Title = proposal.Title,
                Description = $"Coach-approved vocabulary about {proposal.Topic}",
                MediaType = "Vocabulary List",
                Language = language,
                Tags = $"coach-vocabulary-proposal:{context.Operation.OperationId}",
                UserProfileId = context.Operation.Scope.UserProfileId,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.LearningResources.Add(resource);

            foreach (var pair in proposal.Terms)
            {
                var word = new VocabularyWord
                {
                    Id = Guid.NewGuid().ToString(),
                    TargetLanguageTerm = pair.TargetTerm,
                    NativeLanguageTerm = pair.NativeTerm,
                    Lemma = pair.TargetTerm,
                    Language = language,
                    Tags = proposal.Topic,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.VocabularyWords.Add(word);
                db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
                {
                    Id = Guid.NewGuid().ToString(),
                    ResourceId = resource.Id,
                    VocabularyWordId = word.Id
                });
            }

            var response = new CoachVocabularySetApprovalResponse
            {
                ProposalId = context.Operation.OperationId,
                Decision = ApplicationOperationDecision.Accept,
                ResourceId = resource.Id,
                ActivityPath = $"/vocab-quiz?resourceIds={Uri.EscapeDataString(resource.Id)}"
            };
            return new ApplicationOperationHandlerResult(
                new ApplicationStateVersions(0, 0),
                new ApplicationStateVersions(0, 0),
                ReceiptSchemaVersion: 1,
                JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
                ApplicationReversalAvailability.Unavailable,
                ReversalExpiresAtUtc: null,
                Continuation: null);
        }
    }

    public sealed class CoachVocabularySetInProgressException()
        : Exception("The vocabulary proposal is still being accepted. Retry the same request.")
    {
    }
}

internal sealed record CoachVocabularySetClientProjection(
    CoachVocabularySetProposal? Proposal,
    bool WasWithheld)
{
    public static CoachVocabularySetClientProjection None { get; } = new(null, false);
    public static CoachVocabularySetClientProjection Withheld { get; } = new(null, true);
}
