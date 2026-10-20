using System.Data.Common;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Features.Skills;

internal enum SkillsListPhase { Loading, Empty, Populated, LoadError, Blocked }

internal enum SkillFormPhase
{
    None, Loading, Creating, Editing, NotFound, LoadError,
    Saving, SaveError, Deleting, DeleteError, Blocked
}

internal sealed record SkillsListState(
    SkillsListPhase Phase, IReadOnlyList<SkillProfile> Profiles,
    string? ErrorKey = null, string? FeedbackKey = null);

internal sealed record SkillProfileFormState(
    SkillFormPhase Phase, bool Creating, string Title, string Description,
    DateTime? CreatedAt, DateTime? UpdatedAt, string? ErrorKey,
    bool ShowDeleteConfirmation);

internal sealed class SkillsCoordinator : IDisposable
{
    private readonly SkillProfileRepository _repository;
    private readonly DeviceUserScopeProvider _userScope;
    private readonly ILogger<SkillsCoordinator> _logger;
    private string? _owner;
    private DeviceUserScopeProvider.ScopeStamp _ownerStamp;
    private IReadOnlyList<SkillProfile> _profiles = [];
    private SkillsListPhase _listPhase = SkillsListPhase.Loading;
    private SkillFormPhase _formPhase;
    private string? _listErrorKey;
    private string? _listFeedbackKey;
    private string? _formErrorKey;
    private string? _formId;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private DateTime? _createdAt;
    private DateTime? _updatedAt;
    private long _listGeneration;
    private long _formGeneration;
    private bool _open;
    private bool _formOpen;
    private bool _creating;
    private bool _pending;
    private bool _confirmDelete;
    private bool _disposed;

    internal SkillsCoordinator(
        SkillProfileRepository repository,
        DeviceUserScopeProvider userScope,
        ILogger<SkillsCoordinator> logger)
    {
        _repository = repository;
        _userScope = userScope;
        _logger = logger;
    }

    internal event Action? Changed;
    internal bool IsOpen => _open;
    internal bool IsFormOpen => _formOpen;
    internal bool IsPending => _pending;
    internal bool IsDeletePending => _formPhase == SkillFormPhase.Deleting;
    internal bool IsDeleteConfirmationPresented => _confirmDelete;
    internal long ListVersion { get; private set; }
    internal long FormVersion { get; private set; }
    internal SkillsListState ListState =>
        new(_listPhase, _profiles, _listErrorKey, _listFeedbackKey);
    internal SkillProfileFormState FormState =>
        new(_formPhase, _creating, _title, _description, _createdAt,
            _updatedAt, _formErrorKey, _confirmDelete);

    internal async Task<bool> OpenAsync(string? expectedOwner)
    {
        if (_disposed)
            return false;
        if (string.IsNullOrWhiteSpace(expectedOwner)
            || !_userScope.TryGetUserProfileId(out var activeOwner)
            || activeOwner != expectedOwner)
        {
            _logger.LogWarning("Skills navigation refused an absent or untrusted owner.");
            InvalidateOwner();
            return false;
        }
        var stamp = _userScope.CaptureStamp();
        if (stamp.PublishedOwner != expectedOwner || !_userScope.IsCurrent(stamp))
        {
            _logger.LogWarning("Skills navigation refused a changed owner.");
            InvalidateOwner();
            return false;
        }

        _owner = expectedOwner;
        _ownerStamp = stamp;
        _open = true;
        _formOpen = false;
        _profiles = [];
        _listFeedbackKey = null;
        _listErrorKey = null;
        _listPhase = SkillsListPhase.Loading;
        Interlocked.Increment(ref _formGeneration);
        Notify(list: true, form: true);
        await ReloadAsync();
        return IsCurrentOwner(expectedOwner, stamp);
    }

    internal async Task ReloadAsync()
    {
        if (!TryCurrentOwner(out var owner, out var stamp))
            return;
        var generation = Interlocked.Increment(ref _listGeneration);
        _listPhase = SkillsListPhase.Loading;
        _listErrorKey = null;
        Notify(list: true);
        try
        {
            var profiles = await _repository.ListAsync(owner);
            if (!IsCurrentOwner(owner, stamp))
            {
                InvalidateOwner();
                return;
            }
            if (generation != Volatile.Read(ref _listGeneration))
                return;
            _profiles = profiles;
            _listPhase = profiles.Count == 0
                ? SkillsListPhase.Empty : SkillsListPhase.Populated;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            _logger.LogError(ex, "Owned Skills list could not be loaded.");
            if (!IsCurrentOwner(owner, stamp))
            {
                InvalidateOwner();
                return;
            }
            if (generation != Volatile.Read(ref _listGeneration))
                return;
            _profiles = [];
            _listPhase = SkillsListPhase.LoadError;
            _listErrorKey = "SkillEdit_LoadFailed";
        }
        Notify(list: true);
    }

    internal void OpenCreate()
    {
        if (!TryCurrentOwner(out _, out _) || _formOpen || _pending)
            return;
        BeginForm(null);
        _creating = true;
        _formPhase = SkillFormPhase.Creating;
        Notify(form: true);
    }

