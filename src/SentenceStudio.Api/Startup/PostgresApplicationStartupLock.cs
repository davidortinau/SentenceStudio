using CoreSync.PostgreSQL;
using Npgsql;

namespace SentenceStudio.Api.Startup;

/// <summary>
/// Owns the one PostgreSQL backend used for every pre-listen database write.
/// Losing that backend permanently fences this startup attempt, even if the
/// NpgsqlConnection is subsequently reopened from the pool.
/// </summary>
internal sealed class PostgresApplicationStartupLock : IAsyncDisposable
{
    private const string AcquireSql = "SELECT pg_advisory_lock(1397970003, 1)";
    private const string ReleaseSql = "SELECT pg_advisory_unlock(1397970003, 1)";
    private const string LockHeldSql = """
        SELECT EXISTS (
            SELECT 1 FROM pg_locks
            WHERE pid = pg_backend_pid()
              AND locktype = 'advisory'
              AND classid = 1397970003::oid
              AND objid = 1::oid
              AND objsubid = 2
              AND granted
        )
        """;

    private readonly NpgsqlConnection _connection;
    private readonly StartupPostgresSessionFence _fence;
    private readonly CancellationToken _stoppingToken;

    private PostgresApplicationStartupLock(
        NpgsqlConnection connection,
        StartupPostgresSessionFence fence,
        CancellationToken stoppingToken)
    {
        _connection = connection;
        _fence = fence;
        _stoppingToken = stoppingToken;
        DbInterceptors =
        [
            new StartupSessionConnectionInterceptor(fence),
            new StartupSessionCommandInterceptor(fence)
        ];
    }

    public NpgsqlConnection Connection
    {
        get
        {
            EnsureActive();
            return _connection;
        }
    }

    public Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] DbInterceptors { get; }

    public void EnsureActive(CancellationToken cancellationToken = default)
        => _fence.AssertActive(cancellationToken);

    public void AbortStartup() => _fence.LatchLoss();

    public static async Task<PostgresApplicationStartupLock> AcquireAsync(
        string connectionString, CancellationToken cancellationToken = default,
        IStartupConnectionAttestor? attestor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Returning a pooled connection can defer DISCARD ALL (and advisory unlock)
        // until its next borrower. An aborted startup must release the physical
        // backend immediately, including when its one-way fence forbids SQL.
        var sessionSettings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Multiplexing = false
        };
        var connection = new NpgsqlConnection(sessionSettings.ConnectionString);
        try
        {
            // This connection exists before any EF context, so EF interceptors cannot guard it.
            attestor?.ValidateBeforeOpen(connection);
            await connection.OpenAsync(cancellationToken);
            if (attestor is not null)
            {
                await attestor.AttestOpenAsync(connection, cancellationToken);
            }

            var fence = new StartupPostgresSessionFence(
                connection, () => connection.ProcessID, cancellationToken);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = AcquireSql;
                fence.AssertConnection(command.Connection!, cancellationToken);
                await command.ExecuteNonQueryAsync(cancellationToken);
                fence.AssertActive(cancellationToken);

                var startupLock = new PostgresApplicationStartupLock(
                    connection, fence, cancellationToken);
                await startupLock.VerifyHeldAsync(cancellationToken);
                return startupLock;
            }
            catch
            {
                fence.Dispose();
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task VerifyHeldAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive(cancellationToken);
        await using var command = _connection.CreateCommand();
        command.CommandText = LockHeldSql;
        _fence.AssertConnection(command.Connection!, cancellationToken);
        var held = await command.ExecuteScalarAsync(cancellationToken);
        EnsureActive(cancellationToken);
        if (held is not true)
        {
            _fence.LatchLoss();
            throw new InvalidOperationException("The API application startup lock was lost.");
        }
    }

    public async Task ProvisionAsync(
        PostgreSQLSyncProvider provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        await VerifyHeldAsync(cancellationToken);

        // EF's per-migration transactions are complete before this begins. CoreSync's
        // initialization and trigger DDL are atomic on the very same locked backend.
        EnsureActive(cancellationToken);
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
        EnsureActive(cancellationToken);
        await provider.ApplyProvisionAsync(_connection, transaction, cancellationToken);
        EnsureActive(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        EnsureActive(cancellationToken);
        await VerifyHeldAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await VerifyHeldAsync(_stoppingToken);
            await using var command = _connection.CreateCommand();
            command.CommandText = ReleaseSql;
            _fence.AssertConnection(command.Connection!, _stoppingToken);
            var released = await command.ExecuteScalarAsync(_stoppingToken);
            EnsureActive(_stoppingToken);
            if (released is not true)
            {
                _fence.LatchLoss();
                throw new InvalidOperationException("The API application startup lock was lost.");
            }
        }
        finally
        {
            _fence.Dispose();
            await _connection.DisposeAsync();
        }
    }
}
