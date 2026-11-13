using System.Collections.Immutable;
using System.Globalization;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

internal sealed partial class LearningResourcesCoordinator : IDisposable
{
    private readonly NativeResourcePresenter _presenter;
    private readonly ILogger<LearningResourcesCoordinator> _logger;
    private readonly Func<string, string> _text;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NativeResourceSyncAction _syncAction = new();
    private readonly List<SwiftView> _saveButtons = [];
    private readonly List<SwiftView> _importButtons = [];
    private SourceUiStyle? _formStyle;
    private CancellationTokenSource? _quickSearch;
    private long _quickSearchVersion;
    private long _quickSuggestionsRevision;
    private long _formProjectionVersion;
    private long _formGeneration;
    private string? _selectedId;
    private string? _listFeedback;
    private string? _formStatus;
    private string? _formErrorKey;
    private string? _fileStatus;
    private string _quickQuery = string.Empty;
    private string _quickNativeTerm = string.Empty;
    private string _pasteText = string.Empty;
    private string _delimiter = "comma";
    // The add sheet (Captain 2026-11-13: quick add is a sheet with the system search field).
    private SwiftView? _addWordsSearch;
    private SwiftView? _addWordsList;
    private string? _lastAddedTerm;
    private bool _addWordsOpen;
    // The vocabulary rows built so far (Load More adds the next page) and the words the
    // form knew when it was drawn (a word added since is new, wherever it sorts).
    private const int WordPage = 100;
    private int _wordWindow = WordPage;
    private HashSet<string> _knownWordIds = new(StringComparer.Ordinal);
    private SwiftView? _stagedGrid;
    private SwiftView? _importSheetButton;
    private LearningResourceActions? _formActions;
    private LearningResourceInputs? _formInputs;
    private bool _chooseFileAfterImportSheet;
    private SwiftView? _vocabularySection;
    private bool _bulkExpanded;
    private bool _createWordExpanded;
    private bool _formOpen;
    private bool _creating;
    private bool _loadingList;
    /// <summary>
    /// The <see cref="NativeResourcePresenter.ListSourceVersion"/> the last successful read
    /// started at; null before one, and from the start of every read until it succeeds.
    /// </summary>
    private long? _readVersion;
    /// <summary>The version the running read started at.</summary>
    private long? _loadingVersion;
    private bool _generating;
    private bool _importing;
    private bool _quickAdding;
    private bool _fileImporterPresented;
    private bool _disposed;
#if DEBUG
    private bool _controlledStarterLoading;
#endif

    internal LearningResourcesCoordinator(
        NativeResourcePresenter presenter,
        ILogger<LearningResourcesCoordinator> logger,
        Func<string, string> text)
    {
        _presenter = presenter;
        _logger = logger;
        _text = text;
    }

    internal event Action? Changed;
    internal event Action<bool>? FormOpenChanged;
    internal event Action<string>? FormTitleChanged;
    internal event Action<bool>? FileImporterChanged;

    /// <summary>The count under the Resources title.</summary>
    internal string ListSubtitle => LearningResourcesView.CountText(_presenter.List, _text);

    /// <summary>A list row's Study: the app opens Study > Choose My Own with this resource.</summary>
    internal event Action<string>? StudyRequested;

    /// <summary>The listed resource whose Delete awaits the person's confirmation.</summary>
    private string? _pendingListDelete;

    /// <summary>
    /// The resource awaiting delete confirmation, while it is still listed; one that left the
    /// list (a sync, a filter) is no longer awaited, so later deletes are not blocked.
    /// </summary>
    private NativeResourceItem? PendingListDelete()
    {
        if (_pendingListDelete is not { } id)
            return null;
        var item = _presenter.List.Items.IsDefault ? null
            : _presenter.List.Items.FirstOrDefault(row => row.Id == id);
        if (item is null)
            _pendingListDelete = null;
        return item;
    }
    internal bool IsFormOpen => _formOpen;
    internal bool IsCreatingForm => _creating;
    internal long FormProjectionVersion => Interlocked.Read(ref _formProjectionVersion);
    internal bool IsQuickAddSearching =>
        _presenter.Detail.VocabularySearch?.Phase
            == NativeResourceVocabularySearchPhase.Searching;
    internal bool IsFileImporterPresented => _fileImporterPresented;
    internal bool IsNavigationBlocked
    {
        get
        {
            var detail = _presenter.Detail;
            return (detail.Phase is NativeResourceDetailPhase.Saving
                    or NativeResourceDetailPhase.Deleting)
                || detail.Reloading || detail.VocabularyBusy
                || _presenter.List.StarterBusy
                || _generating || _importing || _quickAdding;
        }
    }
    internal NativeResourceListState ListState => _presenter.List;
    internal LearningResourceViewState FormState => ProjectFormState();
    internal bool IsStarterBusy => _presenter.List.StarterBusy;
    internal bool IsDeletePending =>
        _presenter.Detail.Phase == NativeResourceDetailPhase.Deleting;
    internal bool HasQuickAddField
    {
        get
        {
            var detail = _presenter.Detail;
            return _formOpen && !_creating
                && detail.Resource is { IsSmartResource: false }
                && detail.Phase is not (NativeResourceDetailPhase.Idle
                    or NativeResourceDetailPhase.Loading
                    or NativeResourceDetailPhase.NotFound
                    or NativeResourceDetailPhase.LoadError
                    or NativeResourceDetailPhase.Blocked
                    or NativeResourceDetailPhase.ReadOnly);
        }
    }
    /// <summary>Whether the add sheet shows (quick add, Captain 2026-11-13).</summary>
    internal bool IsAddWordsOpen => _addWordsOpen && HasQuickAddField;

    internal SwiftView RenderBrowser(SourceUiStyle style)
    {
        var state = _presenter.List;
#if DEBUG
        if (_controlledStarterLoading)
            state = state with { StarterBusy = true };
#endif
        _drawnPageWidth = PageWidth ?? style.ViewportWidth;
        return LearningResourcesView.Page(state, _listFeedback, style, _text,
            new LearningResourcesActions(
                Search: Search,
                FilterType: type => FilterNow(type, _presenter.List.Language),
                FilterLanguage: language => FilterNow(_presenter.List.MediaType, language),
                KeepFilterTokens: KeepFilterTokens,
                RequestListDelete: RequestListDelete,
                ConfirmListDelete: () => Run(ConfirmListDeleteAsync),
                CancelListDelete: CancelListDelete,
                Study: id => StudyRequested?.Invoke(id),
                ToggleViewMode: ToggleViewMode,
                Open: id => Run(() => OpenAsync(id)),
                Add: OpenCreate,
                CreateStarter: () => Run(CreateStarterAsync),
                Reload: () => Run(LoadListAsync),
                Sort: SetSort,
                OpenFilters: OpenFilterSheet,
                ToggleFilterPanel: ToggleFilterPanel,
                ClearFilters: ClearFilters,
                ClearSearchAndFilters: ClearSearchAndFilters,
                PageWidthChanged: OnPageWidthChanged),
            PageWidth, PendingListDelete() is { } pending
                ? string.IsNullOrWhiteSpace(pending.Title) ? _text("NativeResources_Untitled") : pending.Title
                : null);
    }

#if DEBUG
    internal async Task BeginControlledStarterPreviewAsync(CancellationToken cancellationToken)
    {
        var loaded = await _presenter.LoadListAsync(cancellationToken);
        if (loaded.Phase != NativeResourceListPhase.Populated)
            throw new InvalidOperationException(
                "The starter styling preview needs its own populated resource list.");
        _presenter.Search("__isolated_style_starter_no_match__");
        if (_presenter.List.Phase != NativeResourceListPhase.NoMatches)
            throw new InvalidOperationException(
                "The starter styling preview needs its own empty filtered view.");
        _controlledStarterLoading = true;
    }

