using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentenceStudio.Contracts.Startup;
using SentenceStudio.Services.Vocabulary;
using SentenceStudio.Workers;
using SentenceStudio.Workers.Startup;

namespace SentenceStudio.UnitTests.Workers;

public sealed class WorkerStartupHostingTests
{
    [Fact]
    public async Task Hosted_jobs_do_not_start_until_API_and_schema_are_compatible()
    {
        var receivedRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApi = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new WorkerApiReadinessGateTests.StubHandler(
            (_, cancellationToken) =>
            {
                receivedRequest.TrySetResult();
                return releaseApi.Task.WaitAsync(cancellationToken);
            });
        var metadata = new FakeMetadata();
        var job = new TrackingHostedService();
        using var host = BuildHost(handler, metadata, job);

        var starting = host.StartAsync();
        await receivedRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(starting.IsCompleted);
        Assert.False(job.HasStarted);
        Assert.Equal(0, metadata.Queries);

        releaseApi.SetResult(WorkerApiReadinessGateTests.ReadyResponse());
        await starting.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(job.HasStarted);
        Assert.Equal(1, metadata.Queries);
        Assert.Equal(1, metadata.IdentityQueries);
        await host.StopAsync();
    }

    [Fact]
    public async Task Incompatible_API_fails_host_startup_without_starting_the_job()
    {
        using var handler = new WorkerApiReadinessGateTests.StubHandler(
            (_, _) => Task.FromResult(WorkerApiReadinessGateTests.ReadyResponse("old-contract")));
        var metadata = new FakeMetadata();
        var job = new TrackingHostedService();
        using var host = BuildHost(handler, metadata, job);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("startup contract", failure.Message);
        Assert.False(job.HasStarted);
        Assert.Equal(0, metadata.Queries);
        Assert.Equal(0, metadata.IdentityQueries);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Different_postgres_cluster_with_the_same_database_name_blocks_all_jobs()
    {
        using var handler = new WorkerApiReadinessGateTests.StubHandler(
            (_, _) => Task.FromResult(WorkerApiReadinessGateTests.ReadyResponse()));
        var metadata = new FakeMetadata
        {
            DatabaseIdentity = PostgresDatabaseIdentity.ComputeFromAttestedValues(
                "sentencestudio", 16384, "public", 200002,
                WorkerApiReadinessGateTests.ExpectedMigrationId,
                WorkerApiReadinessGateTests.ExpectedMigrationId)
        };
        var job = new TrackingHostedService();
        using var host = BuildHost(handler, metadata, job);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("database identity does not match", failure.Message);
        Assert.False(job.HasStarted);
        Assert.Equal(1, metadata.IdentityQueries);
        Assert.Equal(1, metadata.Queries);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Host_shutdown_cancels_the_startup_barrier()
    {
        var receivedRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new WorkerApiReadinessGateTests.StubHandler(
            async (_, cancellationToken) =>
            {
                receivedRequest.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        var job = new TrackingHostedService();
        using var host = BuildHost(handler, new FakeMetadata(), job);

        var starting = host.StartAsync();
        await receivedRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.False(job.HasStarted);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Disabled_or_unscoped_backfill_does_not_wait_or_resolve_database_services(
        bool enabled, bool includeUser)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var startup = new SuspendedReadiness();
        var options = new VocabularyPartOfSpeechBackfillOptions { Enabled = enabled };
        if (includeUser)
        {
            options.UserProfileIds.Add("test-profile");
        }
        using var worker = new PartOfSpeechBackfillWorker(
            services, Options.Create(options), NullLogger<PartOfSpeechBackfillWorker>.Instance, startup);

        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(() => worker.HasCompleted);

        Assert.Equal(0, startup.Waits);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Enabled_backfill_waits_for_readiness_before_resolving_database_services()
    {
        var noDatabase = new FailOnServiceResolution();
        var startup = new SuspendedReadiness();
        var options = new VocabularyPartOfSpeechBackfillOptions
        {
            Enabled = true,
            UserProfileIds = ["test-profile"]
        };
        using var worker = new PartOfSpeechBackfillWorker(
            noDatabase, Options.Create(options), NullLogger<PartOfSpeechBackfillWorker>.Instance, startup);

        await worker.StartAsync(CancellationToken.None);
        await startup.Entered.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(worker.HasCompleted);
        Assert.Equal(1, startup.Waits);
        Assert.Equal(0, noDatabase.Calls);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Channel_monitor_waits_for_readiness_before_touching_database_services()
    {
        var noDatabase = new FailOnServiceResolution();
        var startup = new SuspendedReadiness();
        using var worker = new Worker(
            NullLogger<Worker>.Instance, noDatabase, startup);

        await worker.StartAsync(CancellationToken.None);
        await startup.Entered.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, startup.Waits);
        Assert.Equal(0, noDatabase.Calls);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Worker_program_registers_the_barrier_before_both_database_jobs()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);
        var workers = Path.Combine(root.FullName, "src", "SentenceStudio.Workers");
        var program = File.ReadAllText(Path.Combine(workers, "Program.cs"));
        var barrier = program.IndexOf("AddHostedService<WorkerStartupReadiness>", StringComparison.Ordinal);
        var channelMonitor = program.IndexOf("AddHostedService<Worker>()", StringComparison.Ordinal);
        var backfill = program.IndexOf("AddHostedService<PartOfSpeechBackfillWorker>()", StringComparison.Ordinal);

        Assert.True(barrier >= 0 && barrier < channelMonitor && channelMonitor < backfill);
        Assert.Contains("options.ServicesStartConcurrently = false", program);
        Assert.Contains("await _startupReadiness.WaitAsync(stoppingToken)",
            File.ReadAllText(Path.Combine(workers, "Worker.cs")));
        Assert.Contains("await _startupReadiness.WaitAsync(stoppingToken)",
            File.ReadAllText(Path.Combine(workers, "PartOfSpeechBackfillWorker.cs")));
    }

    private static IHost BuildHost(
        HttpMessageHandler handler, FakeMetadata metadata, TrackingHostedService job)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "SentenceStudio.Workers.Tests"
        });
        builder.Services.AddLogging();
        builder.Services.AddHttpClient(
                WorkerApiReadinessGate.ClientName,
                client => client.BaseAddress = new Uri("https://api.example.invalid/"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        builder.Services.AddSingleton<IWorkerMigrationMetadata>(metadata);
        builder.Services.AddSingleton<WorkerStartupReadiness>();
        builder.Services.AddSingleton<IWorkerStartupReadiness>(
            sp => sp.GetRequiredService<WorkerStartupReadiness>());
        builder.Services.AddHostedService<WorkerStartupReadiness>(
            sp => sp.GetRequiredService<WorkerStartupReadiness>());
        builder.Services.AddSingleton(job);
        builder.Services.AddHostedService<TrackingHostedService>(
            sp => sp.GetRequiredService<TrackingHostedService>());
        return builder.Build();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeMetadata : IWorkerMigrationMetadata
    {
        public int Queries { get; private set; }
        public int IdentityQueries { get; private set; }
        public string DatabaseIdentity { get; set; } =
            WorkerApiReadinessGateTests.MatchingDatabaseIdentity;

        public string GetExpectedApplicationMigrationId() =>
            WorkerApiReadinessGateTests.ExpectedMigrationId;

        public Task<PostgresDatabaseReadiness> GetDatabaseReadinessAsync(
            string expectedMigrationId, CancellationToken cancellationToken)
        {
            Assert.Equal(GetExpectedApplicationMigrationId(), expectedMigrationId);
            IdentityQueries++;
            Queries++;
            return Task.FromResult(new PostgresDatabaseReadiness(DatabaseIdentity, 0));
        }
    }

    private sealed class TrackingHostedService : IHostedService
    {
        public bool HasStarted { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            HasStarted = true;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SuspendedReadiness : IWorkerStartupReadiness
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Waits { get; private set; }
        public Task Entered => _entered.Task;

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            Waits++;
            _entered.TrySetResult();
            return _release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FailOnServiceResolution : IServiceProvider
    {
        public int Calls { get; private set; }

        public object? GetService(Type serviceType)
        {
            Calls++;
            throw new InvalidOperationException("Database services cannot resolve before startup readiness.");
        }
    }
}
