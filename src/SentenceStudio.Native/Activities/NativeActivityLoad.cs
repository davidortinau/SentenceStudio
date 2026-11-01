namespace SentenceStudio.Native;

/// <summary>One activity content load (AI sentences, generated content) started by <see cref="NativeActivityLoad.Begin"/>.</summary>
public readonly record struct NativeActivityLoadTicket(long Version, CancellationToken Token);

/// <summary>
/// Single in-flight content load for an activity presenter. A slow or hung AI
/// load must never hold the presenter gate, because Back/Exit waits on that
/// gate to stop and save the timer. Pattern (see NativeActivityCatalog):
/// <list type="number">
/// <item>Under the gate: validate, reset, publish Loading, then <see cref="Begin"/>.</item>
/// <item>Release the gate and await the load with <see cref="NativeActivityLoadTicket.Token"/>.</item>
/// <item>Re-take the gate with <c>CancellationToken.None</c> and apply the result only
/// while <see cref="IsCurrent"/>; otherwise return the current state unchanged.</item>
/// <item>Exit calls <see cref="Cancel"/> before awaiting the gate; Dispose disposes this.</item>
/// </list>
/// A newer <see cref="Begin"/> cancels and supersedes the previous load.
/// </summary>
public sealed class NativeActivityLoad : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _current;
    private long _version;
    private bool _disposed;

    public NativeActivityLoadTicket Begin(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            CancelCore();
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_disposed)
                source.Cancel();
            _current = source;
            return new(++_version, source.Token);
        }
    }

    /// <summary>True while the ticket's load has not been superseded, cancelled or disposed.</summary>
    public bool IsCurrent(NativeActivityLoadTicket ticket)
    {
        lock (_sync)
            return !_disposed && ticket.Version == _version
                && !ticket.Token.IsCancellationRequested;
    }

    /// <summary>Cancels the in-flight load (Exit, owner change) so its result is never applied.</summary>
    public void Cancel()
    {
        lock (_sync)
        {
            CancelCore();
            _version++;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            CancelCore();
            _version++;
        }
    }

    private void CancelCore()
    {
        if (_current is not { } source)
            return;
        _current = null;
        try
        {
            source.Cancel();
        }
        catch (AggregateException)
        {
            // A load's cancellation callback failed; the load is abandoned either way.
        }
        source.Dispose();
    }
}
