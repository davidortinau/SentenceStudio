using CoreFoundation;
using Foundation;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Features.Vocabulary;

internal sealed record VocabularyListPresentation(
    bool Loading, bool Blocked, string? ErrorKey,
    NativeVocabularyCatalog Catalog,
    IReadOnlyList<NativeVocabularyEntry> FilteredWords,
    VocabularyFilterState Filters,
    string SortField, bool SortDescending, bool ListMode,
    string? Feedback, bool FeedbackWarning,
    VocabularyDuplicatePanel? Duplicates = null);

internal sealed record VocabularyEditPresentation(
    bool Loading, bool Saving, NativeVocabularyDetail? Detail,
    NativeVocabularyEditDraft? Draft, string? ErrorKey,
    bool PlayingAudio, NativeQuizPhotoAsset? Photo,
    bool Deleting = false, string? DeleteError = null)
{
    /// <summary>Source VocabularyWordEdit: Find Duplicates is disabled while saving.</summary>
    internal bool CanFindDuplicates =>
        VocabularyWordCommands.CanFindDuplicates(Saving || Deleting, Draft);

    /// <summary>Source Delete, for a word only this owner uses.</summary>
    internal bool CanDelete => VocabularyWordCommands.CanDelete(Saving || Deleting, Detail);
}

internal sealed class VocabularyCoordinator : IDisposable
{
    private readonly INativeResourceStore _store;
    private readonly DeviceUserScopeProvider _userScope;
    private readonly IPreferencesService _preferences;
    private readonly INativeQuizMedia _media;
    private readonly ILogger<VocabularyCoordinator> _logger;
    private readonly Func<string, string> _text;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<SwiftView> _saveButtons = [];
    private readonly List<SwiftView> _menuSaveButtons = [];
    private SwiftView? _resourceCount;
    private SourceUiAppearance? _saveAppearance;
    private NativeVocabularyCatalog _catalog = new(false, [], []);
    private VocabularyFilterState? _filters;
    private NativeVocabularyDetail? _detail;
    private NativeVocabularyEditDraft? _draft;
    private NativeQuizPhotoAsset? _photo;
    private string? _selectedWordId;
    private DeviceUserScopeProvider.ScopeStamp _ownerStamp;
    private string? _owner;
    private string? _listErrorKey;
    private string? _editErrorKey;
    private string? _feedback;
    private bool _feedbackWarning;
    private string _sortField = "added";
    private bool _sortDescending = true;
    private bool _listMode;
    private bool _listOpen;
    private bool _formOpen;
    private bool _loading;
    private bool _formLoading;
    private bool _saving;
    private bool _playingAudio;
    private bool _confirmingDelete;
    private bool _deleting;
    private string? _deleteError;
    private bool _disposed;
    private long _loadGeneration;
    private long _formGeneration;
    private long _listVersion;
    private long _formVersion;
    private long _sheetVersion;

    internal VocabularyCoordinator(
        INativeResourceStore store,
        DeviceUserScopeProvider userScope,
        IPreferencesService preferences,
        INativeQuizMedia media,
        ILogger<VocabularyCoordinator> logger,
        Func<string, string> text)
    {
        _store = store;
        _userScope = userScope;
        _preferences = preferences;
        _media = media;
        _logger = logger;
        _text = text;
        _duplicates = new VocabularyDuplicateFinder(store, logger, text,
            () => Publish(listChanged: true));
    }

    private readonly VocabularyDuplicateFinder _duplicates;

    internal event Action? Changed;

    /// <summary>Opens Recent lookups from the list toolbar (decision 4 B); none hides the button.</summary>
    internal Action? OpenLookups { get; init; }

    internal bool IsOpen => _listOpen;
    internal bool IsFormOpen => _listOpen && _formOpen;
    internal bool IsPhoneFilterPresented =>
        _listOpen && _filters?.PhoneSheetOpen == true;
    internal bool IsNavigationBlocked => _saving || _deleting;

