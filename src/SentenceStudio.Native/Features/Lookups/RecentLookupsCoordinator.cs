using System.Globalization;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Features.Lookups;

internal enum RecentLookupsPhase { Closed, Loading, Empty, Ready, LoadError, Blocked }

/// <summary>One lookup as the native lists show it, with the resources it can be added to.</summary>
internal sealed record RecentLookupRow(
    string Id, string AutomationKey, string Text, string Translation, string Activity,
    string? ResourceTitle, DateTime LastLookedUpAt,
    IReadOnlyList<VocabularyLookupDestination> Destinations);

internal sealed record RecentLookupsState(
    RecentLookupsPhase Phase, IReadOnlyList<RecentLookupRow> Rows, string? AddingId,
    NativeMessage? Notice)
{
    internal static RecentLookupsState Closed { get; } = new(RecentLookupsPhase.Closed, [], null, null);
}

/// <summary>Stable automation IDs (Ailoha, screen readers) shared by both heads.</summary>
internal static class RecentLookupsAccess
{
    public const string Open = "native.vocabulary.lookups.open";
    public const string Page = "native.lookups.page";
    public const string Status = "native.lookups.status";
    public const string Retry = "native.lookups.retry";
    public const string List = "native.lookups.list";
    public const string Notice = "native.lookups.notice";

    public static string Row(RecentLookupRow row) => $"native.lookups.row.{row.AutomationKey}";
    public static string Add(RecentLookupRow row) => $"native.lookups.add.{row.AutomationKey}";
    public static string Destination(RecentLookupRow row, VocabularyLookupDestination destination) =>
        $"native.lookups.add.{row.AutomationKey}.{Key(destination.ResourceId)}";

    /// <summary>"lk:abc" becomes "lk-abc".</summary>
    public static string Key(string id) =>
        new(id.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
}

/// <summary>
/// Recent lookups (Captain 2026-11-04, decision 4 B) for the native Vocabulary screen on iOS and
/// Android: the learner's lookups newest first, each with "Add to vocabulary" into one of their
/// resources. Every read and write is bound to the owner it opened for and stops when that owner
/// is no longer active (the list is then blocked and emptied). Thread-safe.
/// </summary>
internal sealed class RecentLookupsCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly VocabularyLookupRepository _lookups;
    private readonly DeviceUserScopeProvider _userScope;
    private readonly NativePendingWrites _pendingWrites;
    private readonly ILogger<RecentLookupsCoordinator> _logger;
    private string? _owner;
    private DeviceUserScopeProvider.ScopeStamp _stamp;
    private RecentLookupsState _state = RecentLookupsState.Closed;
    private long _generation;
    private int _viewVersion;
    private bool _open;
    private bool _disposed;

    internal RecentLookupsCoordinator(
        VocabularyLookupRepository lookups, DeviceUserScopeProvider userScope,
        NativePendingWrites pendingWrites, ILogger<RecentLookupsCoordinator> logger)
    {
        _lookups = lookups;
        _userScope = userScope;
        _pendingWrites = pendingWrites;
        _logger = logger;
    }

    internal event Action? Changed;

    internal bool IsOpen
    {
        get { lock (_gate) return _open; }
    }

    /// <summary>Bumped on every visible change; heads re-render when it moves.</summary>
    internal int ViewVersion
    {
        get { lock (_gate) return _viewVersion; }
    }

    /// <summary>True while an add is running (Back waits).</summary>
    internal bool IsBusy
    {
        get { lock (_gate) return _state.AddingId is not null; }
    }

