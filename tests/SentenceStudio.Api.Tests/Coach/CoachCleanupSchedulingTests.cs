using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.Cleanup;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The retention pass and the lease that stops replicas racing it.
/// </summary>
/// <remarks>
/// <c>CoachExpiryCleanupService</c> already existed but nothing ever called it, so expired
/// checkpoints accumulated indefinitely — a retention policy that was written down and never
/// enforced. These cover the scheduling half: exactly one runner at a time, a clean stop on
/// cancellation, a retry rather than a crash on failure, and no learner content in the logs.
/// </remarks>
public class CoachCleanupSchedulingTests
{
    [Fact]
    public async Task Runner_RunsThePassWhenItHoldsTheLease()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        await SeedExpiredSessionAsync(harness, db);

        var attempt = await NewRunner(harness, db, new AlwaysGrantsLease()).RunOnceAsync();

        attempt.Ran.Should().BeTrue();
        attempt.Result!.ExpiredSessionsDeleted.Should().Be(1);
    }

    [Fact]
    public async Task Runner_SkipsThePassWhenAnotherReplicaHoldsTheLease()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        await SeedExpiredSessionAsync(harness, db);

        var attempt = await NewRunner(harness, db, new NeverGrantsLease()).RunOnceAsync();

        attempt.Ran.Should().BeFalse();
        attempt.Result.Should().BeNull();
        (await db.CoachSessions.CountAsync()).Should().Be(1,
            "a replica that did not win the lease must delete nothing");
    }

    [Fact]
    public async Task InProcessLease_AdmitsOneHolderAtATime()
    {
        await using var host = NewSqliteHost();
        await using var firstScope = host.CreateAsyncScope();
        await using var secondScope = host.CreateAsyncScope();

        await using var first = await LeaseOf(firstScope).TryAcquireAsync();
        first.Should().NotBeNull();

        // Each scope resolves its own lease, as each pass does; the host's gate is what they share.
        var second = await LeaseOf(secondScope).TryAcquireAsync();
        second.Should().BeNull("two concurrent passes deadlock on overlapping batched deletes");
    }

    [Fact]
    public async Task InProcessLease_ReleasesOnDisposeSoTheNextPassCanRun()
    {
        await using var host = NewSqliteHost();
        await using var firstScope = host.CreateAsyncScope();
        await using var secondScope = host.CreateAsyncScope();

        var first = await LeaseOf(firstScope).TryAcquireAsync();
        first.Should().NotBeNull();
        await first!.DisposeAsync();

        await using var second = await LeaseOf(secondScope).TryAcquireAsync();
        second.Should().NotBeNull("a lease that is never released stops retention forever");
    }

    [Fact]
    public async Task InProcessLease_HeldInOneHostDoesNotRefuseAnotherHost()
    {
        // Issue 341: the gate was one static for the whole process, so a lease held by any host
        // in a test run refused every other host's, and the two tests above failed whenever one
        // was held at the moment they asked.
        await using var busyHost = NewSqliteHost();
        await using var busyScope = busyHost.CreateAsyncScope();
        await using var held = await LeaseOf(busyScope).TryAcquireAsync();
        held.Should().NotBeNull();

        await using var otherHost = NewSqliteHost();
        await using var otherScope = otherHost.CreateAsyncScope();
        await using var other = await LeaseOf(otherScope).TryAcquireAsync();

        other.Should().NotBeNull(
            "the in-process lease guards one host's passes; another host has its own database and its own gate");
    }

    [Fact]
    public async Task Runner_CompletesTheLeaseOnlyAfterAPassSucceeds()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        var lease = new AlwaysGrantsLease();
        await NewRunner(harness, db, lease).RunOnceAsync();

        lease.Handle!.Completed.Should().BeTrue();
        lease.Handle.Disposed.Should().BeTrue("the lease must always be released");
    }

    [Fact]
    public async Task Runner_ReleasesTheLeaseWithoutCommittingWhenThePassThrows()
    {
        using var harness = new CoachPersistenceHarness();
        var db = harness.NewContext();
        var lease = new AlwaysGrantsLease();
        var runner = NewRunner(harness, db, lease);

        // Disposing the context makes the pass throw the way a dropped connection would.
        db.Dispose();

        var run = async () => await runner.RunOnceAsync();

        await run.Should().ThrowAsync<Exception>();
        lease.Handle!.Completed.Should().BeFalse(
            "committing a failed pass would leave a partial delete behind and release the lock as if it worked");
        lease.Handle.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Runner_ObservesCancellation()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var run = async () => await NewRunner(harness, db, new AlwaysGrantsLease()).RunOnceAsync(cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExpiredSessionFilter_CanHoldBackRowsAnotherLaneStillNeeds()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        await SeedExpiredSessionAsync(harness, db);

        var cleanup = new CoachExpiryCleanupService(
            db,
            Microsoft.Extensions.Options.Options.Create(harness.Options),
            harness.Time,
            NullLogger<CoachExpiryCleanupService>.Instance,
            new PreservesEverythingFilter());

        var result = await cleanup.RunAsync();

        result.ExpiredSessionsDeleted.Should().Be(0);
        (await db.CoachSessions.CountAsync()).Should().Be(1,
            "checkpoint expiry and conversation retention are different clocks, and the hook is " +
            "what keeps a ledger-backed row from being deleted by the wrong one");
    }

    [Fact]
    public async Task ExpiredSessionFilter_DefaultsToDeletingEveryExpiredCheckpoint()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        await SeedExpiredSessionAsync(harness, db);

        // No filter supplied: the pre-history default applies.
        var result = await harness.NewCleanupService(db).RunAsync();

        result.ExpiredSessionsDeleted.Should().Be(1);
    }

    [Fact]
    public void CleanupOptions_DefaultToASafeSchedule()
    {
        var options = new CoachCleanupOptions();

        options.Enabled.Should().BeTrue();
        options.Interval.Should().BeGreaterThan(TimeSpan.Zero);
        options.RetryDelay.Should().BeLessThan(options.Interval,
            "a transient failure should not cost a whole cycle");
        options.InitialDelay.Should().BeGreaterThan(TimeSpan.Zero,
            "cleanup must not compete with migration and warm-up on a cold start");
    }

    [Fact]
    public async Task CleanupLogs_CarryNoLearnerContent()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();

        await SeedExpiredSessionAsync(harness, db);

        var log = new CapturingLoggerProvider();
        var cleanup = new CoachExpiryCleanupService(
            db,
            Microsoft.Extensions.Options.Options.Create(harness.Options),
            harness.Time,
            new CapturingLogger<CoachExpiryCleanupService>(log));

        await cleanup.RunAsync();

        log.Messages.Should().NotBeEmpty("the pass has to be observable");
        log.Messages.Should().NotContain(message => message.Contains(CoachPersistenceSamples.LearnerSentinel),
            "a retention log that quotes the conversation outlives the row it deleted");
        log.Messages.Should().NotContain(message => message.Contains(CoachPersistenceSamples.OwnerUserId),
            "counts are enough to diagnose retention; identifiers are not needed");
    }

    private static async Task SeedExpiredSessionAsync(CoachPersistenceHarness harness, CoachDbContext db)
    {
        await harness.NewSessionStore(db).CreateAsync(
            CoachPersistenceSamples.OwnerUserId, CoachPersistenceSamples.CreateRequest());

        harness.Time.Advance(TimeSpan.FromDays(31));
    }

    private static CoachCleanupRunner NewRunner(
        CoachPersistenceHarness harness, CoachDbContext db, ICoachCleanupLease lease) =>
        new(lease, harness.NewCleanupService(db), NullLogger<CoachCleanupRunner>.Instance);

    /// <summary>
    /// A host's services as <c>AddCoachCleanupScheduling</c> registers them, with the coach
    /// context on SQLite, so the lease is the in-process one and comes from the real registration.
    /// </summary>
    private static ServiceProvider NewSqliteHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CoachDbContext>(options => options.UseSqlite("Filename=:memory:"));
        services.AddCoachCleanupScheduling(new ConfigurationBuilder().Build(), new TestingEnvironment());

        return services.BuildServiceProvider();
    }

    private static ICoachCleanupLease LeaseOf(AsyncServiceScope scope)
    {
        var lease = scope.ServiceProvider.GetRequiredService<ICoachCleanupLease>();
        lease.Should().BeOfType<InProcessCoachCleanupLease>();
        return lease;
    }

    private sealed class TestingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "SentenceStudio.Api.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class AlwaysGrantsLease : ICoachCleanupLease
    {
        public FakeHandle? Handle { get; private set; }

        public Task<ICoachCleanupLeaseHandle?> TryAcquireAsync(CancellationToken cancellationToken = default)
        {
            Handle = new FakeHandle();
            return Task.FromResult<ICoachCleanupLeaseHandle?>(Handle);
        }
    }

    private sealed class NeverGrantsLease : ICoachCleanupLease
    {
        public Task<ICoachCleanupLeaseHandle?> TryAcquireAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ICoachCleanupLeaseHandle?>(null);
    }

    private sealed class FakeHandle : ICoachCleanupLeaseHandle
    {
        public bool Completed { get; private set; }

        public bool Disposed { get; private set; }

        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            Completed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PreservesEverythingFilter : ICoachExpiredSessionFilter
    {
        public Task<IReadOnlyList<CoachSession>> SelectDeletableAsync(
            IReadOnlyList<CoachSession> expiredCandidates,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CoachSession>>([]);
    }

    private sealed class CapturingLoggerProvider
    {
        public List<string> Messages { get; } = [];
    }

    private sealed class CapturingLogger<T>(CapturingLoggerProvider sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            sink.Messages.Add(formatter(state, exception));

            if (exception is not null)
            {
                // An exception passed to the logger is serialised in full by every sink, so it
                // counts as part of the emitted message for this assertion.
                sink.Messages.Add(exception.ToString());
            }
        }
    }
}
