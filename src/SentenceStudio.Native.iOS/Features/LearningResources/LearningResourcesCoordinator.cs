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

internal sealed class LearningResourcesCoordinator : IDisposable
{
    private readonly NativeResourcePresenter _presenter;
    private readonly ILogger<LearningResourcesCoordinator> _logger;
    private readonly Func<string, string> _text;
    private readonly Action? _openSkills;
    private readonly Action? _openVocabulary;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NativeResourceSyncAction _syncAction = new();
    private readonly List<SwiftView> _saveButtons = [];
    private readonly List<SwiftView> _importButtons = [];
    private readonly List<SwiftView> _quickOptionNodes = [];
    private SourceUiAppearance? _saveAppearance;
    private SourceUiAppearance? _quickOptionAppearance;
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
    private SwiftView? _quickSearchField;
    private SwiftView? _stagedGrid;
    private SwiftView? _stagedCount;
    private SwiftView? _vocabularySection;
    private int _quickHighlightedIndex = -1;
    private bool _quickSearchFocused;
    private bool _restoreQuickFocus;
    private bool _quickDropdownVisible;
    private bool _bulkExpanded;
    private bool _createWordExpanded;
    private bool _formOpen;
    private bool _creating;
    private bool _loadingList;
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
        Func<string, string> text, Action? openSkills = null,
        Action? openVocabulary = null)
    {
        _presenter = presenter;
        _logger = logger;
        _text = text;
        _openSkills = openSkills;
        _openVocabulary = openVocabulary;
    }

    internal event Action? Changed;
    internal event Action<bool>? FormOpenChanged;
    internal event Action<string>? FormTitleChanged;
    internal event Action<bool>? FileImporterChanged;
    internal bool IsFormOpen => _formOpen;
    internal bool IsCreatingForm => _creating;
    internal long FormProjectionVersion => Interlocked.Read(ref _formProjectionVersion);
    internal long QuickAddSuggestionsVersion =>
        Interlocked.Read(ref _quickSuggestionsRevision);
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
    internal string QuickAddFieldAutomationId =>
        LearningResourceQuickAddView.QuickAddFieldAutomationId;
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
    internal bool IsQuickAddOpen => HasQuickAddField
        && !IsNavigationBlocked && _quickDropdownVisible
        && _quickSearchFocused && !_createWordExpanded
        && !string.IsNullOrWhiteSpace(_quickQuery)
        && _presenter.Detail.VocabularySearch?.Phase is
            (NativeResourceVocabularySearchPhase.Searching
                or NativeResourceVocabularySearchPhase.Ready);

    internal SwiftView RenderBrowser(SourceUiStyle style)
    {
        var state = _presenter.List;
#if DEBUG
        if (_controlledStarterLoading)
            state = state with { StarterBusy = true };
#endif
        return LearningResourcesView.Page(state, _listFeedback, style, _text,
            new LearningResourcesActions(
                Search: Search,
                FilterType: type =>
                {
                    _presenter.Filter(type, _presenter.List.Language);
                    Publish(formChanged: false);
                },
                FilterLanguage: language =>
                {
                    _presenter.Filter(_presenter.List.MediaType, language);
                    Publish(formChanged: false);
                },
                SetViewMode: mode =>
                {
                    _presenter.SetViewMode(mode);
                    Publish(formChanged: false);
                },
                Open: id => Run(() => OpenAsync(id)),
                Add: OpenCreate,
                CreateStarter: () => Run(CreateStarterAsync),
                Reload: () => Run(LoadListAsync),
                OpenSkills: _openSkills), _openVocabulary);
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
        _stagedCount = null;
        _vocabularySection = null;
        _formStyle = style;
        _saveAppearance = style.Appearance;
        var state = ProjectFormState();
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
                ImportText: () => Run(() =>
                    ImportVocabularyTextAsync(_pasteText, _delimiter)));
        var form = LearningResourceView.Page(style, state, _creating, actions,
            new LearningResourceInputs(
                _quickQuery, _quickNativeTerm, _pasteText, _delimiter,
                _bulkExpanded, _createWordExpanded,
                QuickQueryChanged: query =>
                {
                    _quickQuery = query;
                    _quickDropdownVisible = !string.IsNullOrWhiteSpace(query);
                    if (_quickDropdownVisible)
                        _quickSearchFocused = true;
                    _quickHighlightedIndex = -1;
                    _createWordExpanded = false;
                    Run(() => SearchVocabularyAsync(query));
                },
                QuickNativeTermChanged: value => _quickNativeTerm = value,
                PasteTextChanged: value =>
                {
                    _pasteText = value;
                    UpdateImportButtons();
                },
                DelimiterChanged: value =>
                {
                    _delimiter = value;
                    Publish();
                },
                ToggleBulk: () =>
                {
                    _bulkExpanded = !_bulkExpanded;
                    Publish();
                },
                ToggleCreateWord: () =>
                {
                    _createWordExpanded = !_createWordExpanded;
                    _quickDropdownVisible = !_createWordExpanded;
                    Publish();
                },
                ChooseFile: () =>
                {
                    if (!_formOpen || IsNavigationBlocked)
                        return;
                    _fileStatus = null;
                    _fileImporterPresented = true;
                    FileImporterChanged?.Invoke(true);
                },
                RegisterQuickSearch: view =>
                {
                    _quickSearchField = view;
                    if (_restoreQuickFocus || _quickSearchFocused)
                        view.FocusRequested = true;
                    _restoreQuickFocus = false;
                },
                QuickFocusChanged: QuickAddFocusChanged,
                QuickKeyPressed: HandleQuickAddKey,
                RegisterImport: button => _importButtons.Add(button),
                RegisterSave: button => _saveButtons.Add(button),
                RegisterStagedGrid: table => _stagedGrid = table,
                RegisterStagedCount: count => _stagedCount = count,
                RegisterVocabularySection: section =>
                    _vocabularySection = section), _text);
        var content = LearningResourceView.WithToolbar(form, style, state,
            _creating, IsNavigationBlocked, actions,
            button => _saveButtons.Add(button), _text);
        UpdateSaveButtons();
        UpdateImportButtons();
        return content;
    }

    internal SwiftView RenderQuickAddSuggestions(SourceUiStyle style)
    {
        _quickOptionNodes.Clear();
        _quickOptionAppearance = style.Appearance;
        return LearningResourceQuickAddView.QuickAddSuggestions(ProjectFormState(),
            _quickQuery, _quickHighlightedIndex, style, _text,
            id => Run(() => AttachVocabularyAsync(id)),
            () =>
            {
                if (!IsQuickAddOpen)
                    return;
                _createWordExpanded = true;
                _quickDropdownVisible = false;
                Publish();
            }, option => _quickOptionNodes.Add(option));
    }

    internal void DismissQuickAddSuggestions()
    {
        if (!_formOpen || !_quickDropdownVisible)
            return;
        _quickDropdownVisible = false;
        _quickSearchFocused = false;
        if (HasQuickAddField && _quickSearchField is { } field)
            field.FocusRequested = false;
        Publish(formChanged: false);
    }

    internal void OnBrowserAppeared() => Run(LoadListAsync);

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

    private async Task LoadListAsync()
    {
        if (_loadingList || _disposed)
            return;
        _loadingList = true;
        try
        {
            var load = _presenter.LoadListAsync(_lifetime.Token);
            Publish(formChanged: false);
            await load;
            Publish(formChanged: false);
        }
        finally
        {
            _loadingList = false;
        }
    }

    private async Task OpenAsync(string id)
    {
        if (_disposed || _formOpen || IsNavigationBlocked)
            return;
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
        _quickSearchField = null;
        _stagedGrid = null;
        _stagedCount = null;
        _vocabularySection = null;
        _formStyle = null;
        _quickOptionNodes.Clear();
        _quickOptionAppearance = null;
        _quickSearchFocused = false;
        _restoreQuickFocus = false;
        _quickDropdownVisible = false;
        _quickHighlightedIndex = -1;
        _pasteText = string.Empty;
        _delimiter = "comma";
        _bulkExpanded = false;
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
            _quickDropdownVisible = false;
            _quickHighlightedIndex = -1;
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
        _quickSearchField = null;
        _stagedGrid = null;
        _stagedCount = null;
        _vocabularySection = null;
        _formStyle = null;
        _quickOptionNodes.Clear();
        _quickOptionAppearance = null;
        _quickSearchFocused = false;
        _restoreQuickFocus = false;
        _quickDropdownVisible = false;
        _quickHighlightedIndex = -1;
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
                _quickHighlightedIndex = _presenter.Detail.VocabularySearch is {
                    Phase: NativeResourceVocabularySearchPhase.Ready } ? 0 : -1;
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
            "VocabWordEdit_WordAdded", clearQuery: true);

    private Task CreateVocabularyAsync(string target, string native) =>
        MutateVocabularyAsync(
            token => _presenter.CreateNewVocabularyAsync(target, native, token),
            "VocabWordEdit_WordAdded", clearQuery: true);

    private void RemoveVocabulary(string id)
    {
        if (!_formOpen || IsNavigationBlocked)
            return;
        if (_presenter.RemoveStagedVocabulary(id))
        {
            if (_creating && _stagedGrid is { } grid
                && _stagedCount is not null
                && _presenter.Detail.Draft is { } draft
                && !draft.StagedVocabulary.IsDefaultOrEmpty)
            {
                for (var index = 1; index < grid.Children.Count; index++)
                {
                    if (grid.Children[index].AccessibilityIdentifier
                        != $"native.resources.form.vocabulary.word.{id}")
                        continue;
                    var detached = grid.Children[index];
                    grid.RemoveAt(index);
                    detached.Dispose();
                    _stagedCount.Text = string.Format(CultureInfo.CurrentCulture,
                        _text("ResourceAdd_VocabWordsAdded"),
                        draft.StagedVocabulary.Length);
                    Publish(formChanged: false);
                    return;
                }
            }
            Publish();
        }
        else if (!_creating)
            Run(() => MutateVocabularyAsync(
                token => _presenter.RemoveVocabularyAsync(id, token),
                "ResourceEdit_RemoveWord", clearQuery: false));
    }

    private async Task MutateVocabularyAsync(
        Func<CancellationToken, Task<NativeResourceMutationResult>> mutate,
        string successKey, bool clearQuery)
    {
        if (!_formOpen || _creating || IsNavigationBlocked)
            return;
        var formGeneration = Interlocked.Read(ref _formGeneration);
        var selectedId = _selectedId;
        var stageOnly = _presenter.Detail is {
            Resource: { } resource, Draft: { LanguageDirty: true } languageDraft
        } && !string.Equals(languageDraft.Language, resource.Language,
            StringComparison.Ordinal);
        var staged = _presenter.Detail.Draft?.StagedVocabulary
            ?? ImmutableArray<NativeResourceVocabularyRow>.Empty;
        var stagedCount = staged.IsDefault ? 0 : staged.Length;
        var appendedStagedWord = false;
        _quickAdding = true;
        _formErrorKey = null;
        if (!stageOnly)
            Publish();
        try
        {
            var result = await mutate(_lifetime.Token);
            if (!IsCurrentForm(formGeneration, selectedId))
                return;
            if (result == NativeResourceMutationResult.Success)
            {
                _formStatus = _presenter.Detail.Draft is { } draft
                    && !draft.StagedVocabulary.IsDefaultOrEmpty
                    ? null : _text(successKey);
                _formErrorKey = null;
                if (clearQuery
                    && _presenter.Detail.Phase == NativeResourceDetailPhase.Ready)
                {
                    _quickQuery = string.Empty;
                    _quickNativeTerm = string.Empty;
                    _createWordExpanded = false;
                    _quickDropdownVisible = false;
                    _quickHighlightedIndex = -1;
                    await _presenter.SearchVocabularyAsync(
                        string.Empty, _lifetime.Token);
                    appendedStagedWord = stageOnly
                        && TryAppendStagedQuickAdd(stagedCount);
                    _restoreQuickFocus = !appendedStagedWord;
                }
            }
            else if (_presenter.Detail.ErrorKey is null)
                _formErrorKey = "NativeResources_SaveError";
        }
        finally
        {
            _quickAdding = false;
            if (IsCurrentForm(formGeneration, selectedId))
            {
                Publish(formChanged: !appendedStagedWord);
                if (appendedStagedWord && _quickSearchField is { } field)
                    DispatchQueue.MainQueue.DispatchAsync(() =>
                    {
                        if (IsCurrentForm(formGeneration, selectedId)
                            && ReferenceEquals(_quickSearchField, field))
                            field.FocusRequested = true;
                    });
            }
        }
    }

    private bool TryAppendStagedQuickAdd(int previousCount)
    {
        if (_vocabularySection is not { } section
            || _quickSearchField is not { } field
            || _formStyle is not { } style
            || _presenter.Detail.Draft is not { } draft
            || draft.StagedVocabulary.IsDefault
            || draft.StagedVocabulary.Length != previousCount + 1
            || section.Children.Count == 0
            || section.Children[0].AccessibilityIdentifier
                != "native.resources.form.vocabulary")
            return false;

        var word = AsFormWord(draft.StagedVocabulary[^1]);
        var rows = section.Children.FirstOrDefault(child =>
            child.AccessibilityIdentifier == LearningResourceVocabularyView.EditVocabularyRowsId);
        if (rows is not null && rows.Children.Any(child =>
                child.AccessibilityIdentifier ==
                    $"native.resources.form.vocabulary.word.{word.Id}"))
            return false;
        for (var index = 1; index < section.Children.Count; index++)
        {
            if (section.Children[index].AccessibilityIdentifier
                != "native.resources.form.vocabulary.empty")
                continue;
            var empty = section.Children[index];
            section.RemoveAt(index);
            empty.Dispose();
            break;
        }
        if (rows is null)
        {
            rows = LearningResourceVocabularyView.CreateEditVocabularyRows();
            section.Add(rows);
        }
        LearningResourceVocabularyView.AppendEditVocabularyRow(rows, word, false, false,
            RemoveVocabulary, style, _text);
        section.Children[0].Text = string.Format(CultureInfo.CurrentCulture,
            _text("ResourceEdit_VocabularySection"),
            ProjectFormState().Vocabulary.Count);
        field.Text = string.Empty;
        return true;
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

    private void QuickAddFocusChanged(SwiftView view, bool focused)
    {
        if (!ReferenceEquals(_quickSearchField, view) || !_formOpen)
            return;
        _quickSearchFocused = focused;
        if (focused)
        {
            if (!_quickDropdownVisible && !string.IsNullOrWhiteSpace(_quickQuery)
                && _presenter.Detail.VocabularySearch?.Phase
                    == NativeResourceVocabularySearchPhase.Ready)
            {
                _quickDropdownVisible = true;
                Publish(formChanged: false);
            }
            return;
        }
        Run(async () =>
        {
            await Task.Delay(150, _lifetime.Token);
            if (!_quickSearchFocused && !_quickAdding && !_createWordExpanded
                && _quickDropdownVisible && _formOpen)
            {
                _quickDropdownVisible = false;
                Publish(formChanged: false);
            }
        });
    }

    private bool HandleQuickAddKey(ResourceQuickAddKey key)
    {
        if (!_formOpen || _creating || IsNavigationBlocked)
            return false;
        if (key == ResourceQuickAddKey.Escape)
        {
            if (!_quickDropdownVisible && string.IsNullOrWhiteSpace(_quickQuery))
                return false;
            _quickQuery = string.Empty;
            _quickNativeTerm = string.Empty;
            _quickDropdownVisible = false;
            _quickHighlightedIndex = -1;
            _createWordExpanded = false;
            _quickSearch?.Cancel();
            if (_quickSearchField is { } field)
                field.Text = string.Empty;
            Run(() => SearchVocabularyAsync(string.Empty));
            Publish(formChanged: false);
            return true;
        }
        if (!_quickDropdownVisible || !_quickSearchFocused || _createWordExpanded
            || _presenter.Detail.VocabularySearch is not {
                Phase: NativeResourceVocabularySearchPhase.Ready } search)
            return false;
        var count = search.Results.Length == 0
            ? string.IsNullOrWhiteSpace(_quickQuery) ? 0 : 1
            : search.Results.Length;
        if (key is ResourceQuickAddKey.ArrowUp or ResourceQuickAddKey.ArrowDown)
        {
            if (count == 0)
                return false;
            _quickHighlightedIndex = ResourceQuickAddNavigation.Move(
                _quickHighlightedIndex, count, key);
            if (_quickOptionNodes.Count == count
                && _quickOptionAppearance is { } appearance)
                UpdateQuickOptionSelection(appearance);
            else
                Publish();
            return true;
        }
        if (key == ResourceQuickAddKey.Enter
            && _quickHighlightedIndex >= 0 && _quickHighlightedIndex < count)
        {
            if (search.Results.Length == 0)
            {
                _createWordExpanded = true;
                _quickDropdownVisible = false;
                Publish();
            }
            else
                Run(() => AttachVocabularyAsync(
                    search.Results[_quickHighlightedIndex].Id));
            return true;
        }
        return false;
    }

    private void UpdateQuickOptionSelection(SourceUiAppearance appearance)
    {
        for (var index = 0; index < _quickOptionNodes.Count; index++)
        {
            var option = _quickOptionNodes[index];
            var selected = index == _quickHighlightedIndex;
            if (option.Children.Count == 1)
            {
                var label = option.Children[0];
                var fill = selected ? appearance.Primary : appearance.CardSurface;
                var foreground = selected ? appearance.OnPrimary : appearance.Text;
                label.WithRoundedSurface(fill.Light, fill.Dark,
                    appearance.BootstrapRadius);
                if (label.Children.Count == 0)
                    label.WithForegroundColor(foreground.Light, foreground.Dark);
                else
                    for (var row = 0; row < label.Children.Count; row++)
                    {
                        var color = selected ? appearance.OnPrimary
                            : row == 0 ? appearance.Text : appearance.SecondaryText;
                        label.Children[row].WithForegroundColor(color.Light, color.Dark);
                    }
            }
            option.WithAccessibilitySelected(selected);
        }
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
        foreach (var button in _saveButtons)
            SourceUiStyle.SetCustomButtonAvailability(button,
                _saveAppearance ?? throw new InvalidOperationException(
                    "A registered Save button requires its source appearance."),
                SourceButtonRole.Primary, available);
    }

    private void UpdateImportButtons()
    {
        var available = _formOpen && !IsNavigationBlocked
            && !string.IsNullOrWhiteSpace(_pasteText);
        foreach (var button in _importButtons)
            SourceUiStyle.SetCustomButtonAvailability(button,
                _saveAppearance ?? throw new InvalidOperationException(
                    "A registered Import button requires its source appearance."),
                SourceButtonRole.FilledSecondary, available);
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
        _quickOptionNodes.Clear();
        _quickOptionAppearance = null;
        _stagedGrid = null;
        _stagedCount = null;
        _vocabularySection = null;
        _formStyle = null;
        Interlocked.Increment(ref _quickSearchVersion);
        _lifetime.Dispose();
    }
}
