using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentenceStudio.Api.Tests.Startup;
using SentenceStudio.Contracts.Startup;
using SentenceStudio.Data;
using Xunit;

namespace SentenceStudio.Tests.Startup;

internal sealed class StartupGateCrossServiceProof : IAsyncDisposable
{
    internal const string EnabledVariable = "STARTUP_CROSS_SERVICE_PG_ENABLED";
    private const string OwnerRole = "startup_fence_owner";

    private readonly NpgsqlConnection _apiConnection;
    private readonly int _apiBackendPid;
    private readonly StartupFencePostgresConfiguration _apiConfiguration;
    private readonly StartupFencePostgresConfiguration _localConfiguration;
    private readonly StartupFenceDockerAttestation _apiDocker;
    private readonly StartupFenceDockerAttestation _localDocker;
    private readonly int _localBackendPid;

    private StartupGateCrossServiceProof(
        NpgsqlConnection apiConnection, int apiBackendPid,
        ApplicationDbContext localDb, NpgsqlConnection localConnection, int localBackendPid,
        StartupFencePostgresConfiguration apiConfiguration,
        StartupFencePostgresConfiguration localConfiguration,
        StartupFenceDockerAttestation apiDocker, StartupFenceDockerAttestation localDocker,
        string expectedMigrationId, string apiIdentity, string localIdentity)
    {
        _apiConnection = apiConnection;
        _apiBackendPid = apiBackendPid;
        LocalDb = localDb;
        LocalConnection = localConnection;
        _localBackendPid = localBackendPid;
        _apiConfiguration = apiConfiguration;
        _localConfiguration = localConfiguration;
        _apiDocker = apiDocker;
        _localDocker = localDocker;
        ExpectedMigrationId = expectedMigrationId;
        ApiIdentity = apiIdentity;
        LocalIdentity = localIdentity;
    }

    internal ApplicationDbContext LocalDb { get; }
    internal NpgsqlConnection LocalConnection { get; }
    internal string ExpectedMigrationId { get; }
    internal string ApiIdentity { get; }
    internal string LocalIdentity { get; }

