namespace SentenceStudio.Native;

/// <summary>
/// Back/Exit fence for an activity presenter. Exit cancels in-flight work
/// (<see cref="NativeActivityLoad.Cancel"/>, a lifetime or operation source)
/// before it can take the presenter gate, so a cancelled continuation can resume
/// first: inline inside Cancel, on another thread, or by winning the gate while
/// Exit still waits for it. Once Exit has begun, no continuation may return or
/// publish a non-Exit state; it waits for Exit to settle and returns that state.
/// <para>Pattern:</para>
/// <code>
/// public async Task&lt;TState&gt; ExitAsync(CancellationToken cancellationToken)
/// {
///     var exited = false;
///     _exit.Begin();                    // before any Cancel
///     try
///     {
///         _load.Cancel();
///         await _exit.AfterCancelAsync();   // test seam; completed in production
///         await _gate.WaitAsync(cancellationToken);
///         try { ...; exited = true; return Publish(exitState); }
///         finally { _gate.Release(); }
///     }
///     finally { _exit.End(exited); }    // after the gate is released
/// }
///
/// // Every continuation that resumes after an await off the gate:
/// var proceed = await _exit.EnterAsync(_gate);   // always holds the gate
/// try
/// {
///     _busy = false;
///     if (!proceed)
///         return State;                 // the state Exit published
///     ...
/// }
/// finally { _gate.Release(); }
///
/// // A cancelled continuation (catch OperationCanceledException):
/// return await _exit.StateAsync(_gate, () =&gt; State);
/// </code>
/// Dispose calls <see cref="Complete"/> so no waiter can hang. An Exit that fails
/// before it publishes (its own token cancelled) ends unexited: waiters resume
/// with the current state and later continuations run normally.
/// </summary>
public sealed class NativeActivityExit
{
    private readonly object _sync = new();
    private TaskCompletionSource _settled = NewSource();
    private int _pending;
    private bool _done;

    /// <summary>True while an Exit is in progress and after one completed (or disposal).</summary>
    public bool HasBegun
    {
        get
        {
            lock (_sync)
                return _done || _pending > 0;
        }
    }

    /// <summary>Completes once the in-progress Exit has published (or ended unexited).</summary>
    public Task Settled
    {
        get
        {
            lock (_sync)
                return _done || _pending > 0 ? _settled.Task : Task.CompletedTask;
        }
    }

    /// <summary>Marks Exit as begun. Call before cancelling any in-flight work.</summary>
    public void Begin()
    {
        lock (_sync)
            _pending++;
    }

    /// <summary>
    /// Test seam awaited by Exit after it cancelled in-flight work and before it
    /// waits on the gate. Tests hold Exit here so a cancelled continuation
    /// deterministically resumes before Exit publishes. Null in production.
    /// </summary>
    internal Func<Task>? AfterCancel { get; set; }

    /// <summary>Exit calls this between its cancels and its gate wait; see <see cref="AfterCancel"/>.</summary>
    public Task AfterCancelAsync() => AfterCancel?.Invoke() ?? Task.CompletedTask;


    /// <summary>
    /// Ends one Begin. <paramref name="exited"/> is true when Exit published its
    /// destination (or the presenter had already exited). Call after the gate is
    /// released so waiters observe the published state.
    /// </summary>
    public void End(bool exited)
    {
        TaskCompletionSource? release = null;
        lock (_sync)
        {
            if (_pending > 0)
                _pending--;
            if (exited)
                _done = true;
            if (_done)
                release = _settled;
            else if (_pending == 0)
            {
                release = _settled;
                _settled = NewSource();
            }
        }
        release?.TrySetResult();
    }

    /// <summary>Disposal: the presenter is finished; release every waiter.</summary>
    public void Complete() => End(exited: true);

    /// <summary>True once an Exit completed (or the presenter was disposed).</summary>
    public bool IsDone
    {
        get
        {
            lock (_sync)
                return _done;
        }
    }

    /// <summary>
    /// Takes <paramref name="gate"/> for a continuation that resumed off the gate
    /// and always returns holding it. While an Exit is in progress it waits for
    /// Exit to settle first. Returns false once Exit has completed: the caller may
    /// clear its own busy flags but must not publish, and returns the presenter
    /// state (the one Exit published). Returns true when no Exit has begun (or an
    /// Exit ended unexited), so the continuation proceeds normally.
    /// </summary>
    public async Task<bool> EnterAsync(SemaphoreSlim gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        while (true)
        {
            await Settled;
            await gate.WaitAsync(CancellationToken.None);
            lock (_sync)
            {
                if (_pending == 0)
                    return !_done;
            }
            // Exit began after this continuation checked; let it finish first.
            gate.Release();
        }
    }

    /// <summary>
    /// The presenter state for a cancelled or abandoned continuation: Exit's
    /// settled state once Exit has begun, otherwise the current state, read
    /// under <paramref name="gate"/>.
    /// </summary>
    public async Task<T> StateAsync<T>(SemaphoreSlim gate, Func<T> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        await EnterAsync(gate);
        try
        {
            return state();
        }
        finally
        {
            gate.Release();
        }
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
