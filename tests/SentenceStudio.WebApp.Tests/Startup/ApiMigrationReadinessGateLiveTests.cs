using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SentenceStudio.Contracts.Startup;
using SentenceStudio.Tests.Startup;
using SentenceStudio.WebApp.Startup;
using Xunit.Abstractions;

namespace SentenceStudio.WebApp.Tests.Startup;

public sealed class ApiMigrationReadinessGateLiveTests(ITestOutputHelper output)
{
    [StartupCrossServicePostgresFact]
    public async Task Controlled_HTTP_API_A_observed_identity_cannot_release_WebApp_on_attested_database_B()
    {
        await using var proof = await StartupGateCrossServiceProof.OpenAsync();
        Assert.Equal(proof.ExpectedMigrationId,
            proof.LocalDb.Database.GetMigrations().LastOrDefault());

        var beforeGate = await ReadWebAppReadinessAsync(proof, CancellationToken.None);
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
        ApiMigrationReadinessGate.RegisterClient(services, apiA.Address);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ApiMigrationReadinessGate.ClientName);

        var localReads = 0;
        var releases = 0;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ApiMigrationReadinessGate.WaitAsync(
                client,
                async ct =>
                {
                    localReads++;
                    var readiness = await ReadWebAppReadinessAsync(proof, ct);
                    Assert.Equal(proof.LocalIdentity, readiness.DatabaseIdentity);
                    Assert.Equal(0, readiness.PendingMigrationCount);
                    return readiness;
                },
                proof.ExpectedMigrationId, TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(10));
            releases++;
        });

        Assert.Contains(
            "WebApp cannot start: API PostgreSQL database identity does not match",
            failure.Message);
        proof.AssertNoConnectionDetailsIn(failure);
        Assert.Equal(0, releases);
        Assert.Equal(1, localReads);
        Assert.Equal(2, apiA.Requests);
        Assert.False(apiA.ReceivedCredentials);
        proof.AssertLocalBackendUnchanged();
        await proof.AssertClustersRemainAttestedAsync();
        output.WriteLine(
            "Live WebApp gate mismatch: API A identity observed on an attested scratch DB; " +
            "B read via the WebApp EF continuity/migration path; version HTTP is a controlled " +
            "stand-in. Scratch database names differ; production role access was not tested.");
    }

    private static async Task<PostgresDatabaseReadiness> ReadWebAppReadinessAsync(
        StartupGateCrossServiceProof proof, CancellationToken cancellationToken)
    {
        var db = proof.LocalDb;
        Assert.True(db.Database.IsNpgsql());
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            Assert.Same(proof.LocalConnection, connection);
            return await PostgresDatabaseIdentity.ReadReadinessAsync(
                connection,
                () => db.Database.GetDbConnection(),
                () => connection is NpgsqlConnection npgsql ? npgsql.ProcessID : 0,
                async ct => (await db.Database.GetPendingMigrationsAsync(ct)).Count(),
                proof.ExpectedMigrationId, cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
