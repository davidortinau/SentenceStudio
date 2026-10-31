using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Android;

internal sealed record AndroidResourceWord(
    string Id, string TargetTerm, string? NativeTerm, string? Tags,
    LexicalUnitType LexicalUnitType);

/// <summary>Projection of the shared detail state the Compose form renders.</summary>
internal sealed record AndroidResourceFormState
{
    internal NativeResourceDetailPhase Phase { get; init; } = NativeResourceDetailPhase.Loading;
    internal string Title { get; init; } = string.Empty;
    internal string? Description { get; init; }
    internal string? MediaType { get; init; }
    internal string? Language { get; init; }
    internal string? MediaUrl { get; init; }
    internal string? Tags { get; init; }
    internal string? Transcript { get; init; }
    internal string? Translation { get; init; }
    internal DateTime CreatedAt { get; init; }
    internal DateTime UpdatedAt { get; init; }
    internal bool IsSmartResource { get; init; }
    internal bool IsSaving { get; init; }
    internal bool IsReloading { get; init; }
    internal bool IsGenerating { get; init; }
    internal bool IsImporting { get; init; }
    internal bool IsQuickAddSearching { get; init; }
    internal bool IsQuickAddAttaching { get; init; }
    internal bool QuickAddResultsAvailable { get; init; }
    internal bool CanQuickAdd { get; init; }
    internal bool HasDraft { get; init; }
    internal bool CanSave { get; init; } = true;
    internal IReadOnlyList<AndroidResourceWord> Vocabulary { get; init; } = [];
    internal IReadOnlyList<AndroidResourceWord> QuickAddResults { get; init; } = [];
    internal string? ErrorKey { get; init; }
    internal string? Status { get; init; }
    internal NativeResourceSyncPhase Sync { get; init; }
    internal string? SyncErrorKey { get; init; }
    internal bool ShowDeleteConfirmation { get; init; }

    internal bool IsBusy => IsSaving || IsReloading || IsGenerating || IsImporting
        || IsQuickAddAttaching
        || Phase is NativeResourceDetailPhase.Saving or NativeResourceDetailPhase.Deleting;

    internal bool IsUnavailable => Phase is NativeResourceDetailPhase.Idle
        or NativeResourceDetailPhase.Loading or NativeResourceDetailPhase.NotFound
        or NativeResourceDetailPhase.LoadError or NativeResourceDetailPhase.Blocked;

    internal bool IsEditable => !IsSmartResource
        && Phase is NativeResourceDetailPhase.Ready
            or NativeResourceDetailPhase.Creating
            or NativeResourceDetailPhase.SaveError;
}

/// <summary>
/// Android orchestration of the shared <see cref="NativeResourcePresenter"/>.
/// It mirrors the iOS Resources coordinator's user-visible outcomes (feedback,
/// close-after-create, stay-after-edit, delete confirmation, quick add, bulk
/// import and generation) but holds no Android UI types so Compose rendering
/// stays a pure projection and the workflow is testable on the host.
/// </summary>
internal sealed class AndroidResourcesCoordinator : IDisposable
{
    internal const string Comma = "comma";
    internal const string Tab = "tab";

    private readonly NativeResourcePresenter _presenter;
    private readonly ILogger _logger;
    private readonly Func<string, string> _text;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _quickSearch;
    private long _quickSearchVersion;
    private long _formGeneration;
    private string? _selectedId;
    private string? _formStatus;
    private string? _formErrorKey;
    private bool _loadingList;
    private bool _generating;
    private bool _importing;
    private bool _quickAdding;
    private bool _disposed;

    internal AndroidResourcesCoordinator(
        NativeResourcePresenter presenter, ILogger logger, Func<string, string> text)
    {
        _presenter = presenter;
        _logger = logger;
        _text = text;
    }

    internal event Action? Changed;
    internal event Action<bool>? FormOpenChanged;

