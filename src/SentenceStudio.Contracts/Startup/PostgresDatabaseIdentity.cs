using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SentenceStudio.Contracts.Startup;

public enum PostgresDatabaseIdentityFailure
{
    WrongProvider,
    QueryUnavailable,
    MissingDatabase,
    MissingSchema,
    MissingServerInstance,
    MissingSchemaMarker,
    UnexpectedSchemaMarker,
    BackendChanged,
    IdentityChanged
}

public sealed class PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure reason)
    : InvalidOperationException(reason switch
    {
        PostgresDatabaseIdentityFailure.WrongProvider =>
            "PostgreSQL database identity is unavailable: the configured database is not PostgreSQL.",
        PostgresDatabaseIdentityFailure.MissingDatabase =>
            "PostgreSQL database identity is unavailable: database provenance is missing.",
        PostgresDatabaseIdentityFailure.MissingSchema =>
            "PostgreSQL database identity is unavailable: schema provenance is missing.",
        PostgresDatabaseIdentityFailure.MissingServerInstance =>
            "PostgreSQL database identity is unavailable: persisted server identity is missing.",
        PostgresDatabaseIdentityFailure.MissingSchemaMarker =>
            "PostgreSQL database identity is unavailable: persisted schema marker is missing.",
        PostgresDatabaseIdentityFailure.UnexpectedSchemaMarker =>
            "PostgreSQL database identity is unavailable: persisted schema marker is incompatible.",
        PostgresDatabaseIdentityFailure.BackendChanged =>
            "PostgreSQL database identity is unavailable: the readiness backend changed.",
        PostgresDatabaseIdentityFailure.IdentityChanged =>
            "PostgreSQL database identity is unavailable: provenance changed during readiness verification.",
        _ => "PostgreSQL database identity is unavailable: the provenance query failed."
    })
{
    public PostgresDatabaseIdentityFailure Reason { get; } = reason;
}

public readonly record struct PostgresDatabaseReadiness(string DatabaseIdentity, int PendingMigrationCount);

public static class PostgresDatabaseIdentity
{
    // pg_control_system exposes a signed bigint, including for high-bit system IDs.
    // Physical copies inherit this control file and all the database/schema/history inputs.
    private const string ProvenanceSql = """
        SELECT pg_catalog.current_database(),
               database.oid::text,
               pg_catalog.current_schema(),
               control.system_identifier::text,
               (SELECT MAX("MigrationId") FROM "__EFMigrationsHistory")
        FROM pg_catalog.pg_database AS database
        CROSS JOIN pg_catalog.pg_control_system() AS control
        WHERE database.datname = pg_catalog.current_database()
        """;

    /// <summary>
    /// Reads cluster, database, and applied-schema provenance on one already-open PostgreSQL
    /// connection. This single read does not attest continuity with a subsequent EF history
    /// query; use <see cref="ReadReadinessAsync"/> for startup readiness instead. The caller
    /// owns the connection; configured database names or compiled migrations are not substitutes.
    /// </summary>
    public static async Task<string> ReadAsync(
        DbConnection connection, string expectedMigrationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedMigrationId);

