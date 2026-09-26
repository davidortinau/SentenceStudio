using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using SentenceStudio.Application.AppOperations;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Data;
using SentenceStudio.Data.AppOperations;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.AppOperations;

internal sealed class ApplicationOperationSqliteHarness : IAsyncDisposable
{
    internal static readonly DateTime NowUtc =
        new(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _keeper;

    internal ApplicationOperationSqliteHarness()
    {
        ConnectionString =
            $"Data Source=application-operation-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
        _keeper = new SqliteConnection(ConnectionString);
        _keeper.Open();
        Protector = new DataProtectionApplicationOperationContentProtector(
            new EphemeralDataProtectionProvider());

        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    internal string ConnectionString { get; }

    internal IApplicationOperationContentProtector Protector { get; }

    internal ApplicationDbContext NewContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(ConnectionString)
            .AddInterceptors(interceptors)
            .Options;
        return new ApplicationDbContext(options);
    }

    internal ApplicationDbContext NewRetryingContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(
                ConnectionString,
                sqlite => sqlite.ExecutionStrategy(
                    dependencies => new ApplicationOperationTestRetryingStrategy(dependencies)))
            .AddInterceptors(interceptors)
            .Options;
        return new ApplicationDbContext(options);
    }

    internal EfApplicationOperationStore NewStore(ApplicationDbContext db) =>
        new(db, Protector);

    internal ApplicationOperationCoordinator NewCoordinator(ApplicationDbContext db) =>
        new(NewStore(db), Protector);

    internal async Task SeedOwnerAsync(string owner, string name = "Original")
    {
        await using var db = NewContext();
        db.UserProfiles.Add(new UserProfile
        {
            Id = owner,
            Name = name,
            Email = $"{owner}@test.invalid"
        });
        await db.SaveChangesAsync();
    }

    internal static ApplicationOperationScope Scope(
        string owner = "owner-a",
        ApplicationExecutionAuthority authority = ApplicationExecutionAuthority.Server) =>
        new(owner, authority);

    internal static ApplicationOperationProposalCommand Proposal(
        string operationId = "operation-a",
        string owner = "owner-a",
        ApplicationExecutionAuthority authority = ApplicationExecutionAuthority.Server,
        ApplicationConfirmationPolicy confirmation = ApplicationConfirmationPolicy.Accept,
        ReadOnlyMemory<byte>? idempotencyMaterial = null,
        long expectedDomainVersion = 0,
        long expectedSynchronizationVersion = 0,
        IReadOnlyList<ApplicationOperationContent>? contents = null,
        string capabilityCode = "resource.update",
        int capabilityVersion = 1,
        DateTime? createdAtUtc = null,
        DateTime? expiresAtUtc = null,
        DateTime? purgeAfterUtc = null) =>
        new(
            operationId,
            Scope(owner, authority),
            capabilityCode,
            "resource-management",
            capabilityVersion,
            $"sha256:{new string('a', 64)}",
            ApplicationEffectClass.Write,
            confirmation,
            ParentOperationId: null,
            ParentApplicationVersion: null,
            ParentFence: null,
            idempotencyMaterial,
            expectedDomainVersion,
            expectedSynchronizationVersion,
            createdAtUtc ?? NowUtc,
            expiresAtUtc ?? NowUtc.AddMinutes(10),
            purgeAfterUtc ?? NowUtc.AddDays(30),
            contents ??
            [
                new ApplicationOperationContent(
                    ApplicationProtectedContentKind.CanonicalRequest,
                    SchemaVersion: 1,
                    "canonical-request"u8.ToArray()),
                new ApplicationOperationContent(
                    ApplicationProtectedContentKind.ProposalPresentation,
                    SchemaVersion: 1,
                    "safe-preview"u8.ToArray())
            ]);

    internal static ApplicationOperationDecisionCommand Decision(
        string operationId = "operation-a",
        string owner = "owner-a",
        ApplicationExecutionAuthority authority = ApplicationExecutionAuthority.Server,
        ApplicationOperationDecision decision = ApplicationOperationDecision.Accept,
        string decisionReference = "decision-a",
        string? confirmationReference = null,
        ReadOnlyMemory<byte>? confirmationMaterial = null,
        string? leaseId = "lease-a",
        DateTime? decidedAtUtc = null,
        DateTime? leaseExpiresAtUtc = null) =>
        new(
            operationId,
            Scope(owner, authority),
            decision,
            System.Text.Encoding.UTF8.GetBytes(decisionReference),
            confirmationReference,
            confirmationMaterial,
            leaseId,
            leaseExpiresAtUtc ?? NowUtc.AddMinutes(2),
            decidedAtUtc ?? NowUtc.AddSeconds(1));

