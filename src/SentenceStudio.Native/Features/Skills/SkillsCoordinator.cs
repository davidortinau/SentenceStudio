using System.Data.Common;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Features.Skills;

internal enum SkillsListPhase { Loading, Empty, Populated, LoadError, Blocked }

internal enum SkillFormPhase
{
    None, Loading, Creating, Editing, NotFound, LoadError,
    Saving, SaveError, Deleting, DeleteError, Blocked
}

/// <param name="Archived">The list shows the archived skills (the toolbar's Show choice).</param>
/// <param name="FeedbackIsError">The feedback reports a refusal or failure, not a success.</param>
/// <param name="Busy">An archive, restore or delete from the list is running.</param>
/// <param name="Profiles">The skills the list shows: those matching <paramref name="Search"/>.</param>
/// <param name="Search">The search field's text (matched in titles and descriptions).</param>
internal sealed record SkillsListState(
    SkillsListPhase Phase, IReadOnlyList<SkillProfile> Profiles,
    string? ErrorKey = null, string? FeedbackKey = null, bool Archived = false,
    bool FeedbackIsError = false, bool Busy = false, string Search = "");

/// <param name="Archived">The open skill is archived (its toolbar offers Restore, not Archive).</param>
/// <param name="StatusKey">What the editor's last Archive or Restore did.</param>
internal sealed record SkillProfileFormState(
    SkillFormPhase Phase, bool Creating, string Title, string Description,
    DateTime? CreatedAt, DateTime? UpdatedAt, string? ErrorKey,
    bool ShowDeleteConfirmation, bool Archived = false, string? StatusKey = null,
    bool Busy = false);

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
    private bool _showArchived;
    private bool _listFeedbackIsError;
    private bool _listBusy;
    private SkillProfile? _askingListDelete;
    private string _search = string.Empty;
    private bool _formArchived;
    private string? _formStatusKey;
    private string _loadedTitle = string.Empty;
    private string _loadedDescription = string.Empty;

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
    internal bool IsPending => _pending || _listBusy;

    /// <summary>
    /// Another change runs or a confirmation asks: every list and editor action waits (and
    /// says so) until it is done.
    /// </summary>
    private bool IsBlocked => IsPending || _askingListDelete is not null || _confirmDelete;

    /// <summary>The editor holds text not yet saved (a new skill's, or edits to the open one).</summary>
    private bool FormDirty => _formOpen && (_creating
        ? !string.IsNullOrWhiteSpace(_title) || !string.IsNullOrWhiteSpace(_description)
        : _title != _loadedTitle || _description != _loadedDescription);
    internal bool IsDeletePending => _formPhase == SkillFormPhase.Deleting;
    internal bool IsDeleteConfirmationPresented => _confirmDelete;
    internal long ListVersion { get; private set; }
    internal long FormVersion { get; private set; }
    internal SkillsListState ListState =>
        new(_listPhase, Matching(), _listErrorKey, _listFeedbackKey, _showArchived,
            _listFeedbackIsError, _listBusy, _search);

    /// <summary>
    /// The search field (Captain 2026-11-13: a search field like the other Library lists):
    /// narrows the loaded skills by title or description as the learner types.
    /// </summary>
    internal void SetSearch(string value)
    {
        value ??= string.Empty;
        if (!_open || string.Equals(value, _search, StringComparison.Ordinal))
            return;
        _search = value;
        Notify(list: true);
    }

    private (IReadOnlyList<SkillProfile> Source, string Search, IReadOnlyList<SkillProfile> Result)? _matching;

    /// <summary>The matching skills, filtered once per list and search (the state is read often).</summary>
    private IReadOnlyList<SkillProfile> Matching()
    {
        if (_matching is { } cached && ReferenceEquals(cached.Source, _profiles)
            && cached.Search == _search)
            return cached.Result;
        var query = _search.Trim();
        IReadOnlyList<SkillProfile> result = query.Length == 0 ? _profiles
            : _profiles.Where(profile =>
                    (profile.Title ?? string.Empty).Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || (profile.Description ?? string.Empty).Contains(query,
                        StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
        _matching = (_profiles, _search, result);
        return result;
    }

    /// <summary>The search field's text (without building the whole state).</summary>
    internal string SearchText => _search;

    /// <summary>The archive shows (without building the whole state).</summary>
    internal bool ShowingArchived => _showArchived;

    /// <summary>A row's swipe Delete is asking to confirm (never while it runs).</summary>
    internal bool IsListDeletePresented => _open && _askingListDelete is not null;

    /// <summary>The title of the skill whose swipe Delete is asking; null when none asks.</summary>
    internal string? PendingListDeleteTitle =>
        IsListDeletePresented ? _askingListDelete!.Title ?? string.Empty : null;

    /// <summary>The id of the skill whose swipe Delete is asking; null when none asks.</summary>
    internal string? PendingListDeleteId => IsListDeletePresented ? _askingListDelete!.Id : null;
    internal SkillProfileFormState FormState =>
        new(_formPhase, _creating, _title, _description, _createdAt,
            _updatedAt, _formErrorKey, _confirmDelete, _formArchived, _formStatusKey,
            _listBusy);

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
        _listFeedbackIsError = false;
        _listErrorKey = null;
        _listPhase = SkillsListPhase.Loading;
        // Each visit starts on the learner's skills, not the archive, with no search.
        _showArchived = false;
        _search = string.Empty;
        _askingListDelete = null;
        Interlocked.Increment(ref _formGeneration);
        Notify(list: true, form: true);
        await ReloadAsync();
        return IsCurrentOwner(expectedOwner, stamp);
    }

    internal Task ReloadAsync() => ReloadAsync(quiet: false);

    /// <param name="quiet">
    /// Keep the rows shown while it reads (after a swipe action the list stays in place, its
    /// scroll position and swipe animation kept, instead of passing through Loading).
    /// </param>
    private async Task ReloadAsync(bool quiet)
    {
        if (!TryCurrentOwner(out var owner, out var stamp))
            return;
        var generation = Interlocked.Increment(ref _listGeneration);
        var archived = _showArchived;
        if (!quiet || _listPhase is not (SkillsListPhase.Populated or SkillsListPhase.Empty))
        {
            _listPhase = SkillsListPhase.Loading;
            _listErrorKey = null;
            Notify(list: true);
        }
        try
        {
            // The archive asks for every skill and keeps the archived ones; the learner's list
            // is the active ones only, as before.
            IReadOnlyList<SkillProfile> profiles = await _repository.ListAsync(
                owner, includeArchived: archived);
            if (archived)
                profiles = profiles.Where(profile => profile.IsArchived).ToArray();
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
        if (!TryCurrentOwner(out _, out _) || (_formOpen && _creating))
            return;
        // Add while the editor shows beside the list (a split view) replaces it, unless it
        // holds unsaved text or something else runs.
        if (IsBlocked || FormDirty)
        {
            Busy(fromForm: false, FormDirty && !IsBlocked ? "Skills_SaveFirst" : null);
            return;
        }
        BeginForm(null);
        _creating = true;
        _formPhase = SkillFormPhase.Creating;
        Notify(form: true);
    }

    internal async Task OpenEditAsync(string? id)
    {
        if (!TryCurrentOwner(out var owner, out var stamp)
            || (_formOpen && _formId == id && !_creating))
            return;
        // Another row while the editor shows beside the list (a split view) opens that skill
        // instead, as a list selection does; not while a change runs or a dialog asks.
        if (IsBlocked || FormDirty)
        {
            if (_open)
                Busy(fromForm: false, FormDirty && !IsBlocked ? "Skills_SaveFirst" : null);
            return;
        }
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
            var profile = await _repository.GetAsync(id, owner, includeArchived: true);
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
                _loadedTitle = _title;
                _loadedDescription = _description;
                _createdAt = profile.CreatedAt;
                _updatedAt = profile.UpdatedAt;
                _formArchived = profile.IsArchived;
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
        // A list action on this skill (archive, delete) must finish first: a Save read before
        // it commits would write the old archive state back, or bring a deleted skill back.
        if (IsBlocked)
        {
            Busy(fromForm: true);
            return false;
        }
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
                : await _repository.GetAsync(id!, owner, includeArchived: true);
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
            _listFeedbackIsError = false;
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
        if (IsBlocked)
        {
            Busy(fromForm: true);
            return;
        }
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
            || !Editable || _listBusy || _askingListDelete is not null)
            return false;
        var id = _formId;
        var generation = Volatile.Read(ref _formGeneration);
        var removed = false;
        _pending = true;
        _formPhase = SkillFormPhase.Deleting;
        try
        {
            Notify(form: true);
            var profile = await _repository.GetAsync(id!, owner, includeArchived: true);
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
            _listFeedbackIsError = false;
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

    /// <summary>
    /// The toolbar's Show choice: the learner's skills or the archived ones (Captain
    /// 2026-11-12: swipe Archive and Restore on skills; collections live in the list's menu).
    /// </summary>
    internal async Task ShowArchivedAsync(bool archived)
    {
        // The editor may be open beside the list (a split view): switching what the list shows
        // leaves it alone.
        if (!TryCurrentOwner(out _, out _) || _showArchived == archived)
            return;
        if (IsBlocked)
        {
            Busy(fromForm: false);
            return;
        }
        _showArchived = archived;
        _listFeedbackKey = null;
        _listFeedbackIsError = false;
        _profiles = [];
        await ReloadAsync();
    }

    /// <summary>
    /// A row's swipe Archive (in the learner's skills) or Restore (in the archive). Nothing is
    /// deleted: an archived skill leaves the lists and practice until it is restored, through
    /// the same owner-scoped repository call Sam uses. The row goes at once and the list says
    /// what happened.
    /// </summary>
    internal Task<bool> ToggleArchivedAsync(string id) =>
        // A row from a list no longer shown (the Show choice changed) is refused.
        _profiles.Any(profile => profile.Id == id)
            ? ChangeArchivedAsync(id, archive: !_showArchived, fromForm: false)
            : Task.FromResult(false);

    /// <summary>
    /// The editor's Archive or Restore (Captain 2026-11-13: the detail toolbar offers the
    /// row's actions too). The editor stays open with its unsaved text; only the archive flag
    /// changes.
    /// </summary>
    internal Task<bool> ToggleFormArchivedAsync()
    {
        if (!_formOpen || _creating || _formId is not { } id
            || _formPhase is not (SkillFormPhase.Editing or SkillFormPhase.SaveError
                or SkillFormPhase.DeleteError))
            return Task.FromResult(false);
        return ChangeArchivedAsync(id, archive: !_formArchived, fromForm: true);
    }

    /// <summary>
    /// Archives or restores one of the learner's skills, from its row or the editor. Nothing is
    /// deleted, and the editor may be open beside the list. A row that leaves the list shown
    /// goes at once; the list (and the editor, when it shows the skill) say what happened.
    /// </summary>
    private async Task<bool> ChangeArchivedAsync(string id, bool archive, bool fromForm)
    {
        if (!TryCurrentOwner(out var owner, out var stamp))
            return false;
        // Not while the editor still loads this skill: its read could undo the new state.
        if (IsBlocked || (_formOpen && _formId == id && _formPhase == SkillFormPhase.Loading))
        {
            Busy(fromForm);
            return false;
        }
        var changed = false;
        _listBusy = true;
        Notify(list: true, form: _formOpen);
        try
        {
            var result = await _repository.SetArchivedAsync(id, archive, owner);
            if (!IsCurrentOwner(owner, stamp))
            {
                Abandon();
                return false;
            }
            changed = result > 0;
            var openHere = _formOpen && _formId == id;
            if (changed)
            {
                if (_showArchived != archive)
                    RemoveRow(id);
                var done = archive ? "Skills_ArchivedSuccess" : "Skills_RestoredSuccess";
                _listFeedbackKey = done;
                _listFeedbackIsError = false;
                if (openHere)
                {
                    _formArchived = archive;
                    _formStatusKey = done;
                    _formErrorKey = null;
                    // The repository stamps the row's UpdatedAt (UTC) when it archives.
                    _updatedAt = DateTime.UtcNow;
                }
            }
            else if (result == 0)
            {
                // Already moved (another device or Sam), deleted, or no longer the learner's:
                // the reload below shows where it is now; nothing failed. The editor offers
                // the other action from now on.
                _listFeedbackKey = null;
                _listFeedbackIsError = false;
                if (openHere)
                {
                    _formArchived = archive;
                    _formStatusKey = null;
                }
            }
            else
            {
                var failed = archive ? "Skills_ArchiveFailed" : "Skills_RestoreFailed";
                _listFeedbackKey = failed;
                _listFeedbackIsError = true;
                if (openHere)
                {
                    _formStatusKey = null;
                    _formErrorKey = failed;
                }
            }
        }
        finally
        {
            _listBusy = false;
            Notify(list: true, form: _formOpen);
        }
        await ReloadAsync(quiet: true);
        return changed;
    }

    /// <summary>
    /// A list action arrived while another change runs or a confirmation asks: say so, never
    /// drop it silently.
    /// </summary>
    /// <param name="fromForm">The editor asked: it says so too (on a phone the list is hidden).</param>
    /// <param name="key">The reason; by default another change runs.</param>
    private void Busy(bool fromForm, string? key = null)
    {
        key ??= "Skills_WaitForChange";
        _listFeedbackKey = key;
        _listFeedbackIsError = true;
        if (fromForm && _formOpen)
        {
            _formStatusKey = null;
            _formErrorKey = key;
        }
        Notify(list: true, form: fromForm && _formOpen);
    }

    /// <summary>A row's swipe Delete: asks to confirm (one presentation at a time).</summary>
    internal void RequestListDelete(string id)
    {
        if (!TryCurrentOwner(out _, out _)
            || _profiles.FirstOrDefault(profile => profile.Id == id) is not { } profile)
            return;
        if (IsBlocked)
        {
            Busy(fromForm: false);
            return;
        }
        _askingListDelete = profile;
        Notify(list: true);
    }

    /// <summary>Cancel, or the dialog dismissed, while the list delete asks.</summary>
    internal void CancelListDelete()
    {
        if (_askingListDelete is null)
            return;
        _askingListDelete = null;
        Notify(list: true);
    }

    /// <summary>
    /// Deletes the confirmed skill through the same owner-scoped delete as the editor's. The
    /// dialog goes at once; the row goes on success and the list says what happened.
    /// </summary>
    internal async Task<bool> ConfirmListDeleteAsync()
    {
        if (_askingListDelete is not { } profile)
            return false;
        _askingListDelete = null;
        if (!TryCurrentOwner(out var owner, out var stamp))
            return false;
        if (IsBlocked)
        {
            // Something else started after it asked: say so rather than drop it silently.
            Busy(fromForm: false);
            return false;
        }
        var removed = false;
        _listBusy = true;
        Notify(list: true, form: _formOpen);
        try
        {
            // The repository logs a failure and returns -1; it does not throw.
            var count = await _repository.DeleteAsync(profile, owner);
            if (!IsCurrentOwner(owner, stamp))
            {
                Abandon();
                return false;
            }
            removed = count > 0;
            if (removed)
            {
                RemoveRow(profile.Id);
                _listFeedbackKey = "SkillEdit_DeletedSuccess";
                _listFeedbackIsError = false;
                // The editor beside the list showed the deleted skill: it has nothing to show.
                if (_formOpen && _formId == profile.Id)
                    EndForm();
            }
            else
            {
                _listFeedbackKey = count == 0 ? "SkillEdit_NotFound" : "SkillEdit_DeleteFailed";
                _listFeedbackIsError = true;
            }
        }
        finally
        {
            _listBusy = false;
            Notify(list: true, form: true);
        }
        await ReloadAsync(quiet: true);
        return removed;
    }

    /// <summary>
    /// A list action finished after the owner changed or the page closed: block the list for a
    /// changed owner; a closed page has nothing to show.
    /// </summary>
    private void Abandon()
    {
        if (_open)
            InvalidateOwner();
    }

    private void RemoveRow(string id)
    {
        _profiles = _profiles.Where(profile => profile.Id != id).ToArray();
        if (_profiles.Count == 0 && _listPhase == SkillsListPhase.Populated)
            _listPhase = SkillsListPhase.Empty;
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
        // A running list action does not hold the page: the stack has already popped, and the
        // action finds the page closed when it returns.
        if (!_open || _pending)
            return;
        _open = false;
        _owner = null;
        _profiles = [];
        _askingListDelete = null;
        _showArchived = false;
        _search = string.Empty;
        _listFeedbackKey = null;
        _listFeedbackIsError = false;
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
        _formArchived = false;
        _formStatusKey = null;
        _loadedTitle = string.Empty;
        _loadedDescription = string.Empty;
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
        _formArchived = false;
        _formStatusKey = null;
        _loadedTitle = string.Empty;
        _loadedDescription = string.Empty;
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
        _askingListDelete = null;
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