    /// <summary>Source showDeleteConfirm (the scene presents the SwiftUI confirmation).</summary>
    internal bool IsDeleteConfirmationPresented =>
        IsFormOpen && (_confirmingDelete || _deleting);
    internal bool IsDeletePending => IsFormOpen && _deleting;

    /// <summary>Source deleteConfirmMessage for the loaded word; null until it loads.</summary>
    internal string? DeleteMessage => IsFormOpen && _detail is { } detail
        ? VocabularyWordCommands.DeleteMessage(detail, _text) : null;
    internal long ListVersion => Interlocked.Read(ref _listVersion);
    internal long FormVersion => Interlocked.Read(ref _formVersion);
    internal long SheetVersion => Interlocked.Read(ref _sheetVersion);
    internal bool ListMode => _listMode;
    internal string SearchText => TryOwner(out _)
        ? _filters?.CommittedQuery ?? string.Empty : string.Empty;
    internal string WordFormTitle => _text("VocabWordEdit_EditTitle");

    internal VocabularyListPresentation ListState
    {
        get
        {
            if (!TryOwner(out _))
                return new(false, true, "Vocabulary_NoActiveProfile",
                    new(false, [], []), [], new VocabularyFilterState(null, _ => { }),
                    _sortField, _sortDescending, _listMode, null, false);
            return new(_loading, _listErrorKey == "Vocabulary_NoActiveProfile",
                _listErrorKey, _catalog,
                _filters?.Filter(_catalog.Words, _sortField, _sortDescending) ?? [],
                _filters ?? new VocabularyFilterState(null, _ => { }),
                _sortField, _sortDescending, _listMode,
                _feedback, _feedbackWarning, _duplicates.Panel);
        }
    }

    internal VocabularyEditPresentation EditState => TryOwner(out _)
        ? new(_formLoading, _saving, _detail, _draft, _editErrorKey, _playingAudio,
            _photo, _deleting, _deleteError)
        : new(false, false, null, null, "Vocabulary_NoActiveProfile",
            false, null);

    internal async Task<bool> OpenAsync(string owner)
    {
        if (_disposed || _listOpen || !TrustedOwner(owner))
            return false;
        _owner = owner;
        _ownerStamp = _userScope.CaptureStamp();
        _filters = new VocabularyFilterState(
            _preferences.Get(QueryKey(owner), string.Empty), query =>
            {
                if (TryOwner(out var current))
                    _preferences.Set(QueryKey(current), query);
            });
        var sort = _preferences.Get($"vocabulary_sort_{owner}", "added:desc");
        var parts = sort.Split(':', 2);
        _sortField = parts[0] == "updated" ? "updated" : "added";
        _sortDescending = parts.Length < 2 || parts[1] != "asc";
        _listMode = _preferences.Get($"vocabulary_view_{owner}", "grid") == "list";
        _listOpen = true;
        _loading = true;
        _feedback = null;
        _feedbackWarning = false;
        Publish(listChanged: true);
        await ReloadAsync();
        return TryOwner(out _);
    }

    internal void OnListPopped()
    {
        if (!_listOpen || _saving || _deleting)
            return;
        _duplicates.Reset();
        ResetDelete();
        _filters?.CancelPhoneSheet();
        _listOpen = false;
        _formOpen = false;
        _detail = null;
        _draft = null;
        _photo = null;
        _selectedWordId = null;
        _catalog = new(false, [], []);
        _owner = null;
        Interlocked.Increment(ref _loadGeneration);
        Interlocked.Increment(ref _formGeneration);
        Publish(listChanged: true, formChanged: true, sheetChanged: true);
    }

    internal void OnFormPopped()
    {
        if (!_formOpen || _saving || _deleting)
            return;
        ResetDelete();
        _formOpen = false;
        _detail = null;
        _draft = null;
        _photo = null;
        _selectedWordId = null;
        _editErrorKey = null;
        _media.StopPlayback();
        Interlocked.Increment(ref _formGeneration);
        Publish(formChanged: true);
    }

    internal void OnListAppeared() => Run(ReloadAsync);

