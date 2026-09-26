using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Data;
using SentenceStudio.Data.AppOperations;

namespace SentenceStudio.UnitTests.AppOperations;

public sealed class ApplicationOperationTransactionTests
{
    [Theory]
    [InlineData(SettlementFault.Receipt)]
    [InlineData(SettlementFault.Event)]
    [InlineData(SettlementFault.Operation)]
    public async Task Execute_RollsBackHandlerMutationAndAllSettlementRowsWhenCommitFails(
        SettlementFault fault)
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        var interceptor = new SettlementFailureInterceptor(fault);
        await using (var db = harness.NewContext(interceptor))
        {
            var executing = await harness.CreateExecutingAsync(db);
            interceptor.Arm();
            var handler = new ProfileMutationHandler(db, "owner-a");

            var act = async () => await harness.NewStore(db).ExecuteAsync(
                ApplicationOperationSqliteHarness.Execution(executing),
                handler,
                default);

            await act.Should().ThrowAsync<DbUpdateException>();
            handler.InvocationCount.Should().Be(1);
        }

        await using var verification = harness.NewContext();
        (await verification.UserProfiles.SingleAsync(item => item.Id == "owner-a"))
            .Name.Should().Be("Original");
        var operation = await verification.ApplicationOperations.SingleAsync();
        operation.Status.Should().Be(ApplicationOperationStatus.Executing);
        operation.ApplicationVersion.Should().Be(2);
        operation.LeaseId.Should().Be("lease-a");
        (await verification.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
        (await verification.ApplicationProtectedPayloads.CountAsync(
            item => item.ContentKind == ApplicationProtectedContentKind.Receipt)).Should().Be(0);
        (await verification.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.Executed)).Should().Be(0);
    }

    [Fact]
    public async Task Execute_HandlerExceptionRollsBackTrackedMutationAndLeavesLeaseRecoverable()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using (var db = harness.NewContext())
        {
            var executing = await harness.CreateExecutingAsync(db);
            var handler = new ThrowingMutationHandler(db);

            var act = async () => await harness.NewStore(db).ExecuteAsync(
                ApplicationOperationSqliteHarness.Execution(executing),
                handler,
                default);

            await act.Should().ThrowAsync<ControlledHandlerException>();
        }

        await using var verification = harness.NewContext();
        (await verification.UserProfiles.SingleAsync(item => item.Id == "owner-a"))
            .Name.Should().Be("Original");
        var operation = await verification.ApplicationOperations.SingleAsync();
        operation.Status.Should().Be(ApplicationOperationStatus.Executing);
        operation.LeaseId.Should().Be("lease-a");
        (await verification.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteFailure_CleansTheSameUnitOfWorkBeforeCallerRecordsControlledFailure()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using var db = harness.NewContext();
        var executing = await harness.CreateExecutingAsync(
            db,
            expectedDomainVersion: 3,
            expectedSynchronizationVersion: 4);
        var store = harness.NewStore(db);
        var staleHandler = new ProfileMutationHandler(
            db,
            "owner-a",
            beforeVersion: new ApplicationStateVersions(2, 4),
            afterVersion: new ApplicationStateVersions(3, 5));

        var execute = async () => await store.ExecuteAsync(
            ApplicationOperationSqliteHarness.Execution(executing),
            staleHandler,
            default);
        await execute.Should().ThrowAsync<ApplicationOperationValidationException>();

        var failed = await store.TransitionAsync(
            new ApplicationOperationTransitionRequest(
                executing.OperationId,
                executing.Scope,
                executing.Version,
                ApplicationOperationStatus.Failed,
                ByteAssertions.Digest("stale-domain-failure"),
                ApplicationOperationSqliteHarness.NowUtc.AddSeconds(3),
                Lease: null,
                ConfirmationIssue: null,
                ConfirmationUse: null,
                ApplicationOperationFailureCode.StaleDomainVersion,
                EffectStarted: false),
            default);

        failed.Operation.Status.Should().Be(ApplicationOperationStatus.Failed);
        await using var verification = harness.NewContext();
        (await verification.UserProfiles.SingleAsync(item => item.Id == "owner-a"))
            .Name.Should().Be("Original");
        (await verification.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task StoreTransactions_RunThroughConfiguredRetryingExecutionStrategy()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        var fault = new RetryOnceSaveChangesInterceptor();
        await using var db = harness.NewRetryingContext(fault);
        var coordinator = harness.NewCoordinator(db);
        fault.Arm();

        var result = await coordinator.ProposeAsync(
            ApplicationOperationSqliteHarness.Proposal());

        result.Operation.Status.Should().Be(ApplicationOperationStatus.Proposed);
        fault.FailureCount.Should().Be(1);
        fault.AttemptCount.Should().Be(2);
        (await db.ApplicationOperations.CountAsync()).Should().Be(1);
        (await db.ApplicationOperationEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Execute_RetryClearsDirtyTrackerAndCommitsOneEffectReceiptEventAndSettlement()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        var fault = new RetryOnceSaveChangesInterceptor();
        await using (var db = harness.NewRetryingContext(fault))
        {
            var executing = await harness.CreateExecutingAsync(db);
            var handler = new AppendingMutationHandler(db);
            fault.Arm();

            var executed = await harness.NewStore(db).ExecuteAsync(
                ApplicationOperationSqliteHarness.Execution(executing),
                handler,
                default);

            executed.Operation.Status.Should().Be(ApplicationOperationStatus.Executed);
            executed.Receipt.IsReplay.Should().BeFalse();
            handler.InvocationCount.Should().Be(2);
            handler.ObservedNames.Should().Equal("Original", "Original");
            fault.FailureCount.Should().Be(1);
        }

        await using var verification = harness.NewContext();
        (await verification.UserProfiles.AsNoTracking().SingleAsync(item => item.Id == "owner-a"))
            .Name.Should().Be("Original-mutated");
        (await verification.ApplicationOperationReceipts.CountAsync()).Should().Be(1);
        (await verification.ApplicationProtectedPayloads.CountAsync(
            item => item.ContentKind == ApplicationProtectedContentKind.Receipt)).Should().Be(1);
        (await verification.ApplicationOperationEvents.CountAsync(
            item => item.Kind == ApplicationOperationEventKind.Executed)).Should().Be(1);
        var operation = await verification.ApplicationOperations.AsNoTracking().SingleAsync();
        operation.Status.Should().Be(ApplicationOperationStatus.Executed);
        operation.ApplicationVersion.Should().Be(3);
    }

    public enum SettlementFault
    {
        Receipt,
        Event,
        Operation
    }

    private sealed class SettlementFailureInterceptor(SettlementFault fault)
        : SaveChangesInterceptor
    {
        private bool _armed;

        internal void Arm() => _armed = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_armed)
            {
                return base.SavingChangesAsync(eventData, result, cancellationToken);
            }

            _armed = false;
            var context = eventData.Context
                ?? throw new InvalidOperationException("A context is required for settlement sabotage.");
            switch (fault)
            {
                case SettlementFault.Receipt:
                    context.ChangeTracker.Entries<ApplicationOperationReceiptRecord>()
                        .Single(entry => entry.State == EntityState.Added)
                        .Entity.ReceiptVersion = 0;
                    break;
                case SettlementFault.Event:
                    context.ChangeTracker.Entries<ApplicationOperationEventRecord>()
                        .Single(entry =>
                            entry.State == EntityState.Added
                            && entry.Entity.Kind == ApplicationOperationEventKind.Executed)
                        .Entity.Kind = ApplicationOperationEventKind.Unknown;
                    break;
                case SettlementFault.Operation:
                    context.ChangeTracker.Entries<ApplicationOperationRecord>()
                        .Single(entry => entry.State == EntityState.Modified)
                        .Entity.Status = ApplicationOperationStatus.Unknown;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class ThrowingMutationHandler(ApplicationDbContext db)
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
            profile.Name = "Mutated";
            throw new ControlledHandlerException();
        }
    }

    private sealed class AppendingMutationHandler(ApplicationDbContext db)
        : IApplicationOperationHandler
    {
        internal int InvocationCount { get; private set; }

        internal List<string> ObservedNames { get; } = [];

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
            ObservedNames.Add(profile.Name);
            profile.Name += "-mutated";
            return new ApplicationOperationHandlerResult(
                new ApplicationStateVersions(3, 4),
                new ApplicationStateVersions(4, 5),
                ReceiptSchemaVersion: 1,
                ReceiptSource: System.Text.Encoding.UTF8.GetBytes($"receipt-attempt-{InvocationCount}"),
                ApplicationReversalAvailability.Unavailable,
                ReversalExpiresAtUtc: null,
                Continuation: null);
        }
    }

    private sealed class ControlledHandlerException : Exception;
}