    internal void EndControlledStarterPreview()
    {
        _controlledStarterLoading = false;
        Publish(formChanged: false);
    }
#endif

    internal void Search(string? query)
    {
        if (_disposed || string.Equals(_presenter.List.SearchText,
            query ?? string.Empty, StringComparison.Ordinal))
            return;
        _presenter.Search(query);
        Publish(formChanged: false);
    }

    internal SwiftView RenderForm(SourceUiStyle style)
    {
        _saveButtons.Clear();
        _importButtons.Clear();
        _stagedGrid = null;
        _vocabularySection = null;
        _formStyle = style;
        var state = ProjectFormState();
        _knownWordIds = state.Vocabulary.Select(word => word.Id)
            .ToHashSet(StringComparer.Ordinal);
        var actions = new LearningResourceActions(
                Change: ChangeDraft,
                Save: () => Run(SaveAsync),
                Cancel: CancelEdit,
                Reload: () => Run(ReloadDraftAsync),
                Retry: () => Run(ReloadFormAsync),
                Sync: () => Run(SynchronizeAsync),
                RequestDelete: RequestDelete,
                CancelDelete: CancelDelete,
                Delete: () => Run(DeleteResourceAsync),
                Generate: () => Run(GenerateVocabularyAsync),
                AttachWord: id => Run(() => AttachVocabularyAsync(id)),
                CreateWord: () => Run(() => CreateVocabularyAsync(
                    _quickQuery.Trim(), _quickNativeTerm.Trim())),
                RemoveWord: RemoveVocabulary,
                SetWordType: (id, kind) =>
                {
                    if (_formOpen && !IsNavigationBlocked
                        && _presenter.SetStagedLexicalUnitType(id, kind))
                        Publish(formChanged: false);
                },
                ImportText: () =>
                {
                    // The sheet's Import closes it; the form shows the progress and result.
                    if (_bulkExpanded && !_creating)
                    {
                        _bulkExpanded = false;
                        MarkSheetClosing();
                        Publish(formChanged: false);
                    }
                    Run(() => ImportVocabularyTextAsync(_pasteText, _delimiter));
                });
        var form = LearningResourceView.Page(style, state, _creating, actions,
            _formInputs = new LearningResourceInputs(
                _pasteText, _delimiter,
                PasteTextChanged: value =>
                {
                    _pasteText = value;
                    UpdateImportButtons();
                },
                // The segmented picker shows its own choice; Import reads _delimiter. No
                // redraw (one would return the form, under an open sheet too, to the top).
                DelimiterChanged: value => _delimiter = value,
                // Captain 2026-11-13: an existing resource's bulk import and quick add are
                // sheets over the form (the scene presents them; RenderSheet draws them).
                ToggleBulk: OpenImportSheet,
                OpenAddWords: OpenAddWords,
                WordWindow: _wordWindow,
                ShowMoreWords: ShowMoreWords,
                ChooseFile: () =>
                {
                    if (!_formOpen || IsNavigationBlocked)
                        return;
                    // From the import sheet: the file chooser opens once the sheet has gone
                    // (a view cannot present while its sheet is up).
                    if (_bulkExpanded && !_creating)
                    {
                        _chooseFileAfterImportSheet = true;
                        _bulkExpanded = false;
                        MarkSheetClosing();
                        Publish(formChanged: false);
                        return;
                    }
                    _fileStatus = null;
                    _fileImporterPresented = true;
                    FileImporterChanged?.Invoke(true);
                },
                RegisterImport: button => _importButtons.Add(button),
                RegisterStagedGrid: table => _stagedGrid = table,
                RegisterVocabularySection: section =>
                    _vocabularySection = section), _text);
        _formActions = actions;
        var content = LearningResourceView.WithToolbar(form, style, state,
            _creating, IsNavigationBlocked, actions,
            button => _saveButtons.Add(button), _text);
        UpdateSaveButtons();
        UpdateImportButtons();
        return content;
    }

    /// <summary>Whether an existing resource's bulk import or add sheet shows.</summary>
    /// The add sheet shows only while the resource can take words (a sync conflict or reload
    /// closes it, as the form's own quick add was hidden).
    internal bool IsSheetPresented => _formOpen && !_creating && (_bulkExpanded || IsAddWordsOpen);

    // A sheet closed by the app (Done, Cancel, Import, Choose File) reports its dismissal
    // later; until then neither sheet opens, so that late report cannot close the next one.
    private bool _sheetClosing;

    private void MarkSheetClosing()
    {
        _sheetClosing = true;
        // A dismissal that never arrives (the form closed first) must not block the sheets.
        CoreFoundation.DispatchQueue.MainQueue.DispatchAfter(
            new CoreFoundation.DispatchTime(CoreFoundation.DispatchTime.Now, 1_500_000_000),
            () => _sheetClosing = false);
    }

    /// <summary>The sheet that shows: bulk import or quick add.</summary>
    internal SwiftView RenderSheet(SourceUiStyle style) =>
        _addWordsOpen ? RenderAddWordsSheet(style) : RenderImportSheet(style);

    private void OpenAddWords()
    {
        if (!HasQuickAddField || IsNavigationBlocked || _bulkExpanded || _addWordsOpen
            || _sheetClosing || !_presenter.CanQuickAdd)
            return;
        _addWordsOpen = true;
        _quickQuery = string.Empty;
        _quickNativeTerm = string.Empty;
        _createWordExpanded = false;
        _lastAddedTerm = null;
        Publish(formChanged: false);
    }

    private void CloseAddWords()
    {
        if (!_addWordsOpen)
            return;
        _addWordsOpen = false;
        MarkSheetClosing();
        Publish(formChanged: false);
    }

    /// <summary>
    /// Quick add (Captain 2026-11-13): the system search field over a list of the learner's
    /// matching words; tap one to add it, or add the typed word as a new one. Done closes.
    /// </summary>
    private SwiftView RenderAddWordsSheet(SourceUiStyle style)
    {
        _renderedAddWordsRevision = Interlocked.Read(ref _quickSuggestionsRevision);
        var list = Views.List(AddWordsRows(style))
            .WithAccessibilityIdentifier("native.resources.edit.quick.listbox");
        _addWordsList = list;
        var search = Views.Searchable(style.ThemedList(list),
                _text("ResourceEdit_QuickAddPlaceholder"), _quickQuery, query =>
                {
                    if (!_addWordsOpen || query == _quickQuery)
                        return;
                    _quickQuery = query;
                    _quickNativeTerm = string.Empty;
                    _createWordExpanded = false;
                    _lastAddedTerm = null;
                    Run(() => SearchVocabularyAsync(query));
                })
            .OnSubmit(SubmitAddWords)
            .WithAccessibilityIdentifier(LearningResourceQuickAddView.QuickAddFieldAutomationId)
            .WithNavigationTitle(_text("ResourceEdit_QuickAddLabel"))
            .WithInlineNavigationTitle();
        search.FocusRequested = true;
        _addWordsSearch = search;
        var done = Views.Button(_text("Common_Done"), CloseAddWords)
            .WithAccessibilityIdentifier("native.resources.edit.quick.done");
        return Views.NavigationStack(Views.Toolbar(search, done))
            .WithAccessibilityIdentifier("native.resources.edit.quick.sheet");
    }

