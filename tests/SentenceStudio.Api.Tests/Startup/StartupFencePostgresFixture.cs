using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SentenceStudio.Api.Auth;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Startup;

/// <summary>
/// The test runner never creates or drops a database. A fresh, labeled Docker volume and
/// container are independently inspected before connecting to PostgreSQL. Every database
/// in that one cluster is then inventoried before the first test-only SQL write.
/// Reusing a cluster after a run is refused; its audit remains inspectable.
/// Each replica's own open startup connection is checked against this monitor's cluster
/// identity and owner marker before the application obtains a lock or runs migrations.
/// </summary>
internal sealed class StartupFencePostgresFixture : IAsyncDisposable
{
    internal const string EnabledVariable = "STARTUP_FENCE_PG_TEST_ENABLED";
    internal const string ConnectionVariable = "STARTUP_FENCE_PG_TEST_CONNECTION";
    internal const string RunIdVariable = "STARTUP_FENCE_PG_TEST_RUN_ID";
    internal const string ClusterVariable = "STARTUP_FENCE_PG_TEST_CLUSTER_NAME";
    internal const string VolumeVariable = "STARTUP_FENCE_PG_TEST_VOLUME_NAME";
    internal const string ContainerVariable = "STARTUP_FENCE_PG_TEST_CONTAINER_ID";
    internal const string ScenarioVariable = "STARTUP_FENCE_PG_TEST_SCENARIO";

    private const string OwnerRole = "startup_fence_owner";
    private const long ReviewedCatalogObjectCount = 10510;
    private const string ReviewedCatalogSha256 =
        "776683e75782321241d096c2303caa6076ff1fe936ededd039ef8de1cfe0b02f";
    // Exact SELECT from two independently initialized clusters using the Docker-attested image.
    internal const string VirginCatalogDigestSql = """
        WITH catalog(schema_name, kind, definition) AS (
            SELECT n.nspname::text, 'namespace'::text, to_jsonb(n)
            FROM pg_namespace n
            UNION ALL
            SELECT n.nspname, 'relation',
                   to_jsonb(c) - ARRAY['relpages', 'reltuples', 'relallvisible',
                                       'relallfrozen', 'relfilenode', 'relfrozenxid', 'relminmxid']
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            UNION ALL
            SELECT n.nspname, 'attribute', to_jsonb(a)
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            UNION ALL
            SELECT n.nspname, 'index', to_jsonb(i)
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            UNION ALL
            SELECT n.nspname, 'routine', to_jsonb(p)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            UNION ALL
            SELECT n.nspname, 'type', to_jsonb(t)
            FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
            UNION ALL
            SELECT n.nspname, 'constraint', to_jsonb(c)
            FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace
            UNION ALL
            SELECT n.nspname, 'rewrite', to_jsonb(r)
            FROM pg_rewrite r
            JOIN pg_class c ON c.oid = r.ev_class
            JOIN pg_namespace n ON n.oid = c.relnamespace
            UNION ALL
            SELECT n.nspname, 'trigger', to_jsonb(t)
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            UNION ALL
            SELECT n.nspname, 'policy', to_jsonb(p)
            FROM pg_policy p
            JOIN pg_class c ON c.oid = p.polrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            UNION ALL
            SELECT n.nspname, 'extension', to_jsonb(e)
            FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace
            UNION ALL
            SELECT n.nspname, 'collation', to_jsonb(c)
            FROM pg_collation c JOIN pg_namespace n ON n.oid = c.collnamespace
            UNION ALL
            SELECT n.nspname, 'conversion', to_jsonb(c)
            FROM pg_conversion c JOIN pg_namespace n ON n.oid = c.connamespace
            UNION ALL
            SELECT n.nspname, 'operator', to_jsonb(o)
            FROM pg_operator o JOIN pg_namespace n ON n.oid = o.oprnamespace
            UNION ALL
            SELECT n.nspname, 'operator-class', to_jsonb(o)
            FROM pg_opclass o JOIN pg_namespace n ON n.oid = o.opcnamespace
            UNION ALL
            SELECT n.nspname, 'operator-family', to_jsonb(o)
            FROM pg_opfamily o JOIN pg_namespace n ON n.oid = o.opfnamespace
            UNION ALL
            SELECT n.nspname, 'text-config', to_jsonb(t)
            FROM pg_ts_config t JOIN pg_namespace n ON n.oid = t.cfgnamespace
            UNION ALL
            SELECT n.nspname, 'text-dictionary', to_jsonb(t)
            FROM pg_ts_dict t JOIN pg_namespace n ON n.oid = t.dictnamespace
            UNION ALL
            SELECT n.nspname, 'text-parser', to_jsonb(t)
            FROM pg_ts_parser t JOIN pg_namespace n ON n.oid = t.prsnamespace
            UNION ALL
            SELECT n.nspname, 'text-template', to_jsonb(t)
            FROM pg_ts_template t JOIN pg_namespace n ON n.oid = t.tmplnamespace
            UNION ALL
            SELECT n.nspname, 'extended-statistics', to_jsonb(s)
            FROM pg_statistic_ext s JOIN pg_namespace n ON n.oid = s.stxnamespace
            UNION ALL
            SELECT n.nspname, 'enum-label', to_jsonb(e)
            FROM pg_enum e
            JOIN pg_type t ON t.oid = e.enumtypid
            JOIN pg_namespace n ON n.oid = t.typnamespace
            UNION ALL
            SELECT n.nspname, 'range', to_jsonb(r)
            FROM pg_range r
            JOIN pg_type t ON t.oid = r.rngtypid
            JOIN pg_namespace n ON n.oid = t.typnamespace
            UNION ALL
            SELECT n.nspname, 'aggregate', to_jsonb(a)
            FROM pg_aggregate a
            JOIN pg_proc p ON p.oid = a.aggfnoid
            JOIN pg_namespace n ON n.oid = p.pronamespace
            UNION ALL
            SELECT n.nspname, 'cast', to_jsonb(c)
            FROM pg_cast c
            JOIN pg_type t ON t.oid = c.casttarget
            JOIN pg_namespace n ON n.oid = t.typnamespace
            UNION ALL
            SELECT 'pg_catalog', 'access-method', to_jsonb(a)
            FROM pg_am a
            UNION ALL
            SELECT 'pg_catalog', 'language', to_jsonb(l)
            FROM pg_language l
        )
        SELECT count(*) AS object_count,
               encode(sha256(convert_to(
                   string_agg(schema_name || ':' || kind || ':' || definition::text,
                              E'\n' ORDER BY schema_name, kind, definition::text),
                   'UTF8')), 'hex') AS catalog_sha256
        FROM catalog
        WHERE schema_name IN ('pg_catalog', 'pg_toast', 'information_schema', 'public');
        """;
    // PostgreSQL REL_17_10/src/backend/catalog/information_schema.sql, pinned by Docker image ID.
    internal static readonly IReadOnlySet<string> ReviewedInformationSchemaViews = Names("""
        _pg_foreign_data_wrappers _pg_foreign_servers _pg_foreign_table_columns
        _pg_foreign_tables _pg_user_mappings administrable_role_authorizations
        applicable_roles attributes character_sets check_constraint_routine_usage
        check_constraints collation_character_set_applicability collations
        column_column_usage column_domain_usage column_options column_privileges
        column_udt_usage columns constraint_column_usage constraint_table_usage
        data_type_privileges domain_constraints domain_udt_usage domains element_types
        enabled_roles foreign_data_wrapper_options foreign_data_wrappers
        foreign_server_options foreign_servers foreign_table_options foreign_tables
        information_schema_catalog_name key_column_usage parameters
        referential_constraints role_column_grants role_routine_grants role_table_grants
        role_udt_grants role_usage_grants routine_column_usage routine_privileges
        routine_routine_usage routine_sequence_usage routine_table_usage routines
        schemata sequences table_constraints table_privileges tables transforms
        triggered_update_columns triggers udt_privileges usage_privileges
        user_defined_types user_mapping_options user_mappings view_column_usage
        view_routine_usage view_table_usage views
        """);
    internal static readonly IReadOnlySet<string> ReviewedInformationSchemaTables = Names("""
        sql_features sql_implementation_info sql_parts sql_sizing
        """);
    internal static readonly IReadOnlySet<string> ReviewedInformationSchemaFunctions = Names("""
        _pg_char_max_length _pg_char_octet_length _pg_datetime_precision
        _pg_expandarray _pg_index_position _pg_interval_type _pg_numeric_precision
        _pg_numeric_precision_radix _pg_numeric_scale _pg_truetypid _pg_truetypmod
        """);
    internal static readonly IReadOnlySet<string> ReviewedInformationSchemaDomains = Names("""
        cardinal_number character_data sql_identifier time_stamp yes_or_no
        """);
    private static readonly IReadOnlySet<string> ReviewedInformationSchemaConstraints = Names("""
        cardinal_number_domain_check yes_or_no_check
        """);

    private static IReadOnlySet<string> Names(string items)
        => items.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

    private readonly NpgsqlConnectionStringBuilder _connection;
    private readonly NpgsqlConnection _monitor;
    private readonly Guid _runId;
    private readonly string _clusterName;
    private readonly StartupFenceDockerAttestation _docker;
    private readonly StartupFencePostgresScenario _scenario;
    private readonly Dictionary<StartupFenceBackend, string> _attestedFixtureBackends = [];
    private (string SystemIdentifier, string PostmasterStartEpoch)? _attestedCluster;
    private bool _ownerMarkerCreated;

    private StartupFencePostgresFixture(
        NpgsqlConnectionStringBuilder connection, NpgsqlConnection monitor,
        Guid runId, string clusterName, StartupFenceDockerAttestation docker,
        StartupFencePostgresScenario scenario)
    {
        _connection = connection;
        _monitor = monitor;
        _runId = runId;
        _clusterName = clusterName;
        _docker = docker;
        _scenario = scenario;
    }

    internal string Actor(string phase, string replica) => $"sf_{_runId:N}_{phase}_{replica}";
    internal StartupFenceDockerAttestation Docker => _docker;
    internal StartupFencePostgresScenario Scenario => _scenario;

    internal string ConnectionFor(string actor)
        => new NpgsqlConnectionStringBuilder(_connection.ConnectionString)
        {
            ApplicationName = actor
        }.ConnectionString;

    internal static async Task<StartupFencePostgresFixture> OpenAsync()
    {
        var configuration = RequireConfiguration(
            Environment.GetEnvironmentVariable(EnabledVariable),
            Environment.GetEnvironmentVariable(ConnectionVariable),
            Environment.GetEnvironmentVariable(RunIdVariable),
            Environment.GetEnvironmentVariable(ClusterVariable),
            Environment.GetEnvironmentVariable(VolumeVariable),
            Environment.GetEnvironmentVariable(ContainerVariable));
        var scenario = RequireScenario(Environment.GetEnvironmentVariable(ScenarioVariable));
        var docker = await StartupFenceDockerAttestor.AttestAsync(configuration);
        var connection = configuration.Connection;
        var monitor = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(connection.ConnectionString)
            {
                ApplicationName = "startup-fence-monitor"
            }.ConnectionString);

