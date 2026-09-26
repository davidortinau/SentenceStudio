using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Data;
using SentenceStudio.Data.AppOperations;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.AppOperations;

public sealed class ApplicationOperationPostgresTests
{
    private const string MigrationId = "20260903175044_AddApplicationOperationLedger";

    private static readonly string[] LedgerTables =
    [
        "ApplicationOperation",
        "ApplicationOperationConfirmation",
        "ApplicationOperationContinuation",
        "ApplicationOperationEvent",
        "ApplicationOperationReceipt",
        "ApplicationProtectedPayload"
    ];

    [Fact]
    public void Ledger_timestamps_materialized_in_the_host_zone_are_normalized_to_utc()
    {
        using var postgres = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql("Host=localhost;Database=model_only;Username=model")
                .Options);
        var model = postgres.GetService<IDesignTimeModel>().Model;
        var localExpiry = new DateTimeOffset(
                2026, 9, 4, 3, 32, 35, 465, TimeSpan.Zero)
            .ToLocalTime()
            .LocalDateTime;
        var entityTypes = new[]
        {
            typeof(ApplicationOperationRecord),
            typeof(ApplicationProtectedPayloadRecord),
            typeof(ApplicationOperationConfirmationRecord),
            typeof(ApplicationOperationReceiptRecord),
            typeof(ApplicationOperationContinuationRecord),
            typeof(ApplicationOperationEventRecord)
        };

