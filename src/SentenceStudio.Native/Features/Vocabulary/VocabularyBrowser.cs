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
    string? Feedback, bool FeedbackWarning,
    VocabularyDuplicatePanel? Duplicates = null,
    string SheetSortField = "added", bool SheetSortDescending = true,
    bool FilterPanelHidden = false,
    int RowLimit = int.MaxValue)
{
    /// <summary>
    /// The words a head that builds every row up front shows now (iOS): the first
    /// <see cref="RowLimit"/> of <see cref="FilteredWords"/>. A learner can hold thousands of
    /// words, and one view per word for all of them held the iOS app past the system's time
    /// to redraw when it went to the background (2026-11-12, 4,466 words on Duotopia).
    /// </summary>
    internal IEnumerable<NativeVocabularyEntry> ShownWords => FilteredWords.Take(RowLimit);

    /// <summary>The filtered words beyond <see cref="RowLimit"/>.</summary>
    internal int HiddenWordCount => Math.Max(0, FilteredWords.Count - RowLimit);
}

internal sealed record VocabularyBrowserEdit(
    bool Loading, bool Saving, NativeVocabularyDetail? Detail,
    NativeVocabularyEditDraft? Draft, string? ErrorKey,
    bool PlayingAudio, NativeQuizPhotoAsset? Photo,
    bool ConfirmingDelete = false, bool Deleting = false,
    string? DeleteError = null, bool HandingOff = false,
    VocabularyExamplesState? Examples = null, bool Creating = false)
{
    /// <summary>Source VocabularyWordEdit: Find Duplicates is disabled while saving (and absent for a new word).</summary>
    internal bool CanFindDuplicates => !Creating
        && VocabularyWordCommands.CanFindDuplicates(Saving || Deleting || HandingOff, Draft);

    /// <summary>Source Delete, for a word only this owner uses (a new word has nothing to delete).</summary>
    internal bool CanDelete => !Creating
        && VocabularyWordCommands.CanDelete(Saving || Deleting || HandingOff, Detail);

    internal bool CanSave => CanSaveWord(Saving || Deleting || HandingOff, Detail, Draft);

    /// <summary>
    /// Captain 2026-11-10: the word's own resources (the draft's, in the learner's order); both
    /// heads list only these, each removable unless it is a smart resource.
    /// </summary>
    internal IReadOnlyList<NativeVocabularyResource> SelectedResources =>
        Detail is not { } detail || Draft is not { } draft ? []
            : detail.AvailableResources.Where(resource =>
                draft.ResourceIds.Contains(resource.Id, StringComparer.Ordinal)).ToArray();

    /// <summary>
    /// The resources Add to Resource offers: the learner's others, not smart ones (a smart
    /// resource fills itself; ToggleResource refuses it).
    /// </summary>
    internal IReadOnlyList<NativeVocabularyResource> AddableResources =>
        Detail is not { } detail || Draft is not { } draft ? []
            : detail.AvailableResources.Where(resource => !resource.IsSmartResource
                && !draft.ResourceIds.Contains(resource.Id, StringComparer.Ordinal)).ToArray();

    /// <summary>
    /// Source VocabularyWordEdit: Save needs both terms and an editable (owned) word. The iOS
    /// VocabularyCoordinator uses the same rule.
    /// </summary>
    internal static bool CanSaveWord(bool saving, NativeVocabularyDetail? detail,
        NativeVocabularyEditDraft? draft) =>
        !saving
        && detail is not null
        && draft is { } current
        && !string.IsNullOrWhiteSpace(current.TargetTerm)
        && !string.IsNullOrWhiteSpace(current.NativeTerm);
}

/// <summary>What changed, for the head to redraw. A head decides how much each one redraws.</summary>
[Flags]
internal enum VocabularyChanges
{
    None = 0,
    List = 1,
    Form = 2,
    /// <summary>The phone filter sheet.</summary>
    Sheet = 4,
    /// <summary>The delete confirmation alone (shown, pending or gone).</summary>
    DeleteDialog = 8,
    /// <summary>A typed draft field: the open editor already shows the text.</summary>
    DraftText = 16,
    /// <summary>A resource switch: the open editor already shows it; its count changed.</summary>
    ResourceToggle = 32,
    /// <summary>A phone filter sheet choice not applied yet.</summary>
    FilterDraft = 64,
    /// <summary>The editor's Reference Sentences changed (a sentence, a suggestion, the row being edited or the result shown).</summary>
    Examples = 128,
    /// <summary>Typed into a Reference Sentences field: the open editor already shows the text.</summary>
    ExampleText = 256
}