    internal async Task<ApplicationOperationSnapshot> CreateExecutingAsync(
        ApplicationDbContext db,
        string operationId = "operation-a",
        string owner = "owner-a",
        long expectedDomainVersion = 0,
        long expectedSynchronizationVersion = 0)
    {
        var coordinator = NewCoordinator(db);
        await coordinator.ProposeAsync(Proposal(
            operationId,
            owner,
            expectedDomainVersion: expectedDomainVersion,
            expectedSynchronizationVersion: expectedSynchronizationVersion));
        return (await coordinator.DecideAsync(Decision(operationId, owner))).Operation;
    }

    internal static ApplicationOperationExecutionRequest Execution(
        ApplicationOperationSnapshot operation,
        string? leaseId = null,
        DateTime? executedAtUtc = null) =>
        new(
            operation.OperationId,
            operation.Scope,
            operation.Version,
            leaseId ?? operation.LeaseId ?? "lease-a",
            executedAtUtc ?? NowUtc.AddSeconds(2));

    public async ValueTask DisposeAsync()
    {
        await _keeper.DisposeAsync();
    }

    private sealed class ApplicationOperationTestRetryingStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(
            dependencies,
            maxRetryCount: 1,
            maxRetryDelay: TimeSpan.FromMilliseconds(1))
    {
        protected override bool ShouldRetryOn(Exception exception) =>
            exception is ApplicationOperationTransientTestException;
    }
}

internal sealed class ApplicationOperationTransientTestException : Exception;

internal sealed class RetryOnceSaveChangesInterceptor : SaveChangesInterceptor
{
    private int _armed;
    private int _attemptCount;
    private int _failureCount;

    internal int AttemptCount => Volatile.Read(ref _attemptCount);

    internal int FailureCount => Volatile.Read(ref _failureCount);

    internal void Arm() => Interlocked.Exchange(ref _armed, 1);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _attemptCount);
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            Interlocked.Increment(ref _failureCount);
            throw new ApplicationOperationTransientTestException();
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

internal sealed class RetryOnceCommittedTransactionInterceptor : DbTransactionInterceptor
{
    private int _armed;
    private int _failureCount;

    internal int FailureCount => Volatile.Read(ref _failureCount);

    internal void Arm() => Interlocked.Exchange(ref _armed, 1);

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
        throw new ApplicationOperationTransientTestException();
    }
}

internal sealed class ProfileMutationHandler(
    ApplicationDbContext db,
    string owner,
    string capabilityCode = "resource.update",
    int capabilityVersion = 1,
    ApplicationStateVersions? beforeVersion = null,
    ApplicationStateVersions? afterVersion = null,
    ApplicationOperationContinuationWrite? continuation = null)
    : IApplicationOperationHandler
{
    internal int InvocationCount { get; private set; }

    public string CapabilityCode { get; } = capabilityCode;

    public int CapabilityVersion { get; } = capabilityVersion;

    public async Task<ApplicationOperationHandlerResult> ExecuteAsync(
        ApplicationOperationHandlerContext context,
        CancellationToken cancellationToken)
    {
        InvocationCount++;
        var profile = await db.UserProfiles.SingleAsync(
            item => item.Id == owner,
            cancellationToken);
        profile.Name = "Mutated";

        return new ApplicationOperationHandlerResult(
            beforeVersion ?? new ApplicationStateVersions(3, 4),
            afterVersion ?? new ApplicationStateVersions(4, 5),
            ReceiptSchemaVersion: 1,
            ReceiptSource: "durable-receipt"u8.ToArray(),
            ApplicationReversalAvailability.Unavailable,
            ReversalExpiresAtUtc: null,
            continuation);
    }
}

internal static class ByteAssertions
{
    internal static bool ContainsBytes(this byte[] value, ReadOnlySpan<byte> expected)
    {
        if (expected.IsEmpty || value.Length < expected.Length)
        {
            return false;
        }

        for (var index = 0; index <= value.Length - expected.Length; index++)
        {
            if (value.AsSpan(index, expected.Length).SequenceEqual(expected))
            {
                return true;
            }
        }

        return false;
    }

    internal static byte[] Digest(string value) =>
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
}