    internal SwiftView RenderList(SourceUiStyle style) =>
        VocabularyListView.Page(ListState, style, _text,
            new VocabularyListActions(
                Search: Search,
                OpenFilters: OpenPhoneFilters,
                ClearFilters: ClearFilters,
                OpenWord: id => Run(() => OpenWordAsync(id)),
                Reload: () => Run(ReloadAsync),
                Sort: SetSort,
                SetListMode: SetListMode,
                ChangeWideFilter: ChangeWideFilter,
                OpenLookups: OpenLookups,
                Duplicates: new VocabularyDuplicateActions(
                    Close: CloseDuplicates,
                    ShowMore: () => Run(ShowMoreDuplicatesAsync),
                    Merge: term => Run(() => MergeDuplicatesAsync(term)),
                    EditRecord: id => Run(() => OpenWordAsync(id)))));

    internal SwiftView RenderFilterSheet(SourceUiStyle style) =>
        VocabularyListView.PhoneFilterSheet(ListState, style, _text,
            new VocabularyFilterActions(
                Change: (type, value) => ChangeFilter(type, value, live: false),
                Apply: ApplyPhoneFilters, Cancel: CancelPhoneFilters,
                Clear: ClearFilters));

    internal SwiftView RenderForm(SourceUiStyle style)
    {
        _saveButtons.Clear();
        _menuSaveButtons.Clear();
        _resourceCount = null;
        _saveAppearance = style.Appearance;
        var form = VocabularyWordEditView.Page(EditState, style, _text,
            new VocabularyEditActions(
                Change: ChangeDraft,
                ToggleResource: ToggleResource,
                Save: () => Run(SaveAsync),
                Cancel: CancelEdit,
                Retry: () => Run(() => OpenWordAsync(_selectedWordId ?? "")),
                PlayAudio: () => Run(PlayAudioAsync),
                FindDuplicates: () => Run(FindDuplicatesAsync),
                RequestDelete: RequestDelete,
                RegisterSave: view => _saveButtons.Add(view),
                RegisterMenuSave: view => _menuSaveButtons.Add(view),
                RegisterResourceCount: view => _resourceCount = view));
        UpdateSaveButtons();
        return form;
    }

    private async Task ReloadAsync()
    {
        if (!_listOpen || !TryOwner(out var owner))
            return;
        var generation = Interlocked.Increment(ref _loadGeneration);
        _loading = true;
        _listErrorKey = null;
        Publish(listChanged: true);
        try
        {
            var catalog = await _store.ListOwnedVocabularyAsync(
                owner, _lifetime.Token);
            if (generation != Volatile.Read(ref _loadGeneration))
                return;
            if (!TryOwner(out _))
            {
                Block();
                return;
            }
            _catalog = catalog;
            if (!catalog.OwnerValid)
                _listErrorKey = "Vocabulary_NoActiveProfile";
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native Vocabulary list load failed.");
            _listErrorKey = "NativeVocabulary_LoadFailed";
        }
        finally
        {
            if (generation == Volatile.Read(ref _loadGeneration) && !_disposed)
            {
                _loading = false;
                Publish(listChanged: true,
                    sheetChanged: _filters?.PhoneSheetOpen == true);
            }
        }
    }

    private async Task OpenWordAsync(string id)
    {
        if (!_listOpen || _saving || !TryOwner(out var owner)
            || string.IsNullOrWhiteSpace(id))
            return;
        if (!_formOpen && !_catalog.Words.Any(word => word.Id == id))
            return;
        _selectedWordId = id;
        var generation = Interlocked.Increment(ref _formGeneration);
        _formOpen = true;
        _formLoading = true;
        _editErrorKey = null;
        _photo = null;
        ResetDelete();
        Publish(formChanged: true);
        try
        {
            var detail = await _store.GetOwnedVocabularyAsync(
                owner, id, _lifetime.Token);
            if (generation != Volatile.Read(ref _formGeneration)
                || !_formOpen || _selectedWordId != id)
                return;
            if (!TryOwner(out _))
            {
                Block();
                return;
            }
            _detail = detail;
            _draft = detail?.Draft;
            if (detail is null)
                _editErrorKey = "VocabWordEdit_NotFound";
            else if (!string.IsNullOrWhiteSpace(detail.Draft.MnemonicImageUri))
                Run(() => LoadPhotoPreviewAsync(
                    owner, detail.Word.Id, detail.Draft.MnemonicImageUri));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native Vocabulary word load failed.");
            _editErrorKey = "NativeVocabulary_WordLoadFailed";
        }
        finally
        {
            if (generation == Volatile.Read(ref _formGeneration))
            {
                _formLoading = false;
                Publish(formChanged: true);
            }
        }
    }

