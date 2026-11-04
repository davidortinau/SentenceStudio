using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Profiles;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Features.Profile;

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

internal enum ProfileSaveStatus { Idle, Saving, Saved, Failed }

internal sealed record ProfileState(
    ProfilePhase Phase, ProfileDraft Draft, bool ShowDeleteConfirmation,
    ProfileSaveStatus SaveStatus = ProfileSaveStatus.Idle, string? SaveMessageKey = null)
{
    internal bool IsSaving => SaveStatus == ProfileSaveStatus.Saving;
    internal bool CanSave => Phase == ProfilePhase.Ready && !IsSaving;
}

internal sealed class ProfileCoordinator : IDisposable
{
    internal const string SaveInvalidKey = "NativeProfile_SaveInvalid";
    internal const string SaveRejectedKey = "NativeProfile_SaveRejected";
    internal const string SaveFailedKey = "NativeProfile_SaveFailed";
    internal const string SaveLocalFailedKey = "NativeProfile_SaveLocalFailed";
    internal const string SaveUnavailableKey = "NativeProfile_SaveUnavailable";
    internal const string SavedKey = "Profile_Saved";
    private const int MaxNameLength = 200;

    private readonly object _gate = new();
    private readonly UserProfileRepository _repository;
    private readonly DeviceUserScopeProvider _userScope;
    private readonly INativeProfileRemoteStore _remote;
    private readonly ILogger<ProfileCoordinator> _logger;
    private string? _owner;
    private DeviceUserScopeProvider.ScopeStamp _stamp;
    private ProfileDraft _draft = ProfileDraft.Empty;
    // The values as last loaded or saved; an unchanged out-of-list value may be kept.
    private ProfileDraft _stored = ProfileDraft.Empty;
    private ProfilePhase _phase = ProfilePhase.Closed;
    private long _loadGeneration;
    private bool _deleteRequested;
    private bool _open;
    private bool _disposed;
    private int _viewVersion;
    private ProfileSaveStatus _saveStatus;
    private string? _saveMessageKey;
    private Task<bool>? _inflightSave;