    private long _renderedAddWordsRevision = -1;

    /// <summary>
    /// Return (Search) in the add sheet's field: adds the only match, or opens the new-word
    /// step when nothing matches (the old field's Enter did the same).
    /// </summary>
    private void SubmitAddWords()
    {
        if (!IsAddWordsOpen || _quickAdding || string.IsNullOrWhiteSpace(_quickQuery)
            || _presenter.Detail.VocabularySearch is not {
                Phase: NativeResourceVocabularySearchPhase.Ready } search)
            return;
        if (search.Results.Length == 1)
        {
            var id = search.Results[0].Id;
            Run(() => AttachVocabularyAsync(id));
        }
        else if (search.Results.Length == 0 && !_createWordExpanded)
        {
            _createWordExpanded = true;
            Interlocked.Increment(ref _quickSuggestionsRevision);
            Publish(formChanged: false);
        }
    }

    /// <summary>
    /// Redraws the add sheet's rows when the search, its results or the new-word step changed
    /// (on the main thread, from the scene's update); the search field is not touched.
    /// </summary>
    internal void RefreshAddWords(SourceUiStyle style)
    {
        if (!IsAddWordsOpen || _addWordsList is not { } list)
            return;
        var revision = Interlocked.Read(ref _quickSuggestionsRevision);
        if (revision == _renderedAddWordsRevision)
            return;
        _renderedAddWordsRevision = revision;
        while (list.Children.Count > 0)
        {
            var previous = list.Children[0];
            list.RemoveAt(0);
            previous.Dispose();
        }
        foreach (var row in AddWordsRows(style))
            list.Add(row);
    }

    /// <summary>The add sheet's rows as they are now.</summary>
    private SwiftView[] AddWordsRows(SourceUiStyle style)
    {
        var state = ProjectFormState();
        var rows = LearningResourceQuickAddView.SheetRows(state, _quickQuery, _quickAdding,
            _createWordExpanded, _quickNativeTerm, value => _quickNativeTerm = value,
            id => Run(() => AttachVocabularyAsync(id)),
            () =>
            {
                if (!_addWordsOpen)
                    return;
                _createWordExpanded = true;
                Interlocked.Increment(ref _quickSuggestionsRevision);
                Publish(formChanged: false);
            },
            () => Run(() => CreateVocabularyAsync(
                _quickQuery.Trim(), _quickNativeTerm.Trim())),
            style, _text);
        // A failed search, add or create says so here: the form under the sheet is hidden.
        if (state.ErrorKey is { } error)
            rows = [style.Text(Views.Text(LearningResourceView.FormatError(error, _text)),
                        SourceTextRole.Body2, SourceTextTone.Danger)
                    .WithAccessibilityIdentifier("native.resources.edit.quick.error"),
                .. rows];
        else if (rows.Length == 0 && _lastAddedTerm is { Length: > 0 } added)
            return [Views.Label(string.Format(CultureInfo.CurrentCulture,
                        _text("ResourceEdit_WordAddedNamed"), added), "checkmark.circle")
                .WithForegroundColor(style.Appearance.Success.Light,
                    style.Appearance.Success.Dark)
                .WithAccessibilityIdentifier("native.resources.edit.quick.added")];
        return rows;
    }

    private void OpenImportSheet()
    {
        if (!_formOpen || _creating || IsNavigationBlocked || _bulkExpanded || _addWordsOpen
            || _sheetClosing)
            return;
        _bulkExpanded = true;
        _chooseFileAfterImportSheet = false;
        Publish(formChanged: false);
    }

    private void CloseImportSheet()
    {
        if (!_bulkExpanded)
            return;
        _bulkExpanded = false;
        MarkSheetClosing();
        Publish(formChanged: false);
    }

    /// <summary>
    /// The sheet has gone (Cancel, Import, Choose File or a swipe down). Choose File opens the
    /// file chooser now that nothing covers the form.
    /// </summary>
    internal void OnSheetDismissed()
    {
        // Cancel, Done, Import and Choose File already published the close; a swipe down
        // (or a sync that ends quick add) did not.
        var closedByApp = _sheetClosing;
        _sheetClosing = false;
        var swipedAway = !closedByApp && (_bulkExpanded || _addWordsOpen);
        _bulkExpanded = false;
        _addWordsOpen = false;
        _addWordsSearch = null;
        _addWordsList = null;
        _importSheetButton = null;
        if (!_chooseFileAfterImportSheet)
        {
            if (swipedAway)
                Publish(formChanged: false);
            return;
        }
        _chooseFileAfterImportSheet = false;
        if (!_formOpen || IsNavigationBlocked)
            return;
        _fileStatus = null;
        _fileImporterPresented = true;
        FileImporterChanged?.Invoke(true);
    }

    /// <summary>
    /// The bulk import sheet (Captain 2026-11-13): the pasted words and their delimiter, then a
    /// file to import instead; Cancel and Import in its toolbar.
    /// </summary>
    internal SwiftView RenderImportSheet(SourceUiStyle style)
    {
        if (_formActions is not { } actions || _formInputs is not { } formInputs)
            return Views.Text(string.Empty);
        var inputs = formInputs with { PasteText = _pasteText, Delimiter = _delimiter };
        var rows = LearningResourceVocabularyView.ImportRows(ProjectFormState(),
            creating: false, busy: _importing, actions, inputs, style, _text,
            includeImport: false);
        var form = style.GroupedForm(Views.Form(
                Views.Section(_text("ResourceEdit_ImportVocabLabel"), rows[0], rows[1]),
                Views.Section(_text("ResourceEdit_OrImportFile"), rows.Skip(2).ToArray()))
            .WithAccessibilityIdentifier("native.resources.edit.bulk.form"))
            .WithNavigationTitle(_text("ResourceEdit_BulkImportToggle"))
            .WithInlineNavigationTitle();
        // Cancel's role puts it at the sheet's leading edge (the system's cancellation place).
        var cancel = Views.Button(_text("Common_Cancel"), CloseImportSheet)
            .WithButtonRole(SwiftButtonRole.Cancel)
            .WithAccessibilityIdentifier("native.resources.edit.bulk.cancel");
        var import = Views.Button(_text("ResourceEdit_Import"), actions.ImportText)
            .WithAccessibilityIdentifier("native.resources.form.vocabulary.import");
        _importSheetButton = import;
        UpdateImportButtons();
        return Views.NavigationStack(Views.Toolbar(form, cancel, import))
            .WithAccessibilityIdentifier("native.resources.edit.bulk.sheet");
    }

    /// <summary>
    /// The list shows the owner's resources as they are now: the running read started at
    /// the current <see cref="NativeResourcePresenter.ListSourceVersion"/>, or the last read
    /// did, succeeded, and the presenter still shows it (not Idle, a load error or Blocked).
    /// </summary>
    internal bool IsListFresh
    {
        get
        {
            var current = _presenter.ListSourceVersion;
            if (_loadingList)
                return _loadingVersion == current;
            return _readVersion == current && _presenter.List is {
                Phase: NativeResourceListPhase.Populated or NativeResourceListPhase.Empty
                    or NativeResourceListPhase.NoMatches,
                ErrorKey: null };
        }
    }