    internal static async Task<StartupGateCrossServiceProof> OpenAsync()
    {
        RequireEnabled(Environment.GetEnvironmentVariable(EnabledVariable));
        var api = ReadSlot("A");
        var local = ReadSlot("B");
        RequireIndependentTargets(api, local);

        // Both independent Docker attestations MUST finish before either PostgreSQL socket opens.
        var apiDocker = await StartupFenceDockerAttestor.AttestAsync(api);
        var localDocker = await StartupFenceDockerAttestor.AttestAsync(local);
        Require(apiDocker.SystemIdentifier != localDocker.SystemIdentifier,
            "the independently attested clusters have the same physical server identifier");

        var localDb = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(local.Connection.ConnectionString).Options);
        NpgsqlConnection? apiConnection = null;
        try
        {
            var migrations = localDb.Database.GetMigrations().ToArray();
            Require(migrations.Length > 0, "the application has no compiled PostgreSQL migrations");
            var latestMigrationId = migrations[^1];

            apiConnection = new NpgsqlConnection(api.Connection.ConnectionString);
            try
            {
                await apiConnection.OpenAsync();
            }
            catch (NpgsqlException)
            {
                throw new InvalidOperationException(
                    "The Docker-attested API scratch database could not be opened.");
            }

            await SetSessionReadOnlyAsync(apiConnection);
            var apiBackendPid = apiConnection.ProcessID;
            var apiMarker = await AttestOpenedDatabaseAsync(
                apiConnection, api, apiDocker, migrations);
            var apiIdentity = await PostgresDatabaseIdentity.ReadAsync(
                apiConnection, latestMigrationId);
            Require(apiIdentity == ComputeIdentity(apiMarker, latestMigrationId) &&
                    apiConnection.State == ConnectionState.Open &&
                    apiConnection.ProcessID == apiBackendPid,
                "the API identity did not come from its continuously attested database backend");

            try
            {
                await localDb.Database.OpenConnectionAsync();
            }
            catch (NpgsqlException)
            {
                throw new InvalidOperationException(
                    "The Docker-attested local scratch database could not be opened.");
            }

            Require(localDb.Database.IsNpgsql() &&
                    localDb.Database.GetDbConnection() is NpgsqlConnection,
                "local readiness must use an opened Npgsql-backed ApplicationDbContext");
            var localConnection = (NpgsqlConnection)localDb.Database.GetDbConnection();
            await SetSessionReadOnlyAsync(localConnection);
            var localBackendPid = localConnection.ProcessID;
            var localMarker = await AttestOpenedDatabaseAsync(
                localConnection, local, localDocker, migrations);
            var localIdentity = await PostgresDatabaseIdentity.ReadAsync(
                localConnection, latestMigrationId);
            Require(localIdentity == ComputeIdentity(localMarker, latestMigrationId),
                "the local identity did not come from its attested, persisted database provenance");
            Require(localConnection.State == ConnectionState.Open &&
                    localConnection.ProcessID == localBackendPid,
                "the local PostgreSQL backend changed during fixture attestation");
            Require(apiIdentity != localIdentity,
                "the observed API and local fingerprints are equal; no mismatch is proven");

            return new StartupGateCrossServiceProof(
                apiConnection, apiBackendPid, localDb, localConnection, localBackendPid,
                api, local, apiDocker, localDocker, latestMigrationId, apiIdentity, localIdentity);
        }
        catch
        {
            if (apiConnection is not null)
            {
                await apiConnection.DisposeAsync();
            }
            await localDb.DisposeAsync();
            throw;
        }
    }

    internal void AssertLocalBackendUnchanged() =>
        Require(LocalConnection.State == ConnectionState.Open &&
                ReferenceEquals(LocalConnection, LocalDb.Database.GetDbConnection()) &&
                LocalConnection.ProcessID == _localBackendPid,
            "the local EF context changed PostgreSQL backends during readiness");

    internal async Task AssertClustersRemainAttestedAsync()
    {
        var apiDocker = await StartupFenceDockerAttestor.AttestAsync(_apiConfiguration);
        var localDocker = await StartupFenceDockerAttestor.AttestAsync(_localConfiguration);
        Require(apiDocker == _apiDocker && localDocker == _localDocker,
            "one of the inspected Docker clusters changed during the gate check");
        Require(_apiConnection.State == ConnectionState.Open &&
                _apiConnection.ProcessID == _apiBackendPid,
            "the API database backend disconnected during the gate check");
        AssertLocalBackendUnchanged();
        await AssertSessionReadOnlyAsync(_apiConnection);
        await AssertSessionReadOnlyAsync(LocalConnection);
        Require(await PostgresDatabaseIdentity.ReadAsync(
                    _apiConnection, ExpectedMigrationId) == ApiIdentity &&
                await PostgresDatabaseIdentity.ReadAsync(
                    LocalConnection, ExpectedMigrationId) == LocalIdentity,
            "one of the observed database identities changed during the gate check");
        Require(DateTimeOffset.UtcNow < apiDocker.VolumeCreatedAt.AddHours(1) &&
                DateTimeOffset.UtcNow < localDocker.VolumeCreatedAt.AddHours(1),
            "the disposable scratch volumes exceeded their one-hour evidence window");
    }

    internal void AssertNoConnectionDetailsIn(Exception failure)
    {
        var message = failure.ToString();
        var forbidden = new[]
        {
            _apiConfiguration.Connection.ConnectionString,
            _localConfiguration.Connection.ConnectionString,
            _apiConfiguration.Connection.Password,
            _localConfiguration.Connection.Password,
            _apiConfiguration.Connection.Host,
            _localConfiguration.Connection.Host,
            _apiConfiguration.Connection.Database,
            _localConfiguration.Connection.Database,
            _apiConfiguration.Connection.Username,
            _localConfiguration.Connection.Username
        };
        Require(!forbidden.OfType<string>().Where(value => value.Length > 0)
                .Any(value => message.Contains(value, StringComparison.Ordinal)),
            "the readiness rejection disclosed a raw host, database, role, or credential");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await LocalDb.DisposeAsync();
        }
        finally
        {
            await _apiConnection.DisposeAsync();
        }
    }

    internal static void RequireEnabled(string? enabled) =>
        Require(enabled == "1",
            $"{EnabledVariable} must be exactly 1; no Docker or PostgreSQL socket was opened");

    internal static StartupFencePostgresConfiguration RequireConfiguration(
        string? rawConnection, string? rawRunId, string? clusterName,
        string? volumeName, string? containerId)
    {
        Require(Guid.TryParseExact(rawRunId, "N", out var runId) &&
                runId != Guid.Empty &&
                rawRunId == runId.ToString("N") &&
                rawRunId[12] == '4' &&
                rawRunId[16] is '8' or '9' or 'a' or 'b' &&
                clusterName == $"sfence_{runId:N}" &&
                volumeName == $"sfence_pgdata_{runId:N}" &&
                containerId is { Length: 64 } &&
                containerId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "both scratch targets require exact run IDs, cluster/volume names and full container IDs; no Docker or PostgreSQL socket was opened");
        Require(!string.IsNullOrWhiteSpace(rawConnection),
            "the scratch connection is missing; no Docker or PostgreSQL socket was opened");

        NpgsqlConnectionStringBuilder connection;
        try
        {
            connection = new NpgsqlConnectionStringBuilder(rawConnection);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                "Invalid scratch connection configuration; no Docker or PostgreSQL socket was opened.");
        }

        Require(connection.Host == "127.0.0.1" &&
                connection.Port is > 1024 and not 5432 &&
                connection.Database == $"startup_fence_{runId:N}" &&
                connection.Username == OwnerRole &&
                !string.IsNullOrWhiteSpace(connection.Password),
            "scratch connections require the exact loopback port, run-specific database, owner role and password; no Docker or PostgreSQL socket was opened");

        connection.Pooling = false;
        connection.Multiplexing = false;
        connection.Enlist = false;
        connection.Timeout = 5;
        connection.CommandTimeout = 30;
        connection.SearchPath = "public";
        return new StartupFencePostgresConfiguration(
            connection, runId, clusterName!, volumeName!, containerId!);
    }

    internal static void RequireIndependentTargets(
        StartupFencePostgresConfiguration api, StartupFencePostgresConfiguration local) =>
        Require(api.RunId != local.RunId &&
                api.ClusterName != local.ClusterName &&
                api.VolumeName != local.VolumeName &&
                api.ContainerId != local.ContainerId &&
                api.Connection.Port != local.Connection.Port &&
                api.Connection.Database != local.Connection.Database,
            "A and B must have different run IDs, containers, volumes, loopback ports and databases; no Docker or PostgreSQL socket was opened");

    private static StartupFencePostgresConfiguration ReadSlot(string slot)
    {
        var prefix = $"STARTUP_IDENTITY_SCRATCH_PG_{slot}_";
        return RequireConfiguration(
            Environment.GetEnvironmentVariable(prefix + "CONNECTION"),
            Environment.GetEnvironmentVariable(prefix + "RUN_ID"),
            Environment.GetEnvironmentVariable(prefix + "CLUSTER_NAME"),
            Environment.GetEnvironmentVariable(prefix + "VOLUME_NAME"),
            Environment.GetEnvironmentVariable(prefix + "CONTAINER_ID"));
    }

    private static async Task<Marker> AttestOpenedDatabaseAsync(
        NpgsqlConnection connection, StartupFencePostgresConfiguration configuration,
        StartupFenceDockerAttestation docker, string[] expectedMigrations)
    {
        Require(connection.State == ConnectionState.Open &&
                connection.Host == configuration.Connection.Host &&
                connection.Port == configuration.Connection.Port &&
                connection.Database == configuration.Connection.Database &&
                connection.UserName == OwnerRole,
            "the opened PostgreSQL connection is not the Docker-attested scratch target");
        var backendPid = connection.ProcessID;

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
            Require(await shape.ExecuteScalarAsync() is true,
                "the pre-existing fixture-owned marker has the wrong table shape or owner");
        }

        Marker marker;
        await using (var command = connection.CreateCommand())
        {
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
            Require(await reader.ReadAsync(),
                "the fixture-owned marker must already have exactly one row");
            var databaseName = reader.GetString(1);
            Require(uint.TryParse(reader.GetString(2), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var databaseOid),
                "the observed database OID is invalid");
            var schemaName = reader.GetString(3);
            var sqlSystemIdentifier = reader.GetString(7);
            var postmasterStart = reader.GetString(8);
            var started = FromEpoch(postmasterStart);
            Require(reader.GetInt32(0) == backendPid &&
                    databaseName == configuration.Connection.Database &&
                    schemaName == "public" &&
                    reader.GetString(4) == OwnerRole &&
                    reader.GetString(5) == configuration.ClusterName &&
                    !reader.GetBoolean(6) &&
                    MatchesDockerSystemIdentifier(sqlSystemIdentifier, docker.SystemIdentifier) &&
                    reader.GetString(9) == StartupFenceDockerAttestor.PgData &&
                    reader.GetGuid(10) == configuration.RunId &&
                    reader.GetString(11) == configuration.ClusterName &&
                    reader.GetString(12) == databaseName &&
                    reader.GetString(13) == sqlSystemIdentifier &&
                    reader.GetString(14) == postmasterStart &&
                    started >= docker.ContainerStartedAt &&
                    started >= docker.ClusterInitializedAt &&
                    started <= docker.ClusterInitializedAt.AddMinutes(5) &&
                    FromEpoch(reader.GetString(15)) >= started &&
                    FromEpoch(reader.GetString(15)) <= DateTimeOffset.UtcNow.AddSeconds(2),
                "the persisted fixture marker does not belong to the inspected running cluster");
            Require(!await reader.ReadAsync(),
                "the fixture-owned marker must have exactly one row");
            marker = new Marker(databaseName, databaseOid, schemaName, sqlSystemIdentifier);
        }

        await using (var history = connection.CreateCommand())
        {
            history.CommandText = """
                SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId"
                """;
            await using var reader = await history.ExecuteReaderAsync();
            var appliedMigrations = new List<string>();
            while (await reader.ReadAsync())
            {
                appliedMigrations.Add(reader.GetString(0));
            }
            Require(expectedMigrations.SequenceEqual(appliedMigrations, StringComparer.Ordinal),
                "the scratch database does not contain the complete compiled migration history");
        }

        Require(connection.State == ConnectionState.Open && connection.ProcessID == backendPid,
            "the PostgreSQL backend changed during marker and migration-history reads");
        return marker;
    }

    private static async Task SetSessionReadOnlyAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SET default_transaction_read_only = on";
        await command.ExecuteNonQueryAsync();
        await AssertSessionReadOnlyAsync(connection);
    }

    private static async Task AssertSessionReadOnlyAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW default_transaction_read_only";
        Require(await command.ExecuteScalarAsync() is "on",
            "the scratch proof connection must remain session-read-only after its login audit");
    }

    private static string ComputeIdentity(Marker marker, string migrationId) =>
        PostgresDatabaseIdentity.ComputeFromAttestedValues(
            marker.DatabaseName, marker.DatabaseOid, marker.SchemaName,
            long.Parse(marker.SqlSystemIdentifier, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture),
            migrationId, migrationId);

    private static DateTimeOffset FromEpoch(string seconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(
            decimal.Parse(seconds, CultureInfo.InvariantCulture) * 1000m)));

    private static bool MatchesDockerSystemIdentifier(string signedSql, string unsignedDocker) =>
        long.TryParse(signedSql, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
            out var signed) &&
        ulong.TryParse(unsignedDocker, NumberStyles.None, CultureInfo.InvariantCulture,
            out var unsigned) &&
        signed != 0 && unsigned != 0 &&
        signedSql == signed.ToString(CultureInfo.InvariantCulture) &&
        unsignedDocker == unsigned.ToString(CultureInfo.InvariantCulture) &&
        unchecked((ulong)signed) == unsigned;

    private static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                $"Cross-service startup gate proof refused: {reason}.");
        }
    }

    private sealed record Marker(
        string DatabaseName, uint DatabaseOid, string SchemaName, string SqlSystemIdentifier);
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class StartupCrossServicePostgresFactAttribute : FactAttribute
{
    public StartupCrossServicePostgresFactAttribute()
        : this(Environment.GetEnvironmentVariable(StartupGateCrossServiceProof.EnabledVariable))
    {
    }

    internal StartupCrossServicePostgresFactAttribute(string? enabled)
    {
        if (enabled is null or "" or "0")
        {
            Skip = $"Opt-in only: set {StartupGateCrossServiceProof.EnabledVariable}=1 " +
                "with two still-running, separately attested startup-fence scratch clusters.";
        }
    }
}
