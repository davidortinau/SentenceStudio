using System.Text.RegularExpressions;

namespace SentenceStudio.Api.Tests.Infrastructure;

public sealed class AppHostMigrationStartupOrderTests
{
    [Fact]
    public void API_readiness_probe_uses_a_public_post_migration_endpoint_in_run_and_publish_modes()
    {
        var appHost = ReadSource("SentenceStudio.AppHost", "AppHost.cs");
        var apiResource = Regex.Match(
            appHost,
            @"var api = builder\.AddProject<SentenceStudio_Api>\(""api""\).*?\.WithExternalHttpEndpoints\(\)\s*(?://[^\r\n]*[\r\n]\s*)*\.WithHttpHealthCheck\(""/api/version/info""\);",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        apiResource.Success.Should().BeTrue(
            "WaitFor(api) needs an HTTP readiness check, and Development-only /health cannot be used in production");

        var api = ReadSource("SentenceStudio.Api", "Program.cs");
        var versionEndpoints = ReadSource("SentenceStudio.Api", "VersionEndpoints.cs");
        var startupLock = api.IndexOf("PostgresApplicationStartupLock.AcquireAsync(", StringComparison.Ordinal);
        var migration = api.IndexOf("await db.Database.MigrateAsync(startupToken);", StringComparison.Ordinal);
        var classification = api.IndexOf(
            "await backfillService.BackfillLexicalUnitTypesAsync(db, assertSession, startupToken);",
            StringComparison.Ordinal);
        var constituents = api.IndexOf(
            "await backfillService.BackfillPhraseConstituentsAsync(db, assertSession, startupToken);",
            StringComparison.Ordinal);
        var provisioning = api.IndexOf(
            "await startupLock.ProvisionAsync(syncProvider, startupToken);",
            StringComparison.Ordinal);
        var coachMigration = api.IndexOf("await coachDb.Database.MigrateAsync(ct);", StringComparison.Ordinal);
        var feedbackMigration = api.IndexOf("await feedbackDb.Database.MigrateAsync(ct);", StringComparison.Ordinal);
        var versionMapping = api.IndexOf("app.MapVersionEndpoints();", StringComparison.Ordinal);
        var run = api.LastIndexOf("app.Run();", StringComparison.Ordinal);

        api.Should().Contain("API cannot start: no application migrations were discovered.");
        startupLock.Should().BeGreaterThanOrEqualTo(0)
            .And.BeLessThan(migration, "the API replica lock must span migration and backfills");
        migration.Should().BeGreaterThan(startupLock).And.BeLessThan(classification);
        classification.Should().BeGreaterThan(migration).And.BeLessThan(constituents);
        constituents.Should().BeGreaterThan(classification).And.BeLessThan(provisioning);
        coachMigration.Should().BeGreaterThan(provisioning).And.BeLessThan(feedbackMigration);
        feedbackMigration.Should().BeGreaterThan(coachMigration).And.BeLessThan(versionMapping);
        provisioning.Should().BeGreaterThan(constituents).And.BeLessThan(versionMapping,
            "the API must finish shared startup writes before the probe can respond");
        api.Should().Contain("new StartupDbContextFactory(startupLock)")
            .And.Contain("using var db = contexts.CreateApplication();")
            .And.Contain("db, scope.ServiceProvider.GetRequiredService<")
            .And.Contain("using var coachDb = contexts.CreateCoach();")
            .And.Contain("using var feedbackDb = contexts.CreateFeedback();")
            .And.Contain("await devSeeder.SeedAsync(userManager, identityDb, assertSession, startupToken);")
            .And.Contain("StartupDbContextFactory.RequireSingleDatabase(app.Configuration)")
            .And.NotContain("await syncProvider.ApplyProvisionAsync();",
                "the interface method would provision on a different backend");
        versionMapping.Should().BeGreaterThan(provisioning)
            .And.BeLessThan(run, "the version route must be mapped before the API starts listening");
        Regex.IsMatch(api, @"(?m)^app\.MapVersionEndpoints\(\);[ \t\r]*$",
                RegexOptions.CultureInvariant)
            .Should().BeTrue("the probe must be available outside Development");
        Regex.IsMatch(api,
                @"if \(app\.Environment\.IsDevelopment\(\)\)\s*\{\s*app\.MapHealthChecks\(""/health""\);",
                RegexOptions.CultureInvariant)
            .Should().BeTrue("the detailed health endpoint must stay Development-only");
        versionEndpoints.Should().Contain("""app.MapGroup("/api/version")""")
            .And.Contain("""group.MapGet("/info", GetVersionInfo)""")
            .And.Contain("return Results.Ok(new")
            .And.Contain("ApplicationStartupContract = ApiApplicationStartupContract.Current")
            .And.Contain("ApplicationMigrationId = applicationMigrationId")
            .And.Contain("""configuration.GetValue("Database:SkipMigrateOnStartup", false)""")
            .And.Contain("StatusCodes.Status503ServiceUnavailable",
                "an intentionally skipped startup must never advertise API readiness");
        var startupLockSource = ReadSource(
            "SentenceStudio.Api", Path.Combine("Startup", "PostgresApplicationStartupLock.cs"));
        var acquireKey = Regex.Match(startupLockSource, @"pg_advisory_lock\((\d+), (\d+)\)");
        var releaseKey = Regex.Match(startupLockSource, @"pg_advisory_unlock\((\d+), (\d+)\)");
        acquireKey.Success.Should().BeTrue();
        releaseKey.Success.Should().BeTrue();
        acquireKey.Groups[1].Value.Should().Be(releaseKey.Groups[1].Value);
        acquireKey.Groups[2].Value.Should().Be(releaseKey.Groups[2].Value);
        startupLockSource.Should().Contain("await connection.OpenAsync(")
            .And.Contain("await connection.DisposeAsync(")
            .And.Contain("Pooling = false")
            .And.Contain("Multiplexing = false")
            .And.Contain("FROM pg_locks")
            .And.Contain("await _connection.BeginTransactionAsync(cancellationToken)")
            .And.Contain("provider.ApplyProvisionAsync(_connection, transaction, cancellationToken)")
            .And.Contain("await transaction.CommitAsync(cancellationToken)");
        versionEndpoints.Should().NotContain("RequireAuthorization",
            "the health probe must be able to reach the already-public version endpoint without credentials");
    }

    [Fact]
    public void WebApp_locally_waits_for_API_HTTP_readiness_and_is_not_an_application_migration_owner()
    {
        var appHost = ReadSource("SentenceStudio.AppHost", "AppHost.cs");
        var webAppResource = Regex.Match(
            appHost,
            @"var webapp = builder\.AddProject<SentenceStudio_WebApp>\(""webapp""\).*?\.WithExternalHttpEndpoints\(\);",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        webAppResource.Success.Should().BeTrue("the WebApp resource must be present");
        appHost.IndexOf("""WithHttpHealthCheck("/api/version/info")""", StringComparison.Ordinal)
            .Should().BeGreaterThanOrEqualTo(0)
            .And.BeLessThan(webAppResource.Index, "the API must declare its health check before WebApp waits for it");
        webAppResource.Value.Should().MatchRegex(
            @"(?s)\.WithReference\(api\)\s*(?://[^\r\n]*[\r\n]\s*)*\.WaitFor\(api\)",
            "service discovery alone does not order application database migrations");

        var webApp = ReadSource("SentenceStudio.WebApp", "Program.cs");
        webApp.Should().NotContain("db.Database.MigrateAsync(",
            "deployed Container Apps do not inherit AppHost.WaitFor ordering and need a single migration owner");
        webApp.Should().NotContain("BackfillLexicalUnitTypesAsync(")
            .And.NotContain("BackfillPhraseConstituentsAsync(",
                "only the API may run the shared backfills even when an older API revision still serves");
        var gate = webApp.IndexOf("await ApiMigrationReadinessGate.WaitAsync(", StringComparison.Ordinal);
        var checkHistory = webApp.IndexOf("db.Database.GetPendingMigrationsAsync(", StringComparison.Ordinal);
        gate.Should().BeGreaterThanOrEqualTo(0)
            .And.BeLessThan(checkHistory, "the WebApp's schema check must be read-only");
        checkHistory.Should().BeGreaterThan(gate)
            .And.BeLessThan(webApp.LastIndexOf("app.Run();", StringComparison.Ordinal),
                "the WebApp must not listen until the API and schema are compatible");
    }

    [Fact]
    public void Fenced_startup_does_not_replace_runtime_DbContext_or_CoreSync_registrations()
    {
        var api = ReadSource("SentenceStudio.Api", "Program.cs");
        var feedback = ReadSource(
            "SentenceStudio.Api", Path.Combine("Feedback", "FeedbackServiceCollectionExtensions.cs"));

        api.Should().Contain("builder.AddNpgsqlDbContext<ApplicationDbContext>(\"sentencestudio\"")
            .And.Contain("builder.Services.AddDbContext<SentenceStudio.Api.Coach.Persistence.CoachDbContext>(options =>")
            .And.Contain("builder.Services.AddSingleton<PostgreSQLSyncProvider>(sp =>")
            .And.Contain("return new PostgreSQLSyncProvider(configurationBuilder.Build(), ProviderMode.Remote);")
            .And.Contain("builder.Services.AddSingleton<ISyncProvider, NativeOwnerSyncProvider>();")
            .And.Contain("app.Services.GetRequiredService<PostgreSQLSyncProvider>()")
            .And.Contain("var contexts = new StartupDbContextFactory(startupLock);");
        feedback.Should().Contain("services.AddDbContext<FeedbackDbContext>(options =>")
            .And.Contain("services.AddHostedService<FeedbackRetentionBackgroundService>();");
    }

    private static string ReadSource(string project, string file)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src")))
        {
            root = root.Parent;
        }

        if (root is null)
        {
            throw new DirectoryNotFoundException("Could not locate the repository root.");
        }

        return File.ReadAllText(Path.Combine(root.FullName, "src", project, file));
    }
}