    internal async Task OpenEditAsync(string? id)
    {
        if (!TryCurrentOwner(out var owner, out var stamp)
            || _formOpen || _pending)
            return;
        BeginForm(id);
        _formPhase = SkillFormPhase.Loading;
        var generation = Volatile.Read(ref _formGeneration);
        Notify(form: true);
        await LoadFormAsync(owner, stamp, id, generation);
    }

    internal async Task RetryEditAsync()
    {
        if (!TryCurrentOwner(out var owner, out var stamp)
            || !_formOpen || _creating || _pending)
            return;
        var generation = Interlocked.Increment(ref _formGeneration);
        _formPhase = SkillFormPhase.Loading;
        _formErrorKey = null;
        Notify(form: true);
        await LoadFormAsync(owner, stamp, _formId, generation);
    }

    private async Task LoadFormAsync(string owner,
        DeviceUserScopeProvider.ScopeStamp stamp, string? id, long generation)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _formPhase = SkillFormPhase.NotFound;
            _formErrorKey = "SkillEdit_NotFound";
            Notify(form: true);
            return;
        }
        try
        {
            var profile = await _repository.GetSkillProfileAsync(id, owner);
            if (!IsCurrentOwner(owner, stamp))
            {
                InvalidateOwner();
                return;
            }
            if (!_formOpen || _formId != id
                || generation != Volatile.Read(ref _formGeneration))
                return;
            if (profile is null)
            {
                _formPhase = SkillFormPhase.NotFound;
                _formErrorKey = "SkillEdit_NotFound";
            }
            else
            {
                _title = profile.Title ?? string.Empty;
                _description = profile.Description ?? string.Empty;
                _createdAt = profile.CreatedAt;
                _updatedAt = profile.UpdatedAt;
                _formPhase = SkillFormPhase.Editing;
            }
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            _logger.LogError(ex, "Owned Skill could not be loaded.");
            if (!IsCurrentOwner(owner, stamp))
            {
                InvalidateOwner();
                return;
            }
            if (!_formOpen || _formId != id
                || generation != Volatile.Read(ref _formGeneration))
                return;
            _formPhase = SkillFormPhase.LoadError;
            _formErrorKey = "SkillEdit_LoadFailed";
        }
        Notify(form: true);
    }

    internal void SetTitle(string value)
    {
        if (_formOpen && !_pending && Editable)
            _title = value;
    }

    internal void SetDescription(string value)
    {
        if (_formOpen && !_pending && Editable)
            _description = value;
    }

    internal async Task<bool> SaveAsync()
    {
        if (!TryCurrentOwner(out var owner, out var stamp)
            || !_formOpen || _pending || !Editable)
            return false;
        if (string.IsNullOrWhiteSpace(_title))
        {
            _formPhase = SkillFormPhase.SaveError;
            _formErrorKey = "SkillAdd_TitleRequired";
            Notify(form: true);
            return false;
        }

        var generation = Volatile.Read(ref _formGeneration);
        var id = _formId;
        var creating = _creating;
        var title = _title;
        var description = _description;
        var saved = false;
        _pending = true;
        _formPhase = SkillFormPhase.Saving;
        _formErrorKey = null;
        try
        {
            Notify(form: true);
            var profile = creating
                ? new SkillProfile()
                : await _repository.GetSkillProfileAsync(id!, owner);
            if (!IsCurrentForm(owner, stamp, generation, id))
                return false;
            if (profile is null)
            {
                _formPhase = SkillFormPhase.NotFound;
                _formErrorKey = "SkillEdit_NotFound";
                return false;
            }
            profile.Title = title;
            profile.Description = description;
            if (!IsCurrentForm(owner, stamp, generation, id))
                return false;
            var savedId = await _repository.SaveAsync(profile, owner);
            saved = !string.IsNullOrEmpty(savedId);
            if (!IsCurrentForm(owner, stamp, generation, id))
                return false;
            if (!saved)
            {
                _formPhase = SkillFormPhase.SaveError;
                _formErrorKey = creating
                    ? "SkillAdd_SaveFailed" : "SkillEdit_SaveFailed";
                return false;
            }
            _listFeedbackKey = creating
                ? "SkillAdd_SavedSuccess" : "SkillEdit_SavedSuccess";
            EndForm();
            await ReloadAsync();
            return true;
        }
        catch (Exception ex) when (!saved
            && ex is DbException or InvalidOperationException)
        {
            _logger.LogError(ex, "Owned Skill could not be saved.");
            if (!IsCurrentOwner(owner, stamp))
                InvalidateOwner();
            else
            {
                _formPhase = SkillFormPhase.SaveError;
                _formErrorKey = creating
                    ? "SkillAdd_SaveFailed" : "SkillEdit_SaveFailed";
            }
            return false;
        }
        finally
        {
            _pending = false;
            Notify(list: true, form: true);
        }
    }

    internal void RequestDelete()
    {
        if (!TryCurrentOwner(out _, out _) || !_formOpen || _pending
            || _creating || !Editable)
            return;
        _confirmDelete = true;
        Notify();
    }

    internal void CancelDelete()
    {
        if (_pending || !_confirmDelete)
            return;
        _confirmDelete = false;
        Notify();
    }

    internal async Task<bool> ConfirmDeleteAsync()
    {
        if (!TryCurrentOwner(out var owner, out var stamp)
            || !_formOpen || _pending || _creating || !_confirmDelete
            || !Editable)
            return false;
        var id = _formId;
        var generation = Volatile.Read(ref _formGeneration);
        var removed = false;
        _pending = true;
        _formPhase = SkillFormPhase.Deleting;
        try
        {
            Notify(form: true);
            var profile = await _repository.GetSkillProfileAsync(id!, owner);
            if (!IsCurrentForm(owner, stamp, generation, id))
                return false;
            if (profile is null)
            {
                _formPhase = SkillFormPhase.NotFound;
                _formErrorKey = "SkillEdit_NotFound";
                return false;
            }
            var count = await _repository.DeleteAsync(profile, owner);
            removed = count > 0;
            if (!IsCurrentForm(owner, stamp, generation, id))
                return false;
            if (!removed)
            {
                _formPhase = SkillFormPhase.DeleteError;
                _formErrorKey = "SkillEdit_DeleteFailed";
                return false;
            }
            _listFeedbackKey = "SkillEdit_DeletedSuccess";
            EndForm();
            await ReloadAsync();
            return true;
        }
        catch (Exception ex) when (!removed
            && ex is DbException or InvalidOperationException)
        {
            _logger.LogError(ex, "Owned Skill could not be deleted.");
            if (!IsCurrentOwner(owner, stamp))
                InvalidateOwner();
            else
            {
                _formPhase = SkillFormPhase.DeleteError;
                _formErrorKey = "SkillEdit_DeleteFailed";
            }
            return false;
        }
        finally
        {
            _pending = false;
            _confirmDelete = false;
            Notify(list: true, form: true);
        }
    }

    internal void CancelForm()
    {
        if (!_formOpen || _pending)
            return;
        EndForm();
        Notify(form: true);
    }

    internal void Close()
    {
        if (!_open || _pending)
            return;
        _open = false;
        _owner = null;
        _profiles = [];
        _listFeedbackKey = null;
        _listErrorKey = null;
        Interlocked.Increment(ref _listGeneration);
        EndForm();
        Notify(list: true, form: true);
    }

    private bool Editable => _formPhase is SkillFormPhase.Creating
        or SkillFormPhase.Editing or SkillFormPhase.SaveError
        or SkillFormPhase.DeleteError;

    private void BeginForm(string? id)
    {
        _formOpen = true;
        _formId = id;
        _creating = false;
        _title = string.Empty;
        _description = string.Empty;
        _createdAt = null;
        _updatedAt = null;
        _formErrorKey = null;
        _confirmDelete = false;
        Interlocked.Increment(ref _formGeneration);
    }

    private void EndForm()
    {
        _formOpen = false;
        _formId = null;
        _formPhase = SkillFormPhase.None;
        _title = string.Empty;
        _description = string.Empty;
        _createdAt = null;
        _updatedAt = null;
        _formErrorKey = null;
        _confirmDelete = false;
        Interlocked.Increment(ref _formGeneration);
    }

    private bool TryCurrentOwner(out string owner,
        out DeviceUserScopeProvider.ScopeStamp stamp)
    {
        owner = _owner ?? string.Empty;
        stamp = _ownerStamp;
        if (IsCurrentOwner(owner, stamp))
            return true;
        if (_open)
        {
            _logger.LogWarning("Skills action refused a changed or absent owner.");
            InvalidateOwner();
        }
        return false;
    }

    private bool IsCurrentOwner(string owner,
        DeviceUserScopeProvider.ScopeStamp stamp) =>
        !_disposed && _open && !string.IsNullOrWhiteSpace(owner)
        && _owner == owner && stamp.PublishedOwner == owner
        && _userScope.IsCurrent(stamp)
        && _userScope.TryGetUserProfileId(out var activeOwner)
        && activeOwner == owner;

    private bool IsCurrentForm(string owner,
        DeviceUserScopeProvider.ScopeStamp stamp, long generation, string? id)
    {
        if (!IsCurrentOwner(owner, stamp))
        {
            InvalidateOwner();
            return false;
        }
        return _formOpen && _formId == id
            && generation == Volatile.Read(ref _formGeneration);
    }

    private void InvalidateOwner()
    {
        _owner = null;
        _profiles = [];
        _listPhase = SkillsListPhase.Blocked;
        _listErrorKey = NativeText.InvalidCredentials;
        _listFeedbackKey = null;
        Interlocked.Increment(ref _listGeneration);
        EndForm();
        Notify(list: true, form: true);
    }

    private void Notify(bool list = false, bool form = false)
    {
        if (_disposed)
            return;
        if (list)
            ListVersion++;
        if (form)
            FormVersion++;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _owner = null;
        _profiles = [];
        Changed = null;
    }
}
