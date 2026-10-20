using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Features.Profile;

internal enum ProfilePhase { Closed, Loading, Ready, Missing, LoadError, Blocked }

internal sealed record ProfileDraft(
    string Name, string Email, string NativeLanguage, string TargetLanguage,
    string DisplayLanguage, int PreferredSessionMinutes, string TargetCefrLevel)
{
    internal static ProfileDraft Empty { get; } =
        new("", "", "English", "Korean", "English", 15, "A2");

    internal static ProfileDraft FromProfile(UserProfile profile) =>
        new(profile.Name ?? "", profile.Email ?? "",
            string.IsNullOrWhiteSpace(profile.NativeLanguage)
                ? "English" : profile.NativeLanguage,
            string.IsNullOrWhiteSpace(profile.TargetLanguage)
                ? "Korean" : profile.TargetLanguage,
            string.IsNullOrWhiteSpace(profile.DisplayLanguage)
                ? "English" : profile.DisplayLanguage,
            profile.PreferredSessionMinutes > 0
                ? profile.PreferredSessionMinutes : 15,
            string.IsNullOrWhiteSpace(profile.TargetCEFRLevel)
                ? "A2" : profile.TargetCEFRLevel);
}

internal sealed record ProfileState(
    ProfilePhase Phase, ProfileDraft Draft, bool ShowDeleteConfirmation);