    /// <summary>A profile save may have added smart resources: the list is stale.</summary>
    internal void NoteResourcesWritten() => _presenter.NoteResourcesWritten();

    /// <summary>The one freshness path: reads the list unless it is fresh.</summary>
    internal void ReadListIfStale()
    {
        if (!IsListFresh)
            Run(LoadListAsync);
    }

    /// <summary>Opens an owned resource detail from outside the browser (Dashboard insight links).</summary>
    internal void Open(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;
        Run(() => OpenAsync(id));
    }

    internal void OpenCreate()
    {
        if (_disposed || _formOpen || IsNavigationBlocked)
            return;
        var state = _presenter.BeginCreate();
        if (state.Phase != NativeResourceDetailPhase.Creating)
        {
            _listFeedback = _text("NativeResources_LoadError");
            Publish(formChanged: false);
            return;
        }
        ResetForm(creating: true);
        _formOpen = true;
        FormOpenChanged?.Invoke(true);
        Publish();
    }

    internal void OnFormPopped()
    {
        if (!_formOpen || IsNavigationBlocked)
            return;
        _presenter.CancelEdit();
        CloseForm(notifyNavigation: false, publish: false);
    }

    internal void OnFileImporterDismissed()
    {
        _fileImporterPresented = false;
        FileImporterChanged?.Invoke(false);
    }

    internal void OnFileImported(SwiftImportedFile file)
    {
        OnFileImporterDismissed();
        if (!_disposed && _formOpen)
            Run(() => ImportVocabularyFileAsync(file.Data, file.Name, _delimiter));
    }

    internal void OnFileImportError(string code)
    {
        OnFileImporterDismissed();
        if (_disposed || !_formOpen
            || _presenter.Detail.Phase == NativeResourceDetailPhase.Blocked)
            return;
        _fileStatus = _text(code == "tooLarge"
            ? "NativeResources_FileTooLarge" : "NativeResources_FileReadError");
        Publish();
    }

    /// <summary>
    /// Reads the list in place (rows already shown stay until the new ones arrive). The
    /// version is captured before the read, so a read that started before a change is never
    /// fresh; a read that fails or throws leaves the list stale.
    /// </summary>
    private async Task LoadListAsync()
    {
        if (_disposed || _loadingList)
            return;
        _loadingList = true;
        var version = _presenter.ListSourceVersion;
        _loadingVersion = version;
        _readVersion = null;
        try
        {
            var load = _presenter.LoadListAsync(_lifetime.Token, refreshInPlace: true);
            Publish(formChanged: false);
            if (await load is {
                    Phase: NativeResourceListPhase.Populated or NativeResourceListPhase.Empty
                        or NativeResourceListPhase.NoMatches,
                    ErrorKey: null })
                _readVersion = version;
            Publish(formChanged: false);
        }
        finally
        {
            _loadingList = false;
            _loadingVersion = null;
        }
    }

    /// <summary>
    /// The open form has edits that are not saved: the shared rule (fields, staged words), and
    /// text this form holds itself until Save (the pasted word list, the quick-add query).
    /// </summary>
    private bool FormHasUnsavedChanges => _presenter.HasUnsavedEdits
        || !string.IsNullOrWhiteSpace(_pasteText) || !string.IsNullOrWhiteSpace(_quickQuery);

    /// <summary>True while one resource closes so another opens (not a return to the list).</summary>
    internal bool IsSwitchingForm { get; private set; }

    private async Task OpenAsync(string id)
    {
        if (_disposed || IsNavigationBlocked)
            return;
        // Beside the list (split view) another resource can be chosen while one is open: the
        // open one closes first, unless it has unsaved edits (nothing is lost; it says so).
        if (_formOpen)
        {
            if (!_creating && _selectedId == id)
                return;
            if (FormHasUnsavedChanges)
            {
                // An error, as Vocabulary's: the tap did nothing, and this says why.
                _formErrorKey = "ResourceEdit_SaveBeforeOpeningAnother";
                Publish();
                return;
            }
            IsSwitchingForm = true;
            try
            {
                CloseForm(publish: false);
            }
            finally
            {
                IsSwitchingForm = false;
            }
            if (_formOpen)
                return;
        }
        ResetForm(creating: false);
        var formGeneration = Interlocked.Read(ref _formGeneration);
        _selectedId = id;
        _formOpen = true;
        FormOpenChanged?.Invoke(true);
        var load = _presenter.OpenAsync(id, _lifetime.Token);
        Publish();
        await load;
        if (IsCurrentForm(formGeneration, id))
        {
            if (_presenter.Detail is {
                Phase: NativeResourceDetailPhase.Ready,
                Resource: { IsSmartResource: false } })
                _presenter.BeginEdit();
            Publish();
        }
    }

    private void ResetForm(bool creating)
    {
        Interlocked.Increment(ref _formGeneration);
        _creating = creating;
        _selectedId = null;
        _formStatus = null;
        _formErrorKey = null;
        _fileStatus = null;
        _quickQuery = string.Empty;
        _quickNativeTerm = string.Empty;
        _addWordsSearch = null;
        _addWordsList = null;
        _stagedGrid = null;
        _vocabularySection = null;
        _formStyle = null;
        _pasteText = string.Empty;
        _delimiter = "comma";
        _bulkExpanded = false;
        _addWordsOpen = false;
        _lastAddedTerm = null;
        _wordWindow = WordPage;
        _chooseFileAfterImportSheet = false;
        _importSheetButton = null;
        _createWordExpanded = false;
    }

    private bool IsCurrentForm(long generation, string? selectedId) =>
        !_disposed && _formOpen
        && Interlocked.Read(ref _formGeneration) == generation
        && _selectedId == selectedId
        && _presenter.Detail.Phase != NativeResourceDetailPhase.Blocked;

    private LearningResourceViewState ProjectFormState()
    {
        var state = _presenter.Detail;
        var resource = state.Resource;
        var draft = state.Draft;
        var words = (_creating
            ? draft?.StagedVocabulary : resource?.Vocabulary).GetValueOrDefault();
        if (words.IsDefault)
            words = ImmutableArray<NativeResourceVocabularyRow>.Empty;
        if (!_creating && draft is not null
            && !draft.StagedVocabulary.IsDefaultOrEmpty)
            words = words.AddRange(draft.StagedVocabulary);
        var suggestions = state.VocabularySearch?.Results
            ?? ImmutableArray<NativeResourceVocabularyRow>.Empty;
        if (suggestions.IsDefault)
            suggestions = [];
        return new LearningResourceViewState
        {
            Phase = state.Phase,
            Title = draft?.Title ?? resource?.Title ?? string.Empty,
            Description = draft?.Description ?? resource?.Description,
            MediaType = draft?.MediaType ?? resource?.MediaType,
            Language = draft?.Language ?? resource?.Language,
            MediaUrl = draft?.MediaUrl ?? resource?.MediaUrl,
            Tags = draft?.Tags ?? resource?.Tags,
            Transcript = draft?.Transcript ?? resource?.Transcript,
            Translation = draft?.Translation ?? resource?.Translation,
            CreatedAt = resource?.CreatedAt ?? default,
            UpdatedAt = resource?.UpdatedAt ?? default,
            IsSmartResource = resource?.IsSmartResource == true
                || state.Phase == NativeResourceDetailPhase.ReadOnly,
            IsSaving = state.Phase == NativeResourceDetailPhase.Saving,
            IsReloading = state.Reloading,
            IsGenerating = _generating,
            IsImporting = _importing,
            IsQuickAddSearching = state.VocabularySearch?.Phase
                == NativeResourceVocabularySearchPhase.Searching,
            QuickAddResultsAvailable = state.VocabularySearch?.Phase
                == NativeResourceVocabularySearchPhase.Ready,
            IsQuickAddAttaching = _quickAdding || state.VocabularyBusy,
            CanQuickAdd = _presenter.CanQuickAdd,
            HasDraft = draft is not null,
            CanSave = state.LastSaveResult
                != ResourceMetadataUpdateResult.ValidationFailed,
            Vocabulary = words.Select(AsFormWord).ToArray(),
            QuickAddResults = suggestions.Select(AsFormWord).ToArray(),
            ErrorKey = _formErrorKey ?? state.ErrorKey
                ?? state.VocabularySearch?.ErrorKey,
            Status = _formStatus,
            FileStatus = _fileStatus,
            Sync = state.Sync,
            SyncErrorKey = state.SyncErrorKey,
            ShowDeleteConfirmation = state.ShowDeleteConfirmation,
        };
    }

