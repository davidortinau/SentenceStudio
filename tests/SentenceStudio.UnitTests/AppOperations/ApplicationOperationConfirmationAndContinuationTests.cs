using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.UnitTests.AppOperations;

public sealed class ApplicationOperationConfirmationAndContinuationTests
{
    [Fact]
    public async Task ProtectedConfirmation_IsIssuedConsumedOnceAndStoresOnlyDigests()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            confirmation: ApplicationConfirmationPolicy.ProtectedConfirmation));
        var secret = "one-use-confirmation-secret"u8.ToArray();

        var awaiting = await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                confirmationReference: "confirmation-a",
                confirmationMaterial: secret,
                leaseId: null,
                leaseExpiresAtUtc: null));

        awaiting.Operation.Status.Should().Be(ApplicationOperationStatus.AwaitingProtectedConfirmation);
        awaiting.Operation.LeaseId.Should().BeNull();
        var issued = await db.ApplicationOperationConfirmations.AsNoTracking().SingleAsync();
        issued.ConfirmationDigest.Should().Equal(ByteAssertions.Digest("one-use-confirmation-secret"));
        issued.ConfirmationDigest.Should().NotEqual(secret);
        issued.ConsumedAtUtc.Should().BeNull();

        var executing = await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Confirm,
                decisionReference: "confirm-decision",
                confirmationReference: "confirmation-a",
                confirmationMaterial: secret,
                leaseId: "confirmation-lease",
                decidedAtUtc: ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2)));

        executing.Operation.Status.Should().Be(ApplicationOperationStatus.Executing);
        var consumed = await db.ApplicationOperationConfirmations.AsNoTracking().SingleAsync();
        consumed.ConsumedAtUtc.Should().Be(ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2));
        consumed.ConsumedApplicationVersion.Should().Be(executing.Operation.Version.ApplicationVersion);
        consumed.ConsumedFence.Should().Be(executing.Operation.Version.Fence);

        var replay = await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Confirm,
                decisionReference: "confirm-decision",
                confirmationReference: "confirmation-a",
                confirmationMaterial: secret,
                leaseId: "confirmation-lease",
                decidedAtUtc: ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2)));
        replay.IsReplay.Should().BeTrue();
        replay.Operation.Version.Should().Be(executing.Operation.Version);
    }

    [Fact]
    public async Task ProtectedConfirmation_WrongSecretPerformsNoTransitionOrConsumption()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            confirmation: ApplicationConfirmationPolicy.ProtectedConfirmation));
        await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                confirmationReference: "confirmation-a",
                confirmationMaterial: "correct-secret"u8.ToArray(),
                leaseId: null,
                leaseExpiresAtUtc: null));

        var act = async () => await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Confirm,
                decisionReference: "confirm-decision",
                confirmationReference: "confirmation-a",
                confirmationMaterial: "wrong-secret"u8.ToArray(),
                leaseId: "confirmation-lease",
                decidedAtUtc: ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2)));

        await act.Should().ThrowAsync<ApplicationOperationConflictException>();
        (await db.ApplicationOperations.AsNoTracking().SingleAsync()).Status
            .Should().Be(ApplicationOperationStatus.AwaitingProtectedConfirmation);
        (await db.ApplicationOperationConfirmations.AsNoTracking().SingleAsync()).ConsumedAtUtc
            .Should().BeNull();
    }

    [Fact]
    public async Task ProtectedConfirmation_ExpiryWinsAndSecretCannotBeUsed()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        var expiry = ApplicationOperationSqliteHarness.NowUtc.AddSeconds(5);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            confirmation: ApplicationConfirmationPolicy.ProtectedConfirmation,
            expiresAtUtc: expiry));
        await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                confirmationReference: "confirmation-a",
                confirmationMaterial: "expiring-secret"u8.ToArray(),
                leaseId: null,
                leaseExpiresAtUtc: null));

        var expired = await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Confirm,
                confirmationReference: "confirmation-a",
                confirmationMaterial: "expiring-secret"u8.ToArray(),
                decidedAtUtc: expiry));

        expired.Operation.Status.Should().Be(ApplicationOperationStatus.Expired);
        (await db.ApplicationOperationConfirmations.AsNoTracking().SingleAsync()).ConsumedAtUtc
            .Should().BeNull();
        var eventCount = await db.ApplicationOperationEvents.CountAsync();

        var replay = await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Confirm,
                confirmationReference: null,
                confirmationMaterial: null,
                leaseId: null,
                leaseExpiresAtUtc: null,
                decidedAtUtc: expiry.AddSeconds(1)));

        replay.IsReplay.Should().BeTrue();
        replay.Operation.Status.Should().Be(ApplicationOperationStatus.Expired);
        replay.Operation.Version.Should().Be(expired.Operation.Version);
        (await db.ApplicationOperationEvents.CountAsync()).Should().Be(eventCount);
    }

    [Fact]
    public async Task ExpiredProtectedAccept_ExactDecisionReplaysWithoutChallengeAndRejectsMismatches()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        var expiry = ApplicationOperationSqliteHarness.NowUtc.AddSeconds(5);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            confirmation: ApplicationConfirmationPolicy.ProtectedConfirmation,
            expiresAtUtc: expiry));
        var first = await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decisionReference: "expired-accept",
                confirmationReference: "unused-confirmation",
                confirmationMaterial: "unused-secret"u8.ToArray(),
                leaseId: null,
                leaseExpiresAtUtc: null,
                decidedAtUtc: expiry));

        first.IsReplay.Should().BeFalse();
        first.Operation.Status.Should().Be(ApplicationOperationStatus.Expired);
        var stored = await db.ApplicationOperations.AsNoTracking().SingleAsync();
        stored.Decision.Should().Be(ApplicationOperationDecision.Accept);
        stored.DecisionReferenceDigest.Should().NotBeNull();
        stored.DecisionReferenceDigest!.Should().Equal(ByteAssertions.Digest("expired-accept"));
        (await db.ApplicationOperationConfirmations.CountAsync()).Should().Be(0);
        var eventCount = await db.ApplicationOperationEvents.CountAsync();

        var replay = await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decisionReference: "expired-accept",
                confirmationReference: null,
                confirmationMaterial: null,
                leaseId: null,
                leaseExpiresAtUtc: null,
                decidedAtUtc: expiry.AddSeconds(1)));

        replay.IsReplay.Should().BeTrue();
        replay.Operation.Status.Should().Be(ApplicationOperationStatus.Expired);
        replay.Operation.Version.Should().Be(first.Operation.Version);
        (await db.ApplicationOperationEvents.CountAsync()).Should().Be(eventCount);

        var differentDecision = async () => await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Reject,
                decisionReference: "expired-accept",
                leaseId: null,
                leaseExpiresAtUtc: null,
                decidedAtUtc: expiry.AddSeconds(2)));
        var differentReference = async () => await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decisionReference: "different-expired-accept",
                leaseId: null,
                leaseExpiresAtUtc: null,
                decidedAtUtc: expiry.AddSeconds(2)));

        await differentDecision.Should().ThrowAsync<ApplicationOperationConflictException>();
        await differentReference.Should().ThrowAsync<ApplicationOperationConflictException>();
        (await db.ApplicationOperationEvents.CountAsync()).Should().Be(eventCount);
    }

    [Fact]
    public async Task ExpiredProtectedConfirm_ExactDecisionReplaysWithoutUseAndNeverExecutes()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        var expiry = ApplicationOperationSqliteHarness.NowUtc.AddSeconds(5);
        var secret = "expiring-confirmation"u8.ToArray();
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            confirmation: ApplicationConfirmationPolicy.ProtectedConfirmation,
            expiresAtUtc: expiry));
        await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decisionReference: "issue-expiring-confirmation",
                confirmationReference: "confirmation-a",
                confirmationMaterial: secret,
                leaseId: null,
                leaseExpiresAtUtc: null));
        var first = await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Confirm,
                decisionReference: "expired-confirm",
                confirmationReference: "confirmation-a",
                confirmationMaterial: secret,
                leaseId: "unused-expired-lease",
                decidedAtUtc: expiry));

        first.IsReplay.Should().BeFalse();
        first.Operation.Status.Should().Be(ApplicationOperationStatus.Expired);
        var stored = await db.ApplicationOperations.AsNoTracking().SingleAsync();
        stored.Decision.Should().Be(ApplicationOperationDecision.Confirm);
        stored.DecisionReferenceDigest.Should().NotBeNull();
        stored.DecisionReferenceDigest!.Should().Equal(ByteAssertions.Digest("expired-confirm"));
        (await db.ApplicationOperationConfirmations.AsNoTracking().SingleAsync()).ConsumedAtUtc
            .Should().BeNull();
        var eventCount = await db.ApplicationOperationEvents.CountAsync();

        var replay = await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Confirm,
                decisionReference: "expired-confirm",
                confirmationReference: null,
                confirmationMaterial: null,
                leaseId: null,
                leaseExpiresAtUtc: null,
                decidedAtUtc: expiry.AddSeconds(1)));
        var handler = new ProfileMutationHandler(db, "owner-a");
        var execute = async () => await coordinator.ExecuteAsync(
            new ApplicationOperationExecutionRequest(
                replay.Operation.OperationId,
                replay.Operation.Scope,
                replay.Operation.Version,
                "no-expired-lease",
                expiry.AddSeconds(2)),
            handler);

        replay.IsReplay.Should().BeTrue();
        replay.Operation.Status.Should().Be(ApplicationOperationStatus.Expired);
        replay.Operation.Version.Should().Be(first.Operation.Version);
        await execute.Should().ThrowAsync<ApplicationOperationConflictException>();
        handler.InvocationCount.Should().Be(0);
        (await db.ApplicationOperationEvents.CountAsync()).Should().Be(eventCount);
        (await db.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.Executed)).Should().Be(0);
        (await db.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
        (await db.UserProfiles.SingleAsync(item => item.Id == "owner-a")).Name.Should().Be("Original");
    }

    [Fact]
    public async Task ExecutedProtectedConfirm_ReplaysDecisionAndReceiptAfterExpiryWithoutLiveArtifacts()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        var expiry = ApplicationOperationSqliteHarness.NowUtc.AddSeconds(5);
        var secret = "executed-confirmation"u8.ToArray();
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            confirmation: ApplicationConfirmationPolicy.ProtectedConfirmation,
            expiresAtUtc: expiry));
        await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decisionReference: "issue-executed-confirmation",
                confirmationReference: "confirmation-a",
                confirmationMaterial: secret,
                leaseId: null,
                leaseExpiresAtUtc: null));
        var executing = await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Confirm,
                decisionReference: "executed-confirm",
                confirmationReference: "confirmation-a",
                confirmationMaterial: secret,
                leaseId: "confirmation-lease",
                decidedAtUtc: ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2)));
        var firstHandler = new ProfileMutationHandler(db, "owner-a");
        var executed = await coordinator.ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(
                executing.Operation,
                executedAtUtc: ApplicationOperationSqliteHarness.NowUtc.AddSeconds(3)),
            firstHandler);
        var eventCount = await db.ApplicationOperationEvents.CountAsync();

        var expiryReinterpretation = await harness.NewStore(db).TransitionAsync(
            new ApplicationOperationTransitionRequest(
                executing.Operation.OperationId,
                executing.Operation.Scope,
                executing.Operation.Version,
                ApplicationOperationStatus.Expired,
                ByteAssertions.Digest("executed-confirm"),
                expiry.AddSeconds(1),
                Lease: null,
                ConfirmationIssue: null,
                ConfirmationUse: null,
                FailureCode: null,
                EffectStarted: false,
                ApplicationOperationDecision.Confirm),
            default);
        var decisionReplay = await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Confirm,
                decisionReference: "executed-confirm",
                confirmationReference: null,
                confirmationMaterial: null,
                leaseId: null,
                leaseExpiresAtUtc: null,
                decidedAtUtc: expiry.AddSeconds(1)));
        var replayHandler = new ProfileMutationHandler(db, "owner-a");
        var receiptReplay = await coordinator.ExecuteAsync(
            new ApplicationOperationExecutionRequest(
                decisionReplay.Operation.OperationId,
                decisionReplay.Operation.Scope,
                decisionReplay.Operation.Version,
                "expired-lease-is-not-required",
                expiry.AddSeconds(2)),
            replayHandler);

        expiryReinterpretation.IsReplay.Should().BeTrue();
        expiryReinterpretation.Operation.Status.Should().Be(ApplicationOperationStatus.Executed);
        expiryReinterpretation.Operation.Version.Should().Be(executed.Operation.Version);
        decisionReplay.IsReplay.Should().BeTrue();
        decisionReplay.Operation.Status.Should().Be(ApplicationOperationStatus.Executed);
        decisionReplay.Operation.Version.Should().Be(executed.Operation.Version);
        receiptReplay.Receipt.IsReplay.Should().BeTrue();
        receiptReplay.Receipt.ReceiptId.Should().Be(executed.Receipt.ReceiptId);
        receiptReplay.Receipt.ReceiptContent.Should().Equal(executed.Receipt.ReceiptContent);
        firstHandler.InvocationCount.Should().Be(1);
        replayHandler.InvocationCount.Should().Be(0);
        (await db.ApplicationOperationEvents.CountAsync()).Should().Be(eventCount);
        (await db.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.Executed)).Should().Be(1);
        (await db.ApplicationOperationReceipts.CountAsync()).Should().Be(1);
        (await db.UserProfiles.SingleAsync(item => item.Id == "owner-a")).Name.Should().Be("Mutated");
    }

    [Fact]
    public async Task TerminalDecisionReplay_RemainsOwnerAndAuthorityScoped()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        var expiry = ApplicationOperationSqliteHarness.NowUtc.AddSeconds(5);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            confirmation: ApplicationConfirmationPolicy.ProtectedConfirmation,
            expiresAtUtc: expiry));
        await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decisionReference: "owner-scoped-expiry",
                confirmationReference: "unused-confirmation",
                confirmationMaterial: "unused-secret"u8.ToArray(),
                leaseId: null,
                leaseExpiresAtUtc: null,
                decidedAtUtc: expiry));
        var eventCount = await db.ApplicationOperationEvents.CountAsync();

        var otherOwner = async () => await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                owner: "owner-b",
                decisionReference: "owner-scoped-expiry",
                leaseId: null,
                leaseExpiresAtUtc: null,
                decidedAtUtc: expiry.AddSeconds(1)));
        var otherAuthority = async () => await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                authority: ApplicationExecutionAuthority.NativeLocal,
                decisionReference: "owner-scoped-expiry",
                leaseId: null,
                leaseExpiresAtUtc: null,
                decidedAtUtc: expiry.AddSeconds(1)));

        await otherOwner.Should().ThrowAsync<ApplicationOperationConflictException>();
        await otherAuthority.Should().ThrowAsync<ApplicationOperationConflictException>();
        (await db.ApplicationOperationEvents.CountAsync()).Should().Be(eventCount);
    }

    [Fact]
    public async Task ProtectedConfirmation_FirstNonTerminalAttemptsStillRequireArtifacts()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            confirmation: ApplicationConfirmationPolicy.ProtectedConfirmation));

        var missingIssue = async () => await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                confirmationReference: null,
                confirmationMaterial: null,
                leaseId: null,
                leaseExpiresAtUtc: null));

        await missingIssue.Should().ThrowAsync<ApplicationOperationValidationException>();
        (await db.ApplicationOperations.AsNoTracking().SingleAsync()).Status
            .Should().Be(ApplicationOperationStatus.Proposed);
        (await db.ApplicationOperationConfirmations.CountAsync()).Should().Be(0);

        await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                confirmationReference: "confirmation-a",
                confirmationMaterial: "required-secret"u8.ToArray(),
                leaseId: null,
                leaseExpiresAtUtc: null));
        var missingUse = async () => await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                decision: ApplicationOperationDecision.Confirm,
                decisionReference: "confirm-without-use",
                confirmationReference: null,
                confirmationMaterial: null,
                leaseId: "confirmation-lease",
                decidedAtUtc: ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2)));

        await missingUse.Should().ThrowAsync<ApplicationOperationValidationException>();
        (await db.ApplicationOperations.AsNoTracking().SingleAsync()).Status
            .Should().Be(ApplicationOperationStatus.AwaitingProtectedConfirmation);
        (await db.ApplicationOperationConfirmations.AsNoTracking().SingleAsync()).ConsumedAtUtc
            .Should().BeNull();
        (await db.ApplicationOperationEvents.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ProtectedConfirmation_RotationInvalidatesPreviousReference()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            confirmation: ApplicationConfirmationPolicy.ProtectedConfirmation));
        await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                confirmationReference: "confirmation-old",
                confirmationMaterial: "old-secret"u8.ToArray(),
                leaseId: null,
                leaseExpiresAtUtc: null));

        var rotated = await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(
                confirmationReference: "confirmation-new",
                confirmationMaterial: "new-secret"u8.ToArray(),
                decisionReference: "rotation-decision",
                leaseId: null,
                leaseExpiresAtUtc: null,
                decidedAtUtc: ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2)));

        rotated.Operation.Status.Should().Be(ApplicationOperationStatus.AwaitingProtectedConfirmation);
        var confirmations = await db.ApplicationOperationConfirmations.AsNoTracking()
            .OrderBy(item => item.CreatedAtUtc)
            .ToListAsync();
        confirmations.Should().HaveCount(2);
        confirmations[0].ConsumedAtUtc.Should().NotBeNull("rotation must make the old reference unusable");
        confirmations[1].ConsumedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task AutomaticContinuation_ResumesExactlyOnce()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var executing = await harness.CreateExecutingAsync(db);
        var continuation = Continuation(
            "continuation-a",
            allowsAutomaticResume: true,
            parentContinuationId: null);
        var handler = new ProfileMutationHandler(db, "owner-a", continuation: continuation);
        var store = harness.NewStore(db);
        await store.ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(executing),
            handler,
            default);
        var created = await store.FindContinuationAsync(
            executing.Scope,
            continuation.ContinuationId,
            default);

        created.Should().NotBeNull();
        created!.State.Should().Be(ApplicationContinuationState.ReadyToResume);
        created.AutomaticResumeCount.Should().Be(0);
        var resumed = await store.ResumeContinuationAsync(
            executing.Scope,
            continuation.ContinuationId,
            created.ApplicationVersion,
            ApplicationOperationSqliteHarness.NowUtc.AddSeconds(3),
            default);
        var committedReplay = await store.ResumeContinuationAsync(
            executing.Scope,
            continuation.ContinuationId,
            created.ApplicationVersion,
            ApplicationOperationSqliteHarness.NowUtc.AddSeconds(3),
            default);

        resumed.State.Should().Be(ApplicationContinuationState.Completed);
        resumed.AutomaticResumeCount.Should().Be(1);
        resumed.ApplicationVersion.Should().Be(created.ApplicationVersion + 1);
        committedReplay.Should().BeEquivalentTo(resumed);
        var replay = async () => await store.ResumeContinuationAsync(
            executing.Scope,
            continuation.ContinuationId,
            resumed.ApplicationVersion,
            ApplicationOperationSqliteHarness.NowUtc.AddSeconds(4),
            default);
        await replay.Should().ThrowAsync<ApplicationOperationConflictException>();
        (await db.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.ContinuationResumed)).Should().Be(1);
    }

    [Fact]
    public async Task AutomaticContinuation_PostCommitFailureReloadsAndReturnsCommittedResume()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        var fault = new RetryOnceCommittedTransactionInterceptor();
        await using (var db = harness.NewRetryingContext(fault))
        {
            var executing = await harness.CreateExecutingAsync(db);
            var continuation = Continuation(
                "continuation-post-commit",
                allowsAutomaticResume: true,
                parentContinuationId: null);
            var handler = new ProfileMutationHandler(db, "owner-a", continuation: continuation);
            var store = harness.NewStore(db);
            await store.ExecuteAsync(
                ApplicationOperationSqliteHarness.Execution(executing),
                handler,
                default);
            var created = (await store.FindContinuationAsync(
                executing.Scope,
                continuation.ContinuationId,
                default))!;
            var resumedAtUtc = ApplicationOperationSqliteHarness.NowUtc.AddSeconds(3);
            fault.Arm();

            var resumed = await store.ResumeContinuationAsync(
                executing.Scope,
                continuation.ContinuationId,
                created.ApplicationVersion,
                resumedAtUtc,
                default);

            resumed.State.Should().Be(ApplicationContinuationState.Completed);
            resumed.AutomaticResumeCount.Should().Be(1);
            resumed.ResumedAtUtc.Should().Be(resumedAtUtc);
            resumed.ApplicationVersion.Should().Be(created.ApplicationVersion + 1);
            fault.FailureCount.Should().Be(1);
            handler.InvocationCount.Should().Be(1);
        }

        await using var verification = harness.NewContext();
        var persisted = await verification.ApplicationOperationContinuations
            .AsNoTracking()
            .SingleAsync(item => item.Id == "continuation-post-commit");
        persisted.AutomaticResumeCount.Should().Be(1);
        (await verification.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.ContinuationResumed)).Should().Be(1);
        (await verification.ApplicationOperationReceipts.CountAsync()).Should().Be(1);
        (await verification.ApplicationProtectedPayloads.CountAsync(
            item => item.ContentKind == ApplicationProtectedContentKind.Receipt)).Should().Be(1);
        (await verification.UserProfiles.SingleAsync(item => item.Id == "owner-a"))
            .Name.Should().Be("Mutated");
    }

    [Fact]
    public async Task AutomaticContinuation_ReplayFailsClosedForDifferentReferenceOwnerAuthorityOrOutcome()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var executing = await harness.CreateExecutingAsync(db);
        var continuation = Continuation(
            "continuation-replay-boundary",
            allowsAutomaticResume: true,
            parentContinuationId: null);
        var store = harness.NewStore(db);
        await store.ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(executing),
            new ProfileMutationHandler(db, "owner-a", continuation: continuation),
            default);
        var created = (await store.FindContinuationAsync(
            executing.Scope,
            continuation.ContinuationId,
            default))!;
        var resumedAtUtc = ApplicationOperationSqliteHarness.NowUtc.AddSeconds(3);
        await store.ResumeContinuationAsync(
            executing.Scope,
            continuation.ContinuationId,
            created.ApplicationVersion,
            resumedAtUtc,
            default);

        var mismatches = new Func<Task>[]
        {
            () => store.ResumeContinuationAsync(
                executing.Scope,
                "different-continuation",
                created.ApplicationVersion,
                resumedAtUtc,
                default),
            () => store.ResumeContinuationAsync(
                ApplicationOperationSqliteHarness.Scope("owner-b"),
                continuation.ContinuationId,
                created.ApplicationVersion,
                resumedAtUtc,
                default),
            () => store.ResumeContinuationAsync(
                ApplicationOperationSqliteHarness.Scope(
                    authority: ApplicationExecutionAuthority.NativeLocal),
                continuation.ContinuationId,
                created.ApplicationVersion,
                resumedAtUtc,
                default),
            () => store.ResumeContinuationAsync(
                executing.Scope,
                continuation.ContinuationId,
                created.ApplicationVersion,
                resumedAtUtc.AddSeconds(1),
                default)
        };

        foreach (var mismatch in mismatches)
        {
            await mismatch.Should().ThrowAsync<ApplicationOperationConflictException>();
        }

        (await db.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.ContinuationResumed)).Should().Be(1);
    }

    [Fact]
    public async Task AutomaticContinuation_CannotResumeAfterExpiry()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var executing = await harness.CreateExecutingAsync(db);
        var continuation = Continuation(
            "continuation-a",
            allowsAutomaticResume: true,
            parentContinuationId: null,
            expiresAtUtc: ApplicationOperationSqliteHarness.NowUtc.AddSeconds(3));
        var store = harness.NewStore(db);
        await store.ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(executing),
            new ProfileMutationHandler(db, "owner-a", continuation: continuation),
            default);
        var created = (await store.FindContinuationAsync(
            executing.Scope,
            continuation.ContinuationId,
            default))!;

        var act = async () => await store.ResumeContinuationAsync(
            executing.Scope,
            continuation.ContinuationId,
            created.ApplicationVersion,
            continuation.ExpiresAtUtc,
            default);

        await act.Should().ThrowAsync<ApplicationOperationConflictException>();
        (await db.ApplicationOperationContinuations.AsNoTracking().SingleAsync()).AutomaticResumeCount
            .Should().Be(0);
    }

    [Fact]
    public async Task Continuation_CannotNestOrScheduleAResumeFromResumedWork()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var firstDb = harness.NewContext();
        var firstExecuting = await harness.CreateExecutingAsync(firstDb, "operation-a");
        var store = harness.NewStore(firstDb);
        await store.ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(firstExecuting),
            new ProfileMutationHandler(
                firstDb,
                "owner-a",
                continuation: Continuation("continuation-parent", true, null)),
            default);

        await using var secondDb = harness.NewContext();
        var secondExecuting = await harness.CreateExecutingAsync(secondDb, "operation-b");
        var nested = Continuation(
            "continuation-child",
            allowsAutomaticResume: false,
            parentContinuationId: "continuation-parent",
            interactionScope: "child-scope");

        var act = async () => await harness.NewStore(secondDb).ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(secondExecuting),
            new ProfileMutationHandler(secondDb, "owner-a", continuation: nested),
            default);

        await act.Should().ThrowAsync<ApplicationOperationValidationException>();
        await using var verification = harness.NewContext();
        (await verification.ApplicationOperationContinuations.CountAsync()).Should().Be(1);
        (await verification.ApplicationOperations.SingleAsync(item => item.Id == "operation-b"))
            .Status.Should().Be(ApplicationOperationStatus.Executing);
    }

    [Fact]
    public async Task Continuation_SelfCycleFailsWithoutPersistingReceiptOrMutation()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var executing = await harness.CreateExecutingAsync(db);
        var cyclic = Continuation(
            "continuation-cycle",
            allowsAutomaticResume: false,
            parentContinuationId: "continuation-cycle");

        var act = async () => await harness.NewStore(db).ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(executing),
            new ProfileMutationHandler(db, "owner-a", continuation: cyclic),
            default);

        await act.Should().ThrowAsync<ApplicationOperationValidationException>();
        await using var verification = harness.NewContext();
        (await verification.ApplicationOperationContinuations.CountAsync()).Should().Be(0);
        (await verification.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
        (await verification.UserProfiles.SingleAsync(item => item.Id == "owner-a"))
            .Name.Should().Be("Original");
    }

    private static ApplicationOperationContinuationWrite Continuation(
        string id,
        bool allowsAutomaticResume,
        string? parentContinuationId,
        string interactionScope = "interaction-scope",
        DateTime? expiresAtUtc = null) =>
        new(
            id,
            ApplicationContinuationWorkflow.PostReceiptResume,
            WorkflowVersion: 1,
            ByteAssertions.Digest(interactionScope),
            parentContinuationId,
            allowsAutomaticResume,
            StateSchemaVersion: 1,
            ProtectedStateSource: "continuation-state"u8.ToArray(),
            ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2),
            expiresAtUtc ?? ApplicationOperationSqliteHarness.NowUtc.AddMinutes(5),
            ApplicationOperationSqliteHarness.NowUtc.AddDays(1));
}