    internal bool IsFormOpen { get; private set; }
    internal bool IsCreating { get; private set; }
    internal string? ListFeedback { get; private set; }
    internal string QuickQuery { get; private set; } = string.Empty;
    internal string QuickNativeTerm { get; private set; } = string.Empty;
    internal string PasteText { get; private set; } = string.Empty;
    internal string Delimiter { get; private set; } = Comma;
    internal bool BulkExpanded { get; private set; }
    internal bool CreateWordExpanded { get; private set; }
    internal NativeResourceListState ListState => _presenter.List;
    internal bool IsStarterBusy => _presenter.List.StarterBusy;
    internal bool CanQuickAdd => _presenter.CanQuickAdd;

    internal bool IsNavigationBlocked
    {
        get
        {
            var detail = _presenter.Detail;
            return detail.Phase is NativeResourceDetailPhase.Saving
                    or NativeResourceDetailPhase.Deleting
                || detail.Reloading || detail.VocabularyBusy
                || _presenter.List.StarterBusy
                || _generating || _importing || _quickAdding;
        }
    }

    /// <summary>Suggestions show only while a query is typed and the create row is closed.</summary>
    internal bool ShowQuickSuggestions => IsFormOpen && !IsCreating
        && !CreateWordExpanded && !string.IsNullOrWhiteSpace(QuickQuery)
        && _presenter.Detail.VocabularySearch?.Phase is
            NativeResourceVocabularySearchPhase.Searching
            or NativeResourceVocabularySearchPhase.Ready;

    internal AndroidResourceFormState FormState => ProjectFormState();

    internal void OnBrowserAppeared() => Run(LoadListAsync);

    internal void Search(string? query)
    {
        if (_disposed || string.Equals(_presenter.List.SearchText,
            query ?? string.Empty, StringComparison.Ordinal))
            return;
        _presenter.Search(query);
        Publish();
    }

    internal void FilterType(string? type)
    {
        if (_disposed)
            return;
        _presenter.Filter(type, _presenter.List.Language);
        Publish();
    }

    internal void FilterLanguage(string? language)
    {
        if (_disposed)
            return;
        _presenter.Filter(_presenter.List.MediaType, language);
        Publish();
    }

    internal void SetViewMode(NativeResourceViewMode mode)
    {
        if (_disposed)
            return;
        _presenter.SetViewMode(mode);
        Publish();
    }

    internal void Open(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;
        Run(() => OpenAsync(id));
    }

    internal void OpenCreate()
    {
        if (_disposed || IsFormOpen || IsNavigationBlocked)
            return;
        var state = _presenter.BeginCreate();
        if (state.Phase != NativeResourceDetailPhase.Creating)
        {
            ListFeedback = _text("NativeResources_LoadError");
            Publish();
            return;
        }
        ResetForm(creating: true);
        IsFormOpen = true;
        FormOpenChanged?.Invoke(true);
        Publish();
    }

    /// <summary>Back/Cancel: discards the draft and returns to the browser.</summary>
    internal bool CloseForm()
    {
        if (!IsFormOpen || IsNavigationBlocked)
            return false;
        _presenter.CancelEdit();
        CloseFormCore();
        return true;
    }

    internal void ChangeDraft(NativeResourceField field, string? value)
    {
        if (!IsFormOpen || IsNavigationBlocked)
            return;
        _presenter.ChangeDraft(field, value);
        _formStatus = null;
        _formErrorKey = null;
        if (field == NativeResourceField.Language)
        {
            _quickSearch?.Cancel();
            QuickQuery = string.Empty;
            CreateWordExpanded = false;
        }
        Publish();
    }

    internal void Save() => Run(SaveAsync);
    internal void ReloadDraft() => Run(ReloadDraftAsync);
    internal void RetryForm() => Run(ReloadFormAsync);
    internal void CreateStarter() => Run(CreateStarterAsync);
    internal void Generate() => Run(GenerateVocabularyAsync);
    internal void ReloadList() => Run(LoadListAsync);

    internal void RequestDelete()
    {
        if (!IsFormOpen || IsCreating || IsNavigationBlocked
            || ProjectFormState().IsSmartResource)
            return;
        if (_presenter.RequestDelete())
            Publish();
    }