    private static LearningResourceWord AsFormWord(NativeResourceVocabularyRow word) =>
        new(word.Id, word.TargetTerm, word.NativeTerm, word.Tags, word.LexicalUnitType);

    private void ChangeDraft(string fieldName, string? value)
    {
        if (!_formOpen || IsNavigationBlocked
            || !Enum.TryParse<NativeResourceField>(
                fieldName, ignoreCase: false, out var field))
            return;
        _presenter.ChangeDraft(field, value);
        if (field == NativeResourceField.Title && !_creating)
            FormTitleChanged?.Invoke(string.IsNullOrEmpty(value)
                ? _text("Resources_Title") : value);
        _formStatus = null;
        _formErrorKey = null;
        _fileStatus = null;
        if (field == NativeResourceField.Language)
        {
            _quickSearch?.Cancel();
            _quickQuery = string.Empty;
            _createWordExpanded = false;
        }
        UpdateSaveButtons();
        if (field is NativeResourceField.MediaType or NativeResourceField.Language)
            Publish();
    }

    private async Task ReloadFormAsync()
    {
        if (!_formOpen || IsNavigationBlocked)
            return;
        if (_creating && _selectedId is null
            && _presenter.Detail is {
                Phase: NativeResourceDetailPhase.LoadError,
                ErrorKey: "NativeResources_SavedReloadError" })
        {
            var committedId = _presenter.Detail.CommittedResourceId;
            _listFeedback = _text("NativeResources_SavedReloadError");
            CloseForm();
            if (committedId is { Length: > 0 })
                await OpenAsync(committedId);
            return;
        }
        if (_presenter.Detail is {
            Phase: NativeResourceDetailPhase.LoadError,
            ErrorKey: "NativeResources_GenerationPartial"
                or "NativeResources_SavedReloadError",
            Resource: { },
            Draft: { } })
        {
            await ReloadDraftAsync();
            return;
        }
        if (_selectedId is not { } id)
            return;
        var formGeneration = Interlocked.Read(ref _formGeneration);
        var load = _presenter.OpenAsync(id, _lifetime.Token);
        Publish();
        await load;
        if (!IsCurrentForm(formGeneration, id))
            return;
        if (_presenter.Detail is {
            Phase: NativeResourceDetailPhase.Ready,
            Resource: { IsSmartResource: false } })
            _presenter.BeginEdit();
        Publish();
    }

    private void CancelEdit()
    {
        if (!_formOpen || IsNavigationBlocked)
            return;
        _presenter.CancelEdit();
        CloseForm();
    }

    private void CloseForm(bool notifyNavigation = true, bool publish = true)
    {
        if (!_formOpen || IsNavigationBlocked)
            return;
        Interlocked.Increment(ref _formGeneration);
        _formOpen = false;
        _selectedId = null;
        _creating = false;
        _addWordsSearch = null;
        _addWordsList = null;
        _stagedGrid = null;
        _vocabularySection = null;
        _formStyle = null;
        _quickSearch?.Cancel();
        _quickSearch?.Dispose();
        _quickSearch = null;
        Interlocked.Increment(ref _quickSearchVersion);
        if (_fileImporterPresented)
            OnFileImporterDismissed();
        _presenter.InvalidateDetail();
        if (notifyNavigation)
            FormOpenChanged?.Invoke(false);
        if (publish)
            Publish(formChanged: false);
    }

    private async Task SaveAsync()
    {
        if (!_formOpen || IsNavigationBlocked
            || _presenter.Detail is not {
                Draft: { },
                Phase: NativeResourceDetailPhase.Ready
                    or NativeResourceDetailPhase.Creating
                    or NativeResourceDetailPhase.SaveError })
            return;
        var formGeneration = Interlocked.Read(ref _formGeneration);
        var selectedId = _selectedId;
        if (_creating && !string.IsNullOrWhiteSpace(_pasteText)
            && !string.IsNullOrWhiteSpace(_presenter.Detail.Draft?.Title))
        {
            var import = _presenter.StageVocabularyFromText(_pasteText, _delimiter);
            if (import.Result == NativeResourceMutationResult.Success && import.Added > 0)
                _pasteText = string.Empty;
            else if (import.Result is not
                (NativeResourceMutationResult.Success
                    or NativeResourceMutationResult.AlreadyPresent))
            {
                _formErrorKey = import.ErrorKey ?? "NativeResources_SaveError";
                Publish();
                return;
            }
        }
        _formStatus = null;
        _formErrorKey = null;
        _fileStatus = null;
        var save = _presenter.SaveAsync(_lifetime.Token);
        Publish();
        var saved = await save;
        if (!IsCurrentForm(formGeneration, selectedId))
            return;
        if (saved)
        {
            if (_creating)
            {
                _presenter.Search(null);
                _presenter.Filter(null, null);
                _listFeedback = _text("ResourceAdd_SavedSuccess");
                CloseForm();
            }
            else
            {
                _presenter.BeginEdit();
                _formStatus = _text("ResourceEdit_SavedSuccess");
                Publish();
            }
        }
        else
            Publish();
    }

    private async Task ReloadDraftAsync()
    {
        if (!_formOpen || IsNavigationBlocked)
            return;
        var formGeneration = Interlocked.Read(ref _formGeneration);
        var selectedId = _selectedId;
        var reload = _presenter.ReloadDraftAsync(_lifetime.Token);
        Publish();
        var reloaded = await reload;
        if (!IsCurrentForm(formGeneration, selectedId))
            return;
        if (reloaded)
        {
            _formStatus = null;
            _formErrorKey = null;
        }
        Publish();
    }

    private Task SynchronizeAsync() => _disposed
        ? Task.CompletedTask
        : _syncAction.RunAsync(SynchronizeAndRefreshAsync);