        if (connection.State != ConnectionState.Open)
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.QueryUnavailable);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ProvenanceSql;
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.MissingDatabase);
            }

            var databaseName = ReadText(reader, 0);
            var databaseOid = ReadText(reader, 1);
            var schemaName = ReadText(reader, 2);
            var systemIdentifier = ReadText(reader, 3);
            var appliedMigrationId = ReadText(reader, 4);

            if (!uint.TryParse(databaseOid, NumberStyles.None, CultureInfo.InvariantCulture, out var oid))
            {
                throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.MissingDatabase);
            }
            if (!long.TryParse(systemIdentifier, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var clusterId))
            {
                throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.MissingServerInstance);
            }

            return ComputeFromAttestedValues(
                databaseName, oid, schemaName, clusterId, appliedMigrationId, expectedMigrationId);
        }
        catch (PostgresDatabaseIdentityException)
        {
            throw;
        }
        catch (DbException ex) when (ex.SqlState is "42P01" or "42703")
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.MissingSchemaMarker);
        }
        catch (DbException)
        {
            // Provider exceptions may include server addresses or connection details.
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.QueryUnavailable);
        }
        catch (InvalidCastException)
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.QueryUnavailable);
        }
        catch (InvalidOperationException)
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.QueryUnavailable);
        }
    }

    /// <summary>
    /// Attests identity and applied migrations on one uninterrupted client-visible backend.
    /// The caller must open the EF-managed connection first, supply that same context's
    /// connection and its Npgsql backend PID, and keep it open until this method returns.
    /// The PID is a local continuity check, never part of the cross-service fingerprint.
    /// A proxy that switches hidden backends without changing client-visible state/PID needs
    /// independent external attestation; this method cannot prove that routing.
    /// </summary>
    public static async Task<PostgresDatabaseReadiness> ReadReadinessAsync(
        DbConnection connection,
        Func<DbConnection> getCurrentConnection,
        Func<int> getBackendPid,
        Func<CancellationToken, Task<int>> getPendingMigrationCount,
        string expectedMigrationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(getCurrentConnection);
        ArgumentNullException.ThrowIfNull(getBackendPid);
        ArgumentNullException.ThrowIfNull(getPendingMigrationCount);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedMigrationId);

        using var backend = new ReadinessBackendContinuity(
            connection, getCurrentConnection, getBackendPid, cancellationToken);
        backend.AssertActive(cancellationToken);
        var identity = await ReadAsync(connection, expectedMigrationId, cancellationToken);
        backend.AssertActive(cancellationToken);

        int pending;
        try
        {
            pending = await getPendingMigrationCount(cancellationToken);
        }
        finally
        {
            backend.AssertActive(cancellationToken);
        }

        var confirmedIdentity = await ReadAsync(connection, expectedMigrationId, cancellationToken);
        backend.AssertActive(cancellationToken);
        if (!string.Equals(identity, confirmedIdentity, StringComparison.Ordinal))
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.IdentityChanged);
        }
        if (pending < 0)
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.UnexpectedSchemaMarker);
        }

        return new PostgresDatabaseReadiness(identity, pending);
    }

    /// <summary>
    /// Calculates a non-secret equality token that distinguishes different independently
    /// initialized PostgreSQL system identifiers. A physical backup/clone shares the control
    /// identifier, database OID, schema, and history, so hashing these values cannot
    /// distinguish physical copies or establish that system identifiers are globally unique.
    /// An absolute physical-instance guarantee requires a separate, independently provisioned
    /// marker that is not copied with PGDATA; do not infer it from this fingerprint.
    /// </summary>
    public static string ComputeFromAttestedValues(
        string? databaseName, uint databaseOid, string? schemaName, long clusterSystemIdentifier,
        string? appliedMigrationId, string expectedMigrationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedMigrationId);
        if (string.IsNullOrWhiteSpace(databaseName) || databaseOid == 0)
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.MissingDatabase);
        }
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.MissingSchema);
        }
        if (clusterSystemIdentifier == 0)
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.MissingServerInstance);
        }
        if (string.IsNullOrWhiteSpace(appliedMigrationId))
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.MissingSchemaMarker);
        }
        if (!string.Equals(appliedMigrationId, expectedMigrationId, StringComparison.Ordinal))
        {
            throw new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.UnexpectedSchemaMarker);
        }

        var canonical = new StringBuilder("SentenceStudio.PostgreSQL.DatabaseIdentity.v1\n");
        Append(databaseName);
        Append(databaseOid.ToString(CultureInfo.InvariantCulture));
        Append(schemaName);
        Append(clusterSystemIdentifier.ToString(CultureInfo.InvariantCulture));
        Append(appliedMigrationId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();

        void Append(string value)
        {
            canonical.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(value).Append('\n');
        }
    }

    public static bool IsCanonicalFingerprint(string? value)
    {
        if (value is not { Length: 64 })
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static string? ReadText(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed class ReadinessBackendContinuity : IDisposable
    {
        private readonly DbConnection _connection;
        private readonly Func<DbConnection> _getCurrentConnection;
        private readonly Func<int> _getBackendPid;
        private readonly int _backendPid;
        private int _lost;

        public ReadinessBackendContinuity(
            DbConnection connection, Func<DbConnection> getCurrentConnection,
            Func<int> getBackendPid, CancellationToken cancellationToken)
        {
            _connection = connection;
            _getCurrentConnection = getCurrentConnection;
            _getBackendPid = getBackendPid;

            _connection.StateChange += OnStateChange;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsActive())
                {
                    throw LostBackend();
                }

                try
                {
                    _backendPid = _getBackendPid();
                }
                catch (Exception)
                {
                    throw LostBackend();
                }

                AssertActive(cancellationToken);
            }
            catch
            {
                _connection.StateChange -= OnStateChange;
                throw;
            }
        }

        public void AssertActive(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _lost) != 0 || !IsActive())
            {
                throw LostBackend();
            }

            DbConnection currentConnection;
            int currentPid;
            try
            {
                currentConnection = _getCurrentConnection();
                currentPid = _getBackendPid();
            }
            catch (Exception)
            {
                throw LostBackend();
            }

            if (_backendPid <= 0 || currentPid != _backendPid ||
                !ReferenceEquals(_connection, currentConnection) ||
                Volatile.Read(ref _lost) != 0 || !IsActive())
            {
                throw LostBackend();
            }
        }

        private bool IsActive()
        {
            try
            {
                return IsActiveState(_connection.State);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsActiveState(ConnectionState state) =>
            (state & ConnectionState.Open) != 0 &&
            (state & (ConnectionState.Broken | ConnectionState.Connecting)) == 0;

        private PostgresDatabaseIdentityException LostBackend()
        {
            Interlocked.Exchange(ref _lost, 1);
            return new PostgresDatabaseIdentityException(PostgresDatabaseIdentityFailure.BackendChanged);
        }

        private void OnStateChange(object? sender, StateChangeEventArgs args)
        {
            if (!IsActiveState(args.OriginalState) || !IsActiveState(args.CurrentState))
            {
                Interlocked.Exchange(ref _lost, 1);
            }
        }

        public void Dispose() => _connection.StateChange -= OnStateChange;
    }
}
