using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SentenceStudio.Contracts.Startup;
using SentenceStudio.Tests.Startup;
using SentenceStudio.Workers.Startup;
using Xunit.Abstractions;

namespace SentenceStudio.UnitTests.Workers;

public sealed class WorkerApiReadinessGateLiveTests(ITestOutputHelper output)
{
    [StartupCrossServicePostgresFact]
    public async Task Controlled_HTTP_API_A_observed_identity_cannot_release_Worker_on_attested_database_B()
    {
        await using var proof = await StartupGateCrossServiceProof.OpenAsync();
        var metadata = new EfWorkerMigrationMetadata(proof.LocalDb);
        Assert.Equal(proof.ExpectedMigrationId, metadata.GetExpectedApplicationMigrationId());

        var beforeGate = await metadata.GetDatabaseReadinessAsync(
            proof.ExpectedMigrationId, CancellationToken.None);
        Assert.Equal(proof.LocalIdentity, beforeGate.DatabaseIdentity);
        Assert.Equal(0, beforeGate.PendingMigrationCount);
        proof.AssertLocalBackendUnchanged();

        // This is a controlled local HTTP stand-in, not an API process or a production-role test.
        await using var apiA = await ObservedApiVersionHttpStandIn.StartAsync(proof);
        Assert.Equal(proof.ApiIdentity, apiA.DatabaseIdentity);
        Assert.Equal(proof.ExpectedMigrationId, apiA.MigrationId);
        using (var probe = new HttpClient { BaseAddress = apiA.Address })
        using (var response = await probe.GetAsync("/api/version/info"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(proof.ApiIdentity,
                body.RootElement.GetProperty("databaseIdentity").GetString());
            Assert.Equal(proof.ExpectedMigrationId,
                body.RootElement.GetProperty("applicationMigrationId").GetString());
            Assert.Equal(ApiApplicationStartupContract.Current,
                body.RootElement.GetProperty("applicationStartupContract").GetString());
        }
        var services = new ServiceCollection();
        WorkerApiReadinessGate.RegisterClient(services, apiA.Address);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(WorkerApiReadinessGate.ClientName);

        var localReads = 0;
        var releases = 0;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await WorkerApiReadinessGate.WaitAsync(
                client,
                async ct =>
                {
                    localReads++;
                    var readiness = await metadata.GetDatabaseReadinessAsync(
                        proof.ExpectedMigrationId, ct);
                    Assert.Equal(proof.LocalIdentity, readiness.DatabaseIdentity);
                    Assert.Equal(0, readiness.PendingMigrationCount);
                    return readiness;
                },
                proof.ExpectedMigrationId, TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(10));
            releases++;
        });

        Assert.Contains(
            "Workers cannot start: API PostgreSQL database identity does not match",
            failure.Message);
        proof.AssertNoConnectionDetailsIn(failure);
        Assert.Equal(0, releases);
        Assert.Equal(1, localReads);
        Assert.Equal(2, apiA.Requests);
        Assert.False(apiA.ReceivedCredentials);
        proof.AssertLocalBackendUnchanged();
        await proof.AssertClustersRemainAttestedAsync();
        output.WriteLine(
            "Live Worker gate mismatch: API A identity observed on an attested scratch DB; " +
            "B read via Worker EF metadata; version HTTP is a controlled stand-in. " +
            "Scratch database names differ; production role access was not tested.");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("0", true)]
    [InlineData("1", false)]
    [InlineData("invalid", false)]
    [InlineData("2", false)]
    public void Absent_opt_in_skips_but_invalid_opt_in_fails_before_Docker_or_Postgres(
        string? enabled, bool shouldSkip)
    {
        var fact = new StartupCrossServicePostgresFactAttribute(enabled);
        Assert.Equal(shouldSkip, fact.Skip is not null);
        if (!shouldSkip && enabled != "1")
        {
            var failure = Assert.Throws<InvalidOperationException>(
                () => StartupGateCrossServiceProof.RequireEnabled(enabled));
            Assert.Contains("no Docker or PostgreSQL socket was opened", failure.Message);
        }
    }

    [Fact]
    public void Invalid_or_shared_scratch_configuration_fails_without_opening_a_socket()
    {
        const string runA = "0123456789ab4def8123456789abcdef";
        const string runB = "fedcba9876544def8123456789abcdef";
        var a = Configuration(runA, 'a', 55479);
        var b = Configuration(runB, 'b', 55480);

        StartupGateCrossServiceProof.RequireIndependentTargets(a, b);
        Assert.Throws<InvalidOperationException>(
            () => StartupGateCrossServiceProof.RequireIndependentTargets(a, a));
        Assert.Throws<InvalidOperationException>(
            () => StartupGateCrossServiceProof.RequireIndependentTargets(
                a, b with { VolumeName = a.VolumeName }));
        Assert.Throws<InvalidOperationException>(
            () => StartupGateCrossServiceProof.RequireIndependentTargets(
                a, b with { ContainerId = a.ContainerId }));
        var sharedPort = new NpgsqlConnectionStringBuilder(b.Connection.ConnectionString)
        {
            Port = a.Connection.Port
        };
        Assert.Throws<InvalidOperationException>(
            () => StartupGateCrossServiceProof.RequireIndependentTargets(
                a, b with { Connection = sharedPort }));

        var wrongHost = new NpgsqlConnectionStringBuilder(a.Connection.ConnectionString)
        {
            Host = "db.example.invalid"
        };
        var wrongPort = new NpgsqlConnectionStringBuilder(a.Connection.ConnectionString)
        {
            Port = 5432
        };
        var wrongDatabase = new NpgsqlConnectionStringBuilder(a.Connection.ConnectionString)
        {
            Database = "sentencestudio"
        };
        var wrongRole = new NpgsqlConnectionStringBuilder(a.Connection.ConnectionString)
        {
            Username = "dbadmin"
        };
        var missingPassword = new NpgsqlConnectionStringBuilder(a.Connection.ConnectionString)
        {
            Password = ""
        };
        foreach (var invalid in new[]
                 {
                     wrongHost, wrongPort, wrongDatabase, wrongRole, missingPassword
                 })
        {
            var failure = Assert.Throws<InvalidOperationException>(() =>
                StartupGateCrossServiceProof.RequireConfiguration(
                    invalid.ConnectionString, runA, a.ClusterName,
                    a.VolumeName, a.ContainerId));
            Assert.Contains("no Docker or PostgreSQL socket was opened", failure.Message);
        }

        Assert.Throws<InvalidOperationException>(() =>
            StartupGateCrossServiceProof.RequireConfiguration(
                a.Connection.ConnectionString, runA, a.ClusterName,
                a.VolumeName, new string('a', 63)));
    }

    private static SentenceStudio.Api.Tests.Startup.StartupFencePostgresConfiguration Configuration(
        string runId, char containerDigit, int port)
    {
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Database = $"startup_fence_{runId}",
            Username = "startup_fence_owner",
            Password = "placeholder-only"
        };
        return StartupGateCrossServiceProof.RequireConfiguration(
            connection.ConnectionString, runId, $"sfence_{runId}",
            $"sfence_pgdata_{runId}", new string(containerDigit, 64));
    }
}
