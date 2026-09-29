using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Contracts.Startup;
using SentenceStudio.WebApp.Tests.Infrastructure;
using SentenceStudio.WebApp.Startup;

namespace SentenceStudio.WebApp.Tests.Startup;

public sealed class ApiMigrationReadinessGateTests
{
    private const string ExpectedMigrationId = "test_latest_application_migration";
    private static string MatchingDatabaseIdentity { get; } =
        PostgresDatabaseIdentity.ComputeFromAttestedValues(
            "sentencestudio", 16384, "public", 100001, ExpectedMigrationId, ExpectedMigrationId);

    private static Task<string> ReadLocalIdentity(CancellationToken cancellationToken) =>
        Task.FromResult(MatchingDatabaseIdentity);

    [Fact]
    public async Task Waits_for_API_and_then_for_the_application_migration_history()
    {
        var observations = new List<string>();
        using var handler = new StubHandler(attempt =>
        {
            observations.Add("api");
            return attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : ReadyResponse();
        });
        using var client = Client(handler);
        var pending = new Queue<int>([2, 0]);

        await WaitForReadinessAsync(
            client,
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

        observations.Should().Equal("api", "api", "identity", "schema", "api", "identity", "schema");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Rejects_a_nonready_API_without_querying_the_database(HttpStatusCode status)
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(status));
        using var client = Client(handler);
        var schemaQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                client,
                _ =>
                {
                    schemaQueries++;
                    return Task.FromResult(0);
                },
                ReadLocalIdentity,
                ExpectedMigrationId,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(1)));

        failure.Message.Should().Contain($"HTTP {(int)status}");
        schemaQueries.Should().Be(0);
        handler.Requests.Should().Be(1);
    }

    [Fact]
    public async Task Rejects_an_HTML_login_page_even_if_it_returns_HTTP_200()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>Sign in</html>", Encoding.UTF8, "text/html")
        });
        using var client = Client(handler);
        var schemaQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                client,
                _ =>
                {
                    schemaQueries++;
                    return Task.FromResult(0);
                },
                ReadLocalIdentity,
                ExpectedMigrationId,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(1)));

        failure.Message.Should().Contain("did not return JSON");
        schemaQueries.Should().Be(0);
    }

    [Theory]
    [InlineData("{}", "invalid version response")]
    [InlineData("{\"version\":\"\"}", "invalid version")]
    public async Task Rejects_JSON_that_is_not_a_version_response(string payload, string expectedFailure)
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        });
        using var client = Client(handler);
        var schemaQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                client,
                _ =>
                {
                    schemaQueries++;
                    return Task.FromResult(0);
                },
                ReadLocalIdentity,
                ExpectedMigrationId,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(1)));

        failure.Message.Should().Contain(expectedFailure);
        schemaQueries.Should().Be(0);
    }

    [Fact]
    public async Task An_unreachable_API_cannot_be_treated_as_a_successful_startup()
    {
        using var handler = new StubHandler(_ => throw new HttpRequestException("simulated network failure"));
        using var client = Client(handler);
        var schemaQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                client,
                _ =>
                {
                    schemaQueries++;
                    return Task.FromResult(0);
                },
                ReadLocalIdentity,
                ExpectedMigrationId,
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(10)));

        failure.Message.Should().Contain("API migration owner")
            .And.Contain("API connection unavailable");
        schemaQueries.Should().Be(0);
        handler.Requests.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task A_healthy_older_API_does_not_allow_WebApp_to_start_with_pending_migrations()
    {
        using var handler = new StubHandler(_ => ReadyResponse());
        using var client = Client(handler);
        var schemaQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                client,
                _ =>
                {
                    schemaQueries++;
                    return Task.FromResult(1);
                },
                ReadLocalIdentity,
                ExpectedMigrationId,
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(10)));

        failure.Message.Should().Contain("1 application migration(s) still pending");
        schemaQueries.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task An_old_API_with_zero_pending_migrations_cannot_release_the_gate()
    {
        using var handler = new StubHandler(_ => JsonResponse("""{"version":"1.0"}"""));
        using var client = Client(handler);
        var schemaQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                client,
                _ =>
                {
                    schemaQueries++;
                    return Task.FromResult(0);
                },
                ReadLocalIdentity,
                ExpectedMigrationId,
                TimeSpan.FromMilliseconds(400),
                TimeSpan.FromMilliseconds(10)));

        failure.Message.Should().Contain("startup contract is not compatible");
        schemaQueries.Should().Be(0);
        handler.Requests.Should().BeGreaterThan(1);
    }

    [Theory]
    [InlineData("api-exclusive-application-startup-v2", ExpectedMigrationId, "startup contract")]
    [InlineData("older-contract", ExpectedMigrationId, "startup contract")]
    [InlineData(ApiApplicationStartupContract.Current, "previous_migration", "migration identity")]
    public async Task An_incompatible_API_cannot_release_the_gate_even_if_the_database_is_current(
        string contract, string migrationId, string expectedObservation)
    {
        using var handler = new StubHandler(_ => ReadyResponse(contract, migrationId));
        using var client = Client(handler);
        var schemaQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                client,
                _ =>
                {
                    schemaQueries++;
                    return Task.FromResult(0);
                },
                ReadLocalIdentity,
                ExpectedMigrationId,
                TimeSpan.FromMilliseconds(400),
                TimeSpan.FromMilliseconds(10)));

        failure.Message.Should().Contain(expectedObservation);
        schemaQueries.Should().Be(0);
    }

    [Fact]
    public async Task A_malformed_startup_contract_is_a_failure_not_an_old_revision_to_wait_for()
    {
        using var handler = new StubHandler(_ => JsonResponse(
            """{"version":"1.0","applicationStartupContract":42}"""));
        using var client = Client(handler);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                client,
                _ => Task.FromResult(0),
                ReadLocalIdentity,
                ExpectedMigrationId,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(1)));

        failure.Message.Should().Contain("invalid applicationStartupContract");
        handler.Requests.Should().Be(1);
    }

    [Fact]
    public async Task Missing_API_database_identity_is_not_a_readiness_response()
    {
        using var handler = new StubHandler(_ => JsonResponse(JsonSerializer.Serialize(new
        {
            version = "1.0",
            applicationStartupContract = ApiApplicationStartupContract.Current,
            applicationMigrationId = ExpectedMigrationId
        })));
        using var client = Client(handler);
        var identityQueries = 0;
        var schemaQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                client, _ => Task.FromResult(++schemaQueries),
                _ => { identityQueries++; return ReadLocalIdentity(_); },
                ExpectedMigrationId, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1)));

        failure.Message.Should().Contain("invalid databaseIdentity");
        identityQueries.Should().Be(0);
        schemaQueries.Should().Be(0);
        handler.Requests.Should().Be(1);
    }

    [Theory]
    [InlineData("not-a-fingerprint")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Noncanonical_API_database_identity_is_rejected_without_accessing_the_local_database(
        string databaseIdentity)
    {
        using var handler = new StubHandler(_ => ReadyResponse(databaseIdentity: databaseIdentity));
        using var client = Client(handler);
        var localQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                client, _ => Task.FromResult(0),
                _ => { localQueries++; return ReadLocalIdentity(_); },
                ExpectedMigrationId, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1)));

        failure.Message.Should().Contain("invalid databaseIdentity");
        localQueries.Should().Be(0);
        handler.Requests.Should().Be(1);
    }

    [Fact]
    public async Task Identically_named_databases_on_distinct_clusters_cannot_release_the_WebApp()
    {
        const string databaseName = "sentencestudio";
        var apiIdentity = PostgresDatabaseIdentity.ComputeFromAttestedValues(
            databaseName, 16384, "public", 100001, ExpectedMigrationId, ExpectedMigrationId);
        var webIdentity = PostgresDatabaseIdentity.ComputeFromAttestedValues(
            databaseName, 16384, "public", 200002, ExpectedMigrationId, ExpectedMigrationId);
        using var handler = new StubHandler(_ => ReadyResponse(databaseIdentity: apiIdentity));
        using var client = Client(handler);
        var schemaQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                client, _ => Task.FromResult(++schemaQueries),
                _ => Task.FromResult(webIdentity),
                ExpectedMigrationId, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1)));

        failure.Message.Should().Contain("database identity does not match")
            .And.NotContain(databaseName).And.NotContain("200002");
        schemaQueries.Should().Be(1);
        handler.Requests.Should().Be(1);
    }

    [Fact]
    public async Task Missing_local_persisted_schema_marker_never_releases_the_WebApp()
    {
        using var handler = new StubHandler(_ => ReadyResponse());
        using var client = Client(handler);
        var schemaQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                client, _ => Task.FromResult(++schemaQueries),
                _ => Task.FromException<string>(new PostgresDatabaseIdentityException(
                    PostgresDatabaseIdentityFailure.MissingSchemaMarker)),
                ExpectedMigrationId, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(10)));

        failure.Message.Should().Contain("database identity unavailable (MissingSchemaMarker)");
        schemaQueries.Should().Be(0);
        handler.Requests.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Lost_local_backend_cannot_release_WebApp_even_if_the_API_hash_and_history_match()
    {
        using var handler = new StubHandler(_ => ReadyResponse());
        using var client = Client(handler);
        var backendFailures = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApiMigrationReadinessGate.WaitAsync(
                client,
                _ =>
                {
                    backendFailures++;
                    return Task.FromException<PostgresDatabaseReadiness>(
                        new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.BackendChanged));
                },
                ExpectedMigrationId, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10)));

        // The deadline may interrupt a final retry after HTTP succeeds but before the local check.
        failure.Message.Should().StartWith(
            "WebApp cannot start: API migration owner and application schema did not become ready")
            .And.Contain("last observation:");
        failure.InnerException.Should().BeAssignableTo<OperationCanceledException>();
        backendFailures.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_migration_history_query_failure_is_never_treated_as_ready()
    {
        using var handler = new StubHandler(_ => ReadyResponse());
        using var client = Client(handler);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                client,
                _ => throw new InvalidOperationException("simulated history failure"),
                ReadLocalIdentity,
                ExpectedMigrationId,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(1)));

        failure.Message.Should().Be("simulated history failure");
        handler.Requests.Should().Be(1);
    }

    [Theory]
    [InlineData("https://reader:credential@api.example.invalid/")]
    [InlineData("https://reader@api.example.invalid/")]
    [InlineData("ftp://api.example.invalid/")]
    [InlineData("/relative")]
    public void Named_readiness_client_rejects_credentials_and_non_HTTP_API_addresses(string address)
    {
        var services = new ServiceCollection();
        var error = Assert.Throws<ArgumentException>(() =>
            ApiMigrationReadinessGate.RegisterClient(
                services, new Uri(address, UriKind.RelativeOrAbsolute)));

        error.Message.Should().Contain("without credentials")
            .And.NotContain("reader:credential").And.NotContain("api.example.invalid");
    }

    [Fact]
    public async Task A_302_to_a_valid_version_response_is_rejected_by_the_real_named_client()
    {
        await using var server = await StubOperatorApi.StartAsync();
        server.VersionMigrationId = ExpectedMigrationId;
        server.VersionStatus = HttpStatusCode.Redirect;
        server.VersionRedirectTarget = "/api/version/ready-after-redirect";

        var services = new ServiceCollection();
        ApiMigrationReadinessGate.RegisterClient(services, new Uri(server.BaseAddress));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ApiMigrationReadinessGate.ClientName);
        var schemaQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitForReadinessAsync(
                client,
                _ =>
                {
                    schemaQueries++;
                    return Task.FromResult(0);
                },
                ReadLocalIdentity,
                ExpectedMigrationId,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10)));

        failure.Message.Should().Contain("HTTP 302");
        schemaQueries.Should().Be(0);
        server.Requests.Should().ContainSingle(r => r.Path == "/api/version/info");
    }

    [Fact]
    public async Task Real_loopback_API_releases_the_gate_only_for_the_matching_database()
    {
        await using var server = await StubOperatorApi.StartAsync();
        server.VersionMigrationId = ExpectedMigrationId;
        server.VersionDatabaseIdentity = MatchingDatabaseIdentity;

        var services = new ServiceCollection();
        ApiMigrationReadinessGate.RegisterClient(services, new Uri(server.BaseAddress));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ApiMigrationReadinessGate.ClientName);

        await ApiMigrationReadinessGate.WaitAsync(
            client,
            _ => Task.FromResult(new PostgresDatabaseReadiness(MatchingDatabaseIdentity, 0)),
            ExpectedMigrationId, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10));

        server.Requests.Should().ContainSingle(request => request.Path == "/api/version/info");
    }

    [Fact]
    public async Task Real_loopback_API_cluster_A_cannot_release_WebApp_configured_for_cluster_B()
    {
        var apiIdentity = PostgresDatabaseIdentity.ComputeFromAttestedValues(
            "sentencestudio", 16384, "public", 100001, ExpectedMigrationId, ExpectedMigrationId);
        var webIdentity = PostgresDatabaseIdentity.ComputeFromAttestedValues(
            "sentencestudio", 16384, "public", 200002, ExpectedMigrationId, ExpectedMigrationId);
        await using var server = await StubOperatorApi.StartAsync();
        server.VersionMigrationId = ExpectedMigrationId;
        server.VersionDatabaseIdentity = apiIdentity;

        var services = new ServiceCollection();
        ApiMigrationReadinessGate.RegisterClient(services, new Uri(server.BaseAddress));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ApiMigrationReadinessGate.ClientName);
        var localQueries = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApiMigrationReadinessGate.WaitAsync(
                client,
                _ =>
                {
                    localQueries++;
                    return Task.FromResult(new PostgresDatabaseReadiness(webIdentity, 0));
                },
                ExpectedMigrationId, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10)));

        failure.Message.Should().Contain("database identity does not match")
            .And.NotContain("200002").And.NotContain("sentencestudio");
        localQueries.Should().Be(1);
        server.Requests.Should().ContainSingle(request => request.Path == "/api/version/info");
    }

    private static Task WaitForReadinessAsync(
        HttpClient api,
        Func<CancellationToken, Task<int>> getPendingMigrationCount,
        Func<CancellationToken, Task<string>> getLocalDatabaseIdentity,
        string expectedMigrationId,
        TimeSpan? timeout = null,
        TimeSpan? retryInterval = null,
        CancellationToken cancellationToken = default) =>
        ApiMigrationReadinessGate.WaitAsync(
            api,
            async ct => new PostgresDatabaseReadiness(
                await getLocalDatabaseIdentity(ct), await getPendingMigrationCount(ct)),
            expectedMigrationId, timeout, retryInterval, cancellationToken);

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.example.invalid/") };

    private static HttpResponseMessage ReadyResponse(
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

    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsolutePath.Should().Be("/api/version/info");
            request.Headers.Authorization.Should().BeNull();
            return Task.FromResult(respond(++Requests));
        }
    }
}