        var fixture = new StartupFencePostgresFixture(
            connection, monitor, configuration.RunId, configuration.ClusterName, docker, scenario);
        try
        {
            await monitor.OpenAsync();
            await fixture.AttestAsync(monitor, requireFreshDatabase: true);
            await fixture.CreateOwnerMarkerAsync();
            await fixture.AttestAsync(monitor);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal static bool SkipWhenNotOptedIn(string? enabled)
        => enabled is null or "" or "0";

    // pg_control_system()::text is signed bigint; pg_controldata prints the same bits unsigned.
    internal static bool MatchesDockerSystemIdentifier(string? signedSql, string? unsignedDocker)
    {
        if (!long.TryParse(signedSql, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var sql) ||
            !ulong.TryParse(unsignedDocker, NumberStyles.None,
                CultureInfo.InvariantCulture, out var docker) ||
            sql == 0 || docker == 0)
        {
            return false;
        }

        return signedSql == sql.ToString(CultureInfo.InvariantCulture) &&
            unsignedDocker == docker.ToString(CultureInfo.InvariantCulture) &&
            unchecked((ulong)sql) == docker;
    }

    internal static StartupFencePostgresScenario RequireScenario(string? raw)
        => raw switch
        {
            "baseline" => StartupFencePostgresScenario.Baseline,
            "phrase" => StartupFencePostgresScenario.Phrase,
            "coach" => StartupFencePostgresScenario.Coach,
            "feedback" => StartupFencePostgresScenario.Feedback,
            "devseed" => StartupFencePostgresScenario.DevSeed,
            _ => throw new InvalidOperationException(
                "STARTUP_FENCE_PG_TEST_SCENARIO must be exactly baseline, phrase, coach, " +
                "feedback, or devseed. No Docker or SQL was attempted.")
        };

    internal static void RequirePhaseTarget(string phase, string? targetId)
    {
        var valid = phase switch
        {
            "migration" or "coresync" or "coach" or "feedback" => targetId is null,
            "numberseed" or "backfill" or "phrase" =>
                !string.IsNullOrWhiteSpace(targetId),
            "devseed" => string.Equals(
                targetId,
                DevTestAccountSeeder.Accounts[0].Email.ToUpperInvariant(),
                StringComparison.Ordinal),
            _ => false
        };
        if (!valid)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence phase: unknown phase or missing/mismatched " +
                "server-side write target. No SQL was attempted.");
        }
    }

    internal static bool RequiresCoreSyncTakeoverCheckpoint(string phase)
        => phase switch
        {
            "coach" or "feedback" or "devseed" => true,
            "migration" or "numberseed" or "backfill" or "phrase" or "coresync" => false,
            _ => throw new InvalidOperationException(
                "Refusing a takeover checkpoint for an unknown startup writer.")
        };

    internal static StartupFencePostgresConfiguration RequireConfiguration(
        string? enabled, string? raw, string? rawRunId, string? clusterName,
        string? volumeName, string? containerId)
    {
        if (enabled != "1")
        {
            throw new InvalidOperationException(
                "STARTUP_FENCE_PG_TEST_ENABLED must be exactly 1 to run the fault test.");
        }

        var connection = ValidateConfiguration(raw, rawRunId, clusterName, out var runId);
        if (string.IsNullOrWhiteSpace(volumeName) ||
            volumeName != $"sfence_pgdata_{runId:N}" ||
            string.IsNullOrWhiteSpace(containerId) ||
            containerId.Length != 64 ||
            containerId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                "Startup-fence tests require the exact sfence_pgdata_<run-id> volume name " +
                "and full 64-character lowercase Docker container ID. No SQL was attempted.");
        }

        return new StartupFencePostgresConfiguration(
            connection, runId, clusterName!, volumeName, containerId);
    }

    internal static NpgsqlConnectionStringBuilder ValidateConfiguration(
        string? raw, string? rawRunId, string? clusterName, out Guid runId)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            !Guid.TryParseExact(rawRunId, "N", out runId) ||
            runId == Guid.Empty ||
            rawRunId != runId.ToString("N") ||
            rawRunId[12] != '4' ||
            rawRunId[16] is not ('8' or '9' or 'a' or 'b') ||
            !string.Equals(clusterName, $"sfence_{runId:N}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Startup-fence PostgreSQL attestation requires a connection, a new lowercase " +
                "N-format version-4 GUID, " +
                "and a matching sfence_<guid> cluster name. No database writes were attempted.");
        }

        NpgsqlConnectionStringBuilder connection;
        try
        {
            connection = new NpgsqlConnectionStringBuilder(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException("Invalid isolated startup-fence connection configuration.");
        }

        if (!string.Equals(connection.Host, "127.0.0.1", StringComparison.Ordinal) ||
            connection.Port is <= 1024 or 5432 ||
            !string.Equals(connection.Database, $"startup_fence_{runId:N}", StringComparison.Ordinal) ||
            !string.Equals(connection.Username, OwnerRole, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Startup-fence tests accept only 127.0.0.1 on a nondefault port, the dedicated " +
                "startup_fence_<guid> database, and its startup_fence_owner role. No fallback exists.");
        }

        connection.Pooling = false;
        connection.Multiplexing = false;
        connection.Timeout = 5;
        connection.CommandTimeout = 180;
        connection.Enlist = false;
        connection.SearchPath = "public";
        return connection;
    }

    internal void ValidateReplicaBeforeOpen(NpgsqlConnection connection, string actor)
    {
        if (!actor.StartsWith($"sf_{_runId:N}_", StringComparison.Ordinal) ||
            !(actor.EndsWith("_a", StringComparison.Ordinal) ||
              actor.EndsWith("_b", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Refusing startup-fence writes: the replica actor does not belong to this run.");
        }

        ValidateReplicaTarget(connection, _connection, actor);
    }

    internal static void ValidateReplicaTarget(
        NpgsqlConnection connection, NpgsqlConnectionStringBuilder approved, string actor)
    {
        if (connection.State != ConnectionState.Closed)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence writes: the replica must be checked before opening.");
        }

        var actual = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        if (actual.Host != approved.Host || actual.Port != approved.Port ||
            actual.Database != approved.Database || actual.Username != OwnerRole ||
            actual.ApplicationName != actor || actual.Pooling || actual.Multiplexing ||
            actual.Enlist || actual.SearchPath != "public")
        {
            throw new InvalidOperationException(
                "Refusing startup-fence writes: the actual replica target is not " +
                "the configured dedicated loopback database, role, and actor.");
        }
    }

    internal async Task<NpgsqlConnection> OpenConnectionAsync(string actor)
    {
        if (actor is not ("startup-fence-gate" or "startup-fence-killer"))
        {
            throw new InvalidOperationException(
                "Refusing startup-fence SQL: an unexpected auxiliary connection was requested.");
        }

        var connection = new NpgsqlConnection(ConnectionFor(actor));
        try
        {
            ValidateReplicaTarget(connection, _connection, actor);
            await connection.OpenAsync();
            var pid = connection.ProcessID;
            await AttestAsync(connection, expectedActor: actor);
            if (connection.State != ConnectionState.Open || connection.ProcessID != pid)
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence SQL: the attested auxiliary backend changed.");
            }
            await RegisterFixtureBackendAsync(connection, actor, requireAuditedLogin: true);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task RegisterFixtureBackendAsync(
        NpgsqlConnection connection, string actor, bool requireAuditedLogin)
    {
        if (_attestedCluster is null || !_ownerMarkerCreated)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence audit: fixture backend lacks independent cluster attestation.");
        }

        var backend = await ReadOwnBackendAsync(connection, actor);
        var logins = await ScalarAsync<long>(connection, """
            SELECT count(*) FROM public.startup_fence_audit
            WHERE kind = 'login' AND actor = $1
              AND backend_pid = $2 AND backend_start_epoch_us = $3
            """, actor, backend.Pid, backend.BackendStartMicroseconds);
        if (logins != (requireAuditedLogin ? 1 : 0) ||
            !_attestedFixtureBackends.TryAdd(backend, actor))
        {
            throw new InvalidOperationException(
                "Refusing startup-fence audit: fixture backend login or attestation is ambiguous.");
        }
    }

    internal async Task<StartupFenceBackend> ReadOwnBackendAsync(
        NpgsqlConnection connection, string actor)
    {
        if (connection.State != ConnectionState.Open || _attestedCluster is null ||
            !_ownerMarkerCreated)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence audit: the backend must be open and cluster-attested.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pid, (extract(epoch FROM backend_start) * 1000000)::bigint,
                   application_name, datname, backend_type
            FROM pg_stat_activity WHERE pid = pg_backend_pid()
            """;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() ||
            reader.GetInt32(0) != connection.ProcessID ||
            reader.IsDBNull(1) ||
            reader.GetString(2) != actor ||
            reader.GetString(3) != _connection.Database ||
            reader.GetString(4) != "client backend")
        {
            throw new InvalidOperationException(
                "Refusing startup-fence audit: the opened backend identity does not match its actor.");
        }
        var backend = new StartupFenceBackend(reader.GetInt32(0), reader.GetInt64(1));
        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                "Refusing startup-fence audit: the current backend identity is not unique.");
        }
        return backend;
    }

    internal async Task<int> AttestReplicaAsync(
        NpgsqlConnection connection, string actor, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open ||
            connection.Host != _connection.Host ||
            connection.Port != _connection.Port ||
            connection.Database != _connection.Database ||
            connection.UserName != OwnerRole ||
            _attestedCluster is null)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence writes: the opened replica is not the attested scratch target.");
        }

        var pid = connection.ProcessID;
        await AttestAsync(connection, expectedActor: actor, cancellationToken: cancellationToken);
        if (connection.State != ConnectionState.Open || connection.ProcessID != pid)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence writes: the replica backend changed during attestation.");
        }
        return pid;
    }

    internal async Task AttestAsync(
        NpgsqlConnection connection, bool requireFreshDatabase = false,
        string? expectedActor = null, CancellationToken cancellationToken = default)
    {
        if (!requireFreshDatabase && _attestedCluster is null)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence writes: the fixture monitor has not been attested.");
        }

        (string SystemIdentifier, string PostmasterStartEpoch) identity;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT current_database(), current_setting('cluster_name'),
                       current_setting('server_version_num')::integer, current_user,
                       (SELECT rolsuper FROM pg_roles WHERE rolname = current_user),
                       pg_is_in_recovery(), (SELECT system_identifier::text FROM pg_control_system()),
                       current_setting('event_triggers'),
                       pg_postmaster_start_time() >= clock_timestamp() - interval '2 hours',
                       to_regclass('public.startup_fence_owner') IS NOT NULL,
                       extract(epoch FROM pg_postmaster_start_time())::text,
                       current_setting('application_name'),
                       current_setting('data_directory')
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.GetString(0) != _connection.Database ||
                reader.GetString(1) != _clusterName ||
                reader.GetInt32(2) is < 170000 or >= 180000 ||
                reader.GetString(3) != OwnerRole ||
                !reader.GetBoolean(4) ||
                reader.GetBoolean(5) ||
                reader.GetString(7) != "on" ||
                !reader.GetBoolean(8) ||
                reader.GetBoolean(9) != _ownerMarkerCreated ||
                reader.IsDBNull(6) ||
                !MatchesDockerSystemIdentifier(reader.GetString(6), _docker.SystemIdentifier) ||
                reader.GetString(12) != StartupFenceDockerAttestor.PgData ||
                (expectedActor is not null && reader.GetString(11) != expectedActor))
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence writes: PostgreSQL 17, a fresh primary cluster, " +
                    "superuser, event triggers, and the exact scratch database/replica actor are required.");
            }

            identity = (reader.GetString(6), reader.GetString(10));
            var started = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(
                decimal.Parse(identity.PostmasterStartEpoch, CultureInfo.InvariantCulture) *
                1000m)));
            if (started < _docker.ClusterInitializedAt ||
                started < _docker.ContainerStartedAt ||
                started > _docker.ClusterInitializedAt.AddMinutes(5) ||
                started > DateTimeOffset.UtcNow.AddSeconds(2))
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence writes: the PostgreSQL postmaster did not start " +
                    "from the freshly initialized, inspected Docker PGDATA.");
            }
            if (_attestedCluster is { } expected && identity != expected)
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence writes: the replica is not on the monitor's " +
                    "original PostgreSQL system and postmaster instance.");
            }
        }

        if (!_ownerMarkerCreated)
        {
            if (!requireFreshDatabase)
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence writes: the scratch owner marker was not created.");
            }
            await AssertFreshClusterCatalogAsync(
                connection, identity.PostmasterStartEpoch, cancellationToken);
            _attestedCluster = identity;
            return;
        }

        try
        {
            await using var markerShape = connection.CreateCommand();
            markerShape.CommandText = """
                SELECT count(*) = 6 AND count(*) FILTER (WHERE a.attnotnull AND (
                    (a.attname = 'run_id' AND a.atttypid = 'uuid'::regtype) OR
                    (a.attname IN ('cluster_name', 'database_name', 'system_identifier')
                        AND a.atttypid = 'text'::regtype) OR
                    (a.attname IN ('cluster_started_at', 'created_at')
                        AND a.atttypid = 'timestamptz'::regtype)
                )) = 6
                FROM pg_attribute a
                WHERE a.attrelid = to_regclass('public.startup_fence_owner')
                  AND a.attnum > 0 AND NOT a.attisdropped
                """;
            if (await markerShape.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence writes: the operator marker schema is not exact.");
            }

            await using var marker = connection.CreateCommand();
            marker.CommandText = """
                SELECT count(*) = 1 AND count(*) FILTER (
                    WHERE run_id = $1 AND cluster_name = $2
                      AND database_name = current_database()
                      AND system_identifier = (SELECT system_identifier::text FROM pg_control_system())
                      AND cluster_started_at = pg_postmaster_start_time()
                      AND created_at >= $3
                      AND created_at >= pg_postmaster_start_time()
                      AND created_at BETWEEN clock_timestamp() - interval '2 hours'
                                         AND clock_timestamp()
                ) = 1
                FROM public.startup_fence_owner
                """;
            marker.Parameters.AddWithValue(_runId);
            marker.Parameters.AddWithValue(_clusterName);
            marker.Parameters.AddWithValue(_docker.ClusterInitializedAt);
            if (await marker.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence writes: the per-run owner marker does not attest " +
                    "this exact cluster, database, start time, and PostgreSQL system identifier.");
            }
        }
        catch (PostgresException ex)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence writes: owner marker schema is absent or malformed.", ex);
        }

        if (requireFreshDatabase)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence preflight: the owner marker already exists.");
        }
    }

    private async Task CreateOwnerMarkerAsync()
    {
        if (_attestedCluster is not { } identity || _ownerMarkerCreated ||
            _monitor.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence writes: the read-only Docker and database preflight did not pass.");
        }

        await using var transaction = await _monitor.BeginTransactionAsync();
        await using var create = _monitor.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = """
            CREATE TABLE public.startup_fence_owner (
                run_id uuid NOT NULL,
                cluster_name text NOT NULL,
                database_name text NOT NULL,
                system_identifier text NOT NULL,
                cluster_started_at timestamptz NOT NULL,
                created_at timestamptz NOT NULL
            )
            """;
        await create.ExecuteNonQueryAsync();

        await using var insert = _monitor.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO public.startup_fence_owner
                (run_id, cluster_name, database_name, system_identifier,
                 cluster_started_at, created_at)
            SELECT $1, $2, current_database(), $3,
                   pg_postmaster_start_time(), clock_timestamp()
            WHERE current_database() = $4
              AND current_setting('cluster_name') = $2
              AND (SELECT system_identifier::text FROM pg_control_system()) = $3
              AND extract(epoch FROM pg_postmaster_start_time())::text = $5
              AND current_user = 'startup_fence_owner'
            """;
        insert.Parameters.AddWithValue(_runId);
        insert.Parameters.AddWithValue(_clusterName);
        insert.Parameters.AddWithValue(identity.SystemIdentifier);
        insert.Parameters.AddWithValue(_connection.Database);
        insert.Parameters.AddWithValue(identity.PostmasterStartEpoch);
        if (await insert.ExecuteNonQueryAsync() != 1)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence writes: the target changed after read-only preflight.");
        }

        await transaction.CommitAsync();
        _ownerMarkerCreated = true;
    }

    private async Task AssertFreshClusterCatalogAsync(
        NpgsqlConnection monitor, string postmasterStartEpoch,
        CancellationToken cancellationToken)
    {
        await using var databases = monitor.CreateCommand();
        databases.CommandText = """
            SELECT datname, datistemplate, datallowconn,
                   pg_get_userbyid(datdba)
            FROM pg_database ORDER BY datname
            """;
        var discovered = new List<(string Name, bool Template, bool Connectable, string Owner)>();
        await using (var reader = await databases.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                discovered.Add((
                    reader.GetString(0), reader.GetBoolean(1),
                    reader.GetBoolean(2), reader.GetString(3)));
            }
        }

        var expectedNames = new[] { _connection.Database, "postgres", "template0", "template1" };
        if (discovered.Count != expectedNames.Length ||
            !discovered.Select(row => row.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(expectedNames.OrderBy(name => name, StringComparer.Ordinal)) ||
            discovered.Any(row =>
                row.Connectable != (row.Name != "template0") ||
                row.Owner != OwnerRole ||
                row.Template != (row.Name is "template0" or "template1")))
        {
            throw new InvalidOperationException(
                "Refusing startup-fence writes: the cluster must contain exactly the scratch " +
                "database, postgres, and default template0/template1. " +
                "template0 must remain inaccessible; provision a new cluster.");
        }

        IReadOnlyList<StartupFenceCatalogObject>? baseline = null;
        foreach (var database in expectedNames)
        {
            if (database == "template0")
            {
                continue;
            }

            IReadOnlyList<StartupFenceCatalogObject> objects;
            if (database == _connection.Database)
            {
                objects = await AssertDatabaseCatalogAsync(
                    monitor, database, postmasterStartEpoch, cancellationToken);
            }
            else
            {
                await using var census = new NpgsqlConnection(
                    new NpgsqlConnectionStringBuilder(_connection.ConnectionString)
                    {
                        Database = database,
                        ApplicationName = "startup-fence-census"
                    }.ConnectionString);
                await census.OpenAsync(cancellationToken);
                objects = await AssertDatabaseCatalogAsync(
                    census, database, postmasterStartEpoch, cancellationToken);
            }

            if (baseline is null)
            {
                ValidateSystemCatalogSnapshot(objects, objects);
                baseline = objects;
            }
            else
            {
                ValidateSystemCatalogSnapshot(baseline, objects);
            }
        }
    }

    private async Task<IReadOnlyList<StartupFenceCatalogObject>> AssertDatabaseCatalogAsync(
        NpgsqlConnection connection, string database, string postmasterStartEpoch,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await using (var readOnly = connection.CreateCommand())
        {
            readOnly.Transaction = transaction;
            readOnly.CommandText = "SET TRANSACTION READ ONLY";
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var identity = connection.CreateCommand();
        identity.Transaction = transaction;
        identity.CommandText = """
            SELECT current_database(), current_setting('cluster_name'),
                   (SELECT system_identifier::text FROM pg_control_system()),
                   current_setting('data_directory'), current_user, pg_is_in_recovery(),
                   extract(epoch FROM pg_postmaster_start_time())::text
            """;
        await using (var reader = await identity.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.GetString(0) != database ||
                reader.GetString(1) != _clusterName ||
                reader.IsDBNull(2) ||
                !MatchesDockerSystemIdentifier(reader.GetString(2), _docker.SystemIdentifier) ||
                reader.GetString(3) != StartupFenceDockerAttestor.PgData ||
                reader.GetString(4) != OwnerRole ||
                reader.GetBoolean(5) ||
                reader.GetString(6) != postmasterStartEpoch)
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence writes: a cluster database differs from " +
                    "the independently inspected PostgreSQL PGDATA.");
            }
        }

        await using var sessions = connection.CreateCommand();
        sessions.Transaction = transaction;
        sessions.CommandText = """
            SELECT count(*) FROM pg_stat_activity
            WHERE backend_type = 'client backend'
              AND pid <> $1 AND pid <> pg_backend_pid()
            """;
        sessions.Parameters.AddWithValue(_monitor.ProcessID);
        if (await sessions.ExecuteScalarAsync(cancellationToken) is not long count ||
            count != 0)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence writes: an unrelated client is connected to the cluster.");
        }

        await using var objects = connection.CreateCommand();
        objects.Transaction = transaction;
        objects.CommandText = """
            SELECT
              (SELECT count(*) FROM pg_namespace
               WHERE nspname NOT IN ('pg_catalog', 'pg_toast', 'information_schema', 'public')),
              (SELECT count(*) FROM pg_class c
               JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE n.nspname = 'public'),
              (SELECT count(*) FROM pg_proc p
               JOIN pg_namespace n ON n.oid = p.pronamespace
               WHERE n.nspname = 'public'),
              (SELECT count(*) FROM pg_type t
               JOIN pg_namespace n ON n.oid = t.typnamespace
               WHERE n.nspname = 'public'),
              (SELECT count(*) FROM pg_extension
               WHERE extname <> 'plpgsql' OR extnamespace <> 'pg_catalog'::regnamespace),
              (SELECT count(*) FROM pg_event_trigger),
              (SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal),
              (SELECT count(*) FROM pg_constraint
               WHERE connamespace = 'public'::regnamespace),
              (SELECT count(*) FROM pg_rewrite r
               JOIN pg_class c ON c.oid = r.ev_class
               WHERE c.relnamespace = 'public'::regnamespace),
              (SELECT count(*) FROM pg_foreign_server),
              (SELECT count(*) FROM pg_foreign_data_wrapper),
              (SELECT count(*) FROM pg_user_mapping),
              (SELECT count(*) FROM pg_largeobject_metadata),
              (SELECT count(*) FROM pg_publication),
              (SELECT count(*) FROM pg_subscription),
              (SELECT count(*) FROM pg_collation
               WHERE collnamespace = 'public'::regnamespace),
              (SELECT count(*) FROM pg_conversion
               WHERE connamespace = 'public'::regnamespace),
              (SELECT count(*) FROM pg_operator
               WHERE oprnamespace = 'public'::regnamespace),
              (SELECT count(*) FROM pg_opclass
               WHERE opcnamespace = 'public'::regnamespace),
              (SELECT count(*) FROM pg_opfamily
               WHERE opfnamespace = 'public'::regnamespace),
              (SELECT count(*) FROM pg_ts_config
               WHERE cfgnamespace = 'public'::regnamespace),
              (SELECT count(*) FROM pg_ts_dict
               WHERE dictnamespace = 'public'::regnamespace),
              (SELECT count(*) FROM pg_ts_parser
               WHERE prsnamespace = 'public'::regnamespace),
              (SELECT count(*) FROM pg_ts_template
               WHERE tmplnamespace = 'public'::regnamespace),
              (SELECT count(*) FROM pg_statistic_ext
               WHERE stxnamespace = 'public'::regnamespace),
              (SELECT count(*) FROM pg_transform),
              -- PG17's FirstNormalObjectId is 16384; genbki assigns built-in
              -- casts below it. Reject any later user-created cast.
              (SELECT count(*) FROM pg_cast WHERE oid >= 16384),
              (SELECT count(*) FROM pg_am
               WHERE amname NOT IN
                 ('heap', 'btree', 'hash', 'gist', 'gin', 'spgist', 'brin')),
              (SELECT count(*) FROM pg_seclabel),
              (SELECT count(*) FROM pg_shseclabel),
              (SELECT count(*) FROM pg_policy),
              (SELECT count(*) FROM pg_default_acl),
              (SELECT count(*) FROM pg_db_role_setting),
              (SELECT count(*) FROM pg_language
               WHERE lanname NOT IN ('internal', 'c', 'sql', 'plpgsql')),
              (SELECT count(*) FROM pg_tablespace
               WHERE spcname NOT IN ('pg_default', 'pg_global')),
              (SELECT count(*) FROM pg_replication_slots),
              (SELECT count(*) FROM pg_replication_origin),
              (SELECT count(*) FROM pg_prepared_xacts),
              (SELECT count(*) FROM pg_roles
               WHERE rolname <> 'startup_fence_owner' AND rolname !~ '^pg_')
            """;
        await using var objectReader = await objects.ExecuteReaderAsync(cancellationToken);
        if (!await objectReader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Refusing startup-fence writes: the cluster object census returned no results.");
        }
        for (var column = 0; column < objectReader.FieldCount; column++)
        {
            if (objectReader.GetInt64(column) != 0)
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence writes: a database contains existing user " +
                    "objects, extensions, roles, slots, or prior test artifacts.");
            }
        }
        await objectReader.DisposeAsync();

        var snapshot = await ReadSystemCatalogSnapshotAsync(
            connection, transaction, cancellationToken);
        await AssertVirginCatalogDigestAsync(
            connection, transaction, database, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return snapshot;
    }

    private static async Task<IReadOnlyList<StartupFenceCatalogObject>> ReadSystemCatalogSnapshotAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT kind, schema_name, object_id, object_name
            FROM (
                SELECT 'relation'::text AS kind, n.nspname AS schema_name,
                       c.oid::bigint AS object_id, c.relname || ':' || c.relkind::text AS object_name
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                UNION ALL
                SELECT 'routine', n.nspname, p.oid::bigint, p.proname
                FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                UNION ALL
                SELECT 'type', n.nspname, t.oid::bigint, t.typname
                FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
                UNION ALL
                SELECT 'constraint', n.nspname, c.oid::bigint, c.conname
                FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace
                UNION ALL
                SELECT 'rewrite', n.nspname, r.oid::bigint, r.rulename
                FROM pg_rewrite r JOIN pg_class c ON c.oid = r.ev_class
                                  JOIN pg_namespace n ON n.oid = c.relnamespace
                UNION ALL
                SELECT 'trigger', n.nspname, t.oid::bigint, t.tgname
                FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
                                  JOIN pg_namespace n ON n.oid = c.relnamespace
                UNION ALL
                SELECT 'policy', n.nspname, p.oid::bigint, p.polname
                FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
                                 JOIN pg_namespace n ON n.oid = c.relnamespace
                UNION ALL
                SELECT 'extension', n.nspname, e.oid::bigint, e.extname
                FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace
                UNION ALL
                SELECT 'collation', n.nspname, c.oid::bigint, c.collname
                FROM pg_collation c JOIN pg_namespace n ON n.oid = c.collnamespace
                UNION ALL
                SELECT 'conversion', n.nspname, c.oid::bigint, c.conname
                FROM pg_conversion c JOIN pg_namespace n ON n.oid = c.connamespace
                UNION ALL
                SELECT 'operator', n.nspname, o.oid::bigint, o.oprname
                FROM pg_operator o JOIN pg_namespace n ON n.oid = o.oprnamespace
                UNION ALL
                SELECT 'operator-class', n.nspname, o.oid::bigint, o.opcname
                FROM pg_opclass o JOIN pg_namespace n ON n.oid = o.opcnamespace
                UNION ALL
                SELECT 'operator-family', n.nspname, o.oid::bigint, o.opfname
                FROM pg_opfamily o JOIN pg_namespace n ON n.oid = o.opfnamespace
                UNION ALL
                SELECT 'text-config', n.nspname, t.oid::bigint, t.cfgname
                FROM pg_ts_config t JOIN pg_namespace n ON n.oid = t.cfgnamespace
                UNION ALL
                SELECT 'text-dictionary', n.nspname, t.oid::bigint, t.dictname
                FROM pg_ts_dict t JOIN pg_namespace n ON n.oid = t.dictnamespace
                UNION ALL
                SELECT 'text-parser', n.nspname, t.oid::bigint, t.prsname
                FROM pg_ts_parser t JOIN pg_namespace n ON n.oid = t.prsnamespace
                UNION ALL
                SELECT 'text-template', n.nspname, t.oid::bigint, t.tmplname
                FROM pg_ts_template t JOIN pg_namespace n ON n.oid = t.tmplnamespace
                UNION ALL
                SELECT 'extended-statistics', n.nspname, s.oid::bigint, s.stxname
                FROM pg_statistic_ext s JOIN pg_namespace n ON n.oid = s.stxnamespace
            ) catalog
            WHERE schema_name IN ('pg_catalog', 'pg_toast', 'information_schema')
            ORDER BY kind, schema_name, object_id, object_name
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var objects = new List<StartupFenceCatalogObject>();
        while (await reader.ReadAsync(cancellationToken))
        {
            objects.Add(new StartupFenceCatalogObject(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
        }
        return objects;
    }

    private static async Task AssertVirginCatalogDigestAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string database, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = VirginCatalogDigestSql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.FieldCount != 2 ||
            reader.IsDBNull(0))
        {
            throw new InvalidOperationException(
                $"Refusing startup-fence writes: {database} returned no valid catalog digest.");
        }

        var objectCount = reader.GetInt64(0);
        var catalogSha256 = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Refusing startup-fence writes: {database} returned multiple catalog digests.");
        }
        ValidateVirginCatalogDigest(database, objectCount, catalogSha256);
    }

    internal static void ValidateVirginCatalogDigest(
        string database, long objectCount, string? catalogSha256)
    {
        if (objectCount != ReviewedCatalogObjectCount)
        {
            throw new InvalidOperationException(
                $"Refusing startup-fence writes: {database} catalog has {objectCount} objects; " +
                $"the reviewed PostgreSQL 17.10 image requires {ReviewedCatalogObjectCount}.");
        }
        if (catalogSha256 is null || catalogSha256.Length != 64 ||
            catalogSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                $"Refusing startup-fence writes: {database} catalog SHA-256 is not " +
                "64 lowercase hexadecimal characters.");
        }
        if (!string.Equals(catalogSha256, ReviewedCatalogSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing startup-fence writes: {database} catalog definition SHA-256 " +
                $"is {catalogSha256}, not the reviewed image digest {ReviewedCatalogSha256}.");
        }
    }

    internal static void ValidateSystemCatalogSnapshot(
        IReadOnlyList<StartupFenceCatalogObject> baseline,
        IReadOnlyList<StartupFenceCatalogObject> actual)
    {
        var info = baseline.Where(item => item.Schema == "information_schema").ToArray();
        var expectedRelations = ReviewedInformationSchemaViews
            .Select(name => name + ":v")
            .Concat(ReviewedInformationSchemaTables.Select(name => name + ":r"))
            .ToHashSet(StringComparer.Ordinal);
        var baseTypes = ReviewedInformationSchemaViews
            .Concat(ReviewedInformationSchemaTables)
            .Concat(ReviewedInformationSchemaDomains)
            .ToArray();
        var expectedTypes = baseTypes.Concat(baseTypes.Select(name => "_" + name))
            .ToHashSet(StringComparer.Ordinal);
        var informationSchemaIsPristine =
            info.Where(item => item.Kind == "relation").Select(item => item.Name)
                .ToHashSet(StringComparer.Ordinal).SetEquals(expectedRelations) &&
            info.Where(item => item.Kind == "routine").Select(item => item.Name)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(ReviewedInformationSchemaFunctions) &&
            info.Where(item => item.Kind == "type").Select(item => item.Name)
                .ToHashSet(StringComparer.Ordinal).SetEquals(expectedTypes) &&
            info.Where(item => item.Kind == "constraint").Select(item => item.Name)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(ReviewedInformationSchemaConstraints) &&
            info.Count(item => item.Kind == "rewrite" && item.Name == "_RETURN") ==
                ReviewedInformationSchemaViews.Count &&
            info.All(item => item.Kind is
                ("relation" or "routine" or "type" or "constraint" or "rewrite")) &&
            info.GroupBy(item => (item.Kind, item.Name))
                .All(group => group.Key.Kind == "rewrite" || group.Count() == 1);

        var knownToastParents = info
            .Where(item => item.Kind == "relation" &&
                           item.Name.EndsWith(":r", StringComparison.Ordinal) &&
                           ReviewedInformationSchemaTables.Contains(item.Name[..^2]))
            .Select(item => $"pg_toast_{item.Oid}")
            .ToHashSet(StringComparer.Ordinal);
        if (baseline.Count == 0 ||
            new[] { "pg_catalog", "pg_toast", "information_schema" }
                .Any(schema => !baseline.Any(item => item.Schema == schema)) ||
            !informationSchemaIsPristine ||
            baseline.Any(item =>
                item.Schema is not ("pg_catalog" or "pg_toast" or "information_schema") ||
                (item.Schema == "pg_catalog" && item.Oid >= 16384) ||
                (item.Schema == "pg_toast" && item.Oid >= 16384 &&
                 !knownToastParents.Any(parent => item.Kind switch
                 {
                     "relation" => item.Name == parent + ":t" ||
                                   item.Name == parent + "_index:i",
                     "type" => item.Name == parent || item.Name == "_" + parent,
                     _ => false
                 }))) ||
            !baseline.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                "Refusing startup-fence writes: permitted non-public schemas contain " +
                "unknown or pre-existing objects compared with the pristine cluster databases.");
        }
    }

    internal const string AttachDmlAuditFunctionSql = """
        CREATE FUNCTION public.startup_fence_attach_dml_audit()
        RETURNS event_trigger LANGUAGE plpgsql AS $function$
        DECLARE target record;
        BEGIN
            FOR target IN
                SELECT DISTINCT n.nspname, c.relname
                FROM pg_event_trigger_ddl_commands() ddl
                JOIN pg_class c ON ddl.classid = 'pg_class'::regclass
                               AND c.oid = ddl.objid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p')
                  AND (NOT starts_with(n.nspname, 'pg_')
                       OR starts_with(n.nspname, 'pg_temp_'))
                  AND n.nspname <> 'information_schema'
                  AND NOT (n.nspname = 'public' AND c.relname = 'startup_fence_audit')
            LOOP
                IF NOT EXISTS (
                    SELECT 1 FROM pg_trigger
                    WHERE tgrelid = format('%I.%I', target.nspname, target.relname)::regclass
                      AND tgname = 'startup_fence_dml'
                ) THEN
                    EXECUTE format(
                        'CREATE TRIGGER startup_fence_dml AFTER INSERT OR UPDATE OR DELETE ' ||
                        'ON %I.%I FOR EACH ROW EXECUTE FUNCTION public.startup_fence_dml_audit()',
                        target.nspname, target.relname);
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM pg_trigger
                    WHERE tgrelid = format('%I.%I', target.nspname, target.relname)::regclass
                      AND tgname = 'startup_fence_truncate'
                ) THEN
                    EXECUTE format(
                        'CREATE TRIGGER startup_fence_truncate AFTER TRUNCATE ' ||
                        'ON %I.%I FOR EACH STATEMENT ' ||
                        'EXECUTE FUNCTION public.startup_fence_dml_audit()',
                        target.nspname, target.relname);
                END IF;
                IF target.nspname = 'public'
                   AND target.relname IN
                       ('VocabularyWord', 'NumberContext', 'PhraseConstituent', 'AspNetUsers')
                   AND NOT EXISTS (
                       SELECT 1 FROM pg_trigger
                       WHERE tgrelid = format('%I.%I', target.nspname, target.relname)::regclass
                         AND tgname = 'startup_fence_row_gate'
                   ) THEN
                    EXECUTE format(
                        'CREATE TRIGGER startup_fence_row_gate BEFORE INSERT OR UPDATE ' ||
                        'ON %I.%I FOR EACH ROW EXECUTE FUNCTION public.startup_fence_row_gate()',
                        target.nspname, target.relname);
                END IF;
                IF EXISTS (
                    SELECT 1 FROM pg_trigger
                    WHERE tgrelid = format('%I.%I', target.nspname, target.relname)::regclass
                      AND tgname = 'startup_fence_dml'
                      AND tgenabled <> 'A'
                ) THEN
                    EXECUTE format('ALTER TABLE %I.%I ENABLE ALWAYS TRIGGER startup_fence_dml',
                        target.nspname, target.relname);
                END IF;
                IF EXISTS (
                    SELECT 1 FROM pg_trigger
                    WHERE tgrelid = format('%I.%I', target.nspname, target.relname)::regclass
                      AND tgname = 'startup_fence_truncate'
                      AND tgenabled <> 'A'
                ) THEN
                    EXECUTE format('ALTER TABLE %I.%I ENABLE ALWAYS TRIGGER startup_fence_truncate',
                        target.nspname, target.relname);
                END IF;
                IF target.nspname = 'public'
                   AND target.relname IN
                       ('VocabularyWord', 'NumberContext', 'PhraseConstituent', 'AspNetUsers')
                   AND EXISTS (
                       SELECT 1 FROM pg_trigger
                       WHERE tgrelid = format('%I.%I', target.nspname, target.relname)::regclass
                         AND tgname = 'startup_fence_row_gate'
                         AND tgenabled <> 'A'
                   ) THEN
                    EXECUTE format('ALTER TABLE %I.%I ENABLE ALWAYS TRIGGER startup_fence_row_gate',
                        target.nspname, target.relname);
                END IF;
            END LOOP;
        END;
        $function$
        """;

    internal const string RowGateFunctionSql = """
        CREATE FUNCTION public.startup_fence_row_gate()
        RETURNS trigger LANGUAGE plpgsql AS $function$
        DECLARE gate_row record;
                row_data jsonb := to_jsonb(NEW);
        BEGIN
            SELECT phase, target_id, lock_key INTO gate_row
            FROM public.startup_fence_gate
            WHERE actor = current_setting('application_name', true);
            IF NOT FOUND OR tg_table_schema <> 'public' THEN
                RETURN NEW;
            END IF;
            IF (gate_row.phase = 'numberseed' AND tg_table_name = 'NumberContext'
                    AND tg_op = 'UPDATE' AND row_data->>'Id' = gate_row.target_id
                    AND to_jsonb(OLD)->>'DisplayName' IS DISTINCT FROM row_data->>'DisplayName')
                OR (gate_row.phase = 'backfill' AND tg_table_name = 'VocabularyWord'
                    AND tg_op = 'UPDATE' AND row_data->>'Id' = gate_row.target_id
                    AND to_jsonb(OLD)->>'LexicalUnitType' = '0'
                    AND row_data->>'LexicalUnitType' = '2')
                OR (gate_row.phase = 'phrase' AND tg_table_name = 'PhraseConstituent'
                    AND tg_op = 'INSERT' AND row_data->>'PhraseWordId' = gate_row.target_id
                    AND row_data->>'ConstituentWordId' IS NOT NULL)
                OR (gate_row.phase = 'devseed' AND tg_table_name = 'AspNetUsers'
                    AND tg_op = 'INSERT'
                    AND row_data->>'NormalizedUserName' = gate_row.target_id) THEN
                PERFORM pg_advisory_lock(gate_row.lock_key);
                PERFORM pg_advisory_unlock(gate_row.lock_key);
            END IF;
            RETURN NEW;
        END;
        $function$
        """;

    internal const string DdlEndGateFunctionSql = """
        CREATE FUNCTION public.startup_fence_ddl_gate()
        RETURNS event_trigger LANGUAGE plpgsql AS $function$
        DECLARE gate_row record;
                gate_key bigint;
        BEGIN
            SELECT phase, lock_key, takeover_lock_key INTO gate_row
            FROM public.startup_fence_gate
            WHERE actor = current_setting('application_name', true);
            IF NOT FOUND THEN
                RETURN;
            END IF;
            IF (gate_row.phase = 'migration' AND tg_tag = 'CREATE TABLE'
                AND EXISTS (
                    SELECT 1 FROM pg_event_trigger_ddl_commands() ddl
                    WHERE ddl.classid = 'pg_class'::regclass
                      AND ddl.schema_name = 'public'
                      AND ddl.objid <> to_regclass('public."__EFMigrationsHistory"')::oid
                      AND NOT starts_with(ddl.object_identity, 'public.startup_fence_')))
                OR (gate_row.phase = 'coach' AND tg_tag = 'CREATE TABLE'
                    AND EXISTS (
                        SELECT 1 FROM pg_event_trigger_ddl_commands() ddl
                        WHERE ddl.classid = 'pg_class'::regclass
                          AND ddl.objid = to_regclass(
                              'public."__CoachMigrationsHistory"')::oid))
                OR (gate_row.phase = 'feedback' AND tg_tag = 'CREATE TABLE'
                    AND EXISTS (
                        SELECT 1 FROM pg_event_trigger_ddl_commands() ddl
                        WHERE ddl.classid = 'pg_class'::regclass
                          AND ddl.objid = to_regclass(
                              'public."__FeedbackMigrationsHistory"')::oid)) THEN
                gate_key := gate_row.lock_key;
            ELSIF (gate_row.phase = 'coresync' OR gate_row.takeover_lock_key IS NOT NULL)
                AND tg_tag IN ('CREATE FUNCTION', 'CREATE TRIGGER')
                AND EXISTS (
                    SELECT 1 FROM pg_event_trigger_ddl_commands() ddl
                    WHERE ddl.schema_name = 'public'
                      AND (
                          (ddl.classid = 'pg_proc'::regclass
                           AND position('_ct_' in ddl.object_identity) > 0)
                          OR (ddl.classid = 'pg_trigger'::regclass
                              AND EXISTS (
                                  SELECT 1 FROM pg_trigger trigger_row
                                  WHERE trigger_row.oid = ddl.objid
                                    AND position('_ct_' in trigger_row.tgname) > 0
                              ))
                      )) THEN
                gate_key := coalesce(gate_row.takeover_lock_key, gate_row.lock_key);
            END IF;
            IF gate_key IS NOT NULL THEN
                PERFORM pg_advisory_lock(gate_key);
                PERFORM pg_advisory_unlock(gate_key);
            END IF;
        END;
        $function$
        """;

    internal const string SqlDropGateFunctionSql = """
        CREATE FUNCTION public.startup_fence_drop_gate()
        RETURNS event_trigger LANGUAGE plpgsql AS $function$
        DECLARE gate_row record;
        BEGIN
            SELECT phase, lock_key, takeover_lock_key INTO gate_row
            FROM public.startup_fence_gate
            WHERE actor = current_setting('application_name', true);
            IF FOUND AND (gate_row.phase = 'coresync' OR
                          gate_row.takeover_lock_key IS NOT NULL) AND EXISTS (
                SELECT 1 FROM pg_event_trigger_dropped_objects() dropped
                WHERE dropped.original AND dropped.object_type = 'trigger'
                  AND position('_ct_' in dropped.object_identity) > 0
            ) THEN
                PERFORM pg_advisory_lock(
                    coalesce(gate_row.takeover_lock_key, gate_row.lock_key));
                PERFORM pg_advisory_unlock(
                    coalesce(gate_row.takeover_lock_key, gate_row.lock_key));
            END IF;
        END;
        $function$
        """;

    internal const string SqlDropAuditFunctionSql = """
        CREATE FUNCTION public.startup_fence_drop_audit()
        RETURNS event_trigger LANGUAGE plpgsql AS $function$
        DECLARE actor_name text := coalesce(
                    nullif(current_setting('application_name', true), ''), '<unset>');
        BEGIN
            INSERT INTO public.startup_fence_audit
                (phase, kind, actor, backend_pid, backend_start_epoch_us, detail)
            SELECT coalesce(gate.phase, 'unattributed'), 'ddl', actor_name, pg_backend_pid(),
                   public.startup_fence_backend_start_us(),
                   'DROP ' || dropped.object_type || ' ' || dropped.object_identity
            FROM pg_event_trigger_dropped_objects() dropped
            LEFT JOIN public.startup_fence_gate gate ON gate.actor = actor_name;
        END;
        $function$
        """;

    internal const string DdlGateTriggerName = "startup_fence_00_ddl_gate";
    internal const string DropGateTriggerName = "startup_fence_00_drop_gate";
    internal const string AttachDmlTriggerName = "startup_fence_attach_dml";
    internal const string DdlAuditTriggerName = "startup_fence_ddl_end";
    internal const string DropAuditTriggerName = "startup_fence_drop_audit";
    internal static string DdlGateTriggerSql => $"""
        CREATE EVENT TRIGGER {DdlGateTriggerName} ON ddl_command_end
            EXECUTE FUNCTION public.startup_fence_ddl_gate()
        """;
    internal static string DropGateTriggerSql => $"""
        CREATE EVENT TRIGGER {DropGateTriggerName} ON sql_drop
            EXECUTE FUNCTION public.startup_fence_drop_gate()
        """;
    internal static string DropAuditTriggerSql => $"""
        CREATE EVENT TRIGGER {DropAuditTriggerName} ON sql_drop
            EXECUTE FUNCTION public.startup_fence_drop_audit()
        """;

    internal async Task InstallAuditAsync()
    {
        await ExecuteAsync(_monitor, """
            CREATE TABLE public.startup_fence_audit (
                event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                phase text NOT NULL,
                kind text NOT NULL,
                actor text NOT NULL,
                backend_pid integer NOT NULL,
                backend_start_epoch_us bigint NOT NULL,
                subject_pid integer,
                detail text NOT NULL DEFAULT '',
                transaction_id bigint NOT NULL DEFAULT txid_current(),
                observed_at timestamptz NOT NULL DEFAULT clock_timestamp()
            )
            """);
        await ExecuteAsync(_monitor, """
            CREATE TABLE public.startup_fence_gate (
                actor text PRIMARY KEY,
                phase text NOT NULL,
                target_id text,
                lock_key bigint NOT NULL,
                takeover_lock_key bigint
            )
            """);
        await ExecuteAsync(_monitor, """
            CREATE FUNCTION public.startup_fence_backend_start_us()
            RETURNS bigint LANGUAGE plpgsql AS $function$
            DECLARE started bigint;
            BEGIN
                SELECT (extract(epoch FROM backend_start) * 1000000)::bigint INTO started
                FROM pg_stat_activity
                WHERE pid = pg_backend_pid() AND datname = current_database()
                  AND backend_type = 'client backend';
                IF started IS NULL OR started <= 0 THEN
                    RAISE EXCEPTION 'Startup-fence audit cannot attest backend_start for pid %',
                        pg_backend_pid();
                END IF;
                RETURN started;
            END;
            $function$
            """);
        await ExecuteAsync(_monitor, """
            CREATE FUNCTION public.startup_fence_login_audit()
            RETURNS event_trigger LANGUAGE plpgsql AS $function$
            DECLARE actor_name text := coalesce(
                        nullif(current_setting('application_name', true), ''), '<unset>');
            BEGIN
                IF pg_is_in_recovery() THEN
                    RETURN;
                END IF;
                INSERT INTO public.startup_fence_audit
                    (phase, kind, actor, backend_pid, backend_start_epoch_us, detail)
                SELECT coalesce(g.phase, 'unattributed'), 'login', actor_name,
                       pg_backend_pid(), public.startup_fence_backend_start_us(), 'session opened'
                FROM (SELECT 1) marker
                LEFT JOIN public.startup_fence_gate g ON g.actor = actor_name;
            END;
            $function$
            """);
        await ExecuteAsync(_monitor, """
            CREATE EVENT TRIGGER startup_fence_login ON login
                EXECUTE FUNCTION public.startup_fence_login_audit()
            """);
        await ExecuteAsync(_monitor, "ALTER EVENT TRIGGER startup_fence_login ENABLE ALWAYS");
        await ExecuteAsync(_monitor, DdlEndGateFunctionSql);
        await ExecuteAsync(_monitor, DdlGateTriggerSql);
        await ExecuteAsync(_monitor, """
            CREATE FUNCTION public.startup_fence_ddl_audit()
            RETURNS event_trigger LANGUAGE plpgsql AS $function$
            DECLARE actor_name text := coalesce(
                        nullif(current_setting('application_name', true), ''), '<unset>');
            BEGIN
                INSERT INTO public.startup_fence_audit
                    (phase, kind, actor, backend_pid, backend_start_epoch_us, detail)
                SELECT coalesce(gate.phase, 'unattributed'), 'ddl', actor_name, pg_backend_pid(),
                       public.startup_fence_backend_start_us(),
                       command.command_tag || ' ' || coalesce(command.object_identity, '')
                FROM pg_event_trigger_ddl_commands() command
                LEFT JOIN public.startup_fence_gate gate ON gate.actor = actor_name;
            END;
            $function$
            """);
        await ExecuteAsync(_monitor, $"""
            CREATE EVENT TRIGGER {DdlAuditTriggerName} ON ddl_command_end
                EXECUTE FUNCTION public.startup_fence_ddl_audit()
            """);
        await ExecuteAsync(_monitor, SqlDropGateFunctionSql);
        await ExecuteAsync(_monitor, DropGateTriggerSql);
        await ExecuteAsync(_monitor, SqlDropAuditFunctionSql);
        await ExecuteAsync(_monitor, DropAuditTriggerSql);
        await ExecuteAsync(_monitor, RowGateFunctionSql);
        await ExecuteAsync(_monitor, """
            CREATE FUNCTION public.startup_fence_dml_audit()
            RETURNS trigger LANGUAGE plpgsql AS $function$
            DECLARE actor_name text := coalesce(
                        nullif(current_setting('application_name', true), ''), '<unset>');
                    row_id text := '';
            BEGIN
                IF tg_op <> 'TRUNCATE' AND tg_table_name IN
                    ('VocabularyWord', 'NumberContext', 'NumberCounter', 'NumberSubMode',
                     'AspNetUsers', 'UserProfile') THEN
                    row_id := CASE WHEN tg_op = 'DELETE'
                        THEN to_jsonb(OLD)->>'Id' ELSE to_jsonb(NEW)->>'Id' END;
                ELSIF tg_op <> 'TRUNCATE' AND tg_table_name = 'PhraseConstituent' THEN
                    row_id := CASE WHEN tg_op = 'DELETE'
                        THEN (to_jsonb(OLD)->>'PhraseWordId') || ':' ||
                             (to_jsonb(OLD)->>'ConstituentWordId')
                        ELSE (to_jsonb(NEW)->>'PhraseWordId') || ':' ||
                             (to_jsonb(NEW)->>'ConstituentWordId') END;
                ELSIF tg_op <> 'TRUNCATE' AND tg_table_name IN
                    ('__EFMigrationsHistory', '__CoachMigrationsHistory',
                     '__FeedbackMigrationsHistory') THEN
                    row_id := CASE WHEN tg_op = 'DELETE'
                        THEN to_jsonb(OLD)->>'MigrationId'
                        ELSE to_jsonb(NEW)->>'MigrationId' END;
                END IF;
                INSERT INTO public.startup_fence_audit
                    (phase, kind, actor, backend_pid, backend_start_epoch_us, detail)
                SELECT coalesce(g.phase, 'unattributed'), 'dml', actor_name,
                       pg_backend_pid(), public.startup_fence_backend_start_us(),
                       tg_table_name || ':' || tg_op || ':' || coalesce(row_id, '')
                FROM (SELECT 1) marker
                LEFT JOIN public.startup_fence_gate g ON g.actor = actor_name;
                RETURN NULL;
            END;
            $function$
            """);
        await ExecuteAsync(_monitor, AttachDmlAuditFunctionSql);
        await ExecuteAsync(_monitor, $"""
            CREATE EVENT TRIGGER {AttachDmlTriggerName} ON ddl_command_end
                EXECUTE FUNCTION public.startup_fence_attach_dml_audit()
            """);
        foreach (var table in new[] { "startup_fence_owner", "startup_fence_gate" })
        {
            await ExecuteAsync(_monitor, $"""
                CREATE TRIGGER startup_fence_dml AFTER INSERT OR UPDATE OR DELETE
                ON public.{table} FOR EACH ROW
                EXECUTE FUNCTION public.startup_fence_dml_audit()
                """);
            await ExecuteAsync(_monitor, $"""
                CREATE TRIGGER startup_fence_truncate AFTER TRUNCATE
                ON public.{table} FOR EACH STATEMENT
                EXECUTE FUNCTION public.startup_fence_dml_audit()
                """);
            await ExecuteAsync(_monitor,
                $"ALTER TABLE public.{table} ENABLE ALWAYS TRIGGER startup_fence_dml");
            await ExecuteAsync(_monitor,
                $"ALTER TABLE public.{table} ENABLE ALWAYS TRIGGER startup_fence_truncate");
        }

        // Ask the same EF provider used by Program for its exact history schema.
        // DML triggers attach regardless of actor as each table is created, including
        // migration histories; a manual history trigger would double-count the INSERTs.
        using (var context = new ApplicationDbContext(
                   new DbContextOptionsBuilder<ApplicationDbContext>()
                       .UseNpgsql(_monitor).Options))
        {
            var createHistory = context.GetService<IHistoryRepository>().GetCreateScript();
            if (!createHistory.Contains("\"__EFMigrationsHistory\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence migration audit: EF did not supply its " +
                    "application history schema.");
            }
            await ExecuteAsync(_monitor, createHistory);
        }
        await RegisterFixtureBackendAsync(
            _monitor, "startup-fence-monitor", requireAuditedLogin: false);
        if (await CountMigrationsAsync() != 0)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence migration audit: history already contains entries.");
        }
    }

    internal async Task InstallDmlAuditAsync()
    {
        await using var list = _monitor.CreateCommand();
        list.CommandText = """
            SELECT format('%I.%I', n.nspname, c.relname), n.nspname, c.relname,
                   EXISTS (
                       SELECT 1 FROM pg_trigger t
                       WHERE t.tgrelid = c.oid AND NOT t.tgisinternal
                         AND t.tgenabled = 'A'
                         AND t.tgname = 'startup_fence_dml'),
                   EXISTS (
                       SELECT 1 FROM pg_trigger t
                       WHERE t.tgrelid = c.oid AND NOT t.tgisinternal
                         AND t.tgenabled = 'A'
                         AND t.tgname = 'startup_fence_truncate'),
                   EXISTS (
                       SELECT 1 FROM pg_trigger t
                       WHERE t.tgrelid = c.oid AND NOT t.tgisinternal
                         AND t.tgenabled = 'A'
                         AND t.tgname = 'startup_fence_row_gate')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE (NOT starts_with(n.nspname, 'pg_')
                   OR starts_with(n.nspname, 'pg_temp_'))
              AND n.nspname <> 'information_schema'
              AND c.relkind IN ('r', 'p')
              AND NOT (n.nspname = 'public' AND c.relname = 'startup_fence_audit')
            ORDER BY n.nspname, c.relname
            """;
        var tables = new List<string>();
        await using (var reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var table = reader.GetString(0);
                if (!reader.GetBoolean(3) || !reader.GetBoolean(4))
                {
                    throw new InvalidOperationException(
                        $"Refusing startup-fence proof: {table} lacks ALWAYS write/TRUNCATE audit triggers.");
                }
                if (reader.GetString(1) == "public" &&
                    reader.GetString(2) is
                        ("VocabularyWord" or "NumberContext" or "PhraseConstituent" or "AspNetUsers") &&
                    !reader.GetBoolean(5))
                {
                    throw new InvalidOperationException(
                        $"Refusing startup-fence proof: {table} lacks its ALWAYS BEFORE-row write gate.");
                }
                tables.Add(table);
            }
        }

        if (!tables.Any(table => table.Contains("\"VocabularyWord\"", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The real application migrations did not create an audited VocabularyWord.");
        }
    }

    internal async Task<StartupFenceGateKeys> ArmAsync(
        string phase, string actorA, string actorB, string? targetId)
    {
        RequirePhaseTarget(phase, targetId);
        var keyA = NewGateKey();
        var keyB = NewGateKey();
        while (keyB == keyA)
        {
            keyB = NewGateKey();
        }
        long? takeoverKey = null;
        if (RequiresCoreSyncTakeoverCheckpoint(phase))
        {
            do
            {
                takeoverKey = NewGateKey();
            } while (takeoverKey == keyA || takeoverKey == keyB);
        }

        await using var command = _monitor.CreateCommand();
        command.CommandText = """
            INSERT INTO public.startup_fence_gate
                (actor, phase, target_id, lock_key, takeover_lock_key)
            VALUES ($1, $2, $3, $4, NULL), ($5, $2, $3, $6, $7)
            """;
        command.Parameters.AddWithValue(actorA);
        command.Parameters.AddWithValue(phase);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text,
            Value = (object?)targetId ?? DBNull.Value
        });
        command.Parameters.AddWithValue(keyA);
        command.Parameters.AddWithValue(actorB);
        command.Parameters.AddWithValue(keyB);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint,
            Value = (object?)takeoverKey ?? DBNull.Value
        });
        await command.ExecuteNonQueryAsync();
        return new StartupFenceGateKeys(keyA, keyB, takeoverKey);
    }

    internal async Task<StartupFenceGate> HoldGatesAsync(StartupFenceGateKeys keys)
    {
        var connection = await OpenConnectionAsync("startup-fence-gate");
        try
        {
            await ExecuteAsync(connection, "SELECT pg_advisory_lock($1)", keys.A);
            await ExecuteAsync(connection, "SELECT pg_advisory_lock($1)", keys.B);
            if (keys.Takeover is long takeoverKey)
            {
                await ExecuteAsync(connection, "SELECT pg_advisory_lock($1)", takeoverKey);
            }
            return new StartupFenceGate(connection, keys);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal async Task<StartupFenceBackend?> FindLockedAsync(string actor, bool granted)
    {
        await using var command = _monitor.CreateCommand();
        command.CommandText = """
            SELECT a.pid, (extract(epoch FROM a.backend_start) * 1000000)::bigint
            FROM pg_stat_activity a
            JOIN pg_locks l ON l.pid = a.pid
            WHERE a.datname = current_database() AND a.application_name = $1
              AND a.backend_type = 'client backend'
              AND l.locktype = 'advisory'
              AND l.classid = 1397970003::oid AND l.objid = 1::oid
              AND l.objsubid = 2 AND l.granted = $2
            """;
        command.Parameters.AddWithValue(actor);
        command.Parameters.AddWithValue(granted);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var backend = new StartupFenceBackend(reader.GetInt32(0), reader.GetInt64(1));
        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException("Multiple startup sessions matched one replica identity.");
        }
        return backend;
    }

    internal async Task<bool> IsWaitingOnGateAsync(string actor, int pid, long gateKey)
        => await ScalarAsync<bool>(_monitor, """
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity a JOIN pg_locks l ON l.pid = a.pid
                WHERE a.datname = current_database() AND a.application_name = $1
                  AND a.pid = $2 AND l.locktype = 'advisory' AND NOT l.granted
                  AND a.state = 'active' AND a.wait_event_type = 'Lock'
                  AND a.wait_event = 'advisory'
                  AND l.objsubid = 1
                  AND l.classid = ($3::bigint >> 32)::oid
                  AND l.objid = ($3::bigint & 4294967295)::oid
            )
            """, actor, pid, gateKey);

    internal async Task TerminateVerifiedOwnerAsync(
        string actorA, StartupFenceBackend owner, string actorB, StartupFenceBackend waiter)
    {
        await using var killer = await OpenConnectionAsync("startup-fence-killer");
        await AttestAsync(killer);
        await using var command = killer.CreateCommand();
        command.CommandText = """
            SELECT pg_terminate_backend(a.pid, 10000)
            FROM pg_stat_activity a
            WHERE a.pid = $1
              AND (extract(epoch FROM a.backend_start) * 1000000)::bigint = $2
              AND a.application_name = $3 AND a.datname = current_database()
              AND a.backend_type = 'client backend'
              AND a.pid <> pg_backend_pid() AND a.pid <> $4
              AND current_setting('cluster_name') = $5
              AND EXISTS (SELECT 1 FROM public.startup_fence_owner
                          WHERE run_id = $6 AND cluster_name = $5
                            AND database_name = current_database()
                            AND system_identifier =
                                (SELECT system_identifier::text FROM pg_control_system()))
              AND EXISTS (
                  SELECT 1 FROM pg_locks l WHERE l.pid = a.pid AND l.granted
                    AND l.locktype = 'advisory' AND l.classid = 1397970003::oid
                    AND l.objid = 1::oid AND l.objsubid = 2)
              AND EXISTS (
                  SELECT 1 FROM pg_stat_activity b
                  JOIN pg_locks l ON l.pid = b.pid
                  WHERE b.pid = $4
                    AND (extract(epoch FROM b.backend_start) * 1000000)::bigint = $7
                    AND b.application_name = $8 AND b.datname = current_database()
                    AND l.locktype = 'advisory' AND NOT l.granted
                    AND l.classid = 1397970003::oid AND l.objid = 1::oid
                    AND l.objsubid = 2)
            """;
        command.Parameters.AddWithValue(owner.Pid);
        command.Parameters.AddWithValue(owner.BackendStartMicroseconds);
        command.Parameters.AddWithValue(actorA);
        command.Parameters.AddWithValue(waiter.Pid);
        command.Parameters.AddWithValue(_clusterName);
        command.Parameters.AddWithValue(_runId);
        command.Parameters.AddWithValue(waiter.BackendStartMicroseconds);
        command.Parameters.AddWithValue(actorB);
        if (await command.ExecuteScalarAsync() is not true)
        {
            throw new InvalidOperationException(
                "Refusing pg_terminate_backend: exact scratch cluster, marked database, " +
                "lock owner, and waiting competitor were not verified in the same statement.");
        }
    }

    internal async Task<long> RecordTakeoverAsync(
        string phase, string actorA, string actorB, StartupFenceBackend owner,
        StartupFenceBackend successor)
    {
        await using var command = _monitor.CreateCommand();
        command.CommandText = """
            INSERT INTO public.startup_fence_audit
                (phase, kind, actor, backend_pid, backend_start_epoch_us, subject_pid, detail)
            SELECT $1, 'takeover', current_setting('application_name'),
                   pg_backend_pid(), public.startup_fence_backend_start_us(), $2,
                   'old backend gone; new lock granted'
            WHERE NOT EXISTS (
                SELECT 1 FROM pg_stat_activity
                WHERE pid = $3
                  AND (extract(epoch FROM backend_start) * 1000000)::bigint = $4
                  AND application_name = $5)
              AND EXISTS (
                  SELECT 1 FROM pg_stat_activity b
                  JOIN pg_locks l ON l.pid = b.pid
                  WHERE b.pid = $2
                    AND (extract(epoch FROM b.backend_start) * 1000000)::bigint = $6
                    AND b.application_name = $7 AND b.datname = current_database()
                    AND l.locktype = 'advisory' AND l.granted
                    AND l.classid = 1397970003::oid AND l.objid = 1::oid
                    AND l.objsubid = 2)
            RETURNING event_id
            """;
        command.Parameters.AddWithValue(phase);
        command.Parameters.AddWithValue(successor.Pid);
        command.Parameters.AddWithValue(owner.Pid);
        command.Parameters.AddWithValue(owner.BackendStartMicroseconds);
        command.Parameters.AddWithValue(actorA);
        command.Parameters.AddWithValue(successor.BackendStartMicroseconds);
        command.Parameters.AddWithValue(actorB);
        if (await command.ExecuteScalarAsync() is not long id)
        {
            throw new InvalidOperationException(
                "Cannot record takeover: the old backend is still present or B lacks the startup lock.");
        }
        return id;
    }

    internal async Task<List<StartupFenceAuditRow>> ReadAuditAsync(
        string? phase = null, long afterEventId = 0)
    {
        await using var command = _monitor.CreateCommand();
        command.CommandText = phase is null
            ? """
              SELECT event_id, phase, kind, actor, backend_pid, backend_start_epoch_us, subject_pid,
                     detail, transaction_id
              FROM public.startup_fence_audit WHERE event_id > $1 ORDER BY event_id
              """
            : """
              SELECT event_id, phase, kind, actor, backend_pid, backend_start_epoch_us, subject_pid,
                     detail, transaction_id
              FROM public.startup_fence_audit
              WHERE phase = $1 AND event_id > $2 ORDER BY event_id
              """;
        if (phase is not null)
        {
            command.Parameters.AddWithValue(phase);
        }
        command.Parameters.AddWithValue(afterEventId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<StartupFenceAuditRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new StartupFenceAuditRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetString(7), reader.GetInt64(8)));
        }
        return rows;
    }

    internal void AssertAuditProvenance(
        IReadOnlyList<StartupFenceAuditRow> rows,
        IReadOnlyCollection<StartupFenceExpectedReplicaBackend> replicas)
    {
        if (_attestedFixtureBackends.Count(entry =>
                entry.Value == "startup-fence-monitor") != 1)
        {
            throw new InvalidOperationException(
                "Refusing startup-fence proof: the monitor backend was not independently attested.");
        }

        ValidateAuditProvenance(rows, _attestedFixtureBackends, replicas);
    }

    internal static void ValidateAuditProvenance(
        IReadOnlyList<StartupFenceAuditRow> rows,
        IReadOnlyDictionary<StartupFenceBackend, string> fixtureBackends,
        IReadOnlyCollection<StartupFenceExpectedReplicaBackend> replicas)
    {
        var expectedReplicas = replicas.ToDictionary(replica => replica.Backend);
        if (expectedReplicas.Keys.Any(fixtureBackends.ContainsKey))
        {
            throw new InvalidOperationException(
                "Refusing startup-fence proof: a fixture and replica claim the same backend.");
        }

        long previousId = 0;
        foreach (var row in rows)
        {
            if (row.EventId <= previousId || row.BackendPid <= 0 ||
                row.BackendStartMicroseconds <= 0 ||
                row.TransactionId <= 0 ||
                row.Kind is not ("login" or "ddl" or "dml" or "takeover"))
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence proof: audit event order or server identity is invalid.");
            }
            previousId = row.EventId;

            var backend = new StartupFenceBackend(row.BackendPid, row.BackendStartMicroseconds);
            if (fixtureBackends.TryGetValue(backend, out var fixtureActor))
            {
                var takeover = row.Kind == "takeover";
                if (row.Actor != fixtureActor ||
                    (takeover
                        ? fixtureActor != "startup-fence-monitor" ||
                          row.Phase is not ("migration" or "numberseed" or "backfill" or
                                            "coresync" or "phrase" or "coach" or
                                            "feedback" or "devseed") ||
                          row.SubjectPid is not > 0 ||
                          !expectedReplicas.Values.Any(replica =>
                              !replica.LoginOnly && replica.Phase == row.Phase &&
                              replica.Backend.Pid == row.SubjectPid)
                        : row.Phase != "unattributed" ||
                          (fixtureActor == "startup-fence-monitor"
                              ? row.Kind == "login"
                              : row.Kind != "login")))
                {
                    throw new InvalidOperationException(
                        "Refusing startup-fence proof: an attested fixture backend changed actor " +
                        "or performed an unauthorized event.");
                }
                continue;
            }

            if (!expectedReplicas.TryGetValue(backend, out var replica) ||
                row.Actor != replica.Actor || row.Phase != replica.Phase ||
                row.Kind == "takeover" ||
                (replica.LoginOnly && row.Kind != "login"))
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence proof: an unattested or extra backend emitted " +
                    "a login, DDL, or DML event.");
            }
        }
    }

    internal static void ValidateTakeoverOrdering(
        IReadOnlyList<StartupFenceAuditRow> rows, string phase,
        string actorA, string actorB, StartupFenceBackend oldBackend,
        StartupFenceBackend newBackend, long takeoverId)
    {
        var takeovers = rows.Where(row =>
            row.EventId == takeoverId && row.Phase == phase && row.Kind == "takeover").ToArray();
        var a = rows.Where(row => row.Actor == actorA).ToArray();
        var b = rows.Where(row => row.Actor == actorB).ToArray();
        var bWrites = b.Where(row => row.Kind is ("ddl" or "dml")).ToArray();
        var takeover = takeovers.Length == 1 ? takeovers[0] : null;
        if (actorA == actorB || oldBackend == newBackend ||
            oldBackend.Pid == newBackend.Pid ||
            takeover is null || takeover.SubjectPid != newBackend.Pid ||
            takeover.BackendPid == oldBackend.Pid ||
            takeover.BackendPid == newBackend.Pid ||
            a.Count(row => row.Kind == "login") != 1 ||
            a.Any(row => row.Phase != phase ||
                new StartupFenceBackend(row.BackendPid, row.BackendStartMicroseconds) != oldBackend ||
                row.EventId >= takeoverId) ||
            b.Count(row => row.Kind == "login") != 1 ||
            b.Any(row => row.Phase != phase ||
                new StartupFenceBackend(row.BackendPid, row.BackendStartMicroseconds) != newBackend ||
                (row.Kind == "login" && row.EventId >= takeoverId)) ||
            bWrites.Length == 0 ||
            bWrites.Any(row => row.EventId <= takeoverId || row.TransactionId <= 0))
        {
            throw new InvalidOperationException(
                "Refusing startup-fence proof: server takeover audit ordering violation.");
        }
    }

    internal void AssertFixtureLogins(IReadOnlyList<StartupFenceAuditRow> rows)
        => ValidateFixtureLogins(rows, _attestedFixtureBackends);

    internal static void ValidateFixtureLogins(
        IReadOnlyList<StartupFenceAuditRow> rows,
        IReadOnlyDictionary<StartupFenceBackend, string> fixtureBackends)
    {
        foreach (var (backend, actor) in fixtureBackends)
        {
            var logins = rows.Count(row =>
                row.Kind == "login" && row.BackendPid == backend.Pid &&
                row.BackendStartMicroseconds == backend.BackendStartMicroseconds);
            if (logins != (actor == "startup-fence-monitor" ? 0 : 1))
            {
                throw new InvalidOperationException(
                    "Refusing startup-fence proof: an attested auxiliary login is missing " +
                    "or duplicated (the monitor opened before audit installation).");
            }
        }
    }

    internal Task<long> LastAuditIdAsync()
        => ScalarAsync<long>(_monitor,
            "SELECT coalesce(max(event_id), 0) FROM public.startup_fence_audit");

    internal Task<long> CountLiveSessionsAsync(string actor)
        => ScalarAsync<long>(_monitor,
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() " +
            "AND application_name = $1", actor);

    internal Task<long> CountMigrationsAsync()
        => ScalarAsync<long>(_monitor, "SELECT count(*) FROM public.\"__EFMigrationsHistory\"");

    internal async Task AssertMigrationHistoryAbsentAsync(string table)
    {
        if (table is not ("__CoachMigrationsHistory" or "__FeedbackMigrationsHistory"))
        {
            throw new ArgumentException(
                "Only optional startup migration histories can be checked before their first DDL.",
                nameof(table));
        }

        if (!await ScalarAsync<bool>(
                _monitor, "SELECT to_regclass($1) IS NULL", $"public.\"{table}\""))
        {
            throw new InvalidOperationException(
                $"Refusing startup-fence {table} fault: its first migration DDL already ran.");
        }
    }

    internal async Task<List<string>> ReadMigrationIdsAsync(string table = "__EFMigrationsHistory")
    {
        if (table is not ("__EFMigrationsHistory" or "__CoachMigrationsHistory" or
                          "__FeedbackMigrationsHistory"))
        {
            throw new ArgumentException("An unexpected migration history table was requested.", nameof(table));
        }

        await using var command = _monitor.CreateCommand();
        command.CommandText = $"""
            SELECT "MigrationId" FROM public."{table}"
            ORDER BY "MigrationId"
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<string>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }
        return ids;
    }

    internal Task<long> CountSeededNumbersAsync(string table)
    {
        if (table is not ("NumberContext" or "NumberSubMode" or "NumberCounter"))
        {
            throw new ArgumentException("An unexpected number seed table was requested.", nameof(table));
        }

        return ScalarAsync<long>(_monitor, $"SELECT count(*) FROM public.\"{table}\"");
    }

    internal async Task<StartupFenceNumberSeed> StageNumberReseedAsync()
    {
        await using var select = _monitor.CreateCommand();
        select.CommandText = """
            SELECT "Id", "DisplayName"
            FROM public."NumberContext" ORDER BY "Id" LIMIT 1
            """;
        await using var reader = await select.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                "The first startup did not seed a NumberContext for the reseed fault.");
        }
        var original = new StartupFenceNumberSeed(reader.GetString(0), reader.GetString(1));
        await reader.DisposeAsync();

        await using var update = _monitor.CreateCommand();
        update.CommandText = """
            UPDATE public."NumberContext" SET "DisplayName" = $1 WHERE "Id" = $2
            """;
        update.Parameters.AddWithValue($"startup-fence-reseed-{_runId:N}");
        update.Parameters.AddWithValue(original.Id);
        if (await update.ExecuteNonQueryAsync() != 1)
        {
            throw new InvalidOperationException(
                "The disposable NumberContext row could not be staged for the reseed fault.");
        }
        return original;
    }

    internal Task<string> ReadNumberContextNameAsync(string id)
        => ScalarAsync<string>(_monitor,
            "SELECT \"DisplayName\" FROM public.\"NumberContext\" WHERE \"Id\" = $1", id);

    internal Task<long> CountCoreSyncTriggersAsync()
        => ScalarAsync<long>(_monitor, """
            SELECT count(*) FROM pg_trigger
            WHERE tgrelid = 'public."VocabularyWord"'::regclass
              AND tgname IN (
                  '__vocabularyword_ct_insert__',
                  '__vocabularyword_ct_update__',
                  '__vocabularyword_ct_delete__')
              AND NOT tgisinternal
            """);

    internal Task<int> ReadWordClassificationAsync(string wordId)
        => ScalarAsync<int>(_monitor,
            "SELECT \"LexicalUnitType\" FROM public.\"VocabularyWord\" WHERE \"Id\" = $1",
            wordId);

    internal static StartupFencePhraseSeed CreatePhraseSeed()
    {
        var userId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        VocabularyWord Word(string target, string native, LexicalUnitType type) => new()
        {
            Id = Guid.NewGuid().ToString(),
            TargetLanguageTerm = target,
            NativeLanguageTerm = native,
            Language = "ko",
            LexicalUnitType = type,
            CreatedAt = now,
            UpdatedAt = now
        };

        var phrase = Word("안녕 친구", "hello friend", LexicalUnitType.Phrase);
        VocabularyWord[] constituents =
        [
            Word("안녕", "hello", LexicalUnitType.Word),
            Word("친구", "friend", LexicalUnitType.Word)
        ];
        var words = new[] { phrase }.Concat(constituents).ToArray();
        var progress = words.Select(word => new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = word.Id,
            VocabularyWord = word,
            UserId = userId,
            FirstSeenAt = now,
            LastPracticedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        }).ToArray();
        return new StartupFencePhraseSeed(userId, phrase, constituents, progress);
    }

    internal async Task<StartupFencePhraseSeed> SeedPhraseAsync()
    {
        if (!_ownerMarkerCreated || _attestedCluster is null)
        {
            throw new InvalidOperationException(
                "Refusing phrase staging before scratch cluster and owner attestation.");
        }

        var seed = CreatePhraseSeed();
        using (var db = new ApplicationDbContext(
                   new DbContextOptionsBuilder<ApplicationDbContext>()
                       .UseNpgsql(_monitor).Options))
        {
            db.VocabularyWords.Add(seed.Phrase);
            db.VocabularyWords.AddRange(seed.ConstituentWords);
            db.VocabularyProgresses.AddRange(seed.Progress);
            await db.SaveChangesAsync();
        }

        var count = await ScalarAsync<long>(_monitor, """
            SELECT count(*) FROM public."VocabularyProgress"
            WHERE "UserId" = $1 AND "VocabularyWordId" IN ($2, $3, $4)
            """, seed.UserId, seed.Phrase.Id,
            seed.ConstituentWords[0].Id, seed.ConstituentWords[1].Id);
        if (count != seed.Progress.Count ||
            (await ReadPhraseConstituentsAsync(seed.Phrase.Id)).Count != 0)
        {
            throw new InvalidOperationException(
                "The dedicated phrase and explicit-user progress were not staged without constituents.");
        }
        return seed;
    }

    internal async Task<IReadOnlyList<string>> ReadPhraseConstituentsAsync(string phraseId)
    {
        await using var command = _monitor.CreateCommand();
        command.CommandText = """
            SELECT "ConstituentWordId" FROM public."PhraseConstituent"
            WHERE "PhraseWordId" = $1 ORDER BY "ConstituentWordId"
            """;
        command.Parameters.AddWithValue(phraseId);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(0))
            {
                throw new InvalidOperationException(
                    "A phrase constituent was inserted without its matching vocabulary word.");
            }
            result.Add(reader.GetString(0));
        }
        return result;
    }

    internal async Task<IReadOnlyList<StartupFenceSeededDevAccount>> ReadSeededDevAccountsAsync()
    {
        var accounts = new List<StartupFenceSeededDevAccount>();
        foreach (var expected in DevTestAccountSeeder.Accounts)
        {
            await using var command = _monitor.CreateCommand();
            command.CommandText = """
                SELECT u."Id", u."UserProfileId", u."EmailConfirmed",
                       u."NormalizedUserName" = upper($1)
                FROM public."AspNetUsers" u
                JOIN public."UserProfile" p
                  ON p."Id" = u."UserProfileId" AND p."Email" = $1
                WHERE u."Email" = $1
                """;
            command.Parameters.AddWithValue(expected.Email);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync() || reader.IsDBNull(1) ||
                !reader.GetBoolean(2) || !reader.GetBoolean(3))
            {
                throw new InvalidOperationException(
                    "A built-in development account was not inserted and linked to its profile.");
            }
            accounts.Add(new StartupFenceSeededDevAccount(
                reader.GetString(0), reader.GetString(1)));
            if (await reader.ReadAsync())
            {
                throw new InvalidOperationException(
                    "A built-in development account was inserted more than once.");
            }
        }

        if (await ScalarAsync<long>(_monitor, "SELECT count(*) FROM public.\"AspNetUsers\"") !=
                DevTestAccountSeeder.Accounts.Length ||
            await ScalarAsync<long>(_monitor, "SELECT count(*) FROM public.\"UserProfile\"") !=
                DevTestAccountSeeder.Accounts.Length ||
            accounts.Select(account => account.UserId).Distinct().Count() != accounts.Count ||
            accounts.Select(account => account.ProfileId).Distinct().Count() != accounts.Count)
        {
            throw new InvalidOperationException(
                "The scratch database contains unexpected or duplicate development accounts.");
        }
        return accounts;
    }

    internal async Task SeedUnknownWordAsync(string wordId)
    {
        await using var command = _monitor.CreateCommand();
        command.CommandText = """
            INSERT INTO public."VocabularyWord"
                ("Id", "TargetLanguageTerm", "NativeLanguageTerm",
                 "CreatedAt", "UpdatedAt", "LexicalUnitType")
            VALUES ($1, '안녕 친구', 'hello friend', now(), now(), 0)
            """;
        command.Parameters.AddWithValue(wordId);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _monitor.DisposeAsync();

    private static long NewGateKey()
        => (BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) & 0x3fff_ffff_ffff_ffffL) + 1;

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params object[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection, string sql, params object[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }
        return (T)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The scratch PostgreSQL query returned no value."));
    }
}

// Epoch microseconds keep backend identity exact despite the API's legacy Npgsql timestamp switch.
internal sealed record StartupFenceBackend(int Pid, long BackendStartMicroseconds);

internal sealed record StartupFenceCatalogObject(
    string Kind, string Schema, long Oid, string Name);

internal sealed record StartupFenceNumberSeed(string Id, string OriginalName);

internal sealed record StartupFencePhraseSeed(
    string UserId, VocabularyWord Phrase, IReadOnlyList<VocabularyWord> ConstituentWords,
    IReadOnlyList<VocabularyProgress> Progress);

internal sealed record StartupFenceSeededDevAccount(string UserId, string ProfileId);

internal sealed record StartupFenceGateKeys(long A, long B, long? Takeover);

internal enum StartupFencePostgresScenario
{
    Baseline,
    Phrase,
    Coach,
    Feedback,
    DevSeed
}

internal sealed record StartupFenceAuditRow(
    long EventId, string Phase, string Kind, string Actor, int BackendPid,
    long BackendStartMicroseconds, int? SubjectPid, string Detail, long TransactionId);

internal sealed record StartupFenceExpectedReplicaBackend(
    string Actor, string Phase, StartupFenceBackend Backend, bool LoginOnly = false);

internal sealed class StartupFenceGate(
    NpgsqlConnection connection, StartupFenceGateKeys keys) : IAsyncDisposable
{
    private bool _releasedA;
    private bool _releasedB;
    private bool _releasedTakeover;

    internal Task ReleaseAAsync()
        => ReleaseAsync(keys.A, () => _releasedA, () => _releasedA = true);
    internal Task ReleaseBAsync()
        => ReleaseAsync(keys.B, () => _releasedB, () => _releasedB = true);

    internal Task ReleaseTakeoverAsync()
        => keys.Takeover is long key
            ? ReleaseAsync(key, () => _releasedTakeover, () => _releasedTakeover = true)
            : throw new InvalidOperationException(
                "The selected startup writer has no earlier takeover checkpoint.");

    internal Task ReleaseTakeoverIfHeldAsync()
        => keys.Takeover is null ? Task.CompletedTask : ReleaseTakeoverAsync();

    private async Task ReleaseAsync(long key, Func<bool> released, Action markReleased)
    {
        if (released())
        {
            return;
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_unlock($1)";
        command.Parameters.AddWithValue(key);
        if (await command.ExecuteScalarAsync() is not true)
        {
            throw new InvalidOperationException("The isolated test gate was not held.");
        }
        markReleased();
    }

    public async ValueTask DisposeAsync()
    {
        // Closing this unpooled connection releases any remaining test-only gates.
        await connection.DisposeAsync();
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class StartupFencePostgresFactAttribute : FactAttribute
{
    public StartupFencePostgresFactAttribute()
    {
        if (StartupFencePostgresFixture.SkipWhenNotOptedIn(
            Environment.GetEnvironmentVariable(StartupFencePostgresFixture.EnabledVariable)))
        {
            Skip = $"Opt-in only: set {StartupFencePostgresFixture.EnabledVariable}=1 " +
                "for an attested, dedicated PostgreSQL 17 scratch cluster.";
        }
    }
}