    internal void CancelEdit()
    {
        if (_saving || _deleting)
            return;
        OnFormPopped();
    }

    private void ChangeDraft(string field, string value)
    {
        if (_draft is not { } draft || _detail?.IsSharedWithAnotherOwner == true
            || _saving || !TryOwner(out _))
            return;
        _draft = field switch
        {
            nameof(draft.TargetTerm) => draft with { TargetTerm = value },
            nameof(draft.NativeTerm) => draft with { NativeTerm = value },
            nameof(draft.Lemma) => draft with { Lemma = value },
            nameof(draft.Language) => draft with { Language = value },
            nameof(draft.Tags) => draft with { Tags = value },
            nameof(draft.MnemonicText) => draft with { MnemonicText = value },
            nameof(draft.MnemonicImageUri) => draft with { MnemonicImageUri = value },
            nameof(draft.LexicalUnitType) when
                Enum.TryParse<LexicalUnitType>(value, out var type) =>
                    draft with { LexicalUnitType = type },
            _ => draft
        };
        _editErrorKey = null;
        UpdateSaveButtons();
        if (field == nameof(draft.LexicalUnitType))
            Publish(formChanged: true);
    }

    private void ToggleResource(string id, bool selected)
    {
        if (_draft is not { } draft || _detail?.IsSharedWithAnotherOwner == true
            || _saving || !TryOwner(out _)
            || _detail?.AvailableResources.Any(resource =>
                resource.Id == id && !resource.IsSmartResource) != true)
            return;
        var resourceIds = draft.ResourceIds.ToHashSet(StringComparer.Ordinal);
        if (selected)
            resourceIds.Add(id);
        else
            resourceIds.Remove(id);
        _draft = draft with { ResourceIds = resourceIds.ToArray() };
        var hadError = _editErrorKey is not null;
        _editErrorKey = null;
        if (hadError)
            Publish(formChanged: true);
        else if (_resourceCount is not null)
            _resourceCount.Text = string.Format(CultureInfo.CurrentCulture,
                _text("VocabWordEdit_SelectedCount"), resourceIds.Count);
    }

