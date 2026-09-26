using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.UnitTests.AppOperations;

public sealed class ApplicationOperationBoundaryTests
{
    [Fact]
    public async Task CoordinatorRejectsUnknownDecisionBeforeStoreQuery()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        var counter = new ReaderCounter();
        await using var db = harness.NewContext(counter);
        var coordinator = harness.NewCoordinator(db);
        counter.Reset();
        var command = ApplicationOperationSqliteHarness.Decision(
            decision: ApplicationOperationDecision.Unknown);

        var act = async () => await coordinator.DecideAsync(command);

        await act.Should().ThrowAsync<ApplicationOperationValidationException>()
            .WithMessage("*known operation decision*");
        counter.Count.Should().Be(0);
    }

    [Fact]
    public async Task EmptyOwnerAndInvalidAuthorityAreRejectedBeforeDatabaseQuery()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        var counter = new ReaderCounter();
        await using var db = harness.NewContext(counter);
        var store = harness.NewStore(db);
        counter.Reset();

        var empty = async () => await store.FindAsync(
            new ApplicationOperationScope("", ApplicationExecutionAuthority.Server),
            "operation-a",
            default);
        var invalidAuthority = async () => await store.FindAsync(
            new ApplicationOperationScope("owner-a", ApplicationExecutionAuthority.Client),
            "operation-a",
            default);

        await empty.Should().ThrowAsync<ApplicationOperationValidationException>();
        await invalidAuthority.Should().ThrowAsync<ApplicationOperationValidationException>();
        counter.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("owner-b", ApplicationExecutionAuthority.Server)]
    [InlineData("owner-a", ApplicationExecutionAuthority.NativeLocal)]
    public async Task ForeignOwnerOrAuthorityCannotExecuteQueryDomainOrDisclosePayload(
        string owner,
        ApplicationExecutionAuthority authority)
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await harness.SeedOwnerAsync("owner-a");
        await using (var seed = harness.NewContext())
        {
            await harness.CreateExecutingAsync(seed);
        }

        await using var db = harness.NewContext();
        var handler = new ProfileMutationHandler(db, "owner-a");
        var request = new ApplicationOperationExecutionRequest(
            "operation-a",
            new ApplicationOperationScope(owner, authority),
            new ApplicationOperationVersion(2, 0),
            "lease-a",
            ApplicationOperationSqliteHarness.NowUtc.AddSeconds(2));

        var act = async () => await harness.NewStore(db).ExecuteAsync(request, handler, default);

        await act.Should().ThrowAsync<ApplicationOperationConflictException>();
        handler.InvocationCount.Should().Be(0);
        (await db.UserProfiles.AsNoTracking().SingleAsync(item => item.Id == "owner-a"))
            .Name.Should().Be("Original");
        (await db.ApplicationOperationReceipts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DatabaseConstraintsMakeOwnerAndAuthorityImmutableToInvalidValues()
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        await harness.NewCoordinator(db).ProposeAsync(
            ApplicationOperationSqliteHarness.Proposal());

        var authorityAct = async () => await db.Database.ExecuteSqlRawAsync(
            """UPDATE "ApplicationOperation" SET "Authority" = 'Client' WHERE "Id" = 'operation-a';""");
        await authorityAct.Should().ThrowAsync<Exception>();

        var ownerAct = async () => await db.Database.ExecuteSqlRawAsync(
            """UPDATE "ApplicationOperation" SET "UserProfileId" = NULL WHERE "Id" = 'operation-a';""");
        await ownerAct.Should().ThrowAsync<Exception>();

        var operation = await db.ApplicationOperations.AsNoTracking().SingleAsync();
        operation.UserProfileId.Should().Be("owner-a");
        operation.Authority.Should().Be(ApplicationExecutionAuthority.Server);
    }

    [Theory]
    [InlineData(ApplicationEffectClass.Unknown, ApplicationConfirmationPolicy.Accept)]
    [InlineData(ApplicationEffectClass.Read, ApplicationConfirmationPolicy.Accept)]
    [InlineData(ApplicationEffectClass.ExternalEffect, ApplicationConfirmationPolicy.Accept)]
    [InlineData(ApplicationEffectClass.Write, ApplicationConfirmationPolicy.Unknown)]
    [InlineData(ApplicationEffectClass.Write, ApplicationConfirmationPolicy.None)]
    [InlineData((ApplicationEffectClass)999, ApplicationConfirmationPolicy.Accept)]
    [InlineData(ApplicationEffectClass.Write, (ApplicationConfirmationPolicy)999)]
    public async Task ProposalRejectsUnknownOrNonLedgerEffectAndConfirmation(
        ApplicationEffectClass effect,
        ApplicationConfirmationPolicy confirmation)
    {
        await using var harness = new ApplicationOperationSqliteHarness();
        await using var db = harness.NewContext();
        var proposal = ApplicationOperationSqliteHarness.Proposal() with
        {
            Effect = effect,
            Confirmation = confirmation
        };

        var act = async () => await harness.NewCoordinator(db).ProposeAsync(proposal);

        await act.Should().ThrowAsync<ApplicationOperationValidationException>();
        (await db.ApplicationOperations.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(ApplicationOperationContentSubjectKind.Unknown, ApplicationProtectedContentKind.CanonicalRequest)]
    [InlineData(ApplicationOperationContentSubjectKind.Operation, ApplicationProtectedContentKind.Unknown)]
    [InlineData((ApplicationOperationContentSubjectKind)999, ApplicationProtectedContentKind.CanonicalRequest)]
    [InlineData(ApplicationOperationContentSubjectKind.Operation, (ApplicationProtectedContentKind)999)]
    public void ProtectionContextRejectsUnknownEnumValues(
        ApplicationOperationContentSubjectKind subjectKind,
        ApplicationProtectedContentKind contentKind)
    {
        var context = new ApplicationOperationProtectionContext(
            ApplicationOperationSqliteHarness.Scope(),
            subjectKind,
            "operation-a",
            contentKind,
            "resource.update",
            1);

        var act = context.Validate;

        act.Should().Throw<ApplicationOperationValidationException>();
    }

    private sealed class ReaderCounter : DbCommandInterceptor
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
    }
}
