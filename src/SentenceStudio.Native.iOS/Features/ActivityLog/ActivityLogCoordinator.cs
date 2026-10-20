using System.Globalization;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Native.iOS.Features.ActivityLog;

internal enum ActivityLogPhase { Closed, Loading, Empty, Ready, LoadError, Blocked }

internal sealed record ActivityLogState(
    ActivityLogPhase Phase, IReadOnlyList<ActivityLogWeek> Weeks,
    ActivityCategory? Filter, DateTime? SelectedDay,
    bool HasMore, int WeeksLoaded);

internal static class ActivityLogWeekHeader
{
    internal static string Format(ActivityLogWeek week, CultureInfo culture)
    {
        // Week dates are calendar labels for the seven cells, not UTC instants to localize.
        var start = week.WeekStart.Date;
        var end = week.WeekEnd.Date;
        return start.Year == end.Year && start.Month == end.Month
            ? $"{start.ToString("MMM d", culture)} \u2013 {end.Day.ToString(culture)}"
            : $"{start.ToString("MMM d", culture)} \u2013 {end.ToString("MMM d", culture)}";
    }
}

internal sealed class ActivityLogCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly ActivityLogReader _reader;
    private readonly DeviceUserScopeProvider _userScope;
    private readonly ILogger<ActivityLogCoordinator> _logger;
    private readonly Func<DateTime> _utcNow;
    private string? _owner;
    private DeviceUserScopeProvider.ScopeStamp _stamp;
    private ActivityLogState _state =
        new(ActivityLogPhase.Closed, [], null, null, false, 8);
    private CancellationTokenSource? _pendingRead;
    private long _generation;
    private int _viewVersion;
    private bool _open;
    private bool _disposed;

    internal ActivityLogCoordinator(
        ActivityLogReader reader, DeviceUserScopeProvider userScope,
        ILogger<ActivityLogCoordinator> logger, Func<DateTime>? utcNow = null)
    {
        _reader = reader;
        _userScope = userScope;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    internal event Action? Changed;
    internal bool IsOpen
    {
        get { lock (_gate) return _open; }
    }
    internal int ViewVersion
    {
        get { lock (_gate) return _viewVersion; }
    }
    internal ActivityLogState State
    {
        get
        {
            bool revoked;
            ActivityLogState state;
            lock (_gate)
            {
                revoked = RevokeIfStaleLocked();
                state = _state;
            }
            if (revoked)
                Changed?.Invoke();
            return state;
        }
    }

    internal async Task<bool> OpenAsync(
        string? expectedOwner, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedOwner)
            || !_userScope.TryGetUserProfileId(out var current)
            || !string.Equals(expectedOwner, current, StringComparison.Ordinal))
        {
            _logger.LogWarning("Activity Log navigation refused an absent or untrusted owner.");
            return false;
        }
        var stamp = _userScope.CaptureStamp();
        if (stamp.PublishedOwner != expectedOwner || !_userScope.IsCurrent(stamp))
            return false;

        lock (_gate)
        {
            if (_disposed || _open || !_userScope.IsCurrent(stamp))
                return false;
            _owner = expectedOwner;
            _stamp = stamp;
            _open = true;
            _state = new(ActivityLogPhase.Loading, [], null, null, false, 8);
            _userScope.ScopeChanged += OnScopeChanged;
            ++_viewVersion;
        }
        OnScopeChanged();
        await ReloadAsync(cancellationToken);
        lock (_gate)
            return IsCurrentOwnerLocked(expectedOwner, stamp);
    }

    internal Task RefreshAsync(CancellationToken cancellationToken = default) =>
        ResetAndReloadAsync(null, resetFilter: false, cancellationToken);

    internal Task FilterAsync(
        ActivityCategory? filter, CancellationToken cancellationToken = default) =>
        ResetAndReloadAsync(filter, resetFilter: true, cancellationToken);

    private async Task ResetAndReloadAsync(
        ActivityCategory? filter, bool resetFilter, CancellationToken cancellationToken)
    {
        bool revoked;
        lock (_gate)
        {
            revoked = RevokeIfStaleLocked();
            if (!revoked && IsCurrentOwnerLocked(_owner ?? "", _stamp))
                _state = _state with {
                    WeeksLoaded = 8, SelectedDay = null,
                    Filter = resetFilter ? filter : _state.Filter
                };
        }
        if (revoked)
            Changed?.Invoke();
        if (!revoked)
            await ReloadAsync(cancellationToken);
    }

    internal async Task LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        bool revoked;
        bool load;
        lock (_gate)
        {
            revoked = RevokeIfStaleLocked();
            load = !revoked && _state.HasMore
                && _state.Phase is (ActivityLogPhase.Ready or ActivityLogPhase.Empty)
                && IsCurrentOwnerLocked(_owner ?? "", _stamp);
            if (load)
                _state = _state with { WeeksLoaded = _state.WeeksLoaded + 4 };
        }
        if (revoked)
            Changed?.Invoke();
        if (load)
            await ReloadAsync(cancellationToken);
    }

    internal void ToggleDay(DateTime date)
    {
        bool changed;
        lock (_gate)
        {
            changed = RevokeIfStaleLocked();
            if (!changed && _state.Phase == ActivityLogPhase.Ready
                && IsCurrentOwnerLocked(_owner ?? "", _stamp)
                && _state.Weeks.SelectMany(week => week.Days)
                    .Any(day => day.Date.Date == date.Date
                        && day.Plans.Any(plan => plan.Items.Count > 0)))
            {
                _state = _state with {
                    SelectedDay = _state.SelectedDay?.Date == date.Date
                        ? null : date.Date
                };
                ++_viewVersion;
                changed = true;
            }
        }
        if (changed)
            Changed?.Invoke();
    }

    internal async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        string owner;
        DeviceUserScopeProvider.ScopeStamp stamp;
        ActivityCategory? filter;
        int weeksLoaded;
        long generation;
        CancellationTokenSource? previous;
        CancellationTokenSource read;
        bool revoked;
        lock (_gate)
        {
            revoked = RevokeIfStaleLocked();
            if (revoked || !IsCurrentOwnerLocked(_owner ?? "", _stamp))
                return;
            owner = _owner!;
            stamp = _stamp;
            filter = _state.Filter;
            weeksLoaded = _state.WeeksLoaded;
            generation = ++_generation;
            previous = _pendingRead;
            read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pendingRead = read;
            _state = _state with { Phase = ActivityLogPhase.Loading };
            ++_viewVersion;
        }
        CancelIfActive(previous);
        Changed?.Invoke();
        try
        {
            var result = await _reader.ReadAsync(
                owner, _utcNow(), weeksLoaded, filter, read.Token);
            bool applied;
            lock (_gate)
            {
                applied = RevokeIfStaleLocked();
                if (!applied && generation == _generation
                    && IsCurrentOwnerLocked(owner, stamp))
                {
                    _state = _state with {
                        Phase = result.Weeks.Count == 0
                            ? ActivityLogPhase.Empty : ActivityLogPhase.Ready,
                        Weeks = result.Weeks, HasMore = result.HasMore
                    };
                    ++_viewVersion;
                    applied = true;
                }
            }
            if (applied)
                Changed?.Invoke();
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested)
        {
            // Another filter, owner transition or Back has superseded this read.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Owned Activity Log could not be loaded.");
            bool applied;
            lock (_gate)
            {
                applied = RevokeIfStaleLocked();
                if (!applied && generation == _generation
                    && IsCurrentOwnerLocked(owner, stamp))
                {
                    _state = _state with {
                        Phase = ActivityLogPhase.LoadError,
                        Weeks = [], HasMore = false, SelectedDay = null
                    };
                    ++_viewVersion;
                    applied = true;
                }
            }
            if (applied)
                Changed?.Invoke();
        }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_pendingRead, read))
                    _pendingRead = null;
            read.Dispose();
        }
    }

    internal void Close()
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            if (_open)
                _userScope.ScopeChanged -= OnScopeChanged;
            pending = _pendingRead;
            _pendingRead = null;
            _owner = null;
            _stamp = default;
            _open = false;
            _state = new(ActivityLogPhase.Closed, [], null, null, false, 8);
            ++_generation;
            ++_viewVersion;
        }
        CancelIfActive(pending);
    }

    private bool IsCurrentOwnerLocked(
        string owner, DeviceUserScopeProvider.ScopeStamp stamp) =>
        !_disposed && _open && !string.IsNullOrWhiteSpace(owner)
        && _owner == owner && stamp.PublishedOwner == owner
        && _userScope.IsCurrent(stamp)
        && _userScope.TryGetUserProfileId(out var current)
        && current == owner;

    private void OnScopeChanged()
    {
        bool revoked;
        CancellationTokenSource? pending;
        lock (_gate)
        {
            revoked = RevokeIfStaleLocked();
            pending = revoked ? _pendingRead : null;
        }
        CancelIfActive(pending);
        if (revoked)
            Changed?.Invoke();
    }

    private bool RevokeIfStaleLocked()
    {
        if (!_open || _disposed || _state.Phase == ActivityLogPhase.Blocked
            || IsCurrentOwnerLocked(_owner ?? "", _stamp))
            return false;
        _logger.LogWarning("Activity Log discarded after owner scope changed.");
        _owner = null;
        _stamp = default;
        _state = new(ActivityLogPhase.Blocked, [], null, null, false, 8);
        ++_generation;
        ++_viewVersion;
        return true;
    }

    private static void CancelIfActive(CancellationTokenSource? pending)
    {
        try
        {
            pending?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A superseded read may have disposed its token after the lock.
        }
    }

    public void Dispose()
    {
        Close();
        lock (_gate)
        {
            _disposed = true;
            Changed = null;
        }
    }
}
