using System.Data;
using System.Data.Common;

namespace SentenceStudio.Api.Startup;

/// <summary>A one-way latch for the backend which acquired the startup lock.</summary>
internal sealed class StartupPostgresSessionFence : IDisposable
{
    private readonly DbConnection _connection;
    private readonly Func<int> _getBackendPid;
    private readonly int _backendPid;
    private readonly CancellationToken _stoppingToken;
    private int _lost;

    public StartupPostgresSessionFence(
        DbConnection connection, Func<int> getBackendPid, CancellationToken stoppingToken = default)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _getBackendPid = getBackendPid ?? throw new ArgumentNullException(nameof(getBackendPid));
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The API startup backend must already be open.");
        }

        stoppingToken.ThrowIfCancellationRequested();
        _stoppingToken = stoppingToken;
        _backendPid = getBackendPid();
        if (_backendPid <= 0)
        {
            throw new InvalidOperationException("The API startup backend has no valid PostgreSQL process ID.");
        }

        _connection.StateChange += OnStateChange;
        try
        {
            AssertActive();
        }
        catch
        {
            _connection.StateChange -= OnStateChange;
            throw;
        }
    }

    public void AssertConnection(DbConnection connection, CancellationToken cancellationToken = default)
    {
        if (!ReferenceEquals(_connection, connection))
        {
            LatchLoss();
            throw new InvalidOperationException("A startup database command targeted a backend other than the lock owner.");
        }

        AssertActive(cancellationToken);
    }

    public void AssertActive(CancellationToken cancellationToken = default)
    {
        _stoppingToken.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _lost) != 0 || _connection.State != ConnectionState.Open)
        {
            LatchLoss();
            throw new InvalidOperationException("The API application startup lock backend was lost.");
        }

        int currentPid;
        try
        {
            currentPid = _getBackendPid();
        }
        catch (Exception ex)
        {
            LatchLoss();
            throw new InvalidOperationException("The API application startup lock backend was lost.", ex);
        }

        if (currentPid != _backendPid)
        {
            LatchLoss();
            throw new InvalidOperationException("The API application startup lock backend changed.");
        }
    }

    public void LatchLoss() => Interlocked.Exchange(ref _lost, 1);

    private void OnStateChange(object? sender, StateChangeEventArgs args)
    {
        // A reconnect may report Closed -> Open without a separate Closed notification.
        if ((args.OriginalState & ConnectionState.Open) == 0
            || (args.CurrentState & ConnectionState.Open) == 0)
        {
            LatchLoss();
        }
    }

    public void Dispose()
    {
        LatchLoss();
        _connection.StateChange -= OnStateChange;
    }
}
