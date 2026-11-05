using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Features.Vocabulary;

internal sealed record VocabularyBrowserList(
    bool Loading, bool Blocked, string? ErrorKey,
    NativeVocabularyCatalog Catalog,
    IReadOnlyList<NativeVocabularyEntry> FilteredWords,
    VocabularyFilterState Filters,
    string SortField, bool SortDescending, bool ListMode,
    string? Feedback, bool FeedbackWarning);

internal sealed record VocabularyBrowserEdit(
    bool Loading, bool Saving, NativeVocabularyDetail? Detail,
    NativeVocabularyEditDraft? Draft, string? ErrorKey,
    bool PlayingAudio, NativeQuizPhotoAsset? Photo)
{
    internal bool CanSave => CanSaveWord(Saving, Detail, Draft);

    /// <summary>
    /// Source VocabularyWordEdit: Save needs both terms and an editable (owned) word. The iOS
    /// VocabularyCoordinator uses the same rule.
    /// </summary>
    internal static bool CanSaveWord(bool saving, NativeVocabularyDetail? detail,
        NativeVocabularyEditDraft? draft) =>
        !saving
        && detail is { IsSharedWithAnotherOwner: false }
        && draft is { } current
        && !string.IsNullOrWhiteSpace(current.TargetTerm)
        && !string.IsNullOrWhiteSpace(current.NativeTerm);
}

/// <summary>
/// Platform-neutral owner-scoped Vocabulary browser and word editor (source
/// Vocabulary.razor and VocabularyWordEdit.razor), the shared core of the iOS
/// VocabularyCoordinator without its SwiftUI bindings. <see cref="Changed"/> is
/// raised on the calling thread; the head marshals it to its UI thread.
/// </summary>
internal sealed class VocabularyBrowser : IDisposable
{
    private readonly INativeResourceStore _store;
    private readonly DeviceUserScopeProvider _userScope;
    private readonly IPreferencesService _preferences;
    private readonly INativeQuizMedia _media;
    private readonly ILogger<VocabularyBrowser> _logger;
    private readonly Func<string, string> _text;
    private readonly CancellationTokenSource _lifetime = new();
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
    private bool _disposed;
    private long _loadGeneration;
    private long _formGeneration;
    private long _listVersion;
    private long _formVersion;

    internal VocabularyBrowser(
        INativeResourceStore store,
        DeviceUserScopeProvider userScope,
        IPreferencesService preferences,
        INativeQuizMedia media,
        ILogger<VocabularyBrowser> logger,
        Func<string, string> text)
    {
        _store = store;
        _userScope = userScope;
        _preferences = preferences;
        _media = media;
        _logger = logger;
        _text = text;
    }

    internal event Action? Changed;
    internal bool IsOpen => _listOpen;
    internal bool IsFormOpen => _listOpen && _formOpen;
    internal bool IsPhoneFilterPresented =>
        _listOpen && _filters?.PhoneSheetOpen == true;
    internal bool IsNavigationBlocked => _saving;
    internal long ListVersion => Interlocked.Read(ref _listVersion);
    internal long FormVersion => Interlocked.Read(ref _formVersion);