    private async Task SynchronizeAndRefreshAsync()
    {
        var formGeneration = Interlocked.Read(ref _formGeneration);
        var selectedId = _selectedId;
        var synchronization = _presenter.SynchronizeAsync(_lifetime.Token);
        Publish();
        await synchronization;
        if (!await _presenter.RefreshAfterSyncAsync(synchronization, _lifetime.Token))
            return;
        if (selectedId is { } id && IsCurrentForm(formGeneration, id)
            && _presenter.Detail.Draft is null)
        {
            if (!_presenter.IsLatestSynchronization(synchronization))
                return;
            await _presenter.OpenAsync(id, _lifetime.Token);
        }
        if (_presenter.IsLatestSynchronization(synchronization))
            Publish();
    }

    private async Task CreateStarterAsync()
    {
        if (_disposed || _presenter.List.StarterBusy)
            return;
        var create = _presenter.CreateStarterAsync(_lifetime.Token);
        Publish();
        var result = await create;
        if (_disposed || _presenter.List.Phase == NativeResourceListPhase.Blocked)
            return;
        if (result.Result == NativeResourceMutationResult.Success)
        {
            _presenter.Search(null);
            var list = _presenter.Filter(null, null);
            var language = list.Items.FirstOrDefault(item =>
                item.Id == result.ResourceId)?.Language;
            _listFeedback = string.IsNullOrWhiteSpace(language)
                ? _text("NativeResources_SavedReloadError")
                : string.Format(CultureInfo.CurrentCulture,
                    _text("Resources_StarterSuccess"), language);
        }
        else if (result.Result == NativeResourceMutationResult.AlreadyPresent)
            _listFeedback = _text("Resources_StarterExists");
        else
        {
            var key = result.ErrorKey ?? "Resources_StarterFailed";
            _listFeedback = key == "Resources_StarterFailed"
                ? string.Format(CultureInfo.CurrentCulture,
                    _text(key), _text("NativeResources_SaveError"))
                : _text(key);
        }
        Publish();
    }

    private void RequestDelete()
    {
        if (!_formOpen || _creating || IsNavigationBlocked
            || ProjectFormState().IsSmartResource)
            return;
        if (_presenter.RequestDelete())
            Publish(formChanged: false);
    }

    internal void CancelDelete()
    {
        _presenter.CancelDelete();
        Publish(formChanged: false);
    }

    internal void ConfirmDelete() => Run(DeleteResourceAsync);