    internal void CancelDelete()
    {
        _presenter.CancelDelete();
        Publish();
    }

    internal void ConfirmDelete() => Run(DeleteResourceAsync);

    internal void QuickQueryChanged(string query)
    {
        if (!IsFormOpen || IsCreating)
            return;
        QuickQuery = query ?? string.Empty;
        CreateWordExpanded = false;
        Publish();
        Run(() => SearchVocabularyAsync(QuickQuery));
    }

    internal void QuickNativeTermChanged(string value)
    {
        QuickNativeTerm = value ?? string.Empty;
        Publish();
    }

    internal void ExpandCreateWord()
    {
        if (!IsFormOpen || IsCreating || IsNavigationBlocked
            || string.IsNullOrWhiteSpace(QuickQuery))
            return;
        CreateWordExpanded = true;
        Publish();
    }

    /// <summary>IME action on the quick-add field: attach the first match, else open create.</summary>
    internal void SubmitQuickQuery()
    {
        if (!ShowQuickSuggestions || _presenter.Detail.VocabularySearch is not {
            Phase: NativeResourceVocabularySearchPhase.Ready } search)
            return;
        if (search.Results.IsDefaultOrEmpty)
            ExpandCreateWord();
        else
            AttachWord(search.Results[0].Id);
    }

    internal void AttachWord(string id) => Run(() => MutateVocabularyAsync(
        token => _presenter.AttachExistingVocabularyAsync(id, token),
        "VocabWordEdit_WordAdded", clearQuery: true));

    internal void CreateWord()
    {
        var target = QuickQuery.Trim();
        var native = QuickNativeTerm.Trim();
        Run(() => MutateVocabularyAsync(
            token => _presenter.CreateNewVocabularyAsync(target, native, token),
            "VocabWordEdit_WordAdded", clearQuery: true));
    }

    internal void RemoveWord(string id)
    {
        if (!IsFormOpen || IsNavigationBlocked)
            return;
        if (_presenter.RemoveStagedVocabulary(id))
            Publish();
        else if (!IsCreating)
            Run(() => MutateVocabularyAsync(
                token => _presenter.RemoveVocabularyAsync(id, token),
                "ResourceEdit_RemoveWord", clearQuery: false));
    }

    internal void SetWordType(string id, LexicalUnitType kind)
    {
        if (IsFormOpen && !IsNavigationBlocked
            && _presenter.SetStagedLexicalUnitType(id, kind))
            Publish();
    }

    internal void PasteTextChanged(string value)
    {
        PasteText = value ?? string.Empty;
        Publish();
    }

    internal void DelimiterChanged(string value)
    {
        Delimiter = value == Tab ? Tab : Comma;
        Publish();
    }

    internal void ToggleBulk()
    {
        BulkExpanded = !BulkExpanded;
        Publish();
    }

    internal void ImportText() => Run(() => ImportVocabularyTextAsync(PasteText, Delimiter));

    private async Task LoadListAsync()
    {
        if (_loadingList || _disposed)
            return;
        _loadingList = true;
        try
        {
            var load = _presenter.LoadListAsync(_lifetime.Token);
            Publish();
            await load;
            Publish();
        }
        finally
        {
            _loadingList = false;
        }
    }

    private async Task OpenAsync(string id)
    {
        if (_disposed || IsFormOpen || IsNavigationBlocked)
            return;
        ResetForm(creating: false);
        var formGeneration = Interlocked.Read(ref _formGeneration);
        _selectedId = id;
        IsFormOpen = true;
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
        IsCreating = creating;
        _selectedId = null;
        _formStatus = null;
        _formErrorKey = null;
        QuickQuery = string.Empty;
        QuickNativeTerm = string.Empty;
        PasteText = string.Empty;
        Delimiter = Comma;
        BulkExpanded = false;
        CreateWordExpanded = false;
    }

