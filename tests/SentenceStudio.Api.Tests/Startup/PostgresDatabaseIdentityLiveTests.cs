using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentenceStudio.Contracts.Startup;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Tests.Startup;

public sealed class PostgresDatabaseIdentityLiveTests
{
    internal const string EnabledVariable = "STARTUP_IDENTITY_SCRATCH_PG_ENABLED";

    [StartupIdentityPostgresFact]
    public async Task Independently_attested_startup_fence_clusters_have_distinct_identities()
    {
        RequireEnabled(Environment.GetEnvironmentVariable(EnabledVariable));
        var configurationA = RequireConfiguration("A");
        var configurationB = RequireConfiguration("B");
        RequireIndependentTargets(configurationA, configurationB);
        var migrations = ExpectedMigrations(configurationA.Connection.ConnectionString);

        var dockerA = await StartupFenceDockerAttestor.AttestAsync(configurationA);
        var dockerB = await StartupFenceDockerAttestor.AttestAsync(configurationB);
        Assert.NotEqual(dockerA.SystemIdentifier, dockerB.SystemIdentifier);

        var first = await ObserveAsync(configurationA, dockerA, migrations);
        var second = await ObserveAsync(configurationB, dockerB, migrations);

        // Approved fault runs use distinct database names; same-name coverage is in Worker/Web unit tests.
        Assert.NotEqual(first.DatabaseName, second.DatabaseName);
        Assert.Equal(first.SchemaName, second.SchemaName);
        Assert.Equal("public", first.SchemaName);
        Assert.Equal(first.LatestMigrationId, second.LatestMigrationId);
        Assert.NotEqual(first.SystemIdentifier, second.SystemIdentifier);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.True(PostgresDatabaseIdentity.IsCanonicalFingerprint(first.Fingerprint));
        Assert.True(PostgresDatabaseIdentity.IsCanonicalFingerprint(second.Fingerprint));
    }

    [Theory]
    [InlineData("9223372036854775808", "-9223372036854775808")]
    [InlineData("18446744073709551615", "-1")]
    public void Pg_controldata_unsigned_and_SQL_bigint_signed_values_compare_by_bits(
        string controldata, string sql)
    {
        Assert.True(StartupFencePostgresFixture.MatchesDockerSystemIdentifier(sql, controldata));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("0", true)]
    [InlineData("1", false)]
    [InlineData("invalid", false)]
    [InlineData("2", false)]
    public void Only_an_absent_or_disabled_opt_in_skips_the_live_fact(
        string? enabled, bool skipped)
    {
        var fact = new StartupIdentityPostgresFactAttribute(enabled);
        Assert.Equal(skipped, fact.Skip is not null);
        if (!skipped && enabled != "1")
        {
            Assert.Throws<InvalidOperationException>(() => RequireEnabled(enabled));
        }
    }

    [Fact]
    public void Identity_proof_refuses_shared_runs_containers_volumes_or_ports()
    {
        var first = OfflineConfiguration(
            "0123456789ab4def8123456789abcdef", 'a', 55479);
        var second = OfflineConfiguration(
            "fedcba9876544def8123456789abcdef", 'b', 55480);

        RequireIndependentTargets(first, second);
        Assert.Throws<InvalidOperationException>(() => RequireIndependentTargets(first, first));
        Assert.Throws<InvalidOperationException>(() =>
            RequireIndependentTargets(first, second with { ContainerId = first.ContainerId }));
        Assert.Throws<InvalidOperationException>(() =>
            RequireIndependentTargets(first, second with { VolumeName = first.VolumeName }));
        var samePort = new NpgsqlConnectionStringBuilder(second.Connection.ConnectionString)
        {
            Port = first.Connection.Port
        };
        Assert.Throws<InvalidOperationException>(() =>
            RequireIndependentTargets(first, second with { Connection = samePort }));
    }

    [Fact]
    public void Expected_provider_migrations_are_available_without_a_database_connection()
    {
        var configuration = OfflineConfiguration(
            "0123456789ab4def8123456789abcdef", 'a', 55479);

        Assert.NotEmpty(ExpectedMigrations(configuration.Connection.ConnectionString));
    }

    private static void RequireEnabled(string? enabled)
    {
        if (enabled != "1")
        {
            throw new InvalidOperationException(
                $"{EnabledVariable} must be exactly 1. No Docker or SQL was attempted.");
        }
    }

    private static StartupFencePostgresConfiguration RequireConfiguration(string slot)
    {
        var prefix = $"STARTUP_IDENTITY_SCRATCH_PG_{slot}_";
        return StartupFencePostgresFixture.RequireConfiguration(
            "1",
            Environment.GetEnvironmentVariable(prefix + "CONNECTION"),
            Environment.GetEnvironmentVariable(prefix + "RUN_ID"),
            Environment.GetEnvironmentVariable(prefix + "CLUSTER_NAME"),
            Environment.GetEnvironmentVariable(prefix + "VOLUME_NAME"),
            Environment.GetEnvironmentVariable(prefix + "CONTAINER_ID"));
    }