    private async Task SaveAsync()
    {
        if (!_formOpen || _saving || _deleting || _detail is null
            || _detail.IsSharedWithAnotherOwner || _draft is not { } draft
            || !TryOwner(out var owner))
            return;
        if (string.IsNullOrWhiteSpace(draft.TargetTerm)
            || string.IsNullOrWhiteSpace(draft.NativeTerm))
        {
            _editErrorKey = "VocabWordEdit_BothRequired";
            Publish(formChanged: true);
            return;
        }
        _saving = true;
        _editErrorKey = null;
        Publish(formChanged: true);
        try
        {
            var result = await _store.SaveOwnedVocabularyAsync(
                owner, draft, _lifetime.Token);
            if (result is NativeVocabularySaveStatus.Saved
                or NativeVocabularySaveStatus.SavedSyncUnconfirmed)
            {
                _feedback = _text(result == NativeVocabularySaveStatus.Saved
                    ? "VocabWordEdit_UpdatedSuccess"
                    : "NativeVocabulary_SavedSyncUnconfirmed");
                _feedbackWarning =
                    result == NativeVocabularySaveStatus.SavedSyncUnconfirmed;
                _media.StopPlayback();
                _formOpen = false;
                _draft = null;
                _detail = null;
                _photo = null;
                await ReloadAsync();
                await RescanDuplicatesAsync();
            }
            else
            {
                if (!TryOwner(out _))
                {
                    Block();
                    return;
                }
                _editErrorKey = result switch
                {
                    NativeVocabularySaveStatus.Duplicate =>
                        "VocabWordEdit_DuplicateExists",
                    NativeVocabularySaveStatus.Invalid =>
                        _detail.HasConstituentLinks
                            && draft.LexicalUnitType != _detail.Word.LexicalUnitType
                            ? "NativeVocabulary_ConstituentsLocked"
                            : draft.ResourceIds.Count == 0
                                && _detail.Progress is null
                                ? "NativeVocabulary_AssociationRequired"
                                : "VocabWordEdit_SaveFailed",
                    NativeVocabularySaveStatus.Conflict =>
                        "NativeVocabulary_Conflict",
                    NativeVocabularySaveStatus.SharedReadOnly =>
                        "NativeVocabulary_SharedReadOnly",
                    _ => "VocabWordEdit_NotFound"
                };
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native Vocabulary save failed.");
            _editErrorKey = "VocabWordEdit_SaveFailed";
        }
        finally
        {
            _saving = false;
            Publish(listChanged: true, formChanged: true);
        }
    }

    /// <summary>
    /// Source OpenDuplicateManager: leaves the editor (its draft is discarded, as the source
    /// navigates away) for the list's duplicate manager, focused on the draft's term and this
    /// word.
    /// </summary>
    private async Task FindDuplicatesAsync()
    {
        if (!_formOpen || !EditState.CanFindDuplicates || _draft is not { } draft
            || string.IsNullOrWhiteSpace(draft.TargetTerm) || !TryOwner(out var owner))
            return;
        var term = draft.TargetTerm.Trim();
        var wordId = draft.Id;
        ResetDelete();
        _formOpen = false;
        _detail = null;
        _draft = null;
        _photo = null;
        _selectedWordId = null;
        _editErrorKey = null;
        _media.StopPlayback();
        Interlocked.Increment(ref _formGeneration);
        Publish(listChanged: true, formChanged: true);
        await _duplicates.OpenAsync(owner, term, wordId, _lifetime.Token);
        if (!TryOwner(out _))
            Block();
    }

    private async Task ShowMoreDuplicatesAsync()
    {
        if (_listOpen && TryOwner(out var owner))
            await _duplicates.ShowMoreAsync(owner, _lifetime.Token);
    }

    /// <summary>Source MergeDuplicateGroup, then the list reloads (source LoadData).</summary>
    private async Task MergeDuplicatesAsync(string normalizedTerm)
    {
        if (!_listOpen || _formOpen || !TryOwner(out var owner))
            return;
        var outcome = await _duplicates.MergeAsync(owner, normalizedTerm, _lifetime.Token);
        if (outcome is null)
            return;
        if (!TryOwner(out _))
        {
            Block();
            return;
        }
        _feedback = outcome.Feedback;
        _feedbackWarning = outcome.Warning;
        if (outcome.Changed)
            await ReloadAsync();
        else
            Publish(listChanged: true);
    }

    /// <summary>Source CloseCleanupPanel.</summary>
    private void CloseDuplicates()
    {
        if (TryOwner(out _))
            _duplicates.Close();
    }

    private async Task RescanDuplicatesAsync()
    {
        if (_duplicates.IsOpen && TryOwner(out var owner))
            await _duplicates.RescanAsync(owner, _lifetime.Token);
    }

    /// <summary>Source RequestDelete: the scene presents the confirmation.</summary>
    private void RequestDelete()
    {
        if (!_formOpen || !EditState.CanDelete || !TryOwner(out _))
            return;
        _confirmingDelete = true;
        _deleteError = null;
        Publish();
    }

    /// <summary>Source CancelDelete (Cancel, Close or the backdrop); ignored while deleting.</summary>
    internal void CancelDelete()
    {
        if (_deleting || !_confirmingDelete)
            return;
        _confirmingDelete = false;
        Publish();
    }

    internal void ConfirmDelete() => Run(ConfirmDeleteAsync);

    /// <summary>
    /// Source ConfirmDelete: deletes the word, then returns to the list with the source's
    /// success message. A refused or failed delete keeps the editor open and says why.
    /// </summary>
    private async Task ConfirmDeleteAsync()
    {
        if (!_formOpen || !_confirmingDelete || _deleting || _saving
            || _detail is not { } detail || !TryOwner(out var owner))
        {
            _confirmingDelete = false;
            Publish();
            return;
        }
        _deleting = true;
        _deleteError = null;
        UpdateSaveButtons();
        Publish();
        var deleted = false;
        try
        {
            var result = await _store.DeleteOwnedVocabularyAsync(
                owner, detail.Word.Id, _lifetime.Token);
            if (result == NativeVocabularyDeleteStatus.Deleted)
            {
                deleted = true;
                _feedback = _text("VocabWordEdit_DeletedSuccess");
                _feedbackWarning = false;
                _media.StopPlayback();
                _formOpen = false;
                _detail = null;
                _draft = null;
                _photo = null;
                _selectedWordId = null;
                Interlocked.Increment(ref _formGeneration);
            }
            else if (!TryOwner(out _))
            {
                Block();
                return;
            }
            else
                _deleteError = VocabularyWordCommands.DeleteError(result, detail, _text);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native Vocabulary delete failed.");
            _deleteError = VocabularyWordCommands.DeleteError(
                NativeVocabularyDeleteStatus.Failed, detail, _text);
        }
        finally
        {
            _deleting = false;
            _confirmingDelete = false;
            Publish(listChanged: true, formChanged: true);
        }
        if (deleted)
        {
            await ReloadAsync();
            await RescanDuplicatesAsync();
        }
    }

    private void ResetDelete()
    {
        _confirmingDelete = false;
        _deleteError = null;
    }

    private async Task PlayAudioAsync()
    {
        if (!_formOpen || _saving || _playingAudio || _draft is not { } draft
            || !TryOwner(out var owner))
            return;
        _playingAudio = true;
        Publish(formChanged: true);
        try
        {
            if (await _store.GetOwnedVocabularyAsync(
                    owner, draft.Id, _lifetime.Token) is null)
            {
                _editErrorKey = "VocabWordEdit_NotFound";
                return;
            }
            var language = string.IsNullOrWhiteSpace(draft.Language)
                ? "Korean" : draft.Language;
            if (!await _media.PlayPromptAsync(
                draft.TargetTerm, language, _lifetime.Token))
                _editErrorKey = "NativeVocabulary_AudioUnavailable";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Native Vocabulary audio playback failed.");
            _editErrorKey = "NativeVocabulary_AudioUnavailable";
        }
        finally
        {
            _playingAudio = false;
            Publish(formChanged: true);
        }
    }

    private async Task LoadPhotoPreviewAsync(string owner, string wordId, string uri)
    {
        try
        {
            if (await _store.GetOwnedVocabularyAsync(owner, wordId, _lifetime.Token)
                    is null)
                return;
            var photo = await _media.LoadPhotoAsync(uri, _lifetime.Token);
            if (_formOpen && _draft is { } draft
                && draft.Id == wordId && draft.MnemonicImageUri == uri
                && _selectedWordId == wordId && TryOwner(out _))
            {
                _photo = photo;
                Publish(formChanged: true);
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Native Vocabulary image preview unavailable.");
        }
    }

    private void Search(string value)
    {
        if (!TryOwner(out _) || _filters is null)
            return;
        _filters.Search(value);
        Publish(listChanged: true);
    }

    private void OpenPhoneFilters()
    {
        if (!TryOwner(out _) || _filters is null)
            return;
        _filters.OpenPhoneSheet();
        Publish(sheetChanged: true);
    }

    private void ChangeFilter(string type, string? value, bool live)
    {
        if (!TryOwner(out _) || _filters is null)
            return;
        _filters.ChangeFilter(type, value, live);
        if (live)
            Publish(listChanged: true);
    }

    internal void ChangeWideFilter(string type, string? value) =>
        ChangeFilter(type, value, live: true);

    private void ApplyPhoneFilters()
    {
        if (!TryOwner(out _) || _filters is null)
            return;
        _filters.ApplyPhoneSheet();
        Publish(listChanged: true, sheetChanged: true);
    }

    internal void CancelPhoneFilters()
    {
        _filters?.CancelPhoneSheet();
        Publish(sheetChanged: true);
    }

    private void ClearFilters()
    {
        if (!TryOwner(out _) || _filters is null)
            return;
        _filters.ClearAll();
        Publish(listChanged: true, sheetChanged: true);
    }

    private void SetSort(string field)
    {
        if (!TryOwner(out var owner))
            return;
        if (_sortField == field)
            _sortDescending = !_sortDescending;
        else
        {
            _sortField = field;
            _sortDescending = true;
        }
        _preferences.Set($"vocabulary_sort_{owner}",
            $"{_sortField}:{(_sortDescending ? "desc" : "asc")}");
        Publish(listChanged: true);
    }

    private void SetListMode(bool list)
    {
        if (!TryOwner(out var owner))
            return;
        _listMode = list;
        _preferences.Set($"vocabulary_view_{owner}", list ? "list" : "grid");
        Publish(listChanged: true);
    }

    private bool TrustedOwner(string owner) =>
        !string.IsNullOrWhiteSpace(owner)
        && _userScope.TryGetUserProfileId(out var active)
        && string.Equals(active, owner, StringComparison.Ordinal)
        && string.Equals(
            _preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
            owner, StringComparison.Ordinal);

    private bool TryOwner(out string owner)
    {
        owner = _owner ?? string.Empty;
        var valid = !_disposed && _listOpen && TrustedOwner(owner)
            && _userScope.IsCurrent(_ownerStamp);
        if (!valid && !_disposed && _listOpen && _catalog.OwnerValid)
            Block();
        return valid;
    }

    private void Block()
    {
        _logger.LogWarning("Vocabulary view refused a missing or changed active owner.");
        _catalog = new(false, [], []);
        _detail = null;
        _draft = null;
        _photo = null;
        _selectedWordId = null;
        _owner = null;
        Interlocked.Increment(ref _formGeneration);
        Interlocked.Increment(ref _loadGeneration);
        _filters?.CancelPhoneSheet();
        _formOpen = false;
        _formLoading = false;
        _loading = false;
        _feedback = null;
        _duplicates.Reset();
        ResetDelete();
        _listErrorKey = "Vocabulary_NoActiveProfile";
        _media.StopPlayback();
        Publish(listChanged: true, formChanged: true, sheetChanged: true);
    }

    private static string QueryKey(string owner) => $"vocabulary_query_{owner}";

    private void Run(Func<Task> action)
    {
        if (!_disposed)
            _ = RunSafelyAsync(action);
    }

    private async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native Vocabulary action failed.");
            if (!_disposed)
            {
                if (_formOpen)
                    _editErrorKey = "VocabWordEdit_SaveFailed";
                else
                    _listErrorKey = "NativeVocabulary_LoadFailed";
                Publish(listChanged: true, formChanged: true);
            }
        }
    }

    private void UpdateSaveButtons()
    {
        if (_saveAppearance is null)
            return;
        var enabled = _formOpen
            && VocabularyBrowserEdit.CanSaveWord(_saving || _deleting, _detail, _draft);
        foreach (var button in _saveButtons)
            SourceUiStyle.SetCustomButtonAvailability(button,
                _saveAppearance, SourceButtonRole.Primary, enabled);
        foreach (var item in _menuSaveButtons)
            item.IsEnabled = enabled;
    }

    private void Publish(bool listChanged = false, bool formChanged = false,
        bool sheetChanged = false)
    {
        if (_disposed)
            return;
        if (listChanged)
            Interlocked.Increment(ref _listVersion);
        if (formChanged)
            Interlocked.Increment(ref _formVersion);
        if (sheetChanged)
            Interlocked.Increment(ref _sheetVersion);
        if (NSThread.IsMain)
            Changed?.Invoke();
        else
            DispatchQueue.MainQueue.DispatchAsync(() =>
            {
                if (!_disposed)
                    Changed?.Invoke();
            });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        _media.StopPlayback();
        _lifetime.Dispose();
    }
}