    internal RecentLookupsState State
    {
        get
        {
            bool revoked;
            RecentLookupsState state;
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

    internal async Task<bool> OpenAsync(string? expectedOwner, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedOwner)
            || !_userScope.TryGetUserProfileId(out var current)
            || !string.Equals(expectedOwner, current, StringComparison.Ordinal))
        {
            _logger.LogWarning("Recent lookups navigation refused an absent or untrusted owner.");
            return false;
        }
        var stamp = _userScope.CaptureStamp();
        lock (_gate)
        {
            if (_disposed || _open || stamp.PublishedOwner != expectedOwner || !_userScope.IsCurrent(stamp))
                return false;
            _owner = expectedOwner;
            _stamp = stamp;
            _open = true;
            _state = new(RecentLookupsPhase.Loading, [], null, null);
            _userScope.ScopeChanged += OnScopeChanged;
            ++_viewVersion;
        }
        Changed?.Invoke();
        await ReloadAsync(cancellationToken);
        lock (_gate)
            return IsCurrentOwnerLocked(expectedOwner, stamp);
    }

    internal async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        string owner;
        DeviceUserScopeProvider.ScopeStamp stamp;
        long generation;
        RecentLookupsState previous;
        lock (_gate)
        {
            if (RevokeIfStaleLocked() || !IsCurrentOwnerLocked(_owner ?? "", _stamp))
                return;
            owner = _owner!;
            stamp = _stamp;
            generation = ++_generation;
            previous = _state;
            _state = _state with { Phase = RecentLookupsPhase.Loading, Notice = null };
            ++_viewVersion;
        }
        Changed?.Invoke();
        RecentLookupsState next;
        try
        {
            var page = await _lookups.GetRecentAsync(owner, cancellationToken: cancellationToken);
            var rows = page.Items.Select(item => new RecentLookupRow(
                item.Lookup.Id, RecentLookupsAccess.Key(item.Lookup.Id), item.Lookup.Text,
                item.Lookup.Translation, item.Lookup.Activity, item.ResourceTitle,
                item.Lookup.LastLookedUpAt, page.DestinationsFor(item.Lookup))).ToArray();
            next = new(rows.Length == 0 ? RecentLookupsPhase.Empty : RecentLookupsPhase.Ready,
                rows, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Never leave the spinner behind: keep the list shown before, or offer Retry.
            _logger.LogInformation("Recent lookups load was cancelled.");
            next = previous.Phase is RecentLookupsPhase.Ready or RecentLookupsPhase.Empty
                ? previous with { Notice = null }
                : new(RecentLookupsPhase.LoadError, [], null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recent lookups could not be loaded.");
            next = new(RecentLookupsPhase.LoadError, [], null, null);
        }
        bool applied;
        lock (_gate)
        {
            applied = !RevokeIfStaleLocked() && generation == _generation
                && IsCurrentOwnerLocked(owner, stamp);
            if (applied)
            {
                // An add still running keeps its marker, so a second add cannot start meanwhile.
                _state = next with { AddingId = _state.AddingId };
                ++_viewVersion;
            }
        }
        if (applied)
            Changed?.Invoke();
    }

    /// <summary>
    /// "Add to vocabulary": creates or links the learner's word in <paramref name="resourceId"/>
    /// (one of the row's destinations). One add runs at a time.
    /// </summary>
    internal async Task AddAsync(string lookupId, string resourceId, CancellationToken cancellationToken = default)
    {
        string owner;
        DeviceUserScopeProvider.ScopeStamp stamp;
        VocabularyLookupDestination destination;
        lock (_gate)
        {
            if (RevokeIfStaleLocked() || !IsCurrentOwnerLocked(_owner ?? "", _stamp)
                || _state.Phase != RecentLookupsPhase.Ready || _state.AddingId is not null)
                return;
            var row = _state.Rows.FirstOrDefault(item => item.Id == lookupId);
            var match = row?.Destinations.FirstOrDefault(item => item.ResourceId == resourceId);
            if (match is null)
                return;
            destination = match;
            owner = _owner!;
            stamp = _stamp;
            _state = _state with { AddingId = lookupId, Notice = null };
            ++_viewVersion;
        }
        Changed?.Invoke();

        NativeMessage notice;
        try
        {
            var status = await _lookups.AddToVocabularyAsync(owner, lookupId, resourceId, cancellationToken);
            switch (status)
            {
                case ResourceVocabularyWriteStatus.Success:
                    _pendingWrites.MarkPending(owner);
                    notice = NativeMessage.Success("Lookups_Added", destination.Title);
                    break;
                case ResourceVocabularyWriteStatus.AlreadyPresent:
                    notice = NativeMessage.Info("Lookups_AlreadyPresent", destination.Title);
                    break;
                default:
                    _logger.LogWarning("Add to vocabulary from a lookup was refused: {Status}.", status);
                    notice = new(NativeMessageKind.Error, "Lookups_AddFailed");
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            notice = new(NativeMessageKind.Error, "Lookups_AddFailed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Add to vocabulary from a lookup failed.");
            notice = new(NativeMessageKind.Error, "Lookups_AddFailed");
        }
        bool applied;
        lock (_gate)
        {
            applied = !RevokeIfStaleLocked() && IsCurrentOwnerLocked(owner, stamp);
            if (applied)
            {
                _state = _state with { AddingId = null, Notice = notice };
                ++_viewVersion;
            }
        }
        if (applied)
            Changed?.Invoke();
    }

    /// <summary>
    /// A head's action failed unexpectedly outside this coordinator's own error handling:
    /// the page says so (and any add in progress ends) instead of failing silently.
    /// </summary>
    internal void ReportUnexpectedFailure()
    {
        lock (_gate)
        {
            if (!_open)
                return;
            // A failure during a load never leaves the spinner: the load error offers Retry.
            _state = _state with {
                Phase = _state.Phase == RecentLookupsPhase.Loading
                    ? RecentLookupsPhase.LoadError : _state.Phase,
                AddingId = null,
                Notice = new NativeMessage(NativeMessageKind.Error, NativeText.UnexpectedError)
            };
            ++_viewVersion;
        }
        Changed?.Invoke();
    }

    internal void DismissNotice()
    {
        lock (_gate)
        {
            if (_state.Notice is null)
                return;
            _state = _state with { Notice = null };
            ++_viewVersion;
        }
        Changed?.Invoke();
    }

    internal void Close()
    {
        lock (_gate)
        {
            if (_open)
                _userScope.ScopeChanged -= OnScopeChanged;
            _owner = null;
            _stamp = default;
            _open = false;
            _state = RecentLookupsState.Closed;
            ++_generation;
            ++_viewVersion;
        }
    }

    /// <summary>"Reading · Story · 11/4/2026 9:00 AM": activity, resource, local time.</summary>
    internal static string SourceLine(
        RecentLookupRow row, Func<string, string> text, CultureInfo culture, TimeZoneInfo zone)
    {
        var parts = new List<string>
        {
            text(row.Activity == VocabularyLookupActivity.Reading ? "Activity_Reading" : "Activity_Writing")
        };
        if (!string.IsNullOrWhiteSpace(row.ResourceTitle))
            parts.Add(row.ResourceTitle);
        parts.Add(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(row.LastLookedUpAt, DateTimeKind.Utc), zone).ToString("g", culture));
        return string.Join(" · ", parts);
    }

    private bool IsCurrentOwnerLocked(string owner, DeviceUserScopeProvider.ScopeStamp stamp) =>
        !_disposed && _open && !string.IsNullOrWhiteSpace(owner)
        && _owner == owner && stamp.PublishedOwner == owner
        && _userScope.IsCurrent(stamp)
        && _userScope.TryGetUserProfileId(out var current) && current == owner;

    private void OnScopeChanged()
    {
        bool revoked;
        lock (_gate)
            revoked = RevokeIfStaleLocked();
        if (revoked)
            Changed?.Invoke();
    }

    private bool RevokeIfStaleLocked()
    {
        if (!_open || _disposed || _state.Phase == RecentLookupsPhase.Blocked
            || IsCurrentOwnerLocked(_owner ?? "", _stamp))
            return false;
        _logger.LogWarning("Recent lookups discarded after the owner scope changed.");
        _owner = null;
        _stamp = default;
        _state = new(RecentLookupsPhase.Blocked, [], null, null);
        ++_generation;
        ++_viewVersion;
        return true;
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