internal sealed class ProfileCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly UserProfileRepository _repository;
    private readonly DeviceUserScopeProvider _userScope;
    private readonly ILogger<ProfileCoordinator> _logger;
    private string? _owner;
    private DeviceUserScopeProvider.ScopeStamp _stamp;
    private ProfileDraft _draft = ProfileDraft.Empty;
    private ProfilePhase _phase = ProfilePhase.Closed;
    private long _loadGeneration;
    private bool _deleteRequested;
    private bool _open;
    private bool _disposed;
    private int _viewVersion;

    internal ProfileCoordinator(
        UserProfileRepository repository, DeviceUserScopeProvider userScope,
        ILogger<ProfileCoordinator> logger)
    {
        _repository = repository;
        _userScope = userScope;
        _logger = logger;
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
    internal ProfileState State
    {
        get
        {
            ProfileState state;
            bool revoked;
            lock (_gate)
            {
                revoked = RevokeIfStaleLocked();
                state = new(_phase, _draft, _deleteRequested);
            }
            if (revoked)
                Changed?.Invoke();
            return state;
        }
    }

    internal async Task<bool> OpenAsync(
        string? expectedOwner, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed || _open)
                return false;
        }
        if (string.IsNullOrWhiteSpace(expectedOwner)
            || !_userScope.TryGetUserProfileId(out var activeOwner)
            || !string.Equals(expectedOwner, activeOwner, StringComparison.Ordinal))
        {
            _logger.LogWarning("Profile navigation refused an absent or untrusted owner.");
            return false;
        }
        var stamp = _userScope.CaptureStamp();
        if (stamp.PublishedOwner != expectedOwner || !_userScope.IsCurrent(stamp))
        {
            _logger.LogWarning("Profile navigation refused a changed owner.");
            return false;
        }

        lock (_gate)
        {
            if (_disposed || _open)
                return false;
            _owner = expectedOwner;
            _stamp = stamp;
            _open = true;
            _userScope.ScopeChanged += OnScopeChanged;
        }
        OnScopeChanged();
        await ReloadAsync(cancellationToken);
        lock (_gate)
            return IsCurrentOwnerLocked(expectedOwner, stamp)
                && _phase == ProfilePhase.Ready;
    }

    internal async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        string owner;
        DeviceUserScopeProvider.ScopeStamp stamp;
        long generation = 0;
        bool revoked;
        lock (_gate)
        {
            revoked = RevokeIfStaleLocked();
            owner = _owner ?? string.Empty;
            stamp = _stamp;
            if (!revoked && IsCurrentOwnerLocked(owner, stamp))
            {
                generation = ++_loadGeneration;
                _draft = ProfileDraft.Empty;
                _deleteRequested = false;
                _phase = ProfilePhase.Loading;
                ++_viewVersion;
            }
        }
        if (revoked || generation != 0)
            Changed?.Invoke();
        if (generation == 0)
            return;

        ProfileDraft draft;
        ProfilePhase phase;
        try
        {
            // Exact-ID lookup only; never use GetAsync, which runs legacy backfills
            // and can seed unrelated data while opening a read-only form.
            var profile = await _repository.GetByIdAsync(owner, cancellationToken);
            draft = profile is null
                ? ProfileDraft.Empty : ProfileDraft.FromProfile(profile);
            phase = profile is null ? ProfilePhase.Missing : ProfilePhase.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Owned profile could not be loaded.");
            draft = ProfileDraft.Empty;
            phase = ProfilePhase.LoadError;
        }

        bool changed;
        lock (_gate)
        {
            changed = RevokeIfStaleLocked();
            if (!changed && generation == _loadGeneration
                && IsCurrentOwnerLocked(owner, stamp))
            {
                _draft = draft;
                _phase = phase;
                ++_viewVersion;
                changed = true;
            }
        }
        if (changed)
            Changed?.Invoke();
    }

    internal bool SetName(string value) =>
        Edit(draft => draft with { Name = value });

    internal bool SetEmail(string value) =>
        Edit(draft => draft with { Email = value });

    internal bool SetNativeLanguage(string value) =>
        NativeOnboardingOptions.NativeLanguages.Contains(value)
        && Edit(draft => draft with { NativeLanguage = value });

    internal bool SetTargetLanguage(string value) =>
        NativeOnboardingOptions.TargetLanguages.Contains(value)
        && Edit(draft => draft with { TargetLanguage = value });

    internal bool SetDisplayLanguage(string value) =>
        value is ("English" or "Korean")
        && Edit(draft => draft with { DisplayLanguage = value });

    internal bool SetSessionMinutes(int value) =>
        NativeOnboardingOptions.SessionMinutes.Contains(value)
        && Edit(draft => draft with { PreferredSessionMinutes = value });

    internal bool SetTargetCefrLevel(string value) =>
        NativeOnboardingOptions.CefrLevels.Contains(value)
        && Edit(draft => draft with { TargetCefrLevel = value });

    internal void RequestDelete()
    {
        bool changed;
        lock (_gate)
        {
            changed = RevokeIfStaleLocked();
            if (!changed && _phase == ProfilePhase.Ready
                && IsCurrentOwnerLocked(_owner ?? string.Empty, _stamp))
            {
                _deleteRequested = true;
                changed = true;
            }
        }
        if (changed)
            Changed?.Invoke();
    }

    internal void CancelDelete()
    {
        bool changed;
        lock (_gate)
        {
            changed = RevokeIfStaleLocked();
            if (!changed && _deleteRequested)
            {
                _deleteRequested = false;
                changed = true;
            }
        }
        if (changed)
            Changed?.Invoke();
    }

    internal void Close()
    {
        lock (_gate)
            CloseLocked();
    }

    private void CloseLocked()
    {
        if (_open)
            _userScope.ScopeChanged -= OnScopeChanged;
        _open = false;
        _owner = null;
        _stamp = default;
        _draft = ProfileDraft.Empty;
        _deleteRequested = false;
        _phase = ProfilePhase.Closed;
        ++_loadGeneration;
    }

    private bool Edit(Func<ProfileDraft, ProfileDraft> edit)
    {
        bool revoked;
        bool applied;
        lock (_gate)
        {
            revoked = RevokeIfStaleLocked();
            applied = !revoked && _phase == ProfilePhase.Ready
                && IsCurrentOwnerLocked(_owner ?? string.Empty, _stamp);
            if (applied)
                _draft = edit(_draft);
        }
        if (revoked)
            Changed?.Invoke();
        return applied;
    }

    private bool IsCurrentOwnerLocked(
        string owner, DeviceUserScopeProvider.ScopeStamp stamp) =>
        !_disposed && _open && !string.IsNullOrWhiteSpace(owner)
        && _owner == owner && stamp.PublishedOwner == owner
        && _userScope.IsCurrent(stamp)
        && _userScope.TryGetUserProfileId(out var activeOwner)
        && activeOwner == owner;

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
        if (!_open || _disposed || (_phase == ProfilePhase.Blocked && _owner is null)
            || IsCurrentOwnerLocked(_owner ?? string.Empty, _stamp))
            return false;
        _logger.LogWarning("Profile draft discarded after owner scope changed.");
        _owner = null;
        _stamp = default;
        _draft = ProfileDraft.Empty;
        _deleteRequested = false;
        _phase = ProfilePhase.Blocked;
        ++_loadGeneration;
        ++_viewVersion;
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            CloseLocked();
            Changed = null;
        }
    }
}
