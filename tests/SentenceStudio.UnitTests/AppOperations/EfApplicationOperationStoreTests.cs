using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Data.AppOperations;

namespace SentenceStudio.UnitTests.AppOperations;

public sealed class EfApplicationOperationStoreTests
{
    [Fact]
    public async Task FindAndReceiptReads_AreBoundToOwnerAndAuthority()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal());

        var store = harness.NewStore(db);
        (await store.FindAsync(
                ApplicationOperationSqliteHarness.Scope("owner-b"),
                "operation-a",
                default))
            .Should().BeNull();
        (await store.FindAsync(
                ApplicationOperationSqliteHarness.Scope(
                    authority: ApplicationExecutionAuthority.NativeLocal),
                "operation-a",
                default))
            .Should().BeNull();
        (await store.FindReceiptAsync(
                ApplicationOperationSqliteHarness.Scope("owner-b"),
                "operation-a",
                isReplay: true,
                default))
            .Should().BeNull();

        var persisted = await db.ApplicationOperations.SingleAsync();
        persisted.UserProfileId.Should().Be("owner-a");
        persisted.Authority.Should().Be(ApplicationExecutionAuthority.Server);
        persisted.ApplicationVersion.Should().Be(1);
    }

    [Fact]
    public async Task Create_ReplaysIdenticalOperationAndIdempotencyDigestWithoutDuplicatingRows()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        var idempotency = "same-logical-request"u8.ToArray();

        var first = await coordinator.ProposeAsync(
            ApplicationOperationSqliteHarness.Proposal(idempotencyMaterial: idempotency));
        var sameId = await coordinator.ProposeAsync(
            ApplicationOperationSqliteHarness.Proposal(idempotencyMaterial: idempotency));
        var differentId = await coordinator.ProposeAsync(
            ApplicationOperationSqliteHarness.Proposal(
                operationId: "operation-b",
                idempotencyMaterial: idempotency));

        first.IsReplay.Should().BeFalse();
        sameId.IsReplay.Should().BeTrue();
        differentId.IsReplay.Should().BeTrue();
        differentId.Operation.OperationId.Should().Be(first.Operation.OperationId);
        (await db.ApplicationOperations.CountAsync()).Should().Be(1);
        (await db.ApplicationOperationEvents.CountAsync()).Should().Be(1);
        (await db.ApplicationProtectedPayloads.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Create_SameOperationIdWithDifferentDigestFailsClosed()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            idempotencyMaterial: "first-request"u8.ToArray()));

        var act = async () => await coordinator.ProposeAsync(
            ApplicationOperationSqliteHarness.Proposal(
                idempotencyMaterial: "different-request"u8.ToArray()));

        await act.Should().ThrowAsync<ApplicationOperationConflictException>();
        (await db.ApplicationOperations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Create_SameDigestWithDifferentCanonicalRequestIsRejectedAsCollision()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        var key = "caller-key"u8.ToArray();
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            idempotencyMaterial: key));

        var changed = ApplicationOperationSqliteHarness.Proposal(
            operationId: "operation-b",
            idempotencyMaterial: key,
            contents:
            [
                new ApplicationOperationContent(
                    ApplicationProtectedContentKind.CanonicalRequest,
                    1,
                    "different-canonical-request"u8.ToArray())
            ]);

        var act = async () => await coordinator.ProposeAsync(changed);

        await act.Should().ThrowAsync<ApplicationOperationConflictException>();
        (await db.ApplicationOperations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DecisionReplay_ReturnsTheOriginalTransitionWithoutChangingVersionOrEvents()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal());
        var command = ApplicationOperationSqliteHarness.Decision(
            decision: ApplicationOperationDecision.Reject,
            decisionReference: "stable-decision-reference",
            leaseId: null,
            leaseExpiresAtUtc: null);

        var first = await coordinator.DecideAsync(command);
        var replay = await coordinator.DecideAsync(command);

        replay.IsReplay.Should().BeTrue();
        replay.Operation.Status.Should().Be(ApplicationOperationStatus.Rejected);
        replay.Operation.Version.Should().Be(first.Operation.Version);
        (await db.ApplicationOperationEvents.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(ApplicationOperationDecision.Reject, ApplicationOperationStatus.Rejected)]
    [InlineData(ApplicationOperationDecision.Cancel, ApplicationOperationStatus.Cancelled)]
    public async Task Decision_SettlesTerminalRefusalsWithNoLease(
        ApplicationOperationDecision decision,
        ApplicationOperationStatus expected)
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal());

        var result = await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decision: decision,
                leaseId: null,
                leaseExpiresAtUtc: null));

        result.Operation.Status.Should().Be(expected);
        result.Operation.TerminalAtUtc.Should().Be(ApplicationOperationSqliteHarness.NowUtc.AddSeconds(1));
        result.Operation.LeaseId.Should().BeNull();
        var operationEvent = await db.ApplicationOperationEvents.OrderBy(item => item.Sequence).LastAsync();
        operationEvent.ToStatus.Should().Be(expected);
    }

    [Fact]
    public async Task ExpiredDecision_ExpiresInsteadOfApplyingRequestedDecision()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            expiresAtUtc: ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2)));

        var result = await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Accept,
                decidedAtUtc: ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2)));

        result.Operation.Status.Should().Be(ApplicationOperationStatus.Expired);
        result.Operation.LeaseId.Should().BeNull();
        (await db.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Transition_RejectsStaleApplicationVersionAndFenceWithoutMutation()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        var proposed = (await coordinator.ProposeAsync(
            ApplicationOperationSqliteHarness.Proposal())).Operation;
        var store = harness.NewStore(db);

        foreach (var stale in new[]
                 {
                     proposed.Version with { ApplicationVersion = proposed.Version.ApplicationVersion + 1 },
                     proposed.Version with { Fence = proposed.Version.Fence + 1 }
                 })
        {
            var request = new ApplicationOperationTransitionRequest(
                proposed.OperationId,
                proposed.Scope,
                stale,
                ApplicationOperationStatus.Rejected,
                ByteAssertions.Digest("decision"),
                ApplicationOperationSqliteHarness.NowUtc.AddSeconds(1),
                Lease: null,
                ConfirmationIssue: null,
                ConfirmationUse: null,
                FailureCode: null,
                EffectStarted: false);

            var act = async () => await store.TransitionAsync(request, default);
            await act.Should().ThrowAsync<ApplicationOperationConflictException>();
        }

        (await store.FindAsync(proposed.Scope, proposed.OperationId, default))!
            .Status.Should().Be(ApplicationOperationStatus.Proposed);
        (await db.ApplicationOperationEvents.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(UnknownOrdinaryTransition.ProposedToExecuting)]
    [InlineData(UnknownOrdinaryTransition.ProposedToProtectedConfirmation)]
    [InlineData(UnknownOrdinaryTransition.ProtectedConfirmationRotation)]
    [InlineData(UnknownOrdinaryTransition.ProtectedConfirmationToExecuting)]
    [InlineData(UnknownOrdinaryTransition.ProposedToRejected)]
    [InlineData(UnknownOrdinaryTransition.ProtectedConfirmationToRejected)]
    [InlineData(UnknownOrdinaryTransition.ProposedToCancelled)]
    [InlineData(UnknownOrdinaryTransition.ProtectedConfirmationToCancelled)]
    [InlineData(UnknownOrdinaryTransition.ExecutingToExecuted)]
    [InlineData(UnknownOrdinaryTransition.ExecutedToReversed)]
    public async Task UnknownDecisionCannotAuthorizeAnyOrdinaryTransition(
        UnknownOrdinaryTransition transition)
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        var protectedTransition = transition is
            UnknownOrdinaryTransition.ProposedToProtectedConfirmation
            or UnknownOrdinaryTransition.ProtectedConfirmationRotation
            or UnknownOrdinaryTransition.ProtectedConfirmationToExecuting
            or UnknownOrdinaryTransition.ProtectedConfirmationToRejected
            or UnknownOrdinaryTransition.ProtectedConfirmationToCancelled;
        var current = (await coordinator.ProposeAsync(
            ApplicationOperationSqliteHarness.Proposal(
                confirmation: protectedTransition
                    ? ApplicationConfirmationPolicy.ProtectedConfirmation
                    : ApplicationConfirmationPolicy.Accept))).Operation;
        var confirmationSecret = "ordinary-transition-confirmation"u8.ToArray();

        if (transition is
            UnknownOrdinaryTransition.ProtectedConfirmationRotation
            or UnknownOrdinaryTransition.ProtectedConfirmationToExecuting
            or UnknownOrdinaryTransition.ProtectedConfirmationToRejected
            or UnknownOrdinaryTransition.ProtectedConfirmationToCancelled)
        {
            current = (await coordinator.DecideAsync(
                ApplicationOperationSqliteHarness.Decision(
                    decisionReference: "issue-confirmation",
                    confirmationReference: "confirmation-a",
                    confirmationMaterial: confirmationSecret,
                    leaseId: null,
                    leaseExpiresAtUtc: null))).Operation;
        }
        else if (transition is
                 UnknownOrdinaryTransition.ExecutingToExecuted
                 or UnknownOrdinaryTransition.ExecutedToReversed)
        {
            current = (await coordinator.DecideAsync(
                ApplicationOperationSqliteHarness.Decision(
                    decisionReference: "explicit-accept",
                    leaseId: "ordinary-lease"))).Operation;
            if (transition == UnknownOrdinaryTransition.ExecutedToReversed)
            {
                current = (await coordinator.ExecuteAsync(
                    ApplicationOperationSqliteHarness.Execution(current),
                    new ProfileMutationHandler(db, "owner-a"))).Operation;
            }
        }

        var transitionedAt = ApplicationOperationSqliteHarness.NowUtc.AddSeconds(3);
        var target = transition switch
        {
            UnknownOrdinaryTransition.ProposedToExecuting
                or UnknownOrdinaryTransition.ProtectedConfirmationToExecuting =>
                ApplicationOperationStatus.Executing,
            UnknownOrdinaryTransition.ProposedToProtectedConfirmation
                or UnknownOrdinaryTransition.ProtectedConfirmationRotation =>
                ApplicationOperationStatus.AwaitingProtectedConfirmation,
            UnknownOrdinaryTransition.ProposedToRejected
                or UnknownOrdinaryTransition.ProtectedConfirmationToRejected =>
                ApplicationOperationStatus.Rejected,
            UnknownOrdinaryTransition.ProposedToCancelled
                or UnknownOrdinaryTransition.ProtectedConfirmationToCancelled =>
                ApplicationOperationStatus.Cancelled,
            UnknownOrdinaryTransition.ExecutingToExecuted =>
                ApplicationOperationStatus.Executed,
            UnknownOrdinaryTransition.ExecutedToReversed =>
                ApplicationOperationStatus.Reversed,
            _ => throw new ArgumentOutOfRangeException(nameof(transition), transition, null)
        };
        var lease = transition is
            UnknownOrdinaryTransition.ProposedToExecuting
            or UnknownOrdinaryTransition.ProtectedConfirmationToExecuting
                ? new ApplicationOperationLeaseRequest(
                    "unknown-decision-lease",
                    transitionedAt.AddMinutes(2),
                    RecoverExpiredLease: false)
                : null;
        var confirmationIssue = transition is
            UnknownOrdinaryTransition.ProposedToProtectedConfirmation
            or UnknownOrdinaryTransition.ProtectedConfirmationRotation
                ? new ApplicationOperationConfirmationIssue(
                    "unknown-confirmation",
                    ByteAssertions.Digest("unknown-confirmation-secret"),
                    transitionedAt,
                    current.ExpiresAtUtc)
                : null;
        var confirmationUse =
            transition == UnknownOrdinaryTransition.ProtectedConfirmationToExecuting
                ? new ApplicationOperationConfirmationUse(
                    "confirmation-a",
                    ByteAssertions.Digest("ordinary-transition-confirmation"),
                    transitionedAt)
                : null;
        var request = new ApplicationOperationTransitionRequest(
            current.OperationId,
            current.Scope,
            current.Version,
            target,
            ByteAssertions.Digest($"unknown-ordinary-{transition}"),
            transitionedAt,
            lease,
            confirmationIssue,
            confirmationUse,
            FailureCode: null,
            EffectStarted: transition is
                UnknownOrdinaryTransition.ExecutingToExecuted
                or UnknownOrdinaryTransition.ExecutedToReversed,
            ApplicationOperationDecision.Unknown);
        db.ChangeTracker.Clear();
        var operationBefore = await db.ApplicationOperations.AsNoTracking().SingleAsync();
        var eventCount = await db.ApplicationOperationEvents.CountAsync();
        var receiptCount = await db.ApplicationOperationReceipts.CountAsync();
        var protectedPayloadCount = await db.ApplicationProtectedPayloads.CountAsync();
        var confirmationCount = await db.ApplicationOperationConfirmations.CountAsync();
        var ownerName = (await db.UserProfiles.AsNoTracking().SingleAsync()).Name;

        var act = async () => await harness.NewStore(db).TransitionAsync(request, default);

        await act.Should().ThrowAsync<ApplicationOperationConflictException>();
        db.ChangeTracker.Clear();
        var operationAfter = await db.ApplicationOperations.AsNoTracking().SingleAsync();
        operationAfter.Should().BeEquivalentTo(operationBefore);
        (await db.ApplicationOperationEvents.CountAsync()).Should().Be(eventCount);
        (await db.ApplicationOperationReceipts.CountAsync()).Should().Be(receiptCount);
        (await db.ApplicationProtectedPayloads.CountAsync()).Should().Be(protectedPayloadCount);
        (await db.ApplicationOperationConfirmations.CountAsync()).Should().Be(confirmationCount);
        (await db.UserProfiles.AsNoTracking().SingleAsync()).Name.Should().Be(ownerName);
    }

    [Fact]
    public async Task FailedTransition_RequiresControlledCodeAndClearsExpiredLease()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var executing = await harness.CreateExecutingAsync(db);
        var store = harness.NewStore(db);
        var request = new ApplicationOperationTransitionRequest(
            executing.OperationId,
            executing.Scope,
            executing.Version,
            ApplicationOperationStatus.Failed,
            ByteAssertions.Digest("failure-decision"),
            ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2),
            Lease: null,
            ConfirmationIssue: null,
            ConfirmationUse: null,
            ApplicationOperationFailureCode.PreEffectHandlerFailure,
            EffectStarted: false);

        var result = await store.TransitionAsync(request, default);

        result.Operation.Status.Should().Be(ApplicationOperationStatus.Failed);
        result.Operation.LeaseId.Should().BeNull();
        result.Operation.TerminalAtUtc.Should().Be(request.TransitionedAtUtc);
        var failure = await db.ApplicationOperationEvents.SingleAsync(
            item => item.Kind == ApplicationOperationEventKind.Failed);
        failure.FailureCode.Should().Be(ApplicationOperationFailureCode.PreEffectHandlerFailure);
    }

    [Fact]
    public async Task FailedTransition_AfterEffectStartedIsForbidden()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var executing = await harness.CreateExecutingAsync(db);
        var store = harness.NewStore(db);
        var request = new ApplicationOperationTransitionRequest(
            executing.OperationId,
            executing.Scope,
            executing.Version,
            ApplicationOperationStatus.Failed,
            ByteAssertions.Digest("failure-decision"),
            ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2),
            Lease: null,
            ConfirmationIssue: null,
            ConfirmationUse: null,
            ApplicationOperationFailureCode.PreEffectHandlerFailure,
            EffectStarted: true);

        var act = async () => await store.TransitionAsync(request, default);

        await act.Should().ThrowAsync<ApplicationOperationConflictException>();
        (await store.FindAsync(executing.Scope, executing.OperationId, default))!
            .Status.Should().Be(ApplicationOperationStatus.Executing);
    }

    [Fact]
    public async Task FailedTransition_PostCommitFailureReturnsTheCommittedFailureOnce()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        var fault = new RetryOnceCommittedTransactionInterceptor();
        await using (var db = harness.NewRetryingContext(fault))
        {
            var executing = await harness.CreateExecutingAsync(db);
            var request = new ApplicationOperationTransitionRequest(
                executing.OperationId,
                executing.Scope,
                executing.Version,
                ApplicationOperationStatus.Failed,
                ByteAssertions.Digest("stable-pre-effect-failure"),
                ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2),
                Lease: null,
                ConfirmationIssue: null,
                ConfirmationUse: null,
                ApplicationOperationFailureCode.PreEffectHandlerFailure,
                EffectStarted: false);
            fault.Arm();

            var failed = await harness.NewStore(db).TransitionAsync(request, default);

            failed.IsReplay.Should().BeTrue();
            failed.Operation.Status.Should().Be(ApplicationOperationStatus.Failed);
            failed.Operation.Version.ApplicationVersion.Should()
                .Be(executing.Version.ApplicationVersion + 1);
            failed.Operation.Version.Fence.Should().Be(executing.Version.Fence);
            fault.FailureCount.Should().Be(1);
        }

        await using var verification = harness.NewContext();
        (await verification.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.Failed)).Should().Be(1);
        (await verification.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExpirationTransition_PostCommitFailureReturnsCommittedExpirationOnce()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        var fault = new RetryOnceCommittedTransactionInterceptor();
        ApplicationOperationSnapshot proposed;
        await using (var db = harness.NewRetryingContext(fault))
        {
            var coordinator = harness.NewCoordinator(db);
            proposed = (await coordinator.ProposeAsync(
                ApplicationOperationSqliteHarness.Proposal(
                    expiresAtUtc: ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2)))).Operation;
            var expiration = new ApplicationOperationTransitionRequest(
                proposed.OperationId,
                proposed.Scope,
                proposed.Version,
                ApplicationOperationStatus.Expired,
                ByteAssertions.Digest("stable-expiration"),
                proposed.ExpiresAtUtc,
                Lease: null,
                ConfirmationIssue: null,
                ConfirmationUse: null,
                FailureCode: null,
                EffectStarted: false,
                ApplicationOperationDecision.Unknown);
            fault.Arm();

            var expired = await harness.NewStore(db).TransitionAsync(expiration, default);

            expired.IsReplay.Should().BeTrue();
            expired.Operation.Status.Should().Be(ApplicationOperationStatus.Expired);
            expired.Operation.Version.Should().Be(new ApplicationOperationVersion(
                proposed.Version.ApplicationVersion + 1,
                proposed.Version.Fence));
            fault.FailureCount.Should().Be(1);
        }

        await using var verification = harness.NewContext();
        (await verification.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.Expired)).Should().Be(1);
        (await verification.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
        (await verification.ApplicationProtectedPayloads.CountAsync(
            item => item.ContentKind == ApplicationProtectedContentKind.Receipt)).Should().Be(0);
    }

    [Fact]
    public async Task ExpiredLease_CanBeRecoveredOnceWithNewFenceAndOldLeaseCannotExecute()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var executing = await harness.CreateExecutingAsync(db);
        var store = harness.NewStore(db);
        var recoveredAt = executing.LeaseExpiresAtUtc!.Value;
        var recovery = new ApplicationOperationTransitionRequest(
            executing.OperationId,
            executing.Scope,
            executing.Version,
            ApplicationOperationStatus.Executing,
            ByteAssertions.Digest("lease-recovery"),
            recoveredAt,
            new ApplicationOperationLeaseRequest(
                "lease-b",
                recoveredAt.AddMinutes(2),
                RecoverExpiredLease: true),
            ConfirmationIssue: null,
            ConfirmationUse: null,
            FailureCode: null,
            EffectStarted: false);

        var recovered = await store.TransitionAsync(recovery, default);

        recovered.Operation.Version.Fence.Should().Be(executing.Version.Fence + 1);
        recovered.Operation.AttemptCount.Should().Be(2);
        recovered.Operation.LeaseId.Should().Be("lease-b");
        var staleHandler = new ProfileMutationHandler(db, "owner-a");
        var staleAct = async () => await store.ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(recovered.Operation, "lease-a", recoveredAt.AddSeconds(1)),
            staleHandler,
            default);
        await staleAct.Should().ThrowAsync<ApplicationOperationConflictException>();
        staleHandler.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task ExpiredLease_PostCommitFailureReturnsCommittedFenceAndLeaseWithoutIncrementingAgain()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        var fault = new RetryOnceCommittedTransactionInterceptor();
        ApplicationOperationSnapshot executing;
        ApplicationOperationTransitionRequest recovery;
        await using (var db = harness.NewRetryingContext(fault))
        {
            executing = await harness.CreateExecutingAsync(db);
            var recoveredAt = executing.LeaseExpiresAtUtc!.Value;
            recovery = new ApplicationOperationTransitionRequest(
                executing.OperationId,
                executing.Scope,
                executing.Version,
                ApplicationOperationStatus.Executing,
                ByteAssertions.Digest("stable-lease-recovery"),
                recoveredAt,
                new ApplicationOperationLeaseRequest(
                    "lease-post-commit",
                    recoveredAt.AddMinutes(2),
                    RecoverExpiredLease: true),
                ConfirmationIssue: null,
                ConfirmationUse: null,
                FailureCode: null,
                EffectStarted: false);
            fault.Arm();

            var recovered = await harness.NewStore(db).TransitionAsync(recovery, default);

            recovered.IsReplay.Should().BeTrue();
            recovered.Operation.Version.Should().Be(new ApplicationOperationVersion(
                executing.Version.ApplicationVersion + 1,
                executing.Version.Fence + 1));
            recovered.Operation.LeaseId.Should().Be("lease-post-commit");
            recovered.Operation.LeaseExpiresAtUtc.Should().Be(recovery.Lease!.LeaseExpiresAtUtc);
            recovered.Operation.AttemptCount.Should().Be(2);
            fault.FailureCount.Should().Be(1);
        }

        await using var verification = harness.NewContext();
        var persisted = await verification.ApplicationOperations.AsNoTracking().SingleAsync();
        persisted.ApplicationVersion.Should().Be(executing.Version.ApplicationVersion + 1);
        persisted.Fence.Should().Be(executing.Version.Fence + 1);
        persisted.AttemptCount.Should().Be(2);
        persisted.LeaseId.Should().Be("lease-post-commit");
        (await verification.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.LeaseRecovered)).Should().Be(1);
        (await verification.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
        (await verification.ApplicationProtectedPayloads.CountAsync(
            item => item.ContentKind == ApplicationProtectedContentKind.Receipt)).Should().Be(0);
    }

    [Fact]
    public async Task InternalTransitionReplayFailsClosedForDifferentReferenceOwnerAuthorityOrDesiredState()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var executing = await harness.CreateExecutingAsync(db);
        var recoveredAt = executing.LeaseExpiresAtUtc!.Value;
        var recovery = new ApplicationOperationTransitionRequest(
            executing.OperationId,
            executing.Scope,
            executing.Version,
            ApplicationOperationStatus.Executing,
            ByteAssertions.Digest("stable-lease-recovery"),
            recoveredAt,
            new ApplicationOperationLeaseRequest(
                "lease-b",
                recoveredAt.AddMinutes(2),
                RecoverExpiredLease: true),
            ConfirmationIssue: null,
            ConfirmationUse: null,
            FailureCode: null,
            EffectStarted: false);
        var store = harness.NewStore(db);
        await store.TransitionAsync(recovery, default);

        var mismatches = new[]
        {
            recovery with { DecisionReferenceDigest = ByteAssertions.Digest("different-reference") },
            recovery with { Scope = ApplicationOperationSqliteHarness.Scope("owner-b") },
            recovery with
            {
                Scope = ApplicationOperationSqliteHarness.Scope(
                    authority: ApplicationExecutionAuthority.NativeLocal)
            },
            recovery with
            {
                Lease = recovery.Lease! with { LeaseId = "different-lease" }
            },
            recovery with
            {
                ExpectedVersion = recovery.ExpectedVersion with
                {
                    ApplicationVersion = recovery.ExpectedVersion.ApplicationVersion + 1
                }
            },
            recovery with
            {
                ExpectedVersion = recovery.ExpectedVersion with
                {
                    Fence = recovery.ExpectedVersion.Fence + 1
                }
            },
            recovery with
            {
                TargetStatus = ApplicationOperationStatus.Failed,
                Lease = null,
                FailureCode = ApplicationOperationFailureCode.PreEffectHandlerFailure
            }
        };

        foreach (var mismatch in mismatches)
        {
            var act = async () => await store.TransitionAsync(mismatch, default);
            await act.Should().ThrowAsync<ApplicationOperationConflictException>();
        }

        var persisted = await db.ApplicationOperations.AsNoTracking().SingleAsync();
        persisted.Fence.Should().Be(executing.Version.Fence + 1);
        persisted.AttemptCount.Should().Be(2);
        persisted.LeaseId.Should().Be("lease-b");
        (await db.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.LeaseRecovered)).Should().Be(1);
    }

    [Fact]
    public async Task ExpiredLease_PreCommitFailureRetriesAndCommitsOneRecovery()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        var fault = new RetryOnceSaveChangesInterceptor();
        await using var db = harness.NewRetryingContext(fault);
        var executing = await harness.CreateExecutingAsync(db);
        var recoveredAt = executing.LeaseExpiresAtUtc!.Value;
        var recovery = new ApplicationOperationTransitionRequest(
            executing.OperationId,
            executing.Scope,
            executing.Version,
            ApplicationOperationStatus.Executing,
            ByteAssertions.Digest("pre-commit-lease-recovery"),
            recoveredAt,
            new ApplicationOperationLeaseRequest(
                "lease-after-pre-commit-failure",
                recoveredAt.AddMinutes(2),
                RecoverExpiredLease: true),
            ConfirmationIssue: null,
            ConfirmationUse: null,
            FailureCode: null,
            EffectStarted: false);
        var saveAttemptsBeforeRecovery = fault.AttemptCount;
        fault.Arm();

        var recovered = await harness.NewStore(db).TransitionAsync(recovery, default);

        recovered.IsReplay.Should().BeFalse();
        recovered.Operation.Version.Fence.Should().Be(executing.Version.Fence + 1);
        recovered.Operation.AttemptCount.Should().Be(2);
        fault.FailureCount.Should().Be(1);
        fault.AttemptCount.Should().Be(saveAttemptsBeforeRecovery + 2);
        (await db.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.LeaseRecovered)).Should().Be(1);
    }

    [Fact]
    public async Task UnknownDecisionCannotAuthorizeAnUndeclaredInternalTransition()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        var proposed = (await coordinator.ProposeAsync(
            ApplicationOperationSqliteHarness.Proposal())).Operation;
        var request = new ApplicationOperationTransitionRequest(
            proposed.OperationId,
            proposed.Scope,
            proposed.Version,
            ApplicationOperationStatus.Executed,
            ByteAssertions.Digest("undeclared-internal-transition"),
            ApplicationOperationSqliteHarness.NowUtc.AddSeconds(1),
            Lease: null,
            ConfirmationIssue: null,
            ConfirmationUse: null,
            FailureCode: null,
            EffectStarted: false,
            ApplicationOperationDecision.Unknown);

        var act = async () => await harness.NewStore(db).TransitionAsync(request, default);

        await act.Should().ThrowAsync<ApplicationOperationConflictException>();
        (await db.ApplicationOperationEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Execute_ReplaysReceiptAfterResponseLossAndMutatesDomainOnce()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        string receiptId;

        await using (var firstDb = harness.NewContext())
        {
            var executing = await harness.CreateExecutingAsync(firstDb);
            var coordinator = harness.NewCoordinator(firstDb);
            var handler = new ProfileMutationHandler(firstDb, "owner-a");
            var first = await coordinator.ExecuteAsync(
                ApplicationOperationSqliteHarness.Execution(executing),
                handler);
            handler.InvocationCount.Should().Be(1);
            first.Receipt.IsReplay.Should().BeFalse();
            first.Receipt.ReceiptContent.Should().Equal("durable-receipt"u8.ToArray());
            receiptId = first.Receipt.ReceiptId;
        }

        await using (var retryDb = harness.NewContext())
        {
            var coordinator = harness.NewCoordinator(retryDb);
            var handler = new ProfileMutationHandler(retryDb, "owner-a");
            var staleRequest = new ApplicationOperationExecutionRequest(
                "operation-a",
                ApplicationOperationSqliteHarness.Scope(),
                new ApplicationOperationVersion(2, 0),
                "lost-response-lease",
                ApplicationOperationSqliteHarness.NowUtc.AddMinutes(1));

            var replay = await coordinator.ExecuteAsync(staleRequest, handler);

            replay.Receipt.IsReplay.Should().BeTrue();
            replay.Receipt.ReceiptId.Should().Be(receiptId);
            replay.Receipt.ReceiptContent.Should().Equal("durable-receipt"u8.ToArray());
            handler.InvocationCount.Should().Be(0);
        }

        await using var verification = harness.NewContext();
        (await verification.UserProfiles.SingleAsync(item => item.Id == "owner-a"))
            .Name.Should().Be("Mutated");
        (await verification.ApplicationOperationReceipts.CountAsync()).Should().Be(1);
        (await verification.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.Executed)).Should().Be(1);
    }

    [Theory]
    [InlineData(2, 4)]
    [InlineData(3, 3)]
    public async Task Execute_StaleDomainOrSynchronizationVersionRollsBackTrackedMutation(
        long domainVersion,
        long synchronizationVersion)
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var executing = await harness.CreateExecutingAsync(
            db,
            expectedDomainVersion: 3,
            expectedSynchronizationVersion: 4);
        var handler = new ProfileMutationHandler(
            db,
            "owner-a",
            beforeVersion: new ApplicationStateVersions(domainVersion, synchronizationVersion),
            afterVersion: new ApplicationStateVersions(domainVersion + 1, synchronizationVersion + 1));
        var store = harness.NewStore(db);

        var act = async () => await store.ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(executing),
            handler,
            default);

        await act.Should().ThrowAsync<ApplicationOperationValidationException>();
        handler.InvocationCount.Should().Be(1);

        await using var verification = harness.NewContext();
        (await verification.UserProfiles.SingleAsync(item => item.Id == "owner-a"))
            .Name.Should().Be("Original");
        (await verification.ApplicationOperations.SingleAsync()).Status
            .Should().Be(ApplicationOperationStatus.Executing);
        (await verification.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Execute_StaleFenceFailsBeforeHandlerOrDisclosure()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var executing = await harness.CreateExecutingAsync(db);
        var handler = new ProfileMutationHandler(db, "owner-a");
        var request = ApplicationOperationSqliteHarness.Execution(executing) with
        {
            ExpectedVersion = executing.Version with { Fence = executing.Version.Fence + 1 }
        };

        var act = async () => await harness.NewStore(db).ExecuteAsync(request, handler, default);

        await act.Should().ThrowAsync<ApplicationOperationConflictException>();
        handler.InvocationCount.Should().Be(0);
        (await db.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ReversalChild_IsTheOnlyPathThatReversesExecutedParent()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var parentExecuting = await harness.CreateExecutingAsync(db);
        var parentHandler = new ReversibleProfileMutationHandler(db, "owner-a");
        var parentResult = await harness.NewStore(db).ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(parentExecuting),
            parentHandler,
            default);
        var parent = parentResult.Operation;

        var reversalCommand = ApplicationOperationSqliteHarness.Proposal(
            operationId: "operation-reversal",
            idempotencyMaterial: "reversal-key"u8.ToArray()) with
        {
            ParentOperationId = parent.OperationId,
            ParentApplicationVersion = parent.Version.ApplicationVersion,
            ParentFence = parent.Version.Fence
        };
        var coordinator = harness.NewCoordinator(db);
        var reversal = (await coordinator.ProposeAsync(reversalCommand)).Operation;
        reversal = (await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                operationId: reversal.OperationId,
                leaseId: "reversal-lease"))).Operation;
        var reversalHandler = new ProfileMutationHandler(db, "owner-a");

        await coordinator.ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(reversal, "reversal-lease"),
            reversalHandler);

        var settledParent = await db.ApplicationOperations.AsNoTracking()
            .SingleAsync(item => item.Id == parent.OperationId);
        var settledReceipt = await db.ApplicationOperationReceipts.AsNoTracking()
            .SingleAsync(item => item.OperationId == parent.OperationId);
        settledParent.Status.Should().Be(ApplicationOperationStatus.Reversed);
        settledReceipt.Reversal.Should().Be(ApplicationReversalAvailability.Completed);
        settledReceipt.ReversalOperationId.Should().Be(reversal.OperationId);
    }

    public enum UnknownOrdinaryTransition
    {
        ProposedToExecuting,
        ProposedToProtectedConfirmation,
        ProtectedConfirmationRotation,
        ProtectedConfirmationToExecuting,
        ProposedToRejected,
        ProtectedConfirmationToRejected,
        ProposedToCancelled,
        ProtectedConfirmationToCancelled,
        ExecutingToExecuted,
        ExecutedToReversed
    }

    private sealed class ReversibleProfileMutationHandler(
        Microsoft.EntityFrameworkCore.DbContext db,
        string owner)
        : IApplicationOperationHandler
    {
        public string CapabilityCode => "resource.update";

        public int CapabilityVersion => 1;

        public async Task<ApplicationOperationHandlerResult> ExecuteAsync(
            ApplicationOperationHandlerContext context,
            CancellationToken cancellationToken)
        {
            var profile = await db.Set<SentenceStudio.Shared.Models.UserProfile>()
                .SingleAsync(item => item.Id == owner, cancellationToken);
            profile.Name = "Reversible";
            return new ApplicationOperationHandlerResult(
                new ApplicationStateVersions(3, 4),
                new ApplicationStateVersions(4, 5),
                1,
                "reversible-receipt"u8.ToArray(),
                ApplicationReversalAvailability.Available,
                ApplicationOperationSqliteHarness.NowUtc.AddDays(1),
                Continuation: null);
        }
    }
}