    private static void RequireIndependentTargets(
        StartupFencePostgresConfiguration first, StartupFencePostgresConfiguration second)
    {
        if (first.RunId == second.RunId ||
            first.ClusterName == second.ClusterName ||
            first.VolumeName == second.VolumeName ||
            first.ContainerId == second.ContainerId ||
            first.Connection.Port == second.Connection.Port ||
            first.Connection.Database == second.Connection.Database)
        {
            throw new InvalidOperationException(
                "Identity evidence requires two different approved run IDs, containers, " +
                "PGDATA volumes, loopback ports, and databases. No Docker or SQL was attempted.");
        }
    }

    private static StartupFencePostgresConfiguration OfflineConfiguration(
        string runId, char containerDigit, int port)
    {
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Database = $"startup_fence_{runId}",
            Username = "startup_fence_owner"
        };
        return StartupFencePostgresFixture.RequireConfiguration(
            "1", connection.ConnectionString, runId, $"sfence_{runId}",
            $"sfence_pgdata_{runId}", new string(containerDigit, 64));
    }

    private static string[] ExpectedMigrations(string connectionString)
    {
        using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(connectionString).Options);
        var migrations = context.Database.GetMigrations().ToArray();
        Assert.NotEmpty(migrations);
        return migrations;
    }

    private static async Task<Observation> ObserveAsync(
        StartupFencePostgresConfiguration configuration,
        StartupFenceDockerAttestation docker, string[] expectedMigrations)
    {
        await using var connection = new NpgsqlConnection(configuration.Connection.ConnectionString);
        await connection.OpenAsync();
        var backendPid = connection.ProcessID;
        var marker = await ReadPreexistingOwnerMarkerAsync(connection, configuration, docker);
        var persistedMigrations = await ReadPersistedMigrationsAsync(connection);
        Assert.Equal(expectedMigrations, persistedMigrations);
        var latestMigrationId = persistedMigrations[^1];

        var fingerprint = await PostgresDatabaseIdentity.ReadAsync(
            connection, latestMigrationId);
        Assert.Equal(
            PostgresDatabaseIdentity.ComputeFromAttestedValues(
                marker.DatabaseName, marker.DatabaseOid, marker.SchemaName,
                long.Parse(marker.SystemIdentifier, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture),
                latestMigrationId, latestMigrationId),
            fingerprint);

        await SetSearchPathAsync(connection, "pg_catalog");
        try
        {
            var missing = await Assert.ThrowsAsync<PostgresDatabaseIdentityException>(() =>
                PostgresDatabaseIdentity.ReadAsync(connection, latestMigrationId));
            Assert.Equal(PostgresDatabaseIdentityFailure.MissingSchemaMarker, missing.Reason);
        }
        finally
        {
            await SetSearchPathAsync(connection, "public");
        }

        Assert.Equal(backendPid, connection.ProcessID);
        Assert.Equal(fingerprint,
            await PostgresDatabaseIdentity.ReadAsync(connection, latestMigrationId));
        Assert.Equal(backendPid, connection.ProcessID);
        return new Observation(marker.DatabaseName, marker.SchemaName,
            marker.SystemIdentifier, latestMigrationId, fingerprint);
    }

    private static async Task<MarkerObservation> ReadPreexistingOwnerMarkerAsync(
        NpgsqlConnection connection, StartupFencePostgresConfiguration configuration,
        StartupFenceDockerAttestation docker)
    {
        await using (var shape = connection.CreateCommand())
        {
            shape.CommandText = """
                SELECT count(*) = 6 AND count(*) FILTER (WHERE a.attnotnull AND (
                    (a.attname = 'run_id' AND a.atttypid = 'uuid'::regtype) OR
                    (a.attname IN ('cluster_name', 'database_name', 'system_identifier')
                        AND a.atttypid = 'text'::regtype) OR
                    (a.attname IN ('cluster_started_at', 'created_at')
                        AND a.atttypid = 'timestamptz'::regtype)
                )) = 6 AND EXISTS (
                    SELECT 1 FROM pg_catalog.pg_class c
                    WHERE c.oid = pg_catalog.to_regclass('public.startup_fence_owner')
                      AND c.relkind = 'r'
                      AND pg_catalog.pg_get_userbyid(c.relowner) = 'startup_fence_owner'
                )
                FROM pg_catalog.pg_attribute a
                WHERE a.attrelid = pg_catalog.to_regclass('public.startup_fence_owner')
                  AND a.attnum > 0 AND NOT a.attisdropped
                """;
            Assert.True(await shape.ExecuteScalarAsync() is true,
                "The pre-existing fixture-owned scratch marker must have its exact table schema.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pg_catalog.pg_backend_pid(),
                   pg_catalog.current_database(), database.oid::text,
                   pg_catalog.current_schema(), current_user,
                   current_setting('cluster_name'), pg_catalog.pg_is_in_recovery(),
                   control.system_identifier::text,
                   extract(epoch FROM pg_catalog.pg_postmaster_start_time())::text,
                   current_setting('data_directory'),
                   marker.run_id, marker.cluster_name, marker.database_name,
                   marker.system_identifier,
                   extract(epoch FROM marker.cluster_started_at)::text,
                   extract(epoch FROM marker.created_at)::text
            FROM pg_catalog.pg_database AS database
            CROSS JOIN pg_catalog.pg_control_system() AS control
            CROSS JOIN public.startup_fence_owner AS marker
            WHERE database.datname = pg_catalog.current_database()
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(),
            "The fixture must already have written exactly one owner marker on this backend.");
        Assert.Equal(connection.ProcessID, reader.GetInt32(0));
        var databaseName = reader.GetString(1);
        var databaseOid = uint.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
        var schemaName = reader.GetString(3);
        Assert.Equal(configuration.Connection.Database, databaseName);
        Assert.Equal("public", schemaName);
        Assert.Equal(configuration.Connection.Username, reader.GetString(4));
        Assert.Equal(configuration.ClusterName, reader.GetString(5));
        Assert.False(reader.GetBoolean(6));
        Assert.False(reader.IsDBNull(7), "SQL system identifier must be present.");
        var systemIdentifier = reader.GetString(7);
        Assert.True(StartupFencePostgresFixture.MatchesDockerSystemIdentifier(
            systemIdentifier, docker.SystemIdentifier),
            "The SQL system identifier must match the Docker-attested 64-bit identifier.");
        var postmasterStart = reader.GetString(8);
        Assert.Equal(StartupFenceDockerAttestor.PgData, reader.GetString(9));
        Assert.Equal(configuration.RunId, reader.GetGuid(10));
        Assert.Equal(configuration.ClusterName, reader.GetString(11));
        Assert.Equal(databaseName, reader.GetString(12));
        Assert.False(reader.IsDBNull(13), "The owner marker must contain a system identifier.");
        Assert.Equal(systemIdentifier, reader.GetString(13));
        Assert.Equal(postmasterStart, reader.GetString(14));
        var started = FromEpoch(postmasterStart);
        Assert.InRange(started, docker.ContainerStartedAt,
            docker.ClusterInitializedAt.AddMinutes(5));
        Assert.True(started >= docker.ClusterInitializedAt);
        Assert.InRange(FromEpoch(reader.GetString(15)), started,
            DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.False(await reader.ReadAsync(),
            "The fixture-owned marker must have exactly one row.");

        return new MarkerObservation(databaseName, databaseOid, schemaName, systemIdentifier);
    }

    private static DateTimeOffset FromEpoch(string seconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(
            decimal.Parse(seconds, CultureInfo.InvariantCulture) * 1000m)));

    private static async Task<string[]> ReadPersistedMigrationsAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId"
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var migrations = new List<string>();
        while (await reader.ReadAsync())
        {
            migrations.Add(reader.GetString(0));
        }
        Assert.NotEmpty(migrations);
        return migrations.ToArray();
    }

    private static async Task SetSearchPathAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = schema switch
        {
            "pg_catalog" => "SET search_path TO pg_catalog",
            "public" => "SET search_path TO public",
            _ => throw new ArgumentOutOfRangeException(nameof(schema))
        };
        await command.ExecuteNonQueryAsync();
    }

    private sealed record MarkerObservation(
        string DatabaseName, uint DatabaseOid, string SchemaName, string SystemIdentifier);

    private sealed record Observation(
        string DatabaseName, string SchemaName, string SystemIdentifier,
        string LatestMigrationId, string Fingerprint);
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class StartupIdentityPostgresFactAttribute : FactAttribute
{
    public StartupIdentityPostgresFactAttribute()
        : this(Environment.GetEnvironmentVariable(PostgresDatabaseIdentityLiveTests.EnabledVariable))
    {
    }

    internal StartupIdentityPostgresFactAttribute(string? enabled)
    {
        if (StartupFencePostgresFixture.SkipWhenNotOptedIn(enabled))
        {
            Skip = $"Opt-in only: set {PostgresDatabaseIdentityLiveTests.EnabledVariable}=1 " +
                "after two independent, attested startup-fence fault proofs.";
        }
    }
}