        foreach (var clrType in entityTypes)
        {
            foreach (var property in model.FindEntityType(clrType)!.GetProperties()
                         .Where(property =>
                             property.ClrType == typeof(DateTime)
                             || property.ClrType == typeof(DateTime?)))
            {
                var converter = property.GetValueConverter();
                converter.Should().NotBeNull($"{clrType.Name}.{property.Name} is a UTC instant");
                var normalized = converter!.ConvertFromProvider(localExpiry)
                    .Should().BeOfType<DateTime>().Subject;
                normalized.Kind.Should().Be(DateTimeKind.Utc);
                normalized.Should().Be(localExpiry.ToUniversalTime());
            }
        }
    }

    [ApplicationOperationPostgresFact]
    public async Task Migration_UpAndDownRoundTripOnDisposableDatabase()
    {
        await using var harness = await ApplicationOperationPostgresHarness.CreateAsync(
            "migration",
            migrate: false);
        await using var db = harness.NewContext();
        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        migrations.Should().Contain(MigrationId);
        var migrationIndex = Array.IndexOf(migrations, MigrationId);
        migrationIndex.Should().BeGreaterThan(0);
        var previousMigration = migrations[migrationIndex - 1];

        await db.Database.MigrateAsync(MigrationId);
        await using (var connection = new NpgsqlConnection(harness.ConnectionString))
        {
            await connection.OpenAsync();
            (await NamesAsync(
                connection,
                """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";"""))
                .Should().Contain(MigrationId);
            (await NamesAsync(
                connection,
                "SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename;"))
                .Should().Contain(LedgerTables);
        }

        await db.GetService<IMigrator>().MigrateAsync(previousMigration);

        await using var reverted = new NpgsqlConnection(harness.ConnectionString);
        await reverted.OpenAsync();
        var remaining = await NamesAsync(
            reverted,
            "SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename;");
        remaining.Should().NotContain(LedgerTables);
        remaining.Should().NotBeEmpty("only the new migration is reverted");
        (await NamesAsync(
            reverted,
            """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";"""))
            .Should().NotContain(MigrationId);
    }

    [ApplicationOperationPostgresFact]
    public async Task OwnershipAuthorityReceiptAndReplayUseRealProvider()
    {
        await using var harness = await ApplicationOperationPostgresHarness.CreateAsync("replay");
        await using (var seed = harness.NewContext())
        {
            seed.UserProfiles.Add(new UserProfile
            {
                Id = "owner-a",
                Name = "Original",
                Email = "owner-a@test.invalid"
            });
            await seed.SaveChangesAsync();
            var coordinator = new ApplicationOperationCoordinator(
                harness.NewStore(seed),
                harness.Protector);
            await coordinator.ProposeAsync(Proposal());
            var executing = (await coordinator.DecideAsync(Decision(
                ApplicationOperationDecision.Accept,
                "accept-decision",
                "lease-a"))).Operation;
            var handler = new ProviderMutationHandler(seed);
            var executed = await coordinator.ExecuteAsync(
                new ApplicationOperationExecutionRequest(
                    executing.OperationId,
                    executing.Scope,
                    executing.Version,
                    "lease-a",
                    NowUtc.AddSeconds(2)),
                handler);
            handler.InvocationCount.Should().Be(1);
            executed.Receipt.IsReplay.Should().BeFalse();
        }

        await using (var retry = harness.NewContext())
        {
            var store = harness.NewStore(retry);
            (await store.FindAsync(
                new ApplicationOperationScope("owner-b", ApplicationExecutionAuthority.Server),
                "operation-a",
                default)).Should().BeNull();
            (await store.FindAsync(
                new ApplicationOperationScope("owner-a", ApplicationExecutionAuthority.NativeLocal),
                "operation-a",
                default)).Should().BeNull();
            var handler = new ProviderMutationHandler(retry);
            var replay = await store.ExecuteAsync(
                new ApplicationOperationExecutionRequest(
                    "operation-a",
                    Scope,
                    new ApplicationOperationVersion(2, 0),
                    "response-was-lost",
                    NowUtc.AddMinutes(1)),
                handler,
                default);
            replay.Receipt.IsReplay.Should().BeTrue();
            handler.InvocationCount.Should().Be(0);
        }

        await using var verify = harness.NewContext();
        (await verify.UserProfiles.SingleAsync(item => item.Id == "owner-a"))
            .Name.Should().Be("Mutated");
        (await verify.ApplicationOperationReceipts.CountAsync()).Should().Be(1);
    }

    [ApplicationOperationPostgresFact]
    public async Task ProtectedConfirmReplaysExecutedReceiptAfterExpiryWithoutDuplicateEffect()
    {
        await using var harness = await ApplicationOperationPostgresHarness.CreateAsync(
            "protected_replay");
        string receiptId;
        await using (var seed = harness.NewContext())
        {
            seed.UserProfiles.Add(new UserProfile
            {
                Id = "owner-a",
                Name = "Original",
                Email = "owner-a@test.invalid"
            });
            await seed.SaveChangesAsync();
            var coordinator = new ApplicationOperationCoordinator(
                harness.NewStore(seed),
                harness.Protector);
            await coordinator.ProposeAsync(Proposal(
                confirmation: ApplicationConfirmationPolicy.ProtectedConfirmation));
            await coordinator.DecideAsync(new ApplicationOperationDecisionCommand(
                "operation-a",
                Scope,
                ApplicationOperationDecision.Accept,
                "issue-protected-confirmation"u8.ToArray(),
                "confirmation-a",
                "confirmation-secret"u8.ToArray(),
                LeaseId: null,
                LeaseExpiresAtUtc: null,
                NowUtc.AddSeconds(1)));
            var executing = await coordinator.DecideAsync(
                new ApplicationOperationDecisionCommand(
                    "operation-a",
                    Scope,
                    ApplicationOperationDecision.Confirm,
                    "execute-protected-confirmation"u8.ToArray(),
                    "confirmation-a",
                    "confirmation-secret"u8.ToArray(),
                    "confirmation-lease",
                    NowUtc.AddMinutes(2),
                    NowUtc.AddSeconds(2)));
            var handler = new ProviderMutationHandler(seed);
            var executed = await coordinator.ExecuteAsync(
                new ApplicationOperationExecutionRequest(
                    executing.Operation.OperationId,
                    executing.Operation.Scope,
                    executing.Operation.Version,
                    "confirmation-lease",
                    NowUtc.AddSeconds(3)),
                handler);

            handler.InvocationCount.Should().Be(1);
            receiptId = executed.Receipt.ReceiptId;
        }

        await using (var retry = harness.NewContext())
        {
            var coordinator = new ApplicationOperationCoordinator(
                harness.NewStore(retry),
                harness.Protector);
            var decisionReplay = await coordinator.DecideAsync(
                new ApplicationOperationDecisionCommand(
                    "operation-a",
                    Scope,
                    ApplicationOperationDecision.Confirm,
                    "execute-protected-confirmation"u8.ToArray(),
                    ConfirmationReferenceId: null,
                    ConfirmationMaterial: null,
                    LeaseId: null,
                    LeaseExpiresAtUtc: null,
                    NowUtc.AddMinutes(11)));
            var handler = new ProviderMutationHandler(retry);
            var receiptReplay = await coordinator.ExecuteAsync(
                new ApplicationOperationExecutionRequest(
                    decisionReplay.Operation.OperationId,
                    decisionReplay.Operation.Scope,
                    decisionReplay.Operation.Version,
                    "expired-lease-is-not-required",
                    NowUtc.AddMinutes(12)),
                handler);

            decisionReplay.IsReplay.Should().BeTrue();
            decisionReplay.Operation.Status.Should().Be(ApplicationOperationStatus.Executed);
            receiptReplay.Receipt.IsReplay.Should().BeTrue();
            receiptReplay.Receipt.ReceiptId.Should().Be(receiptId);
            handler.InvocationCount.Should().Be(0);
        }

        await using var verify = harness.NewContext();
        (await verify.UserProfiles.SingleAsync(item => item.Id == "owner-a"))
            .Name.Should().Be("Mutated");
        (await verify.ApplicationOperationReceipts.CountAsync()).Should().Be(1);
        (await verify.ApplicationOperationEvents.CountAsync()).Should().Be(4);
        (await verify.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.Executed)).Should().Be(1);
    }

    [ApplicationOperationPostgresFact]
    public async Task ConcurrentAcceptRejectAndConfirmHaveSingleWinners()
    {
        await using var harness = await ApplicationOperationPostgresHarness.CreateAsync("races");
        await using (var seed = harness.NewContext())
        {
            var coordinator = new ApplicationOperationCoordinator(
                harness.NewStore(seed),
                harness.Protector);
            await coordinator.ProposeAsync(Proposal("decision-race"));
            await coordinator.ProposeAsync(Proposal(
                "confirmation-race",
                ApplicationConfirmationPolicy.ProtectedConfirmation));
            await coordinator.DecideAsync(new ApplicationOperationDecisionCommand(
                "confirmation-race",
                Scope,
                ApplicationOperationDecision.Accept,
                "issue-decision"u8.ToArray(),
                "confirmation-a",
                "confirmation-secret"u8.ToArray(),
                LeaseId: null,
                LeaseExpiresAtUtc: null,
                NowUtc.AddSeconds(1)));
        }

        var decisions = await Task.WhenAll(
            RunDecisionAsync(
                harness,
                "decision-race",
                ApplicationOperationDecision.Accept,
                "accept-race",
                "accept-lease"),
            RunDecisionAsync(
                harness,
                "decision-race",
                ApplicationOperationDecision.Reject,
                "reject-race",
                lease: null));
        AssertSingleWinner(decisions);

        var confirmations = await Task.WhenAll(
            RunConfirmationAsync(harness, "confirm-a", "confirm-lease-a"),
            RunConfirmationAsync(harness, "confirm-b", "confirm-lease-b"));
        AssertSingleWinner(confirmations);

        await using var verification = harness.NewContext();
        (await verification.ApplicationOperationConfirmations
            .SingleAsync(item => item.OperationId == "confirmation-race"))
            .ConsumedAtUtc.Should().NotBeNull();
        (await verification.ApplicationOperations
            .SingleAsync(item => item.Id == "confirmation-race"))
            .AttemptCount.Should().Be(1);
    }

    [ApplicationOperationPostgresFact]
    public async Task ConcurrentSameIdempotencyKeyCreationProducesOneOperationAndOneProposedEvent()
    {
        await using var harness = await ApplicationOperationPostgresHarness.CreateAsync("create_race");
        const string sharedKey = "shared-create-key";

        var results = await Task.WhenAll(
            RunProposalAsync(harness, Proposal("create-race-a", idempotencyKey: sharedKey)),
            RunProposalAsync(harness, Proposal("create-race-b", idempotencyKey: sharedKey)));

        results.Select(result => result.Operation.OperationId).Distinct().Should().ContainSingle();
        results.Count(result => result.IsReplay).Should().Be(1);

        await using var verification = harness.NewContext();
        (await verification.ApplicationOperations.CountAsync()).Should().Be(1);
        (await verification.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.Proposed)).Should().Be(1);
    }

    [ApplicationOperationPostgresFact]
    public async Task SettlementFailureRollsBackDomainReceiptEventAndOperationTogether()
    {
        await using var harness = await ApplicationOperationPostgresHarness.CreateAsync("rollback");
        var interceptor = new ReceiptFailureInterceptor();
        await using (var db = harness.NewContext(interceptor))
        {
            db.UserProfiles.Add(new UserProfile
            {
                Id = "owner-a",
                Name = "Original",
                Email = "owner-a@test.invalid"
            });
            await db.SaveChangesAsync();
            var coordinator = new ApplicationOperationCoordinator(
                harness.NewStore(db),
                harness.Protector);
            await coordinator.ProposeAsync(Proposal());
            var executing = (await coordinator.DecideAsync(Decision(
                ApplicationOperationDecision.Accept,
                "accept-decision",
                "lease-a"))).Operation;
            interceptor.Arm();

            var act = async () => await coordinator.ExecuteAsync(
                new ApplicationOperationExecutionRequest(
                    executing.OperationId,
                    executing.Scope,
                    executing.Version,
                    "lease-a",
                    NowUtc.AddSeconds(2)),
                new ProviderMutationHandler(db));

            await act.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verification = harness.NewContext();
        (await verification.UserProfiles.SingleAsync(item => item.Id == "owner-a"))
            .Name.Should().Be("Original");
        var operation = await verification.ApplicationOperations.SingleAsync();
        operation.Status.Should().Be(ApplicationOperationStatus.Executing);
        (await verification.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
        (await verification.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.Executed)).Should().Be(0);
    }

    [ApplicationOperationPostgresFact]
    public async Task ContinuationResumePostCommitFailureReturnsTheCommittedResumeOnce()
    {
        await using var harness = await ApplicationOperationPostgresHarness.CreateAsync(
            "resume_commit");
        var fault = new CommitAmbiguityInterceptor();
        await using (var db = harness.NewRetryingContext(fault))
        {
            db.UserProfiles.Add(new UserProfile
            {
                Id = "owner-a",
                Name = "Original",
                Email = "owner-a@test.invalid"
            });
            await db.SaveChangesAsync();
            var coordinator = new ApplicationOperationCoordinator(
                harness.NewStore(db),
                harness.Protector);
            await coordinator.ProposeAsync(Proposal());
            var executing = (await coordinator.DecideAsync(Decision(
                ApplicationOperationDecision.Accept,
                "accept-resume",
                "resume-lease"))).Operation;
            var continuation = new ApplicationOperationContinuationWrite(
                "postgres-continuation",
                ApplicationContinuationWorkflow.PostReceiptResume,
                WorkflowVersion: 1,
                ApplicationOperationDigest.Compute("postgres-interaction"u8.ToArray()),
                ParentContinuationId: null,
                AllowsAutomaticResume: true,
                StateSchemaVersion: 1,
                ProtectedStateSource: "postgres-continuation-state"u8.ToArray(),
                NowUtc.AddSeconds(2),
                NowUtc.AddMinutes(5),
                NowUtc.AddDays(1));
            await coordinator.ExecuteAsync(
                new ApplicationOperationExecutionRequest(
                    executing.OperationId,
                    executing.Scope,
                    executing.Version,
                    "resume-lease",
                    NowUtc.AddSeconds(2)),
                new ProviderMutationHandler(db, continuation));
            var store = harness.NewStore(db);
            var created = (await store.FindContinuationAsync(
                Scope,
                continuation.ContinuationId,
                default))!;
            fault.ArmOnce();

            var resumed = await store.ResumeContinuationAsync(
                Scope,
                continuation.ContinuationId,
                created.ApplicationVersion,
                NowUtc.AddSeconds(3),
                default);

            resumed.AutomaticResumeCount.Should().Be(1);
            resumed.State.Should().Be(ApplicationContinuationState.Completed);
            fault.FailureCount.Should().Be(1);
        }

        await using var verification = harness.NewContext();
        (await verification.ApplicationOperationContinuations.SingleAsync())
            .AutomaticResumeCount.Should().Be(1);
        (await verification.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.ContinuationResumed)).Should().Be(1);
        (await verification.ApplicationOperationReceipts.CountAsync()).Should().Be(1);
    }

    [ApplicationOperationPostgresFact]
    public async Task LeaseRecoveryPostCommitFailureReturnsTheCommittedFenceAndLeaseOnce()
    {
        await using var harness = await ApplicationOperationPostgresHarness.CreateAsync(
            "lease_commit");
        var fault = new CommitAmbiguityInterceptor();
        await using (var db = harness.NewRetryingContext(fault))
        {
            var coordinator = new ApplicationOperationCoordinator(
                harness.NewStore(db),
                harness.Protector);
            await coordinator.ProposeAsync(Proposal());
            var executing = (await coordinator.DecideAsync(Decision(
                ApplicationOperationDecision.Accept,
                "accept-recovery",
                "expired-lease"))).Operation;
            var recoveredAt = executing.LeaseExpiresAtUtc!.Value;
            var recovery = new ApplicationOperationTransitionRequest(
                executing.OperationId,
                executing.Scope,
                executing.Version,
                ApplicationOperationStatus.Executing,
                ApplicationOperationDigest.Compute("postgres-lease-recovery"u8.ToArray()),
                recoveredAt,
                new ApplicationOperationLeaseRequest(
                    "recovered-lease",
                    recoveredAt.AddMinutes(2),
                    RecoverExpiredLease: true),
                ConfirmationIssue: null,
                ConfirmationUse: null,
                FailureCode: null,
                EffectStarted: false);
            fault.ArmOnce();

            var recovered = await harness.NewStore(db).TransitionAsync(recovery, default);

            recovered.IsReplay.Should().BeTrue();
            recovered.Operation.Version.Should().Be(new ApplicationOperationVersion(
                executing.Version.ApplicationVersion + 1,
                executing.Version.Fence + 1));
            recovered.Operation.LeaseId.Should().Be("recovered-lease");
            recovered.Operation.AttemptCount.Should().Be(2);
            fault.FailureCount.Should().Be(1);
        }

        await using var verification = harness.NewContext();
        var persisted = await verification.ApplicationOperations.SingleAsync();
        persisted.Fence.Should().Be(1);
        persisted.AttemptCount.Should().Be(2);
        persisted.LeaseId.Should().Be("recovered-lease");
        (await verification.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.LeaseRecovered)).Should().Be(1);
        (await verification.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
    }

    [ApplicationOperationPostgresFact]
    public async Task UnknownDecisionCannotAuthorizeOrdinaryAcceptTransition()
    {
        await using var harness = await ApplicationOperationPostgresHarness.CreateAsync(
            "unknown_decision");
        await using var db = harness.NewContext();
        var coordinator = new ApplicationOperationCoordinator(
            harness.NewStore(db),
            harness.Protector);
        var proposed = (await coordinator.ProposeAsync(Proposal())).Operation;
        var request = new ApplicationOperationTransitionRequest(
            proposed.OperationId,
            proposed.Scope,
            proposed.Version,
            ApplicationOperationStatus.Executing,
            ApplicationOperationDigest.Compute("unknown-postgres-accept"u8.ToArray()),
            NowUtc.AddSeconds(1),
            new ApplicationOperationLeaseRequest(
                "unknown-postgres-lease",
                NowUtc.AddMinutes(2),
                RecoverExpiredLease: false),
            ConfirmationIssue: null,
            ConfirmationUse: null,
            FailureCode: null,
            EffectStarted: false,
            ApplicationOperationDecision.Unknown);

        var act = async () => await harness.NewStore(db).TransitionAsync(request, default);

        await act.Should().ThrowAsync<ApplicationOperationConflictException>();
        (await db.ApplicationOperationEvents.CountAsync()).Should().Be(1);
        (await db.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
        (await db.ApplicationOperations.SingleAsync()).Status
            .Should().Be(ApplicationOperationStatus.Proposed);
    }

    private static readonly DateTime NowUtc =
        new(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc);

    private static readonly ApplicationOperationScope Scope =
        new("owner-a", ApplicationExecutionAuthority.Server);

    private static ApplicationOperationProposalCommand Proposal(
        string id = "operation-a",
        ApplicationConfirmationPolicy confirmation = ApplicationConfirmationPolicy.Accept,
        string? idempotencyKey = null) =>
        new(
            id,
            Scope,
            "resource.update",
            "resource-management",
            1,
            $"sha256:{new string('a', 64)}",
            ApplicationEffectClass.Write,
            confirmation,
            ParentOperationId: null,
            ParentApplicationVersion: null,
            ParentFence: null,
            IdempotencyMaterial:             System.Text.Encoding.UTF8.GetBytes(idempotencyKey ?? $"{id}-key"),
            ExpectedDomainVersion: 0,
            ExpectedSynchronizationVersion: 0,
            NowUtc,
            NowUtc.AddMinutes(10),
            NowUtc.AddDays(30),
            [
                new ApplicationOperationContent(
                    ApplicationProtectedContentKind.CanonicalRequest,
                    1,
                    "canonical-request"u8.ToArray())
            ]);

    private static async Task<ApplicationOperationTransitionResult> RunProposalAsync(
        ApplicationOperationPostgresHarness harness,
        ApplicationOperationProposalCommand command)
    {
        await using var db = harness.NewContext();
        return await new ApplicationOperationCoordinator(harness.NewStore(db), harness.Protector)
            .ProposeAsync(command);
    }

    private static ApplicationOperationDecisionCommand Decision(
        ApplicationOperationDecision decision,
        string reference,
        string? lease) =>
        new(
            "operation-a",
            Scope,
            decision,
            System.Text.Encoding.UTF8.GetBytes(reference),
            ConfirmationReferenceId: null,
            ConfirmationMaterial: null,
            lease,
            lease is null ? null : NowUtc.AddMinutes(2),
            NowUtc.AddSeconds(1));

    private static async Task<Exception?> RunDecisionAsync(
        ApplicationOperationPostgresHarness harness,
        string operationId,
        ApplicationOperationDecision decision,
        string reference,
        string? lease)
    {
        try
        {
            await using var db = harness.NewContext();
            await new ApplicationOperationCoordinator(harness.NewStore(db), harness.Protector)
                .DecideAsync(new ApplicationOperationDecisionCommand(
                    operationId,
                    Scope,
                    decision,
                    System.Text.Encoding.UTF8.GetBytes(reference),
                    ConfirmationReferenceId: null,
                    ConfirmationMaterial: null,
                    lease,
                    lease is null ? null : NowUtc.AddMinutes(2),
                    NowUtc.AddSeconds(2)));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> RunConfirmationAsync(
        ApplicationOperationPostgresHarness harness,
        string reference,
        string lease)
    {
        try
        {
            await using var db = harness.NewContext();
            await new ApplicationOperationCoordinator(harness.NewStore(db), harness.Protector)
                .DecideAsync(new ApplicationOperationDecisionCommand(
                    "confirmation-race",
                    Scope,
                    ApplicationOperationDecision.Confirm,
                    System.Text.Encoding.UTF8.GetBytes(reference),
                    "confirmation-a",
                    "confirmation-secret"u8.ToArray(),
                    lease,
                    NowUtc.AddMinutes(2),
                    NowUtc.AddSeconds(2)));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void AssertSingleWinner(IReadOnlyCollection<Exception?> outcomes)
    {
        outcomes.Count(exception => exception is null).Should().Be(1);
        outcomes.Count(exception => exception is ApplicationOperationConflictException).Should().Be(1);
    }

    private static async Task<string[]> NamesAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private sealed class ProviderMutationHandler(
        ApplicationDbContext db,
        ApplicationOperationContinuationWrite? continuation = null)
        : IApplicationOperationHandler
    {
        internal int InvocationCount { get; private set; }

        public string CapabilityCode => "resource.update";

        public int CapabilityVersion => 1;

        public async Task<ApplicationOperationHandlerResult> ExecuteAsync(
            ApplicationOperationHandlerContext context,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            var profile = await db.UserProfiles.SingleAsync(
                item => item.Id == "owner-a",
                cancellationToken);
            profile.Name = "Mutated";
            return new ApplicationOperationHandlerResult(
                new ApplicationStateVersions(1, 1),
                new ApplicationStateVersions(2, 2),
                1,
                "provider-receipt"u8.ToArray(),
                ApplicationReversalAvailability.Unavailable,
                ReversalExpiresAtUtc: null,
                Continuation: continuation);
        }
    }

    private sealed class CommitAmbiguityInterceptor : DbTransactionInterceptor
    {
        private int _armed;
        private int _failureCount;

        internal int FailureCount => Volatile.Read(ref _failureCount);

        internal void ArmOnce() => Interlocked.Exchange(ref _armed, 1);

        public override void TransactionCommitted(
            DbTransaction transaction,
            TransactionEndEventData eventData)
        {
            ThrowIfArmed();
            base.TransactionCommitted(transaction, eventData);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed();
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }

        private void ThrowIfArmed()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
            {
                return;
            }

            Interlocked.Increment(ref _failureCount);
            throw new NpgsqlException(
                "Simulated connection loss after the ledger commit was applied.",
                new System.Net.Sockets.SocketException(54));
        }
    }

    private sealed class ReceiptFailureInterceptor : SaveChangesInterceptor
    {
        private bool _armed;

        internal void Arm() => _armed = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_armed)
            {
                _armed = false;
                eventData.Context!.ChangeTracker
                    .Entries<ApplicationOperationReceiptRecord>()
                    .Single(entry => entry.State == EntityState.Added)
                    .Entity.ReceiptVersion = 0;
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