    internal ProfileCoordinator(
        UserProfileRepository repository, DeviceUserScopeProvider userScope,
        ILogger<ProfileCoordinator> logger, INativeProfileRemoteStore? remote = null)
    {
        _repository = repository;
        _userScope = userScope;
        _logger = logger;
        // Native profile sync is download-only: without the owner-checked API
        // there is no safe save, so Save reports Unavailable instead of writing.
        _remote = remote ?? new UnavailableNativeProfileRemoteStore();
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
                state = new(_phase, _draft, _deleteRequested, _saveStatus, _saveMessageKey);
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
                _stored = ProfileDraft.Empty;
                _deleteRequested = false;
                _saveStatus = ProfileSaveStatus.Idle;
                _saveMessageKey = null;
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
                _stored = draft;
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
        NativeOnboardingOptions.DisplayLanguages.Contains(value)
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

    /// <summary>
    /// Saves the draft's editable fields: the server first (native profile sync is
    /// download-only, so a local-only change would be reverted by the next download),
    /// then the local row with exactly the values the server stored. Concurrent calls
    /// share one in-flight save. Returns true only when both writes succeeded for the
    /// owner that is still active.
    /// </summary>
    internal async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool>? flight = null;
        Task<bool>? running;
        lock (_gate)
        {
            running = _inflightSave;
            if (running is null)
            {
                flight = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _inflightSave = flight.Task;
            }
        }
        if (running is not null)
            return await running;

        try
        {
            var saved = await SaveOnceAsync(cancellationToken);
            flight!.TrySetResult(saved);
            return saved;
        }
        catch
        {
            flight!.TrySetResult(false);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inflightSave, flight!.Task))
                    _inflightSave = null;
            }
        }
    }

    private async Task<bool> SaveOnceAsync(CancellationToken cancellationToken)
    {
        string owner;
        DeviceUserScopeProvider.ScopeStamp stamp;
        long generation;
        ProfileDraft draft;
        bool revoked;
        bool invalid = false;
        bool started = false;
        lock (_gate)
        {
            revoked = RevokeIfStaleLocked();
            owner = _owner ?? string.Empty;
            stamp = _stamp;
            generation = _loadGeneration;
            draft = _draft;
            if (!revoked && _phase == ProfilePhase.Ready
                && IsCurrentOwnerLocked(owner, stamp))
            {
                invalid = !IsSavable(draft, _stored);
                _saveStatus = invalid ? ProfileSaveStatus.Failed : ProfileSaveStatus.Saving;
                _saveMessageKey = invalid ? SaveInvalidKey : null;
                started = !invalid;
                ++_viewVersion;
            }
        }
        if (revoked || invalid || started)
            Changed?.Invoke();
        if (!started)
        {
            if (!invalid)
                _logger.LogWarning("Profile save refused without an open owned profile.");
            return false;
        }

        var fields = new NativeProfileFields(draft.Name.Trim(), draft.NativeLanguage,
            draft.TargetLanguage, draft.DisplayLanguage, draft.PreferredSessionMinutes,
            draft.TargetCefrLevel);
        NativeProfileSaveResult remote;
        try
        {
            remote = await _remote.SaveProfileAsync(owner, fields, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FinishSave(owner, stamp, generation, ProfileSaveStatus.Idle, null, null);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Profile save failed before the server accepted it.");
            FinishSave(owner, stamp, generation, ProfileSaveStatus.Failed, SaveFailedKey, null);
            return false;
        }

        if (remote.Outcome != NativeProfileRemoteOutcome.Saved || remote.Saved is null)
        {
            _logger.LogWarning("Profile save was not accepted by the server ({Outcome}).",
                remote.Outcome);
            FinishSave(owner, stamp, generation, ProfileSaveStatus.Failed,
                remote.Outcome switch
                {
                    NativeProfileRemoteOutcome.TransportFailure => NativeText.ConnectionError,
                    NativeProfileRemoteOutcome.Unavailable => SaveUnavailableKey,
                    _ => SaveRejectedKey
                }, null);
            return false;
        }

        // The owner may have changed while the request was in flight. The server
        // already holds the change for the original owner; that owner's next sync
        // download brings it to this device, so nothing is written here.
        if (!IsSaveCurrent(owner, stamp, generation))
            return false;

        var accepted = remote.Saved;
        int local;
        try
        {
            local = await _repository.SaveEditableProfileForOwnerAsync(owner, accepted.Name,
                accepted.NativeLanguage, accepted.TargetLanguage, accepted.DisplayLanguage,
                accepted.PreferredSessionMinutes, accepted.TargetCefrLevel,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FinishSave(owner, stamp, generation, ProfileSaveStatus.Idle, null, null);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Profile was saved on the server but the local row was not updated.");
            FinishSave(owner, stamp, generation, ProfileSaveStatus.Failed, SaveLocalFailedKey, null);
            return false;
        }
        if (local < 0)
        {
            _logger.LogWarning(
                "Profile was saved on the server but the local row was not updated ({Result}).",
                local);
            FinishSave(owner, stamp, generation, ProfileSaveStatus.Failed, SaveLocalFailedKey, null);
            return false;
        }

        var savedDraft = new ProfileDraft(accepted.Name, remote.Email ?? draft.Email,
            accepted.NativeLanguage, accepted.TargetLanguage, accepted.DisplayLanguage,
            accepted.PreferredSessionMinutes, accepted.TargetCefrLevel);
        return FinishSave(owner, stamp, generation, ProfileSaveStatus.Saved, SavedKey,
            savedDraft);
    }

    /// <summary>
    /// Same rule as PUT /api/v1/profile/me: each list field is a shared
    /// <see cref="LearnerProfileOptions"/> choice or equals the stored value, so an
    /// older out-of-list value never blocks saving a name change.
    /// </summary>
    internal static bool IsSavable(ProfileDraft draft, ProfileDraft stored)
    {
        var name = draft.Name?.Trim() ?? string.Empty;
        return name.Length > 0
            && (name.Length <= MaxNameLength
                || string.Equals(name, stored.Name, StringComparison.Ordinal))
            && LearnerProfileOptions.IsAcceptable(LearnerProfileOptions.Languages,
                draft.NativeLanguage, stored.NativeLanguage)
            && LearnerProfileOptions.IsAcceptable(LearnerProfileOptions.Languages,
                draft.TargetLanguage, stored.TargetLanguage)
            && LearnerProfileOptions.IsAcceptable(LearnerProfileOptions.DisplayLanguages,
                draft.DisplayLanguage, stored.DisplayLanguage)
            && LearnerProfileOptions.IsAcceptableSessionMinutes(
                draft.PreferredSessionMinutes, stored.PreferredSessionMinutes)
            && LearnerProfileOptions.IsAcceptable(LearnerProfileOptions.CefrLevels,
                draft.TargetCefrLevel, stored.TargetCefrLevel);
    }

    private bool IsSaveCurrent(
        string owner, DeviceUserScopeProvider.ScopeStamp stamp, long generation)
    {
        bool revoked;
        bool current;
        lock (_gate)
        {
            revoked = RevokeIfStaleLocked();
            current = !revoked && generation == _loadGeneration
                && IsCurrentOwnerLocked(owner, stamp);
        }
        if (revoked)
            Changed?.Invoke();
        if (!current)
            _logger.LogWarning("Profile save result discarded after the owner or screen changed.");
        return current;
    }

    private bool FinishSave(string owner, DeviceUserScopeProvider.ScopeStamp stamp,
        long generation, ProfileSaveStatus status, string? messageKey, ProfileDraft? draft)
    {
        bool changed;
        var applied = false;
        lock (_gate)
        {
            changed = RevokeIfStaleLocked();
            if (!changed && generation == _loadGeneration
                && IsCurrentOwnerLocked(owner, stamp))
            {
                _saveStatus = status;
                _saveMessageKey = messageKey;
                if (draft is not null)
                    _draft = _stored = draft;
                ++_viewVersion;
                changed = true;
                applied = status == ProfileSaveStatus.Saved;
            }
        }
        if (changed)
            Changed?.Invoke();
        return applied;
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
        _stored = ProfileDraft.Empty;
        _deleteRequested = false;
        _saveStatus = ProfileSaveStatus.Idle;
        _saveMessageKey = null;
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
            // Edits wait for an in-flight save: its result replaces the draft.
            applied = !revoked && _phase == ProfilePhase.Ready
                && _saveStatus != ProfileSaveStatus.Saving
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
        _stored = ProfileDraft.Empty;
        _deleteRequested = false;
        _saveStatus = ProfileSaveStatus.Idle;
        _saveMessageKey = null;
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
