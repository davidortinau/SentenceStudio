using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Data;
using SentenceStudio.Data.AppOperations;

namespace SentenceStudio.UnitTests.AppOperations;

public sealed class ApplicationOperationMaintenanceTests
{
    [Fact]
    public async Task EmptyOwner_ExportAndDeleteIssueNoDatabaseCommands()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        var counter = new CommandCounter();
        await using var db = harness.NewContext(counter);
        var maintenance = new EfApplicationOperationMaintenance(db);
        counter.Reset();

        var exported = await maintenance.ExportOwnerAsync(" ", default);
        var deleted = await maintenance.DeleteOwnerAsync(string.Empty, default);

        exported.UserProfileId.Should().BeEmpty();
        exported.Operations.Should().BeEmpty();
        deleted.Should().Be(0);
        counter.Count.Should().Be(0);
    }

    [Fact]
    public async Task OwnerExport_DisclosesSafeMetadataAndOmitsEveryProtectedPayload()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var canonical = "owner-export-canonical"u8.ToArray();
        var receiptSource = "owner-export-receipt"u8.ToArray();
        var coordinator = harness.NewCoordinator(db);
        var proposed = await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            idempotencyMaterial: "owner-export-key"u8.ToArray(),
            contents:
            [
                new ApplicationOperationContent(
                    ApplicationProtectedContentKind.CanonicalRequest,
                    1,
                    canonical)
            ]));
        var executing = await coordinator.DecideAsync(ApplicationOperationSqliteHarness.Decision(
            operationId: proposed.Operation.OperationId));
        await coordinator.ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(executing.Operation),
            new ExportReceiptHandler(db, receiptSource));

        var export = await new EfApplicationOperationMaintenance(db)
            .ExportOwnerAsync("owner-a", default);
        var json = JsonSerializer.Serialize(export);

        export.Operations.Should().ContainSingle();
        export.Operations[0].OperationId.Should().Be("operation-a");
        export.Operations[0].CapabilityCode.Should().Be("resource.update");
        export.Operations[0].Status.Should().Be(nameof(ApplicationOperationStatus.Executed));
        export.Receipts.Should().ContainSingle();
        export.Receipts[0].OperationId.Should().Be("operation-a");
        export.Receipts[0].ReceiptVersion.Should().Be(1);
        export.AuthorityCoverage.Should().BeEquivalentTo(
        [
            new ApplicationOperationAuthorityExportCoverage(
                nameof(ApplicationExecutionAuthority.Server),
                Included: true,
                OmissionReason: null),
            new ApplicationOperationAuthorityExportCoverage(
                nameof(ApplicationExecutionAuthority.NativeLocal),
                Included: false,
                "No rows for this authority were present in the current store; other stores were not queried.")
        ]);
        export.ProtectedPayloadDisposition.Should().Be(
            "Protected payloads are omitted because recipient-bound export re-encryption is not configured.");
        export.ProtectedPayloads.Should().HaveCount(2)
            .And.OnlyContain(payload =>
                payload.OmissionReason
                    == "Protected content omitted because recipient-bound export re-encryption is not configured.");
        json.Should().NotContain(System.Text.Encoding.UTF8.GetString(canonical));
        json.Should().NotContain(Convert.ToBase64String(canonical));
        json.Should().NotContain(System.Text.Encoding.UTF8.GetString(receiptSource));
        json.Should().NotContain(Convert.ToBase64String(receiptSource));
        json.Should().NotContain("owner-export-key");
        json.Should().NotContain("decision-a");
        foreach (var forbidden in new[]
                 {
                     "Ciphertext",
                     "IdempotencyDigest",
                     "CanonicalRequestDigest",
                     "ConfirmationDigest",
                     "ConfirmationVerifier",
                     "DecisionReferenceDigest",
                     "InteractionScopeDigest",
                     "Secret",
                     "LeaseId",
                     "LeaseExpiresAtUtc",
                     "Fence"
                 })
        {
            json.Should().NotContain($"\"{forbidden}\"");
        }
    }

    [Fact]
    public async Task OwnerDeletion_RetryWrapsTheWholeTransactionAndClearsFailedAttemptState()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using (var seed = harness.NewContext())
        {
            var coordinator = harness.NewCoordinator(seed);
            await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
                operationId: "owner-a-operation",
                owner: "owner-a"));
            await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
                operationId: "owner-b-operation",
                owner: "owner-b"));
        }

        var fault = new RetryOnceSaveChangesInterceptor();
        await using (var db = harness.NewRetryingContext(fault))
        {
            fault.Arm();
            var deleted = await new EfApplicationOperationMaintenance(db)
                .DeleteOwnerAsync("owner-a", default);

            deleted.Should().Be(1);
            fault.FailureCount.Should().Be(1);
            fault.AttemptCount.Should().Be(2);
        }

        await using var verification = harness.NewContext();
        (await verification.ApplicationOperations.AsNoTracking().SingleAsync())
            .Id.Should().Be("owner-b-operation");
        (await verification.ApplicationOperationEvents.CountAsync(
            item => item.UserProfileId == "owner-a")).Should().Be(0);
        (await verification.ApplicationProtectedPayloads.CountAsync(
            item => item.UserProfileId == "owner-a")).Should().Be(0);
    }

    [Fact]
    public async Task Retention_RetryRebuildsExpirationAndWritesOneEvent()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        var expiry = ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2);
        await using (var seed = harness.NewContext())
        {
            await harness.NewCoordinator(seed).ProposeAsync(
                ApplicationOperationSqliteHarness.Proposal(
                    operationId: "expired-on-retry",
                    expiresAtUtc: expiry,
                    purgeAfterUtc: expiry.AddDays(10)));
        }

        var fault = new RetryOnceSaveChangesInterceptor();
        await using (var db = harness.NewRetryingContext(fault))
        {
            fault.Arm();
            var result = await new EfApplicationOperationMaintenance(db)
                .ApplyAsync(expiry, maximumOperations: 20, default);

            result.ExpiredOperations.Should().Be(1);
            result.PurgedOperations.Should().Be(0);
            fault.FailureCount.Should().Be(1);
            fault.AttemptCount.Should().Be(2);
        }

        await using var verification = harness.NewContext();
        var operation = await verification.ApplicationOperations.AsNoTracking().SingleAsync();
        operation.Status.Should().Be(ApplicationOperationStatus.Expired);
        operation.ApplicationVersion.Should().Be(2);
        (await verification.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.Expired)).Should().Be(1);
    }

    [Fact]
    public async Task OwnerExport_ExcludesForeignOwnerRowsAndContent()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            operationId: "owner-a-operation",
            owner: "owner-a",
            contents:
            [
                new ApplicationOperationContent(
                    ApplicationProtectedContentKind.CanonicalRequest,
                    1,
                    "owner-a-private"u8.ToArray())
            ]));
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            operationId: "owner-b-operation",
            owner: "owner-b",
            contents:
            [
                new ApplicationOperationContent(
                    ApplicationProtectedContentKind.CanonicalRequest,
                    1,
                    "owner-b-private"u8.ToArray())
            ]));

        var export = await new EfApplicationOperationMaintenance(db)
            .ExportOwnerAsync("owner-a", default);
        var json = JsonSerializer.Serialize(export);

        json.Should().Contain("owner-a-operation");
        json.Should().NotContain("owner-b-operation");
        json.Should().NotContain(Convert.ToBase64String("owner-b-private"u8.ToArray()));
    }

    [Fact]
    public async Task OwnerDeletion_IsScopedAndSecondPassDeletesZeroRows()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            operationId: "owner-a-operation",
            owner: "owner-a"));
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            operationId: "owner-b-operation",
            owner: "owner-b"));
        var maintenance = new EfApplicationOperationMaintenance(db);

        (await maintenance.DeleteOwnerAsync("owner-a", default)).Should().Be(1);
        (await maintenance.DeleteOwnerAsync("owner-a", default)).Should().Be(0);

        var remaining = await db.ApplicationOperations.AsNoTracking().SingleAsync();
        remaining.Id.Should().Be("owner-b-operation");
        remaining.UserProfileId.Should().Be("owner-b");
        (await db.ApplicationProtectedPayloads.CountAsync(
            item => item.UserProfileId == "owner-a")).Should().Be(0);
        (await db.ApplicationOperationEvents.CountAsync(
            item => item.UserProfileId == "owner-a")).Should().Be(0);
    }

    [Fact]
    public async Task Retention_ExpiresOnlyEligibleProposalsAndLeavesExecutingOperationClaimed()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        var expiry = ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            operationId: "expired-proposal",
            expiresAtUtc: expiry,
            purgeAfterUtc: expiry.AddDays(10)));
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            operationId: "executing-operation",
            expiresAtUtc: expiry,
            purgeAfterUtc: expiry.AddDays(10)));
        await coordinator.DecideAsync(ApplicationOperationSqliteHarness.Decision(
            operationId: "executing-operation",
            leaseId: "active-lease",
            leaseExpiresAtUtc: expiry.AddMinutes(10)));

        var result = await new EfApplicationOperationMaintenance(db).ApplyAsync(
            expiry,
            maximumOperations: 20,
            default);

        result.ExpiredOperations.Should().Be(1);
        var operations = await db.ApplicationOperations.AsNoTracking()
            .OrderBy(item => item.Id)
            .ToListAsync();
        operations.Single(item => item.Id == "expired-proposal").Status
            .Should().Be(ApplicationOperationStatus.Expired);
        operations.Single(item => item.Id == "executing-operation").Status
            .Should().Be(ApplicationOperationStatus.Executing);
    }

    [Fact]
    public async Task Retention_PreservesReplayTombstoneAndIdempotencySafetyAfterContentPurge()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        var idempotency = "retained-idempotency"u8.ToArray();
        string originalOperationId;
        await using (var db = harness.NewContext())
        {
            var coordinator = harness.NewCoordinator(db);
            await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
                idempotencyMaterial: idempotency,
                purgeAfterUtc: ApplicationOperationSqliteHarness.NowUtc.AddMinutes(5)));
            var executing = (await coordinator.DecideAsync(
                ApplicationOperationSqliteHarness.Decision())).Operation;
            var executed = await coordinator.ExecuteAsync(
                ApplicationOperationSqliteHarness.Execution(executing),
                new ProfileMutationHandler(db, "owner-a"));
            originalOperationId = executed.Operation.OperationId;

            var retention = await new EfApplicationOperationMaintenance(db).ApplyAsync(
                ApplicationOperationSqliteHarness.NowUtc.AddMinutes(5),
                maximumOperations: 20,
                default);
            retention.PurgedOperations.Should().Be(1);
        }

        await using var retryDb = harness.NewContext();
        var replay = await harness.NewCoordinator(retryDb).ProposeAsync(
            ApplicationOperationSqliteHarness.Proposal(
                operationId: "operation-after-retention",
                idempotencyMaterial: idempotency));

        replay.IsReplay.Should().BeTrue();
        replay.Operation.OperationId.Should().Be(originalOperationId);
        (await retryDb.ApplicationOperations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Retention_ContinuationPurgesAtOwnDeadlineBeforeOperationTombstone()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        var continuationPurge = ApplicationOperationSqliteHarness.NowUtc.AddHours(1);
        var operationPurge = continuationPurge.AddHours(1);
        await using var db = harness.NewContext();
        await ExecuteWithContinuationAsync(
            harness,
            db,
            "operation-continuation-first",
            operationPurge,
            Continuation(
                "continuation-first",
                expiresAtUtc: continuationPurge.AddMinutes(-1),
                purgeAfterUtc: continuationPurge));
        var maintenance = new EfApplicationOperationMaintenance(db);

        await maintenance.ApplyAsync(continuationPurge.AddTicks(-1), 20, default);

        (await db.ApplicationOperationContinuations.CountAsync()).Should().Be(1);
        (await db.ApplicationProtectedPayloads.CountAsync(payload =>
            payload.SubjectKind == ApplicationOperationContentSubjectKind.Continuation))
            .Should().Be(1);

        var result = await maintenance.ApplyAsync(continuationPurge, 20, default);

        result.PurgedOperations.Should().Be(0);
        (await db.ApplicationOperationContinuations.CountAsync()).Should().Be(0);
        (await db.ApplicationProtectedPayloads.CountAsync(payload =>
            payload.SubjectKind == ApplicationOperationContentSubjectKind.Continuation))
            .Should().Be(0);
        var operation = await db.ApplicationOperations.AsNoTracking().SingleAsync();
        operation.PayloadPurgedAtUtc.Should().BeNull();
        operation.PurgeAfterUtc.Should().Be(operationPurge);
    }

    [Fact]
    public async Task Retention_OperationPurgeWaitsForActiveContinuationAndRequiredState()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        var operationPurge = ApplicationOperationSqliteHarness.NowUtc.AddMinutes(5);
        var continuationExpiry = operationPurge.AddMinutes(5);
        await using var db = harness.NewContext();
        var executed = await ExecuteWithContinuationAsync(
            harness,
            db,
            "operation-active-continuation",
            operationPurge,
            Continuation(
                "active-continuation",
                expiresAtUtc: continuationExpiry,
                purgeAfterUtc: continuationExpiry.AddMinutes(5),
                allowsAutomaticResume: true,
                workflow: ApplicationContinuationWorkflow.PostReceiptResume));

        var result = await new EfApplicationOperationMaintenance(db)
            .ApplyAsync(operationPurge, 20, default);

        result.PurgedOperations.Should().Be(0);
        var operation = await db.ApplicationOperations.AsNoTracking().SingleAsync();
        operation.PayloadPurgedAtUtc.Should().BeNull();
        (await db.ApplicationOperationReceipts.CountAsync()).Should().Be(1);
        (await db.ApplicationProtectedPayloads.CountAsync(payload =>
            payload.SubjectKind == ApplicationOperationContentSubjectKind.Continuation
            && payload.SubjectId == "active-continuation")).Should().Be(1);

        var resumed = await harness.NewStore(db).ResumeContinuationAsync(
            executed.Scope,
            "active-continuation",
            expectedApplicationVersion: 1,
            operationPurge.AddMinutes(1),
            default);
        resumed.State.Should().Be(ApplicationContinuationState.Completed);
    }

    [Fact]
    public async Task Retention_PayloadDeadlinesAreIndependentBeforeAndAfterOperationPurge()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        var earlyPayloadPurge = ApplicationOperationSqliteHarness.NowUtc.AddMinutes(5);
        var operationPurge = earlyPayloadPurge.AddMinutes(5);
        var latePayloadPurge = operationPurge.AddMinutes(5);
        await using var db = harness.NewContext();
        var operation = TerminalOperation("payload-deadlines", "owner-a", operationPurge);
        db.ApplicationOperations.Add(operation);
        AddPayload(
            db,
            operation,
            "payload-early",
            ApplicationProtectedContentKind.CanonicalRequest,
            earlyPayloadPurge);
        AddPayload(
            db,
            operation,
            "payload-late",
            ApplicationProtectedContentKind.ProposalPresentation,
            latePayloadPurge);
        await db.SaveChangesAsync();
        var maintenance = new EfApplicationOperationMaintenance(db);

        await maintenance.ApplyAsync(earlyPayloadPurge.AddTicks(-1), 20, default);
        (await db.ApplicationProtectedPayloads.CountAsync()).Should().Be(2);

        await maintenance.ApplyAsync(earlyPayloadPurge, 20, default);
        (await db.ApplicationProtectedPayloads.Select(payload => payload.Id).ToListAsync())
            .Should().Equal("payload-late");
        (await db.ApplicationOperations.AsNoTracking().SingleAsync())
            .PayloadPurgedAtUtc.Should().BeNull();

        await maintenance.ApplyAsync(operationPurge, 20, default);
        (await db.ApplicationProtectedPayloads.CountAsync()).Should().Be(1);
        (await db.ApplicationOperations.AsNoTracking().SingleAsync())
            .PayloadPurgedAtUtc.Should().BeNull();

        await maintenance.ApplyAsync(latePayloadPurge.AddTicks(-1), 20, default);
        (await db.ApplicationProtectedPayloads.CountAsync()).Should().Be(1);

        var result = await maintenance.ApplyAsync(latePayloadPurge.AddTicks(1), 20, default);
        result.PurgedOperations.Should().Be(1);
        (await db.ApplicationProtectedPayloads.CountAsync()).Should().Be(0);
        (await db.ApplicationOperations.AsNoTracking().SingleAsync())
            .PayloadPurgedAtUtc.Should().Be(latePayloadPurge.AddTicks(1));
    }

    [Fact]
    public async Task Retention_ConfirmationIsRetainedJustBeforeAndRemovedAtExactExpiry()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        var expiry = ApplicationOperationSqliteHarness.NowUtc.AddMinutes(5);
        await using var db = harness.NewContext();
        var coordinator = harness.NewCoordinator(db);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            operationId: "confirmation-boundary",
            confirmation: ApplicationConfirmationPolicy.ProtectedConfirmation,
            expiresAtUtc: expiry,
            purgeAfterUtc: expiry.AddMinutes(5)));
        await coordinator.DecideAsync(ApplicationOperationSqliteHarness.Decision(
            operationId: "confirmation-boundary",
            confirmationReference: "confirmation-boundary-reference",
            confirmationMaterial: "confirmation-boundary-secret"u8.ToArray(),
            leaseId: null,
            leaseExpiresAtUtc: null));
        var maintenance = new EfApplicationOperationMaintenance(db);

        await maintenance.ApplyAsync(expiry.AddTicks(-1), 20, default);
        (await db.ApplicationOperationConfirmations.CountAsync()).Should().Be(1);

        var result = await maintenance.ApplyAsync(expiry, 20, default);

        result.ExpiredOperations.Should().Be(1);
        (await db.ApplicationOperationConfirmations.CountAsync()).Should().Be(0);
        (await db.ApplicationOperations.AsNoTracking().SingleAsync()).Status
            .Should().Be(ApplicationOperationStatus.Expired);
    }

    [Fact]
    public async Task Retention_LinkedContinuationChainMakesForwardProgressWithBatchSizeOne()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        var continuationPurge = ApplicationOperationSqliteHarness.NowUtc.AddHours(1);
        var operationPurge = continuationPurge.AddHours(1);
        await using var db = harness.NewContext();
        await ExecuteWithContinuationAsync(
            harness,
            db,
            "operation-root-continuation",
            operationPurge,
            Continuation(
                "continuation-root",
                expiresAtUtc: continuationPurge.AddMinutes(-1),
                purgeAfterUtc: continuationPurge));
        await ExecuteWithContinuationAsync(
            harness,
            db,
            "operation-child-continuation",
            operationPurge,
            Continuation(
                "continuation-child",
                expiresAtUtc: continuationPurge.AddMinutes(-1),
                purgeAfterUtc: continuationPurge,
                parentContinuationId: "continuation-root"));
        var maintenance = new EfApplicationOperationMaintenance(db);
        var remainingCounts = new List<int>
        {
            await CountContinuationComponentRowsAsync(db)
        };

        for (var attempt = 0; attempt < 4 && remainingCounts[^1] > 0; attempt++)
        {
            await maintenance.ApplyAsync(continuationPurge, maximumOperations: 1, default);
            remainingCounts.Add(await CountContinuationComponentRowsAsync(db));
        }

        remainingCounts[^1].Should().Be(0);
        remainingCounts.Zip(remainingCounts.Skip(1))
            .Should().OnlyContain(pair => pair.First > pair.Second);
        (await db.ApplicationOperations.CountAsync()).Should().Be(2);
        (await db.ApplicationOperations.CountAsync(operation =>
            operation.PayloadPurgedAtUtc != null)).Should().Be(0);
    }

    [Fact]
    public async Task Retention_LinkedOperationsAdvanceOneAtATimeWithBatchSizeOne()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        var deadline = ApplicationOperationSqliteHarness.NowUtc.AddMinutes(5);
        await using var db = harness.NewContext();
        var parent = TerminalOperation("linked-parent", "owner-a", deadline);
        var child = TerminalOperation("linked-child", "owner-a", deadline);
        child.ParentOperationId = parent.Id;
        child.ParentApplicationVersion = parent.ApplicationVersion;
        child.ParentFence = parent.Fence;
        child.ParentOperation = parent;
        db.ApplicationOperations.AddRange(parent, child);
        await db.SaveChangesAsync();
        var maintenance = new EfApplicationOperationMaintenance(db);

        (await maintenance.ApplyAsync(deadline.AddTicks(-1), 1, default))
            .PurgedOperations.Should().Be(0);
        (await maintenance.ApplyAsync(deadline, 1, default))
            .PurgedOperations.Should().Be(1);
        (await db.ApplicationOperations.CountAsync(operation =>
            operation.PayloadPurgedAtUtc != null)).Should().Be(1);

        (await maintenance.ApplyAsync(deadline, 1, default))
            .PurgedOperations.Should().Be(1);
        (await db.ApplicationOperations.CountAsync(operation =>
            operation.PayloadPurgedAtUtc != null)).Should().Be(2);
    }

    [Fact]
    public async Task Retention_TransientPurgeRetryRebuildsSelectionAndPreservesForeignScope()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        var deadline = ApplicationOperationSqliteHarness.NowUtc.AddMinutes(5);
        await using (var seed = harness.NewContext())
        {
            var due = TerminalOperation("due-server-owner-a", "owner-a", deadline);
            seed.ApplicationOperations.Add(due);
            AddPayload(
                seed,
                due,
                "due-server-owner-a-payload",
                ApplicationProtectedContentKind.CanonicalRequest,
                deadline);

            var foreignOwner = TerminalOperation(
                "later-server-owner-b",
                "owner-b",
                deadline.AddHours(1));
            seed.ApplicationOperations.Add(foreignOwner);
            AddPayload(
                seed,
                foreignOwner,
                "later-server-owner-b-payload",
                ApplicationProtectedContentKind.CanonicalRequest,
                deadline.AddHours(1));

            var foreignAuthority = TerminalOperation(
                "later-native-owner-a",
                "owner-a",
                deadline.AddHours(1),
                ApplicationExecutionAuthority.NativeLocal);
            seed.ApplicationOperations.Add(foreignAuthority);
            AddPayload(
                seed,
                foreignAuthority,
                "later-native-owner-a-payload",
                ApplicationProtectedContentKind.CanonicalRequest,
                deadline.AddHours(1));
            await seed.SaveChangesAsync();
        }

        var fault = new RetryOnceSaveChangesInterceptor();
        await using (var db = harness.NewRetryingContext(fault))
        {
            fault.Arm();
            var result = await new EfApplicationOperationMaintenance(db)
                .ApplyAsync(deadline, 20, default);

            result.PurgedOperations.Should().Be(1);
            fault.FailureCount.Should().Be(1);
            fault.AttemptCount.Should().Be(2);
        }

        await using var verification = harness.NewContext();
        (await verification.ApplicationProtectedPayloads.Select(payload => payload.Id).ToListAsync())
            .Should().BeEquivalentTo(
                ["later-server-owner-b-payload", "later-native-owner-a-payload"]);
        (await verification.ApplicationOperations.SingleAsync(operation =>
            operation.Id == "due-server-owner-a")).PayloadPurgedAtUtc.Should().Be(deadline);
        (await verification.ApplicationOperations.CountAsync(operation =>
            operation.PayloadPurgedAtUtc == null)).Should().Be(2);
    }

    private static async Task<ApplicationOperationSnapshot> ExecuteWithContinuationAsync(
        ApplicationOperationSqliteHarness harness,
        ApplicationDbContext db,
        string operationId,
        DateTime operationPurgeAfterUtc,
        ApplicationOperationContinuationWrite continuation)
    {
        var coordinator = harness.NewCoordinator(db);
        await coordinator.ProposeAsync(ApplicationOperationSqliteHarness.Proposal(
            operationId: operationId,
            purgeAfterUtc: operationPurgeAfterUtc));
        var executing = (await coordinator.DecideAsync(
            ApplicationOperationSqliteHarness.Decision(operationId: operationId))).Operation;
        var result = await coordinator.ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(executing),
            new ProfileMutationHandler(db, "owner-a", continuation: continuation));
        return result.Operation;
    }

    private static ApplicationOperationContinuationWrite Continuation(
        string continuationId,
        DateTime expiresAtUtc,
        DateTime purgeAfterUtc,
        bool allowsAutomaticResume = false,
        string? parentContinuationId = null,
        ApplicationContinuationWorkflow workflow = ApplicationContinuationWorkflow.Clarification) =>
        new(
            continuationId,
            workflow,
            WorkflowVersion: 1,
            ByteAssertions.Digest($"{continuationId}-scope"),
            parentContinuationId,
            allowsAutomaticResume,
            StateSchemaVersion: 1,
            ProtectedStateSource: System.Text.Encoding.UTF8.GetBytes($"{continuationId}-state"),
            ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2),
            expiresAtUtc,
            purgeAfterUtc);

    private static ApplicationOperationRecord TerminalOperation(
        string operationId,
        string owner,
        DateTime purgeAfterUtc,
        ApplicationExecutionAuthority authority = ApplicationExecutionAuthority.Server)
    {
        var terminalAtUtc = ApplicationOperationSqliteHarness.NowUtc.AddSeconds(1);
        return new ApplicationOperationRecord
        {
            Id = operationId,
            UserProfileId = owner,
            Authority = authority,
            CapabilityCode = "resource.update",
            CapabilityFamily = "resource-management",
            CapabilityVersion = 1,
            CapabilityFingerprint = $"sha256:{new string('a', 64)}",
            Effect = ApplicationEffectClass.Write,
            Confirmation = ApplicationConfirmationPolicy.Accept,
            Status = ApplicationOperationStatus.Executed,
            CanonicalRequestDigest = ByteAssertions.Digest($"{operationId}-request"),
            ExpectedDomainVersion = 0,
            ExpectedSynchronizationVersion = 0,
            ApplicationVersion = 1,
            Fence = 0,
            AttemptCount = 0,
            CreatedAtUtc = ApplicationOperationSqliteHarness.NowUtc,
            UpdatedAtUtc = terminalAtUtc,
            ExpiresAtUtc = ApplicationOperationSqliteHarness.NowUtc.AddHours(1),
            PurgeAfterUtc = purgeAfterUtc,
            TerminalAtUtc = terminalAtUtc
        };
    }

    private static void AddPayload(
        ApplicationDbContext db,
        ApplicationOperationRecord operation,
        string payloadId,
        ApplicationProtectedContentKind contentKind,
        DateTime purgeAfterUtc)
    {
        db.ApplicationProtectedPayloads.Add(new ApplicationProtectedPayloadRecord
        {
            Id = payloadId,
            OperationId = operation.Id,
            UserProfileId = operation.UserProfileId,
            SubjectKind = ApplicationOperationContentSubjectKind.Operation,
            SubjectId = operation.Id,
            ContentKind = contentKind,
            ProtectionVersion = 1,
            SchemaVersion = 1,
            Ciphertext = System.Text.Encoding.UTF8.GetBytes($"{payloadId}-ciphertext"),
            PlaintextLength = payloadId.Length,
            CreatedAtUtc = ApplicationOperationSqliteHarness.NowUtc,
            PurgeAfterUtc = purgeAfterUtc,
            Operation = operation
        });
    }

    private static async Task<int> CountContinuationComponentRowsAsync(ApplicationDbContext db) =>
        await db.ApplicationOperationContinuations.CountAsync()
        + await db.ApplicationProtectedPayloads.CountAsync(payload =>
            payload.SubjectKind == ApplicationOperationContentSubjectKind.Continuation);

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        internal void Reset() => Interlocked.Exchange(ref _count, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Interlocked.Increment(ref _count);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class ExportReceiptHandler(ApplicationDbContext db, byte[] receiptSource)
        : IApplicationOperationHandler
    {
        public string CapabilityCode => "resource.update";

        public int CapabilityVersion => 1;

        public async Task<ApplicationOperationHandlerResult> ExecuteAsync(
            ApplicationOperationHandlerContext context,
            CancellationToken cancellationToken)
        {
            var profile = await db.UserProfiles.SingleAsync(
                item => item.Id == "owner-a",
                cancellationToken);
            profile.Name = "Exported";
            return new ApplicationOperationHandlerResult(
                new ApplicationStateVersions(1, 1),
                new ApplicationStateVersions(2, 2),
                ReceiptSchemaVersion: 1,
                receiptSource,
                ApplicationReversalAvailability.Unavailable,
                ReversalExpiresAtUtc: null,
                Continuation: null);
        }
    }
}