/// <summary>
/// Platform-neutral owner-scoped Vocabulary browser and word editor (source
/// Vocabulary.razor and VocabularyWordEdit.razor). Android uses it as is; the iOS
/// VocabularyCoordinator derives from it and adds only its SwiftUI bindings, through
/// <see cref="OffUiAsync{T}"/> and <see cref="OnChanged"/>. <see cref="Changed"/> is raised on
/// the calling thread; the head marshals it to its UI thread.
/// </summary>
internal partial class VocabularyBrowser : IDisposable, IVocabularyCleanupHost
{
    private readonly INativeResourceStore _store;
    private readonly DeviceUserScopeProvider _userScope;
    private readonly IPreferencesService _preferences;
    private readonly INativeQuizMedia _media;
    private readonly ILogger<VocabularyBrowser> _logger;
    private readonly Func<string, string> _text;
    private readonly CancellationTokenSource _lifetime = new();
    // Read once: CancellationTokenSource.Token throws after Dispose, and work cut short by
    // disposal still reads the token.
    private readonly CancellationToken _lifetimeToken;
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
    /// <summary>The phone filter sheet's sort until Apply (null while the sheet is closed).</summary>
    private (string Field, bool Descending)? _sheetSort;
    /// <summary>The wide layout's filter column is hidden (Captain 2026-11-09: the toolbar's Filters button shows and hides it).</summary>
    private bool _filterPanelHidden;
    private bool _listOpen;
    private bool _formOpen;
    private bool _loading;
    private bool _formLoading;
    private bool _saving;
    /// <summary>
    /// Find Duplicates handed this editor's word to the Vocabulary page, which is opening: the
    /// host releases the editor once it has, so nothing may start that the release would cut
    /// short (Save, Delete, Close).
    /// </summary>
    private bool _handingOff;
    private bool _playingAudio;
    /// <summary>The editor holds a new word (source VocabularyWordEdit add mode), not a stored one.</summary>
    private bool _creating;
    private bool _disposed;
    private long _loadGeneration;
    private long _formGeneration;
    private long _listVersion;
    private long _formVersion;
    private long _sheetVersion;

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
        _lifetimeToken = _lifetime.Token;
        _cleanup = new VocabularyWordCleanup(store, logger, text, this);
    }

    /// <summary>Find Duplicates, the duplicate manager and Delete.</summary>
    private readonly VocabularyWordCleanup _cleanup;

    internal event Action? Changed;
    internal bool IsOpen => _listOpen;
    internal bool IsFormOpen => _listOpen && _formOpen;
    internal bool IsPhoneFilterPresented =>
        _listOpen && _filters?.PhoneSheetOpen == true;
    internal bool IsNavigationBlocked => _saving || AnyDeleting || _handingOff;

    /// <summary>
    /// The open duplicate page, without filtering the list (<see cref="ListState"/> carries the
    /// same panel); null when it is closed or the owner is not trusted.
    /// </summary>
    internal VocabularyDuplicatePanel? DuplicatePanel =>
        _listOpen && TryOwner(out _) ? _cleanup.Duplicates : null;

    /// <summary>The open editor is adding a new word (source VocabularyWordEdit add mode).</summary>
    internal bool IsCreatingWord => IsFormOpen && _creating;

    /// <summary>Source showDeleteConfirm.</summary>
    internal bool IsDeleteConfirmationPresented => _cleanup.IsDeleteConfirmationPresented;
    internal bool IsDeletePending => IsFormOpen && _cleanup.Deleting;

    /// <summary>Source deleteConfirmMessage for the loaded word; null until it loads.</summary>
    internal string? DeleteMessage => _cleanup.DeleteMessage;
    internal long ListVersion => Interlocked.Read(ref _listVersion);
    internal long FormVersion => Interlocked.Read(ref _formVersion);
    internal long SheetVersion => Interlocked.Read(ref _sheetVersion);
    internal bool ListMode => _listMode;
    internal bool FilterPanelHidden => _filterPanelHidden;

    /// <summary>
    /// The list's sort and grid/list choice: when it changes, the cards a head shows are a
    /// different arrangement (the iOS page starts again at the top).
    /// </summary>
    internal string Arrangement =>
        $"{_sortField}:{(_sortDescending ? "desc" : "asc")}:{(_listMode ? "list" : "grid")}";
    internal string SearchText => TryOwner(out _)
        ? _filters?.CommittedQuery ?? string.Empty : string.Empty;

    protected bool IsDisposed => _disposed;

    /// <summary>The screen's localized text for <paramref name="key"/> (a head passes it to its views).</summary>
    protected string Text(string key) => _text(key);

    /// <summary>Whether Save can run now (the rule of every Save button).</summary>
    protected bool CanSaveOpenWord => _formOpen && !_handingOff
        && VocabularyBrowserEdit.CanSaveWord(_saving || AnyDeleting, _detail, _draft);

    internal VocabularyBrowserList ListState
    {
        get
        {
            if (!TryOwner(out _))
                return new(false, true, "Vocabulary_NoActiveProfile",
                    new(false, [], []), [], new VocabularyFilterState(null, _ => { }),
                    _sortField, _sortDescending, _listMode, null, false,
                    SheetSortField: _sortField, SheetSortDescending: _sortDescending,
                    FilterPanelHidden: _filterPanelHidden);
            return new(_loading, _listErrorKey == "Vocabulary_NoActiveProfile",
                _listErrorKey, _catalog,
                _filters?.Filter(_catalog.Words, _sortField, _sortDescending) ?? [],
                _filters ?? new VocabularyFilterState(null, _ => { }),
                _sortField, _sortDescending, _listMode,
                _feedback, _feedbackWarning, _cleanup.Duplicates,
                SheetSortField: _sheetSort?.Field ?? _sortField,
                SheetSortDescending: _sheetSort?.Descending ?? _sortDescending,
                FilterPanelHidden: _filterPanelHidden,
                RowLimit: _rowLimit);
        }
    }

    /// <summary>The owner's word count from the last read, kept after the list closes (the Library row shows it).</summary>
    private (string Owner, int Count)? _knownWordCount;

    /// <summary>
    /// How many words the active owner has, from the last read of the list; null before one
    /// read or for another owner.
    /// </summary>
    internal int? KnownWordCount => _knownWordCount is { } known && TrustedOwner(known.Owner)
        ? known.Count : null;

    /// <summary>The list read or changed its words: the count the Library row shows follows.</summary>
    private void RememberWordCount(string owner) =>
        _knownWordCount = _catalog.OwnerValid ? (owner, _catalog.Words.Count) : null;

    /// <summary>The rows a head builds at first, and each time it reaches the end of them.</summary>
    internal const int RowPage = 100;

    private int _rowLimit = RowPage;

    /// <summary>
    /// Another search, filter, sort or arrangement starts the row window again at the first
    /// page (its results start at the top). Called where each of them changes.
    /// </summary>
    private void ResetRowWindow() => _rowLimit = RowPage;

    /// <summary>The list reached its last shown row: the next page joins it.</summary>
    internal void ShowMoreRows()
    {
        if (!_listOpen || _rowLimit >= int.MaxValue - RowPage)
            return;
        _rowLimit += RowPage;
        Notify(VocabularyChanges.List);
    }

    internal VocabularyBrowserEdit EditState => TryOwner(out _)
        ? new(_formLoading, _saving, _detail, _draft, _editErrorKey, _playingAudio,
            _photo, _cleanup.ConfirmingDelete, _cleanup.Deleting, _cleanup.DeleteError, _handingOff,
            ExamplesState, _creating)
        : new(false, false, null, null, "Vocabulary_NoActiveProfile",
            false, null);

    internal async Task<bool> OpenAsync(string owner)
    {
        if (_disposed || _listOpen || !TrustedOwner(owner))
            return false;
        _owner = owner;
        _ownerStamp = _userScope.CaptureStamp();
        // Every visit starts with no search or filter, as the source page does (its query comes
        // only from the link that opened it). Captain 2026-11-12: "The first load had a filter
        // already applied. Shouldn't be." The sort and grid/list choice are still remembered.
        _filters = new VocabularyFilterState(null, _ => ResetRowWindow());
        ResetRowWindow();
        var sort = _preferences.Get($"vocabulary_sort_{owner}", "added:desc");
        var parts = sort.Split(':', 2);
        _sortField = parts[0] == "updated" ? "updated" : "added";
        _sortDescending = parts.Length < 2 || parts[1] != "asc";
        _listMode = _preferences.Get($"vocabulary_view_{owner}", "grid") == "list";
        _filterPanelHidden = _preferences.Get(FilterPanelKey(owner), "shown") == "hidden";
        _sheetSort = null;
        _listOpen = true;
        _loading = true;
        _feedback = null;
        _feedbackWarning = false;
        Notify(VocabularyChanges.List);
        await ReloadAsync();
        return TryOwner(out _);
    }

    /// <summary>Back from the list: drops the catalog, the open word and the owner.</summary>
    internal void Close()
    {
        if (!_listOpen || _saving || AnyDeleting)
            return;
        _cleanup.Reset();
        _askingListDelete = null;
        ResetRowWindow();
        OnListReset();
        ResetExamples();
        _examplesChanged = false;
        _creating = false;
        _filters?.CancelPhoneSheet();
        _sheetSort = null;
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
        Notify(VocabularyChanges.List | VocabularyChanges.Form | VocabularyChanges.Sheet);
    }

    /// <summary>
    /// Back or Cancel from the word editor discards the draft. Sentences saved meanwhile change
    /// the word's strength badge, so the list reads its words again.
    /// </summary>
    internal void CloseWord()
    {
        if (!_formOpen || _saving || AnyDeleting || _handingOff)
            return;
        var reload = _examplesChanged;
        _cleanup.ResetDelete();
        LeaveWord();
        Notify(VocabularyChanges.Form);
        // A read that reports its own failure on the list (ReloadAsync catches it).
        if (reload)
            _ = ReloadAsync();
    }

    internal async Task ReloadAsync()
    {
        if (!_listOpen || !TryOwner(out var owner))
            return;
        var generation = Interlocked.Increment(ref _loadGeneration);
        var resourcesChanged = false;
        _loading = true;
        _listErrorKey = null;
        // This read counts the sentences saved so far.
        _examplesChanged = false;
        Notify(VocabularyChanges.List);
        try
        {
            // Off the UI thread: the catalog query reads every owned word.
            var catalog = await OffUiAsync(() => _store.ListOwnedVocabularyAsync(
                owner, _lifetimeToken));
            if (generation != Volatile.Read(ref _loadGeneration))
                return;
            if (!TryOwner(out _))
            {
                Block();
                return;
            }
            _catalog = catalog;
            RememberWordCount(owner);
            if (!catalog.OwnerValid)
                _listErrorKey = "Vocabulary_NoActiveProfile";
            // An open Add Word form chooses from the resources as they are now (one deleted
            // or synced away leaves the choice).
            if (_creating && _formOpen && _detail is { } detail && _draft is { } draft)
            {
                var resources = catalog.Resources;
                _detail = detail with { AvailableResources = resources };
                _draft = draft with { ResourceIds = draft.ResourceIds.Where(id =>
                    resources.Any(resource => resource.Id == id)).ToArray() };
                resourcesChanged = true;
            }
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
                Notify(VocabularyChanges.List | (_filters?.PhoneSheetOpen == true
                        ? VocabularyChanges.Sheet : VocabularyChanges.None)
                    | (resourcesChanged ? VocabularyChanges.ResourceToggle : VocabularyChanges.None));
            }
        }
    }

    /// <summary>
    /// The open editor holds changes that are not saved: a new word with any term typed, or an
    /// existing word whose draft differs from the one it loaded.
    /// </summary>
    internal bool HasUnsavedEdits => _formOpen && _draft is { } draft
        && (_creating
            ? !string.IsNullOrWhiteSpace(draft.TargetTerm) || !string.IsNullOrWhiteSpace(draft.NativeTerm)
            : _detail is { } detail && !SameDraft(detail.Draft, draft));

    private static bool SameDraft(NativeVocabularyEditDraft loaded, NativeVocabularyEditDraft current) =>
        current with { ResourceIds = loaded.ResourceIds } == loaded
        && loaded.ResourceIds.SequenceEqual(current.ResourceIds, StringComparer.Ordinal);

    internal async Task OpenWordAsync(string id)
    {
        if (!_listOpen || _saving || _listDeleting || !TryOwner(out var owner)
            || string.IsNullOrWhiteSpace(id))
            return;
        if (!_formOpen && !_catalog.Words.Any(word => word.Id == id))
            return;
        // Beside the list (the iPad and iPhone Duo split view) another word can be chosen while
        // one is open: unsaved edits stay, and the editor says to save them first.
        if (_formOpen && !string.Equals(id, _selectedWordId, StringComparison.Ordinal)
            && HasUnsavedEdits)
        {
            _editErrorKey = "VocabWordEdit_SaveBeforeOpeningAnother";
            Notify(VocabularyChanges.Form);
            return;
        }
        _selectedWordId = id;
        var generation = Interlocked.Increment(ref _formGeneration);
        ResetExamples();
        _creating = false;
        _formOpen = true;
        _formLoading = true;
        _editErrorKey = null;
        _photo = null;
        _cleanup.ResetDelete();
        Notify(VocabularyChanges.Form);
        try
        {
            var detail = await OffUiAsync(() => _store.GetOwnedVocabularyAsync(
                owner, id, _lifetimeToken));
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
                Notify(VocabularyChanges.Form);
            }
        }
    }

    internal Task RetryWordAsync() => _creating ? OpenNewWordAsync()
        : OpenWordAsync(_selectedWordId ?? string.Empty);

    /// <summary>
    /// Source Vocabulary Add: the editor opens on a new word in the owner's target language
    /// (Korean when none is set), of type Word, in no resource yet, with the owner's resources to
    /// choose from. When the search found nothing, its words start the target term (source
    /// GetInitialTargetTermForAdd). Nothing is stored until Save. It waits for the list to load,
    /// which reads the resources to choose from.
    /// </summary>
    internal async Task OpenNewWordAsync()
    {
        if (!_listOpen || _saving || _loading || !_catalog.OwnerValid || !TryOwner(out var owner))
            return;
        if (_formOpen && !_creating)
            return;
        var terms = _filters?.FreeTextTerms ?? [];
        var initialTarget = terms.Count > 0
            && (_filters?.Filter(_catalog.Words, _sortField, _sortDescending).Count ?? 0) == 0
                ? string.Join(" ", terms).Trim() : string.Empty;
        _selectedWordId = null;
        var generation = Interlocked.Increment(ref _formGeneration);
        ResetExamples();
        _creating = true;
        _formOpen = true;
        _formLoading = true;
        _editErrorKey = null;
        _photo = null;
        _detail = null;
        _draft = null;
        _cleanup.ResetDelete();
        Notify(VocabularyChanges.Form);
        string? language = null;
        try
        {
            language = await OffUiAsync(() => _store.GetOwnerTargetLanguageAsync(
                owner, _lifetimeToken));
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            // The form still opens; the learner can choose the language.
            _logger.LogWarning(ex, "Native Vocabulary could not read the target language for a new word.");
        }
        if (generation != Volatile.Read(ref _formGeneration) || !_formOpen || !_creating)
            return;
        if (!TryOwner(out _))
        {
            Block();
            return;
        }
        language = string.IsNullOrWhiteSpace(language) ? "Korean" : language.Trim();
        var now = DateTime.UtcNow;
        _draft = new NativeVocabularyEditDraft(string.Empty, now, initialTarget, string.Empty,
            null, language, null, null, null, LexicalUnitType.Word, []);
        _detail = new NativeVocabularyDetail(
            new NativeVocabularyEntry(string.Empty, string.Empty, string.Empty,
                LexicalUnitType.Word, null, null, language, now, now, "Basic",
                LearningStatus.Unknown, false, []),
            _draft, _catalog.Resources, null, [], [], [], false, NativeVocabularyDeleteBlock.None);
        _formLoading = false;
        Notify(VocabularyChanges.Form);
    }

    internal void ChangeDraft(string field, string value)
    {
        if (_draft is not { } draft
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
        // The type changes which sections the editor shows; typed text is already on screen.
        Notify(field == nameof(draft.LexicalUnitType)
            ? VocabularyChanges.Form : VocabularyChanges.DraftText);
    }

    internal void ToggleResource(string id, bool selected)
    {
        if (_draft is not { } draft
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
        Notify(hadError ? VocabularyChanges.Form : VocabularyChanges.ResourceToggle);
    }

    internal async Task SaveAsync()
    {
        if (!_formOpen || _saving || AnyDeleting || _handingOff || _detail is null
            || _draft is not { } draft
            || !TryOwner(out var owner))
            return;
        if (string.IsNullOrWhiteSpace(draft.TargetTerm)
            || string.IsNullOrWhiteSpace(draft.NativeTerm))
        {
            _editErrorKey = "VocabWordEdit_BothRequired";
            Notify(VocabularyChanges.Form);
            return;
        }
        _saving = true;
        _editErrorKey = null;
        Notify(VocabularyChanges.Form);
        var saved = false;
        var creating = _creating;
        try
        {
            var result = creating
                ? (await OffUiAsync(() => _store.CreateOwnedVocabularyAsync(
                    owner, draft, _lifetimeToken))).Status
                : await OffUiAsync(() => _store.SaveOwnedVocabularyAsync(
                    owner, draft, _lifetimeToken));
            if (result is NativeVocabularySaveStatus.Saved
                or NativeVocabularySaveStatus.SavedSyncUnconfirmed)
            {
                _feedback = _text(result != NativeVocabularySaveStatus.Saved
                    ? "NativeVocabulary_SavedSyncUnconfirmed"
                    : creating ? "VocabWordEdit_AddedSuccess" : "VocabWordEdit_UpdatedSuccess");
                _feedbackWarning =
                    result == NativeVocabularySaveStatus.SavedSyncUnconfirmed;
                _media.StopPlayback();
                ResetExamples();
                _creating = false;
                _formOpen = false;
                _draft = null;
                _detail = null;
                _photo = null;
                saved = true;
                await ReloadAsync();
            }
            else
            {
                if (!TryOwner(out _))
                {
                    Block();
                    return;
                }
                // A new word has nothing to be "not found": a refused create says it failed.
                _editErrorKey = creating && result == NativeVocabularySaveStatus.NotFoundOrForbidden
                    ? "VocabWordEdit_SaveFailed" : SaveErrorKey(result, _detail, draft);
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
            Notify(VocabularyChanges.List | VocabularyChanges.Form);
        }
        // Source: back on the list the manager scans again, once the save no longer holds
        // the editor. Leaving the list cancels it quietly.
        if (saved)
            await Guard(_cleanup.RescanDuplicatesAsync);
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
                : "VocabWordEdit_SaveFailed",
        NativeVocabularySaveStatus.Conflict => "NativeVocabulary_Conflict",
        _ => "VocabWordEdit_NotFound"
    };

    /// <summary>
    /// Where Find Duplicates goes when this editor is hosted outside the Vocabulary page (Import
    /// Content results): it receives the term and word and opens that page's duplicate manager.
    /// True when the host took the handoff: this editor stays open, with Save, Delete and Close
    /// waiting, until the Vocabulary page has opened, and the host then releases it (or reports a
    /// failure with <see cref="ShowEditError"/>).
    /// False when it cannot start now: the editor stays open and says so. Null on the Vocabulary
    /// page itself, which opens its own.
    /// </summary>
    internal Func<string, string, bool>? FindDuplicatesElsewhere { get; init; }

    /// <summary>Source OpenDuplicateManager (see <see cref="VocabularyWordCleanup"/>).</summary>
    internal Task FindDuplicatesAsync() => _creating ? Task.CompletedTask :
        Guard(() => _cleanup.FindDuplicatesAsync(FindDuplicatesElsewhere is { } elsewhere
            ? (term, wordId) =>
            {
                if (!elsewhere(term, wordId))
                {
                    ShowEditError(FindDuplicatesFailedKey);
                    return;
                }
                _handingOff = true;
                Notify(VocabularyChanges.Form);
            }
            : null));

    /// <summary>A handoff to the Vocabulary page that did not happen.</summary>
    internal const string FindDuplicatesFailedKey = "NativeVocabulary_LoadFailed";

    /// <summary>
    /// The open editor shows <paramref name="key"/> (a host's failure on the editor's behalf); a
    /// pending handoff has ended, so Save, Delete and Close work again.
    /// </summary>
    internal void ShowEditError(string key)
    {
        // A handoff that did not happen gives the editor back.
        _handingOff = false;
        if (_disposed || !IsFormOpen)
            return;
        _editErrorKey = key;
        Notify(VocabularyChanges.Form);
    }

    /// <summary>The duplicate manager focused on a term and word handed over from an editor elsewhere.</summary>
    internal Task OpenDuplicatesAsync(string term, string? wordId) =>
        Guard(() => _cleanup.OpenDuplicatesAsync(term, wordId));

    /// <summary>The list's Clean Up Duplicates: the duplicate page over all of the owner's words.</summary>
    internal Task OpenAllDuplicatesAsync() => Guard(_cleanup.OpenAllDuplicatesAsync);

    /// <summary>Source ShowMoreDuplicates.</summary>
    internal Task ShowMoreDuplicatesAsync() => Guard(_cleanup.ShowMoreDuplicatesAsync);

    /// <summary>Source MergeDuplicateGroup, then the list reloads (source LoadData).</summary>
    internal Task MergeDuplicatesAsync(string normalizedTerm) =>
        Guard(() => _cleanup.MergeDuplicatesAsync(normalizedTerm));

    /// <summary>Merge all: every safe group in one step, then the list reloads.</summary>
    internal Task MergeAllDuplicatesAsync() => Guard(_cleanup.MergeAllDuplicatesAsync);

    /// <summary>Skip: a review group leaves the duplicate page while it is open.</summary>
    internal void SkipDuplicates(string normalizedTerm) => _cleanup.SkipDuplicates(normalizedTerm);

    /// <summary>A copy goes into, or is left out of, its review group's Merge.</summary>
    internal void ToggleDuplicateCopy(string normalizedTerm, string wordId) =>
        _cleanup.ToggleDuplicateCopy(normalizedTerm, wordId);

    /// <summary>Source CloseCleanupPanel.</summary>
    internal void CloseDuplicates() => _cleanup.CloseDuplicates();

    /// <summary>Source RequestDelete: shows the confirmation.</summary>
    internal void RequestDelete()
    {
        // One confirmation at a time: not while the list's swipe Delete is asking or running.
        if (!_creating && _askingListDelete is null && !_listDeleting)
            _cleanup.RequestDelete();
    }

    /// <summary>Source CancelDelete (Cancel, Close or the backdrop); ignored while deleting.</summary>
    internal void CancelDelete() => _cleanup.CancelDelete();

    /// <summary>Source ConfirmDelete (see <see cref="VocabularyWordCleanup"/>).</summary>
    internal Task ConfirmDeleteAsync() => Guard(_cleanup.ConfirmDeleteAsync);

    /// <summary>Work cut short by disposal ends quietly.</summary>
    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
    }

    internal async Task PlayAudioAsync()
    {
        if (!_formOpen || _saving || _playingAudio || _draft is not { } draft
            || !TryOwner(out var owner))
            return;
        _playingAudio = true;
        Notify(VocabularyChanges.Form);
        try
        {
            // A new word is not stored yet: only the typed term is spoken.
            if (!_creating && !await OffUiAsync(() => _store.OwnsVocabularyAsync(
                    owner, draft.Id, _lifetimeToken)))
            {
                _editErrorKey = "VocabWordEdit_NotFound";
                return;
            }
            var language = string.IsNullOrWhiteSpace(draft.Language)
                ? "Korean" : draft.Language;
            if (!await _media.PlayPromptAsync(
                draft.TargetTerm, language, _lifetimeToken))
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
            Notify(VocabularyChanges.Form);
        }
    }

    private async Task LoadPhotoPreviewAsync(string owner, string wordId, string uri)
    {
        try
        {
            if (!await OffUiAsync(() => _store.OwnsVocabularyAsync(
                    owner, wordId, _lifetimeToken)))
                return;
            var photo = await _media.LoadPhotoAsync(uri, _lifetimeToken);
            if (_formOpen && _draft is { } draft
                && draft.Id == wordId && draft.MnemonicImageUri == uri
                && _selectedWordId == wordId && TryOwner(out _))
            {
                _photo = photo;
                Notify(VocabularyChanges.Form);
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
        Notify(VocabularyChanges.List);
    }

    /// <summary>The search bar's text beside its filter chips changed (the chips stay).</summary>
    internal void SearchField(string value)
    {
        if (!TryOwner(out _) || _filters is null)
            return;
        _filters.SearchField(value);
        Notify(VocabularyChanges.List);
    }

    /// <summary>
    /// Tokens were deleted in the search field: their filters go. The list always redraws,
    /// so the field's tokens match the filters again even when nothing could change.
    /// </summary>
    internal void KeepChips(IReadOnlyList<string> remaining)
    {
        if (TryOwner(out _))
            _filters?.KeepChips(remaining);
        Notify(VocabularyChanges.List);
    }

    internal void OpenPhoneFilters()
    {
        if (!TryOwner(out _) || _filters is null)
            return;
        _filters.OpenPhoneSheet();
        _sheetSort = (_sortField, _sortDescending);
        Notify(VocabularyChanges.Sheet);
    }

    /// <summary><paramref name="live"/> applies at once (wide layout); otherwise it edits the sheet draft.</summary>
    internal void ChangeFilter(string type, string? value, bool live)
    {
        if (!TryOwner(out _) || _filters is null)
            return;
        _filters.ChangeFilter(type, value, live);
        Notify(live ? VocabularyChanges.List : VocabularyChanges.FilterDraft);
    }

    internal void ChangeWideFilter(string type, string? value) =>
        ChangeFilter(type, value, live: true);

    internal void ApplyPhoneFilters()
    {
        if (!TryOwner(out var owner) || _filters is null)
            return;
        _filters.ApplyPhoneSheet();
        if (_sheetSort is { } sort)
        {
            // The sheet's sort is applied with its filters (Cancel leaves the list as it was).
            var changed = sort.Field != _sortField || sort.Descending != _sortDescending;
            (_sortField, _sortDescending) = sort;
            ResetRowWindow();
            _sheetSort = null;
            if (changed)
                SaveSort(owner);
        }
        Notify(VocabularyChanges.List | VocabularyChanges.Sheet);
    }

    internal void CancelPhoneFilters()
    {
        _filters?.CancelPhoneSheet();
        _sheetSort = null;
        Notify(VocabularyChanges.Sheet);
    }

    /// <summary>
    /// The phone filter sheet's Added or Updated: the same rule as <see cref="SetSort"/> on the
    /// sheet's draft, applied with the filters.
    /// </summary>
    internal void SetSheetSort(string field)
    {
        if (!TryOwner(out _) || _filters?.PhoneSheetOpen != true || _sheetSort is not { } sort)
            return;
        _sheetSort = NextSort(sort.Field, sort.Descending, field);
        Notify(VocabularyChanges.Sheet);
    }

    /// <summary>The wide layout's toolbar Filters button: shows or hides the filter column.</summary>
    internal void ToggleFilterPanel()
    {
        if (!TryOwner(out var owner))
            return;
        _filterPanelHidden = !_filterPanelHidden;
        _preferences.Set(FilterPanelKey(owner), _filterPanelHidden ? "hidden" : "shown");
        Notify(VocabularyChanges.List);
    }

    internal void ClearFilters()
    {
        if (!TryOwner(out _) || _filters is null)
            return;
        _filters.ClearAll();
        Notify(VocabularyChanges.List | VocabularyChanges.Sheet);
    }

    internal void SetSort(string field)
    {
        if (!TryOwner(out var owner))
            return;
        (_sortField, _sortDescending) = NextSort(_sortField, _sortDescending, field);
        ResetRowWindow();
        SaveSort(owner);
        // An open sheet's draft follows, so its Apply cannot put back the sort it opened with.
        if (_sheetSort is not null)
            _sheetSort = (_sortField, _sortDescending);
        Notify(VocabularyChanges.List | (_sheetSort is null
            ? VocabularyChanges.None : VocabularyChanges.Sheet));
    }

    /// <summary>The sort already chosen flips its direction; the other one starts newest first.</summary>
    private static (string Field, bool Descending) NextSort(
        string current, bool descending, string chosen) =>
        current == chosen ? (current, !descending)
            : (chosen == "updated" ? "updated" : "added", true);

    private void SaveSort(string owner) =>
        _preferences.Set($"vocabulary_sort_{owner}",
            $"{_sortField}:{(_sortDescending ? "desc" : "asc")}");

    internal void SetListMode(bool list)
    {
        if (!TryOwner(out var owner))
            return;
        _listMode = list;
        ResetRowWindow();
        _preferences.Set($"vocabulary_view_{owner}", list ? "list" : "grid");
        Notify(VocabularyChanges.List);
    }

    /// <summary>
    /// An action the head started and did not await failed unexpectedly: logged, and shown as
    /// the editor's save error or the list's load error.
    /// </summary>
    protected void ReportActionFailure(Exception ex)
    {
        _logger.LogError(ex, "Native Vocabulary action failed.");
        if (_disposed)
            return;
        if (_formOpen)
            _editErrorKey = "VocabWordEdit_SaveFailed";
        else
            _listErrorKey = "NativeVocabulary_LoadFailed";
        Notify(VocabularyChanges.List | VocabularyChanges.Form);
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
        _askingListDelete = null;
        _knownWordCount = null;
        OnListReset();
        _detail = null;
        _draft = null;
        _photo = null;
        _selectedWordId = null;
        _owner = null;
        Interlocked.Increment(ref _formGeneration);
        Interlocked.Increment(ref _loadGeneration);
        _filters?.CancelPhoneSheet();
        _sheetSort = null;
        _formOpen = false;
        _formLoading = false;
        _loading = false;
        _feedback = null;
        _cleanup.Reset();
        ResetExamples();
        _examplesChanged = false;
        _creating = false;
        _listErrorKey = "Vocabulary_NoActiveProfile";
        _media.StopPlayback();
        Notify(VocabularyChanges.List | VocabularyChanges.Form | VocabularyChanges.Sheet);
    }

    /// <summary>Leaves the editor and discards its draft. No publish.</summary>
    private void LeaveWord()
    {
        ResetExamples();
        _creating = false;
        _formOpen = false;
        _detail = null;
        _draft = null;
        _photo = null;
        _selectedWordId = null;
        _editErrorKey = null;
        _media.StopPlayback();
        Interlocked.Increment(ref _formGeneration);
    }

    private static string FilterPanelKey(string owner) => $"vocabulary_filter_panel_{owner}";

    private void Notify(VocabularyChanges changes)
    {
        if (!_disposed)
            OnChanged(changes);
    }

    /// <summary>
    /// Redraws for <paramref name="changes"/>. Android redraws its single Compose tree: the
    /// sheet and its draft redraw the list, and the editor's in-place changes redraw the form.
    /// </summary>
    protected virtual void OnChanged(VocabularyChanges changes)
    {
        const VocabularyChanges list = VocabularyChanges.List | VocabularyChanges.Sheet
            | VocabularyChanges.FilterDraft;
        const VocabularyChanges form = VocabularyChanges.Form | VocabularyChanges.DeleteDialog
            | VocabularyChanges.DraftText | VocabularyChanges.ResourceToggle
            | VocabularyChanges.Examples | VocabularyChanges.ExampleText;
        Publish((changes & list) != 0, (changes & form) != 0, sheet: false);
    }

    /// <summary>Moves the given versions on and raises <see cref="Changed"/>.</summary>
    protected void Publish(bool list, bool form, bool sheet)
    {
        if (_disposed)
            return;
        if (list)
            Interlocked.Increment(ref _listVersion);
        if (form)
            Interlocked.Increment(ref _formVersion);
        if (sheet)
            Interlocked.Increment(ref _sheetVersion);
        RaiseChanged();
    }

    protected virtual void RaiseChanged() => Changed?.Invoke();

    /// <summary>The list closed or its owner was refused: a head drops what it kept for the list (a chooser left open).</summary>
    protected virtual void OnListReset()
    {
    }

    /// <summary>
    /// Runs store work off the UI thread: SQLite work is synchronous under its async API. On
    /// Android the awaiting UI-thread caller resumes through the looper's context; the iOS head
    /// resumes on the main queue itself.
    /// </summary>
    protected virtual Task<T> OffUiAsync<T>(Func<Task<T>> work) =>
        Task.Run(work, _lifetimeToken);

    bool IVocabularyCleanupHost.TryOpenOwner(out string owner)
    {
        // A closed list is not an owner change: nothing to block or report.
        owner = string.Empty;
        return !_disposed && _listOpen && TryOwner(out owner);
    }

    bool IVocabularyCleanupHost.IsFormOpen => IsFormOpen;
    bool IVocabularyCleanupHost.IsSaving => _saving || _handingOff;
    NativeVocabularyDetail? IVocabularyCleanupHost.OpenDetail => _detail;
    NativeVocabularyEditDraft? IVocabularyCleanupHost.OpenDraft => _draft;
    CancellationToken IVocabularyCleanupHost.Lifetime => _lifetimeToken;
    bool IVocabularyCleanupHost.IsDisposed => _disposed;
    void IVocabularyCleanupHost.LeaveWord() => LeaveWord();

    void IVocabularyCleanupHost.ShowListFeedback(string message, bool warning)
    {
        _feedback = message;
        _feedbackWarning = warning;
    }

    Task IVocabularyCleanupHost.ReloadListAsync() => ReloadAsync();

    async Task IVocabularyCleanupHost.RefreshOpenWordAsync()
    {
        if (!_formOpen || _creating || _selectedWordId is not { } id || !TryOwner(out var owner))
            return;
        var generation = Volatile.Read(ref _formGeneration);
        try
        {
            var fresh = await OffUiAsync(() => _store.GetOwnedVocabularyAsync(
                owner, id, _lifetimeToken));
            if (fresh is null || generation != Volatile.Read(ref _formGeneration)
                || !_formOpen || _selectedWordId != id || !TryOwner(out _))
                return;
            // The learner's draft stays; only what the word allows now changes.
            _detail = fresh;
            Notify(VocabularyChanges.Form);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Native Vocabulary word refresh after a refused delete failed.");
        }
    }

    void IVocabularyCleanupHost.PublishChanges(bool list, bool form, bool deleteDialog) =>
        Notify((list ? VocabularyChanges.List : VocabularyChanges.None)
            | (form ? VocabularyChanges.Form : VocabularyChanges.None)
            | (deleteDialog ? VocabularyChanges.DeleteDialog : VocabularyChanges.None));

    Task<T> IVocabularyCleanupHost.OffUiAsync<T>(Func<Task<T>> work) => OffUiAsync(work);

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