    internal VocabularyBrowserList ListState
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
                _feedback, _feedbackWarning);
        }
    }

    internal VocabularyBrowserEdit EditState => TryOwner(out _)
        ? new(_formLoading, _saving, _detail, _draft, _editErrorKey, _playingAudio,
            _photo)
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

    /// <summary>Back from the list: drops the catalog, the open word and the owner.</summary>
    internal void Close()
    {
        if (!_listOpen || _saving)
            return;
        _filters?.CancelPhoneSheet();
        _listOpen = false;
        _formOpen = false;
        _detail = null;
        _draft = null;
        _photo = null;
        _selectedWordId = null;
        _catalog = new(false, [], []);
        _owner = null;
        _media.StopPlayback();
        Interlocked.Increment(ref _loadGeneration);
        Interlocked.Increment(ref _formGeneration);
        Publish(listChanged: true, formChanged: true);
    }

    /// <summary>Back or Cancel from the word editor discards the draft.</summary>
    internal void CloseWord()
    {
        if (!_formOpen || _saving)
            return;
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

    internal async Task ReloadAsync()
    {
        if (!_listOpen || !TryOwner(out var owner))
            return;
        var generation = Interlocked.Increment(ref _loadGeneration);
        _loading = true;
        _listErrorKey = null;
        Publish(listChanged: true);
        try
        {
            // Off the UI thread: the catalog query reads every owned word.
            var catalog = await Task.Run(() => _store.ListOwnedVocabularyAsync(
                owner, _lifetime.Token), _lifetime.Token);
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
                Publish(listChanged: true);
            }
        }
    }

    internal async Task OpenWordAsync(string id)
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
        Publish(formChanged: true);
        try
        {
            var detail = await Task.Run(() => _store.GetOwnedVocabularyAsync(
                owner, id, _lifetime.Token), _lifetime.Token);
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
                _ = LoadPhotoPreviewAsync(
                    owner, detail.Word.Id, detail.Draft.MnemonicImageUri);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native Vocabulary word load failed.");
            _editErrorKey = "NativeVocabulary_WordLoadFailed";
        }
        finally
        {
            if (generation == Volatile.Read(ref _formGeneration) && !_disposed)
            {
                _formLoading = false;
                Publish(formChanged: true);
            }
        }
    }

    internal Task RetryWordAsync() => OpenWordAsync(_selectedWordId ?? string.Empty);

    internal void ChangeDraft(string field, string value)
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
        Publish(formChanged: true);
    }

    internal void ToggleResource(string id, bool selected)
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
        _editErrorKey = null;
        Publish(formChanged: true);
    }

    internal async Task SaveAsync()
    {
        if (!_formOpen || _saving || _detail is null
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
            var result = await Task.Run(() => _store.SaveOwnedVocabularyAsync(
                owner, draft, _lifetime.Token), _lifetime.Token);
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
            }
            else
            {
                if (!TryOwner(out _))
                {
                    Block();
                    return;
                }
                _editErrorKey = SaveErrorKey(result, _detail, draft);
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

    /// <summary>The source and iOS message for each refused save.</summary>
    internal static string SaveErrorKey(NativeVocabularySaveStatus result,
        NativeVocabularyDetail detail, NativeVocabularyEditDraft draft) => result switch
    {
        NativeVocabularySaveStatus.Duplicate => "VocabWordEdit_DuplicateExists",
        NativeVocabularySaveStatus.Invalid =>
            detail.HasConstituentLinks
                && draft.LexicalUnitType != detail.Word.LexicalUnitType
                ? "NativeVocabulary_ConstituentsLocked"
                : draft.ResourceIds.Count == 0 && detail.Progress is null
                    ? "NativeVocabulary_AssociationRequired"
                    : "VocabWordEdit_SaveFailed",
        NativeVocabularySaveStatus.Conflict => "NativeVocabulary_Conflict",
        NativeVocabularySaveStatus.SharedReadOnly => "NativeVocabulary_SharedReadOnly",
        _ => "VocabWordEdit_NotFound"
    };

    internal async Task PlayAudioAsync()
    {
        if (!_formOpen || _saving || _playingAudio || _draft is not { } draft
            || !TryOwner(out var owner))
            return;
        _playingAudio = true;
        Publish(formChanged: true);
        try
        {
            if (await Task.Run(() => _store.GetOwnedVocabularyAsync(
                    owner, draft.Id, _lifetime.Token), _lifetime.Token) is null)
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
        catch (OperationCanceledException) when (_disposed)
        {
            return;
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
            if (await Task.Run(() => _store.GetOwnedVocabularyAsync(
                    owner, wordId, _lifetime.Token), _lifetime.Token) is null)
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

    internal void Search(string value)
    {
        if (!TryOwner(out _) || _filters is null)
            return;
        _filters.Search(value);
        Publish(listChanged: true);
    }

    internal void OpenPhoneFilters()
    {
        if (!TryOwner(out _) || _filters is null)
            return;
        _filters.OpenPhoneSheet();
        Publish(listChanged: true);
    }

    /// <summary><paramref name="live"/> applies at once (wide layout); otherwise it edits the sheet draft.</summary>
    internal void ChangeFilter(string type, string? value, bool live)
    {
        if (!TryOwner(out _) || _filters is null)
            return;
        _filters.ChangeFilter(type, value, live);
        Publish(listChanged: true);
    }

    internal void ApplyPhoneFilters()
    {
        if (!TryOwner(out _) || _filters is null)
            return;
        _filters.ApplyPhoneSheet();
        Publish(listChanged: true);
    }

    internal void CancelPhoneFilters()
    {
        _filters?.CancelPhoneSheet();
        Publish(listChanged: true);
    }

    internal void ClearFilters()
    {
        if (!TryOwner(out _) || _filters is null)
            return;
        _filters.ClearAll();
        Publish(listChanged: true);
    }

    internal void SetSort(string field)
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

    internal void SetListMode(bool list)
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
        _listErrorKey = "Vocabulary_NoActiveProfile";
        _media.StopPlayback();
        Publish(listChanged: true, formChanged: true);
    }

    private static string QueryKey(string owner) => $"vocabulary_query_{owner}";

    private void Publish(bool listChanged = false, bool formChanged = false)
    {
        if (_disposed)
            return;
        if (listChanged)
            Interlocked.Increment(ref _listVersion);
        if (formChanged)
            Interlocked.Increment(ref _formVersion);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Changed = null;
        _lifetime.Cancel();
        _media.StopPlayback();
        _lifetime.Dispose();
    }
}
