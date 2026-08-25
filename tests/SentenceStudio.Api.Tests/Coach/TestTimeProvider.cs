namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// A controllable <see cref="TimeProvider"/> for tests. Time only moves when the test moves it, so
/// budget expiry and abandoned-run reclaim are deterministic instead of wall-clock dependent.
/// </summary>
/// <remarks>
/// <para>
/// Timers created through this provider are virtual too. Anything that schedules work on
/// <see cref="TimeProvider.CreateTimer"/> — the turn lease heartbeat, most obviously — fires
/// exactly when <see cref="Advance"/> walks the clock past its due time, and never on a real
/// thread-pool timer. Without that, a test of "the lease is renewed before it expires" would
/// either sleep for the real interval or assert nothing at all.
/// </para>
/// <para>
/// <see cref="Advance"/> steps to each due time in turn rather than jumping straight to the
/// target, so a periodic timer fires once per elapsed period and each callback observes the clock
/// value it would have observed in real time.
/// </para>
/// </remarks>
internal sealed class TestTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = new();
    private DateTimeOffset _utcNow;

    public TestTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new FakeTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward, firing every timer that comes due on the way.</summary>
    public void Advance(TimeSpan delta)
    {
        if (delta <= TimeSpan.Zero)
        {
            return;
        }

        var target = GetUtcNow().Add(delta);

        while (NextDueAtOrBefore(target) is { } next)
        {
            lock (_gate)
            {
                _utcNow = next.Due;
            }

            next.Timer.Fire();
        }

        lock (_gate)
        {
            _utcNow = target;
        }
    }

    private (FakeTimer Timer, DateTimeOffset Due)? NextDueAtOrBefore(DateTimeOffset target)
    {
        lock (_gate)
        {
            FakeTimer? winner = null;
            DateTimeOffset winningDue = default;

            foreach (var timer in _timers)
            {
                if (timer.NextDue is not { } due || due > target)
                {
                    continue;
                }

                if (winner is null || due < winningDue)
                {
                    winner = timer;
                    winningDue = due;
                }
            }

            return winner is null ? null : (winner, winningDue);
        }
    }

    private void Remove(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly TestTimeProvider _provider;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public FakeTimer(TestTimeProvider provider, TimerCallback callback, object? state)
        {
            _provider = provider;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? NextDue { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return false;
            }

            _period = period;
            NextDue = dueTime == Timeout.InfiniteTimeSpan || dueTime < TimeSpan.Zero
                ? null
                : _provider.GetUtcNow().Add(dueTime);

            return true;
        }

        public void Fire()
        {
            if (_disposed)
            {
                return;
            }

            // Rescheduled before the callback runs, so a callback that disposes or reschedules
            // the timer wins rather than being overwritten on the way out.
            NextDue = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero
                ? null
                : _provider.GetUtcNow().Add(_period);

            _callback(_state);
        }

        public void Dispose()
        {
            _disposed = true;
            NextDue = null;
            _provider.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