    private bool IsCurrentForm(long generation, string? selectedId) =>
        !_disposed && IsFormOpen
        && Interlocked.Read(ref _formGeneration) == generation
        && _selectedId == selectedId
        && _presenter.Detail.Phase != NativeResourceDetailPhase.Blocked;

    private AndroidResourceFormState ProjectFormState()
    {
        var state = _presenter.Detail;
        var resource = state.Resource;
        var draft = state.Draft;
        var words = (IsCreating
            ? draft?.StagedVocabulary : resource?.Vocabulary).GetValueOrDefault();
        if (words.IsDefault)
            words = ImmutableArray<NativeResourceVocabularyRow>.Empty;
        if (!IsCreating && draft is not null
            && !draft.StagedVocabulary.IsDefaultOrEmpty)
            words = words.AddRange(draft.StagedVocabulary);
        var suggestions = state.VocabularySearch?.Results
            ?? ImmutableArray<NativeResourceVocabularyRow>.Empty;
        if (suggestions.IsDefault)
            suggestions = [];
        return new AndroidResourceFormState
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
            CanSave = state.LastSaveResult != ResourceMetadataUpdateResult.ValidationFailed,
            Vocabulary = words.Select(AsWord).ToArray(),
            QuickAddResults = suggestions.Select(AsWord).ToArray(),
            ErrorKey = _formErrorKey ?? state.ErrorKey ?? state.VocabularySearch?.ErrorKey,
            Status = _formStatus,
            Sync = state.Sync,
            SyncErrorKey = state.SyncErrorKey,
            ShowDeleteConfirmation = state.ShowDeleteConfirmation,
        };
    }

    private static AndroidResourceWord AsWord(NativeResourceVocabularyRow word) =>
        new(word.Id, word.TargetTerm, word.NativeTerm, word.Tags, word.LexicalUnitType);

    private async Task ReloadFormAsync()
    {
        if (!IsFormOpen || IsNavigationBlocked)
            return;
        if (IsCreating && _selectedId is null
            && _presenter.Detail is {
                Phase: NativeResourceDetailPhase.LoadError,
                ErrorKey: "NativeResources_SavedReloadError" })
        {
            var committedId = _presenter.Detail.CommittedResourceId;
            ListFeedback = _text("NativeResources_SavedReloadError");
            CloseFormCore();
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

    private void CloseFormCore()
    {
        if (!IsFormOpen)
            return;
        Interlocked.Increment(ref _formGeneration);
        IsFormOpen = false;
        _selectedId = null;
        IsCreating = false;
        CreateWordExpanded = false;
        _quickSearch?.Cancel();
        _quickSearch?.Dispose();
        _quickSearch = null;
        Interlocked.Increment(ref _quickSearchVersion);
        _presenter.InvalidateDetail();
        FormOpenChanged?.Invoke(false);
        Publish();
    }

    private async Task SaveAsync()
    {
        if (!IsFormOpen || IsNavigationBlocked
            || _presenter.Detail is not {
                Draft: { },
                Phase: NativeResourceDetailPhase.Ready
                    or NativeResourceDetailPhase.Creating
                    or NativeResourceDetailPhase.SaveError })
            return;
        var formGeneration = Interlocked.Read(ref _formGeneration);
        var selectedId = _selectedId;
        // Source ResourceAdd appends leftover pasted vocabulary on Save.
        if (IsCreating && !string.IsNullOrWhiteSpace(PasteText)
            && !string.IsNullOrWhiteSpace(_presenter.Detail.Draft?.Title))
        {
            var import = _presenter.StageVocabularyFromText(PasteText, Delimiter);
            if (import.Result == NativeResourceMutationResult.Success && import.Added > 0)
                PasteText = string.Empty;
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
        var save = _presenter.SaveAsync(_lifetime.Token);
        Publish();
        var saved = await save;
        if (!IsCurrentForm(formGeneration, selectedId))
            return;
        if (saved)
        {
            if (IsCreating)
            {
                _presenter.Search(null);
                _presenter.Filter(null, null);
                ListFeedback = _text("ResourceAdd_SavedSuccess");
                CloseFormCore();
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
        if (!IsFormOpen || IsNavigationBlocked)
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
            var language = list.Items.IsDefault ? null : list.Items.FirstOrDefault(item =>
                item.Id == result.ResourceId)?.Language;
            ListFeedback = string.IsNullOrWhiteSpace(language)
                ? _text("NativeResources_SavedReloadError")
                : string.Format(CultureInfo.CurrentCulture,
                    _text("Resources_StarterSuccess"), language);
        }
        else if (result.Result == NativeResourceMutationResult.AlreadyPresent)
            ListFeedback = _text("Resources_StarterExists");
        else
        {
            var key = result.ErrorKey ?? "Resources_StarterFailed";
            ListFeedback = key == "Resources_StarterFailed"
                ? string.Format(CultureInfo.CurrentCulture,
                    _text(key), _text("NativeResources_SaveError"))
                : _text(key);
        }
        Publish();
    }

    private async Task DeleteResourceAsync()
    {
        if (!IsFormOpen || IsCreating || IsNavigationBlocked
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
            ListFeedback = _text("ResourceEdit_DeletedSuccess");
            CloseFormCore();
        }
        else
            Publish();
    }

    private async Task GenerateVocabularyAsync()
    {
        if (!IsFormOpen || IsCreating || IsNavigationBlocked)
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
        if (!IsFormOpen || IsCreating || !_presenter.CanQuickAdd)
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
            Publish();
            await result;
            if (IsCurrentForm(formGeneration, selectedId)
                && generation == Volatile.Read(ref _quickSearchVersion))
                Publish();
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

    private async Task MutateVocabularyAsync(
        Func<CancellationToken, Task<NativeResourceMutationResult>> mutate,
        string successKey, bool clearQuery)
    {
        if (!IsFormOpen || IsCreating || IsNavigationBlocked)
            return;
        var formGeneration = Interlocked.Read(ref _formGeneration);
        var selectedId = _selectedId;
        _quickAdding = true;
        _formErrorKey = null;
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
                    QuickQuery = string.Empty;
                    QuickNativeTerm = string.Empty;
                    CreateWordExpanded = false;
                    await _presenter.SearchVocabularyAsync(string.Empty, _lifetime.Token);
                }
            }
            else if (_presenter.Detail.ErrorKey is null)
                _formErrorKey = "NativeResources_SaveError";
        }
        finally
        {
            _quickAdding = false;
            if (IsCurrentForm(formGeneration, selectedId))
                Publish();
        }
    }

    private async Task ImportVocabularyTextAsync(string raw, string delimiter)
    {
        if (!IsFormOpen || IsNavigationBlocked || string.IsNullOrWhiteSpace(raw))
            return;
        if (IsCreating)
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

    private void ShowImportResult(NativeResourceImportResult result)
    {
        if (result.Result is NativeResourceMutationResult.Success
            or NativeResourceMutationResult.AlreadyPresent)
        {
            _formErrorKey = null;
            if (result.Added > 0)
            {
                _formStatus = string.Format(CultureInfo.CurrentCulture,
                    _text(IsCreating
                        ? "ResourceAdd_ImportedCount" : "ResourceEdit_ImportedCount"),
                    result.Added);
                PasteText = string.Empty;
            }
            else
                _formStatus = _text("ResourceAdd_NoDuplicates");
        }
        else
        {
            _formStatus = null;
            _formErrorKey = result.ErrorKey ?? "NativeResources_SaveError";
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
            _logger.LogError(ex, "Native Android Resources action failed.");
            if (_disposed || formGeneration != Interlocked.Read(ref _formGeneration)
                || _presenter.Detail.Phase == NativeResourceDetailPhase.Blocked)
                return;
            if (IsFormOpen)
                _formErrorKey = "NativeResources_SaveError";
            else
                ListFeedback = _text("NativeResources_LoadError");
            Publish();
        }
    }

    private void Publish()
    {
        if (!_disposed)
            Changed?.Invoke();
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
        Interlocked.Increment(ref _quickSearchVersion);
        _lifetime.Dispose();
    }
}
