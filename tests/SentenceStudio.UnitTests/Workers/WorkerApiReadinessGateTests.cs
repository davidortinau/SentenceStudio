using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Contracts.Startup;
using SentenceStudio.Workers.Startup;

namespace SentenceStudio.UnitTests.Workers;

public sealed class WorkerApiReadinessGateTests
{
    internal const string ExpectedMigrationId = "test_latest_application_migration";
    internal static string MatchingDatabaseIdentity { get; } =
        PostgresDatabaseIdentity.ComputeFromAttestedValues(
            "sentencestudio", 16384, "public", 100001, ExpectedMigrationId, ExpectedMigrationId);

    private static Task<string> ReadLocalIdentity(CancellationToken cancellationToken) =>
        Task.FromResult(MatchingDatabaseIdentity);

    [Fact]
    public async Task Waits_for_delayed_API_and_then_for_local_migration_history()
    {
        var observations = new List<string>();
        using var handler = new StubHandler((attempt, _) =>
        {
            observations.Add("api");
            return attempt switch
            {
                1 => Task.FromException<HttpResponseMessage>(new HttpRequestException("API not listening")),
                2 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                _ => Task.FromResult(ReadyResponse())
            };
        });
        using var api = Client(handler);
        var pending = new Queue<int>([2, 0]);

        await WaitForReadinessAsync(
            api,
            _ =>
            {
                observations.Add("schema");
                return Task.FromResult(pending.Dequeue());
            },
            _ =>
            {
                observations.Add("identity");
                return ReadLocalIdentity(_);
            },
            ExpectedMigrationId,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(1));

        Assert.Equal(new[] { "api", "api", "api", "identity", "schema", "api", "identity", "schema" }, observations);
        Assert.Equal(4, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Transient_HTTP_status_retries_before_querying_migrations(HttpStatusCode status)
    {
        using var handler = new StubHandler((attempt, _) => Task.FromResult(
            attempt == 1 ? new HttpResponseMessage(status) : ReadyResponse()));
        using var api = Client(handler);
        var queries = 0;

        await WaitForReadinessAsync(
            api, _ => Task.FromResult(++queries - 1), ReadLocalIdentity, ExpectedMigrationId,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1));

        Assert.Equal(2, handler.Requests);
        Assert.Equal(1, queries);
    }

    [Fact]
    public async Task Transient_database_failure_retries_without_claiming_readiness()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ReadyResponse()));
        using var api = Client(handler);
        var queries = 0;

        await WaitForReadinessAsync(
            api,
            _ => ++queries == 1
                ? Task.FromException<int>(new TemporaryDatabaseException())
                : Task.FromResult(0),
            ReadLocalIdentity, ExpectedMigrationId, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1));

        Assert.Equal(2, handler.Requests);
        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task Unavailable_migration_history_exhausts_the_deadline_without_starting()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ReadyResponse()));
        using var api = Client(handler);
        var queries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                api,
                _ =>
                {
                    queries++;
                    return Task.FromException<int>(new TemporaryDatabaseException());
                },
                ReadLocalIdentity, ExpectedMigrationId,
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(10)));

        Assert.Contains("PostgreSQL database identity or migration history unavailable", failure.Message);
        Assert.True(queries > 1);
    }

    [Fact]
    public async Task Unreachable_API_exhausts_the_deadline_without_querying_the_database()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("API unavailable")));
        using var api = Client(handler);
        var queries = 0;
        var identityQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                api, _ => Task.FromResult(++queries),
                _ => { identityQueries++; return ReadLocalIdentity(_); },
                ExpectedMigrationId,
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(10)));

        Assert.Contains("API migration owner", failure.Message);
        Assert.Contains("API connection unavailable", failure.Message);
        Assert.True(handler.Requests > 1);
        Assert.Equal(0, queries);
        Assert.Equal(0, identityQueries);
    }

    [Fact]
    public async Task Pending_migrations_never_let_a_healthy_API_release_the_gate()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ReadyResponse()));
        using var api = Client(handler);
        var queries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                api,
                _ =>
                {
                    queries++;
                    return Task.FromResult(1);
                },
                ReadLocalIdentity, ExpectedMigrationId,
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(10)));

        Assert.Contains("1 application migration(s) still pending", failure.Message);
        Assert.True(queries > 1);
    }

    [Fact]
    public async Task External_shutdown_cancels_an_in_flight_API_request()
    {
        using var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ReadyResponse();
        });
        using var api = Client(handler);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var queries = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WaitForReadinessAsync(
                api, _ => Task.FromResult(++queries), ReadLocalIdentity, ExpectedMigrationId,
                TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1), shutdown.Token));

        Assert.Equal(0, queries);
    }

    [Fact]
    public async Task External_shutdown_cancels_retry_backoff_without_waiting_for_the_deadline()
    {
        var receivedRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler((_, _) =>
        {
            receivedRequest.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using var api = Client(handler);
        using var shutdown = new CancellationTokenSource();

        var waiting = WaitForReadinessAsync(
            api, _ => Task.FromResult(0), ReadLocalIdentity, ExpectedMigrationId,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), shutdown.Token);
        await receivedRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        shutdown.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Unauthorized_or_wrong_endpoint_is_a_terminal_failure(HttpStatusCode status)
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(status)));
        using var api = Client(handler);
        var queries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                api, _ => Task.FromResult(++queries), ReadLocalIdentity, ExpectedMigrationId,
                TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1)));

        Assert.Contains($"HTTP {(int)status}", failure.Message);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(0, queries);
    }

    [Theory]
    [InlineData("<html>Sign in</html>", "text/html", "did not return JSON")]
    [InlineData("{", "application/json", "invalid JSON")]
    [InlineData("{}", "application/json", "invalid version response")]
    [InlineData("{\"version\":\"1.0\"}", "application/json", "invalid applicationStartupContract")]
    [InlineData("{\"version\":\"1.0\",\"applicationStartupContract\":42}",
        "application/json", "invalid applicationStartupContract")]
    public async Task Invalid_API_response_is_a_terminal_failure(
        string body, string contentType, string observation)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            }));
        using var api = Client(handler);
        var queries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                api, _ => Task.FromResult(++queries), ReadLocalIdentity, ExpectedMigrationId,
                TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1)));

        Assert.Contains(observation, failure.Message);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(0, queries);
    }

    [Theory]
    [InlineData("api-exclusive-application-startup-v2", ExpectedMigrationId, "startup contract")]
    [InlineData("outdated-contract", ExpectedMigrationId, "startup contract")]
    [InlineData(ApiApplicationStartupContract.Current, "older_migration", "migration identity")]
    public async Task Mismatched_contract_or_migration_is_a_terminal_failure(
        string contract, string migrationId, string observation)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ReadyResponse(contract, migrationId)));
        using var api = Client(handler);
        var queries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                api, _ => Task.FromResult(++queries), ReadLocalIdentity, ExpectedMigrationId,
                TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1)));

        Assert.Contains(observation, failure.Message);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(0, queries);
    }

    [Fact]
    public async Task Missing_migration_identity_cannot_release_the_gate()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            JsonSerializer.Serialize(new
            {
                version = "1.0",
                applicationStartupContract = ApiApplicationStartupContract.Current
            }))));
        using var api = Client(handler);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                api, _ => Task.FromResult(0), ReadLocalIdentity, ExpectedMigrationId,
                TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1)));

        Assert.Contains("invalid applicationMigrationId", failure.Message);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Missing_API_database_identity_cannot_release_the_gate_or_query_the_local_database()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            JsonSerializer.Serialize(new
            {
                version = "1.0",
                applicationStartupContract = ApiApplicationStartupContract.Current,
                applicationMigrationId = ExpectedMigrationId
            }))));
        using var api = Client(handler);
        var identityQueries = 0;
        var historyQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                api, _ => Task.FromResult(++historyQueries),
                _ => { identityQueries++; return ReadLocalIdentity(_); },
                ExpectedMigrationId, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1)));

        Assert.Contains("invalid databaseIdentity", failure.Message);
        Assert.Equal(0, identityQueries);
        Assert.Equal(0, historyQueries);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("not-a-fingerprint")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Noncanonical_API_database_identity_is_not_accepted(string databaseIdentity)
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(ReadyResponse(databaseIdentity: databaseIdentity)));
        using var api = Client(handler);
        var localQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                api, _ => Task.FromResult(0),
                _ => { localQueries++; return ReadLocalIdentity(_); },
                ExpectedMigrationId, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1)));

        Assert.Contains("invalid databaseIdentity", failure.Message);
        Assert.Equal(0, localQueries);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Identically_named_databases_on_distinct_clusters_cannot_release_workers()
    {
        const string databaseName = "sentencestudio";
        var apiIdentity = PostgresDatabaseIdentity.ComputeFromAttestedValues(
            databaseName, 16384, "public", 100001, ExpectedMigrationId, ExpectedMigrationId);
        var workerIdentity = PostgresDatabaseIdentity.ComputeFromAttestedValues(
            databaseName, 16384, "public", 200002, ExpectedMigrationId, ExpectedMigrationId);
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(ReadyResponse(databaseIdentity: apiIdentity)));
        using var api = Client(handler);
        var historyQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                api, _ => Task.FromResult(++historyQueries),
                _ => Task.FromResult(workerIdentity),
                ExpectedMigrationId, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1)));

        Assert.Contains("database identity does not match", failure.Message);
        Assert.DoesNotContain(databaseName, failure.Message);
        Assert.DoesNotContain("100001", failure.Message);
        Assert.Equal(1, historyQueries);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Missing_local_schema_marker_never_releases_workers()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ReadyResponse()));
        using var api = Client(handler);
        var historyQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                api, _ => Task.FromResult(++historyQueries),
                _ => Task.FromException<string>(new PostgresDatabaseIdentityException(
                    PostgresDatabaseIdentityFailure.MissingSchemaMarker)),
                ExpectedMigrationId, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(10)));

        Assert.Contains("database identity unavailable (MissingSchemaMarker)", failure.Message);
        Assert.Equal(0, historyQueries);
        Assert.True(handler.Requests > 1);
    }

    [Fact]
    public async Task Lost_local_backend_cannot_release_workers_even_if_the_API_hash_and_history_match()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ReadyResponse()));
        using var api = Client(handler);
        var localAttempts = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkerApiReadinessGate.WaitAsync(
                api,
                _ =>
                {
                    localAttempts++;
                    return Task.FromException<PostgresDatabaseReadiness>(
                        new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.BackendChanged));
                },
                ExpectedMigrationId, TimeSpan.FromMilliseconds(180), TimeSpan.FromMilliseconds(10)));

        Assert.Contains("database identity unavailable (BackendChanged)", failure.Message);
        Assert.True(localAttempts > 1);
        Assert.Equal(localAttempts, handler.Requests);
    }

    [Fact]
    public async Task Pascal_cased_API_contract_fields_are_accepted()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            JsonSerializer.Serialize(new
            {
                Version = "1.0",
                ApplicationStartupContract = ApiApplicationStartupContract.Current,
                ApplicationMigrationId = ExpectedMigrationId,
                DatabaseIdentity = MatchingDatabaseIdentity
            }))));
        using var api = Client(handler);

        await WaitForReadinessAsync(
            api, _ => Task.FromResult(0), ReadLocalIdentity, ExpectedMigrationId,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1));

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void Named_client_has_its_own_timeout_and_cannot_follow_redirects_or_send_cookies()
    {
        var services = new ServiceCollection();
        WorkerApiReadinessGate.RegisterClient(services, new Uri("https+http://api"));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(WorkerApiReadinessGate.ClientName);

        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
        Assert.Equal("https+http://api/", client.BaseAddress?.ToString());

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(WorkerApiReadinessGate.ClientName);
        while (handler is DelegatingHandler delegating && delegating.InnerHandler is { } inner)
        {
            handler = inner;
        }
        var primary = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(primary.AllowAutoRedirect);
        Assert.False(primary.UseCookies);
    }

    [Fact]
    public async Task Unexpected_schema_query_error_is_terminal_not_success()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(ReadyResponse()));
        using var api = Client(handler);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                api, _ => throw new InvalidOperationException("migration model mismatch"),
                ReadLocalIdentity, ExpectedMigrationId, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1)));

        Assert.Equal("migration model mismatch", failure.Message);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(500, 1000)]
    [InlineData(4000, 5000)]
    [InlineData(5000, 5000)]
    public void Retry_delay_doubles_with_a_five_second_cap(int previousMilliseconds, int nextMilliseconds)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(nextMilliseconds),
            WorkerApiReadinessGate.NextRetryDelay(TimeSpan.FromMilliseconds(previousMilliseconds)));
    }

    private static Task WaitForReadinessAsync(
        HttpClient api,
        Func<CancellationToken, Task<int>> getPendingMigrationCount,
        Func<CancellationToken, Task<string>> getLocalDatabaseIdentity,
        string expectedMigrationId,
        TimeSpan? timeout = null,
        TimeSpan? retryDelay = null,
        CancellationToken cancellationToken = default) =>
        WorkerApiReadinessGate.WaitAsync(
            api,
            async ct => new PostgresDatabaseReadiness(
                await getLocalDatabaseIdentity(ct), await getPendingMigrationCount(ct)),
            expectedMigrationId, timeout, retryDelay, cancellationToken);

    internal static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.example.invalid/") };

    internal static HttpResponseMessage ReadyResponse(
        string contract = ApiApplicationStartupContract.Current,
        string migrationId = ExpectedMigrationId,
        string? databaseIdentity = null) =>
        JsonResponse(JsonSerializer.Serialize(new
        {
            version = "1.0",
            applicationStartupContract = contract,
            applicationMigrationId = migrationId,
            databaseIdentity = databaseIdentity ?? MatchingDatabaseIdentity
        }));

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    internal sealed class StubHandler(
        Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/version/info", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            return respond(++Requests, cancellationToken);
        }
    }

    private sealed class TemporaryDatabaseException : DbException { }
}