    /// <summary>
    /// The system dialog closed (Delete, Cancel or a tap outside it). SwiftUI can report the
    /// close before Delete's action runs, so the request is dropped on the next turn of the
    /// main loop, once Delete has read it (as the list's dialog does).
    /// </summary>
    internal void DeleteDialogClosed()
    {
        if (!_presenter.Detail.ShowDeleteConfirmation)
            return;
        CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (_disposed || !_presenter.Detail.ShowDeleteConfirmation
                || _presenter.Detail.Phase == NativeResourceDetailPhase.Deleting)
                return;
            CancelDelete();
        });
    }

    /// <summary>A list row's Delete: asks first (the list's confirmation dialog).</summary>
    private void RequestListDelete(string id)
    {
        if (_disposed || IsNavigationBlocked || _pendingListDelete is not null)
            return;
        _pendingListDelete = id;
        Publish(formChanged: false);
    }

    /// <summary>
    /// The dialog closed (Cancel, a tap outside it, or after Delete). SwiftUI can report the
    /// close before the Delete action runs, so the pending delete is dropped on the next turn
    /// of the main loop: Delete has read it by then.
    /// </summary>
    private void CancelListDelete()
    {
        if (_pendingListDelete is not { } id)
            return;
        CoreFoundation.DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (_disposed || _pendingListDelete != id)
                return;
            _pendingListDelete = null;
            Publish(formChanged: false);
        });
    }

    /// <summary>
    /// The confirmed list delete: owner-scoped in the presenter. The open form closes when it
    /// showed this resource; the list says what happened.
    /// </summary>
    private async Task ConfirmListDeleteAsync()
    {
        if (_pendingListDelete is not { } id)
            return;
        _pendingListDelete = null;
        var result = await _presenter.DeleteListedAsync(id, _lifetime.Token);
        if (result == NativeResourceMutationResult.Success)
        {
            _listFeedback = _text("ResourceEdit_DeletedSuccess");
            if (_formOpen && _selectedId == id)
                CloseForm(publish: false);
            // The delete committed but the list could not be read again: read it now.
            if (!_presenter.List.Items.IsDefault
                && _presenter.List.Items.Any(item => item.Id == id))
                await LoadListAsync();
        }
        else
            _listFeedback = _text("NativeResources_DeleteFailed");
        Publish(formChanged: false);
    }

    internal void ReloadDraft() => Run(ReloadDraftAsync);

    private async Task DeleteResourceAsync()
    {
        if (!_formOpen || _creating || IsNavigationBlocked
            || !_presenter.Detail.ShowDeleteConfirmation)
            return;
        var formGeneration = Interlocked.Read(ref _formGeneration);
        var selectedId = _selectedId;
        var deletion = _presenter.ConfirmDeleteAsync(_lifetime.Token);
        Publish();
        var result = await deletion;
        if (!IsCurrentForm(formGeneration, selectedId))
            return;
        if (result == NativeResourceMutationResult.Success)
        {
            _presenter.Search(null);
            _presenter.Filter(null, null);
            _listFeedback = _text("ResourceEdit_DeletedSuccess");
            CloseForm();
        }
        else
            Publish();
    }

    private async Task GenerateVocabularyAsync()
    {
        if (!_formOpen || _creating || IsNavigationBlocked)
            return;
        var formGeneration = Interlocked.Read(ref _formGeneration);
        var selectedId = _selectedId;
        _generating = true;
        _formStatus = null;
        _formErrorKey = null;
        Publish();
        try
        {
            var result = await _presenter.GenerateVocabularyAsync(_lifetime.Token);
            if (!IsCurrentForm(formGeneration, selectedId))
                return;
            if (result.Result == NativeResourceMutationResult.Success)
                _formStatus = result.Added > 0
                    ? string.Format(CultureInfo.CurrentCulture,
                        _text("ResourceEdit_GenerateSuccess"),
                        result.GeneratedTotal, result.CreatedCount,
                        result.ExistingLinkedCount)
                    : _text("ResourceAdd_NoDuplicates");
            else
                _formErrorKey = result.ErrorKey ?? "ResourceEdit_GenerateFailed";
        }
        finally
        {
            _generating = false;
            if (IsCurrentForm(formGeneration, selectedId))
                Publish();
        }
    }

    private async Task SearchVocabularyAsync(string query)
    {
        if (!_formOpen || _creating || !_presenter.CanQuickAdd)
            return;
        var formGeneration = Interlocked.Read(ref _formGeneration);
        var selectedId = _selectedId;
        _quickSearch?.Cancel();
        _quickSearch?.Dispose();
        var generation = Interlocked.Increment(ref _quickSearchVersion);
        using var search = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _quickSearch = search;
        try
        {
            var result = _presenter.SearchVocabularyAsync(query, search.Token);
            Interlocked.Increment(ref _quickSuggestionsRevision);
            Publish(formChanged: false);
            await result;
            if (IsCurrentForm(formGeneration, selectedId)
                && generation == Volatile.Read(ref _quickSearchVersion))
            {
                Interlocked.Increment(ref _quickSuggestionsRevision);
                Publish(formChanged: _presenter.Detail.VocabularySearch?.Phase
                    == NativeResourceVocabularySearchPhase.Error);
            }
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_quickSearch, search))
                _quickSearch = null;
        }
    }

    private Task AttachVocabularyAsync(string id) =>
        MutateVocabularyAsync(
            token => _presenter.AttachExistingVocabularyAsync(id, token),
            "VocabWordEdit_WordAdded", clearQuery: true,
            addedTerm: ProjectFormState().QuickAddResults
                .FirstOrDefault(word => word.Id == id)?.TargetTerm);

    private Task CreateVocabularyAsync(string target, string native) =>
        MutateVocabularyAsync(
            token => _presenter.CreateNewVocabularyAsync(target, native, token),
            "VocabWordEdit_WordAdded", clearQuery: true, addedTerm: target);

    private void RemoveVocabulary(string id)
    {
        if (!_formOpen)
            return;
        if (IsNavigationBlocked)
        {
            // A refused swipe: the swiped row may already look gone, so redraw what is true.
            Publish();
            return;
        }
        if (_presenter.RemoveStagedVocabulary(id))
        {
            // The staged words' Section drops the row in place and its header takes the new
            // count (the last word's removal rebuilds the form without the Section).
            if (_creating && _stagedGrid is { } section
                && _presenter.Detail.Draft is { } draft
                && !draft.StagedVocabulary.IsDefaultOrEmpty)
            {
                for (var index = 0; index < section.Children.Count; index++)
                {
                    if (section.Children[index].AccessibilityIdentifier
                        != $"native.resources.form.vocabulary.word.{id}")
                        continue;
                    var detached = section.Children[index];
                    section.RemoveAt(index);
                    detached.Dispose();
                    section.Text = LearningResourceVocabularyView.StagedHeader(
                        draft.StagedVocabulary.Length, _text);
                    Publish(formChanged: false);
                    return;
                }
            }
            Publish();
        }
        else if (!_creating)
        {
            // In place only while no status or error shows: those rows sit at the top of the
            // form, and changing them needs the full redraw (the removal confirmation too).
            var inPlace = _formStatus is null && _formErrorKey is null;
            Run(() => MutateVocabularyAsync(
                token => _presenter.RemoveVocabularyAsync(id, token),
                "ResourceEdit_RemoveWord", clearQuery: false,
                applyInPlace: inPlace ? () => TryRemoveWordRow(id) : null));
        }
    }

    /// <summary>
    /// A removed word leaves its Section in place, so the form keeps its scroll position (a
    /// rebuilt Form returns to the top); the header takes the new count.
    /// </summary>
    private bool TryRemoveWordRow(string id)
    {
        if (_vocabularySection is not { } section || _formStyle is not { } style)
            return false;
        for (var index = 0; index < section.Children.Count; index++)
        {
            if (section.Children[index].AccessibilityIdentifier
                != $"native.resources.form.vocabulary.word.{id}")
                continue;
            var row = section.Children[index];
            section.RemoveAt(index);
            row.Dispose();
            _knownWordIds.Remove(id);
            // The window counts the rows shown, so Load More starts after the last one.
            _wordWindow = Math.Max(0, _wordWindow - 1);
            var count = ProjectFormState().Vocabulary.Count;
            if (count == 0)
                section.Add(LearningResourceVocabularyView.EmptyRow(style, _text));
            section.Text = LearningResourceVocabularyView.VocabularyHeader(count, _text);
            return true;
        }
        return false;
    }

    private async Task MutateVocabularyAsync(
        Func<CancellationToken, Task<NativeResourceMutationResult>> mutate,
        string successKey, bool clearQuery, Func<bool>? applyInPlace = null,
        string? addedTerm = null)
    {
        if (!_formOpen || _creating || IsNavigationBlocked)
            return;
        var formGeneration = Interlocked.Read(ref _formGeneration);
        var selectedId = _selectedId;
        // From the add sheet, the form under it takes the new word in place (a rebuilt Form
        // returns to the top); the sheet confirms the word instead of a status row.
        var fromSheet = clearQuery && _addWordsOpen;
        var appliedInPlace = false;
        _quickAdding = true;
        _formErrorKey = null;
        // An in-place change (a swiped-away word, a word added from the sheet) does not redraw
        // the form first.
        if (applyInPlace is null && !fromSheet)
            Publish();
        else if (fromSheet)
        {
            // The sheet's rows show the add is running (they disable until it ends).
            Interlocked.Increment(ref _quickSuggestionsRevision);
            Publish(formChanged: false);
        }
        try
        {
            var result = await mutate(_lifetime.Token);
            if (!IsCurrentForm(formGeneration, selectedId))
                return;
            if (result == NativeResourceMutationResult.Success
                && applyInPlace is not null && applyInPlace())
                appliedInPlace = true;
            else if (result == NativeResourceMutationResult.Success)
            {
                if (clearQuery
                    && _presenter.Detail.Phase == NativeResourceDetailPhase.Ready)
                {
                    _quickQuery = string.Empty;
                    _quickNativeTerm = string.Empty;
                    _createWordExpanded = false;
                    _lastAddedTerm = addedTerm;
                    if (_addWordsSearch is { } search)
                        search.Text = string.Empty;
                    await _presenter.SearchVocabularyAsync(
                        string.Empty, _lifetime.Token);
                    Interlocked.Increment(ref _quickSuggestionsRevision);
                    appliedInPlace = fromSheet && TryAppendNewWordRows();
                }
                if (!appliedInPlace)
                {
                    _formStatus = _presenter.Detail.Draft is { } draft
                        && !draft.StagedVocabulary.IsDefaultOrEmpty
                        ? null : _text(successKey);
                    _formErrorKey = null;
                }
            }
            else if (_presenter.Detail.ErrorKey is null)
                _formErrorKey = "NativeResources_SaveError";
        }
        finally
        {
            _quickAdding = false;
            if (fromSheet)
                Interlocked.Increment(ref _quickSuggestionsRevision);
            if (IsCurrentForm(formGeneration, selectedId))
                Publish(formChanged: !appliedInPlace);
        }
    }

    /// <summary>
    /// Adds the rows of words the vocabulary Section does not show yet (a word attached or
    /// created from the add sheet, saved or staged while the language is unsaved), in place;
    /// the header takes the new count. False (the caller redraws) when a shown word has gone.
    /// </summary>
    private bool TryAppendNewWordRows()
    {
        if (_vocabularySection is not { } section || _formStyle is not { } style)
            return false;
        var vocabulary = ProjectFormState().Vocabulary;
        var ids = vocabulary.Select(word => word.Id).ToHashSet(StringComparer.Ordinal);
        // A word the form knew has gone: redraw rather than guess.
        if (!_knownWordIds.IsSubsetOf(ids))
            return false;
        var added = vocabulary.Where(word => !_knownWordIds.Contains(word.Id)).ToList();
        foreach (var word in added)
            _knownWordIds.Add(word.Id);
        if (added.Count > 0)
            for (var index = 0; index < section.Children.Count; index++)
            {
                if (section.Children[index].AccessibilityIdentifier
                    != "native.resources.form.vocabulary.empty")
                    continue;
                var empty = section.Children[index];
                section.RemoveAt(index);
                empty.Dispose();
                break;
            }
        // The words are the Section's last rows, in the list's order: a new word goes in its
        // place among the shown rows, or waits for Load More when it sorts past them.
        var firstWord = 0;
        while (firstWord < section.Children.Count
            && !section.Children[firstWord].AccessibilityIdentifier.StartsWith(
                "native.resources.form.vocabulary.word.", StringComparison.Ordinal)
            && section.Children[firstWord].AccessibilityIdentifier
                != LearningResourceVocabularyView.LoadMoreId)
            firstWord++;
        var hasMore = section.Children.Count > 0 && section.Children[^1].AccessibilityIdentifier
            == LearningResourceVocabularyView.LoadMoreId;
        foreach (var word in added)
        {
            var order = IndexOfWord(vocabulary, word.Id);
            if (hasMore && order >= _wordWindow)
                continue;
            section.Insert(firstWord + order, LearningResourceVocabularyView.EditVocabularyRow(
                word, false, false, RemoveVocabulary, style, _text));
            _wordWindow++;
        }
        section.Text = LearningResourceVocabularyView.VocabularyHeader(vocabulary.Count, _text);
        return true;
    }

    private static int IndexOfWord(IReadOnlyList<LearningResourceWord> words, string id)
    {
        for (var index = 0; index < words.Count; index++)
            if (words[index].Id == id)
                return index;
        return words.Count;
    }

    /// <summary>Load More: the next page of word rows, added in place before the button.</summary>
    private void ShowMoreWords()
    {
        // Not while a save, import or change runs: its rows would draw enabled.
        if (!_formOpen || IsNavigationBlocked
            || _vocabularySection is not { } section || _formStyle is not { } style)
            return;
        var state = ProjectFormState();
        var next = state.Vocabulary.Skip(_wordWindow).Take(WordPage).ToList();
        var last = section.Children.Count - 1;
        if (last < 0 || section.Children[last].AccessibilityIdentifier
                != LearningResourceVocabularyView.LoadMoreId)
            return;
        var insertAt = last;
        foreach (var word in next)
            section.Insert(insertAt++, LearningResourceVocabularyView.EditVocabularyRow(word,
                state.IsSmartResource, false, RemoveVocabulary, style, _text));
        _wordWindow += next.Count;
        if (_wordWindow >= state.Vocabulary.Count)
        {
            var more = section.Children[^1];
            section.RemoveAt(section.Children.Count - 1);
            more.Dispose();
        }
    }

    private async Task ImportVocabularyTextAsync(string raw, string delimiter)
    {
        if (!_formOpen || IsNavigationBlocked || string.IsNullOrWhiteSpace(raw))
            return;
        if (_creating)
        {
            ShowImportResult(_presenter.StageVocabularyFromText(raw, delimiter));
            return;
        }
        var formGeneration = Interlocked.Read(ref _formGeneration);
        var selectedId = _selectedId;
        _importing = true;
        Publish();
        try
        {
            var result = await _presenter.ImportVocabularyFromTextAsync(
                raw, delimiter, _lifetime.Token);
            if (IsCurrentForm(formGeneration, selectedId))
                ShowImportResult(result);
        }
        finally
        {
            _importing = false;
            if (IsCurrentForm(formGeneration, selectedId))
                Publish();
        }
    }

    private async Task ImportVocabularyFileAsync(
        byte[] content, string fileName, string delimiter)
    {
        if (!_formOpen || IsNavigationBlocked)
            return;
        var formGeneration = Interlocked.Read(ref _formGeneration);
        var selectedId = _selectedId;
        _importing = true;
        _fileStatus = null;
        Publish();
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            var result = _creating
                ? await _presenter.StageVocabularyFromFileAsync(
                    stream, content.Length, fileName, delimiter, _lifetime.Token)
                : await _presenter.ImportVocabularyFromFileAsync(
                    stream, content.Length, fileName, delimiter, _lifetime.Token);
            if (IsCurrentForm(formGeneration, selectedId))
                ShowImportResult(result, fileName);
        }
        finally
        {
            _importing = false;
            if (IsCurrentForm(formGeneration, selectedId))
                Publish();
        }
    }

    private void ShowImportResult(
        NativeResourceImportResult result, string? fileName = null)
    {
        if (result.Result is NativeResourceMutationResult.Success
            or NativeResourceMutationResult.AlreadyPresent)
        {
            _formErrorKey = null;
            if (result.Added > 0)
            {
                _formStatus = string.Format(CultureInfo.CurrentCulture,
                    _text(_creating
                        ? "ResourceAdd_ImportedCount" : "ResourceEdit_ImportedCount"),
                    result.Added);
                _pasteText = string.Empty;
                if (fileName is not null)
                    _fileStatus = string.Format(CultureInfo.CurrentCulture,
                        _text(_creating
                            ? "ResourceAdd_FileImportStatus"
                            : "ResourceEdit_FileImportStatus"),
                        result.Added, fileName);
            }
            else
                _formStatus = _text(fileName is not null
                    ? _creating ? "ResourceAdd_NoDuplicatesInFile"
                        : "ResourceEdit_NoDuplicatesInFile"
                    : "ResourceAdd_NoDuplicates");
        }
        else
        {
            _formStatus = null;
            var key = result.ErrorKey ?? "NativeResources_SaveError";
            if (key is "ResourceAdd_FileImportFailed"
                or "ResourceEdit_FileImportFailed")
            {
                _formErrorKey = null;
                _fileStatus = string.Format(CultureInfo.CurrentCulture,
                    _text(key), _text("Common_Error"));
            }
            else
                _formErrorKey = key;
        }
        Publish();
    }

    private void Run(Func<Task> action)
    {
        if (_disposed)
            return;
        _ = RunSafelyAsync(action, Interlocked.Read(ref _formGeneration));
    }

    private async Task RunSafelyAsync(Func<Task> action, long formGeneration)
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
            _logger.LogError(ex, "Native iOS Resources action failed.");
            if (_disposed || formGeneration != Interlocked.Read(ref _formGeneration)
                || _presenter.Detail.Phase == NativeResourceDetailPhase.Blocked)
                return;
            if (_formOpen)
                _formErrorKey = "NativeResources_SaveError";
            else
                _listFeedback = _text("NativeResources_LoadError");
            Publish();
        }
    }

    private void UpdateSaveButtons()
    {
        var state = _presenter.Detail;
        var available = _formOpen && !IsNavigationBlocked
            && state.LastSaveResult != ResourceMetadataUpdateResult.ValidationFailed
            && state.Phase is (NativeResourceDetailPhase.Ready
                or NativeResourceDetailPhase.Creating
                or NativeResourceDetailPhase.SaveError);
        // The toolbar's plain Save (Captain 2026-11-10: no filled Save on any editor).
        foreach (var button in _saveButtons)
            button.IsEnabled = available;
    }

    private void UpdateImportButtons()
    {
        var available = _formOpen && !IsNavigationBlocked
            && !string.IsNullOrWhiteSpace(_pasteText);
        foreach (var button in _importButtons)
            button.IsEnabled = available;
        if (_importSheetButton is { } sheetImport)
            sheetImport.IsEnabled = available;
    }

    private void Publish(bool formChanged = true)
    {
        if (_disposed)
            return;
        if (formChanged)
            Interlocked.Increment(ref _formProjectionVersion);
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
        _presenter.InvalidateSync();
        _presenter.InvalidateDetail();
        _lifetime.Cancel();
        _quickSearch?.Cancel();
        _quickSearch?.Dispose();
        _stagedGrid = null;
        _vocabularySection = null;
        _formStyle = null;
        Interlocked.Increment(ref _quickSearchVersion);
        _lifetime.Dispose();
    }
}
