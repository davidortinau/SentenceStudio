using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Features.Import;

/// <summary>Source ImportStep (ImportContent.razor:641); the source never sets Commit.</summary>
public enum ImportContentStep { Source, Harvest, Preview, Complete }

/// <summary>Source ConfidenceBand: High &gt;= 0.85, Medium &gt;= 0.70, else Low.</summary>
public enum ImportConfidenceBand { High, Medium, Low }

/// <summary>The source harvest checkboxes.</summary>
public enum ImportHarvest { Transcript, Sentences, Phrases, Words }

/// <summary>The source delimiter options (value attributes of the select).</summary>
public static class ImportDelimiters
{
    public const string Auto = "auto";
    public const string Comma = ",";
    public const string Tab = "\\t";
    public const string Pipe = "|";
    public static readonly ImmutableArray<string> All = [Auto, Comma, Tab, Pipe];
}

/// <summary>The auto-detect result shown in the confidence gate.</summary>
public sealed record ImportAutoDetect(
    ContentType ContentType, float Confidence, string Note, ImportConfidenceBand Band);

/// <summary>One editable preview row (the source copies these fields of each parsed row).</summary>
public sealed record ImportPreviewRow(
    int Index, int RowNumber, string Target, string Native, RowStatus Status, string? Error,
    bool Selected, bool AiTranslated, LexicalUnitType Type, bool Duplicate, string? DuplicateReason);

public sealed record ImportContentState(
    bool Open,
    bool Blocked,
    ImportContentStep Step,
    string RawText,
    ContentType ContentType,
    string Delimiter,
    bool HasHeaderRow,
    bool IsParsing,
    ImportAutoDetect? AutoDetect,
    bool AutoDetectConfirmed,
    ContentType ResolvedContentType,
    bool ShowTypeChooser,
    bool HarvestStepReady,
    bool HarvestTranscript,
    bool HarvestSentences,
    bool HarvestPhrases,
    bool HarvestWords,
    bool ShowHarvestValidationError,
    bool HasPreview,
    string DetectedFormat,
    ImmutableArray<string> Warnings,
    ImmutableArray<ImportPreviewRow> Rows,
    bool SelectAllRows,
    int SelectedCount,
    ImportTargetMode TargetMode,
    string NewResourceTitle,
    string NewResourceDescription,
    bool ShowTitleValidationError,
    string TargetLanguage,
    string NativeLanguage,
    string SelectedResourceId,
    ImmutableArray<NativeImportTargetResource> Resources,
    DedupMode DedupMode,
    bool IsCommitting,
    ContentImportResult? Result,
    ImportItemStatus? ResultFilter,
    ImmutableArray<ContentImportItemResult> FilteredItems,
    ImmutableArray<NativeMessage> Messages)
{
    /// <summary>Source: the Import (commit) action shows once a preview is up.</summary>
    public bool CanCommit => Step == ImportContentStep.Preview && HasPreview && Result is null;

    /// <summary>Source: the paste area and format hints lock once the source step is left.</summary>
    public bool SourceLocked => Step > ImportContentStep.Source;
}

/// <summary>
/// The source Import Content page (src/SentenceStudio.UI/Pages/ImportContent.razor) for one
/// verified owner: paste, format hints, the auto-detect confidence gate, the harvest choices,
/// the editable preview, the target resource, duplicate handling, commit, and the results with
/// their filter. Every call goes through <see cref="INativeContentImport"/> (the shared
/// ContentImportService); an absent or changed owner blocks the screen and nothing is read or
/// written. <see cref="Version"/> changes when the page must be drawn again; typing, row
/// checkboxes and the other bound inputs only update the state, as the source bindings do.
/// </summary>
public sealed class ImportContentCoordinator : IDisposable
{
    private readonly INativeContentImport _import;
    private readonly DeviceUserScopeProvider _userScope;
    private readonly ILogger _logger;
    private readonly Func<string, string> _text;
    private readonly CancellationTokenSource _lifetime = new();
    private string? _owner;
    private DeviceUserScopeProvider.ScopeStamp _ownerStamp;
    private bool _open;
    private bool _blocked;
    private bool _disposed;
    private long _operation;

    // Source fields (ImportContent.razor:647-702), with the same defaults.
    private ImportContentStep _step = ImportContentStep.Source;
    private string _rawText = string.Empty;
    private ContentType _contentType = ContentType.Vocabulary;
    private string _delimiter = ImportDelimiters.Auto;
    private bool _hasHeaderRow;
    private ImportAutoDetect? _autoDetect;
    private bool _autoDetectConfirmed;
    private ContentType _resolvedContentType = ContentType.Vocabulary;
    private bool _showTypeChooser;
    private bool _harvestTranscript;
    private bool _harvestSentences;
    private bool _harvestPhrases;
    private bool _harvestWords = true;
    private bool _showHarvestValidationError;
    private bool _harvestStepReady;
    private bool _isParsing;
    private bool _selectAllRows = true;
    private ContentImportPreview? _preview;
    private List<ImportRow> _rows = [];
    private ImportTargetMode _targetMode = ImportTargetMode.New;
    private string _newResourceTitle = string.Empty;
    private string _newResourceDescription = string.Empty;
    private bool _showTitleValidationError;
    private string _targetLanguage = "Korean";
    private string _nativeLanguage = "English";
    private string _selectedResourceId = string.Empty;
    private ImmutableArray<NativeImportTargetResource> _resources = [];
    private DedupMode _dedupMode = DedupMode.Skip;
    private bool _isCommitting;
    private ContentImportResult? _result;
    private ImportItemStatus? _resultFilter;
    private ImmutableArray<NativeMessage> _messages = [];

    public ImportContentCoordinator(INativeContentImport import, DeviceUserScopeProvider userScope,
        ILogger<ImportContentCoordinator> logger, Func<string, string> text)
    {
        _import = import;
        _userScope = userScope;
        _logger = logger;
        _text = text;
    }

    /// <summary>Raised when the page must be drawn again (<see cref="Version"/> changed).</summary>
    public event Action? Changed;

    public long Version { get; private set; }

    public bool IsOpen => _open;

    /// <summary>A commit is running: Back waits for its result (source: the page shows a spinner).</summary>
    public bool IsNavigationBlocked => _isCommitting;

    /// <summary>The source language choices (SentenceStudio.Common.Constants.Languages).</summary>
    public static IReadOnlyList<string> Languages => SentenceStudio.Common.Constants.Languages;

    public ImportContentState State
    {
        get
        {
            var rows = _rows.Select((row, index) => new ImportPreviewRow(index, row.RowNumber,
                    row.TargetLanguageTerm ?? string.Empty, row.NativeLanguageTerm ?? string.Empty,
                    row.Status, row.Error, row.IsSelected, row.IsAiTranslated, row.LexicalUnitType,
                    row.IsDuplicate, row.DuplicateReason))
                .ToImmutableArray();
            return new(_open, _blocked, _step, _rawText, _contentType, _delimiter, _hasHeaderRow,
                _isParsing, _autoDetect, _autoDetectConfirmed, _resolvedContentType, _showTypeChooser,
                _harvestStepReady, _harvestTranscript, _harvestSentences, _harvestPhrases,
                _harvestWords, _showHarvestValidationError, _preview is not null,
                _preview?.DetectedFormat ?? string.Empty,
                _preview?.Warnings.ToImmutableArray() ?? [], rows, _selectAllRows,
                _rows.Count(row => row.IsSelected), _targetMode, _newResourceTitle,
                _newResourceDescription, _showTitleValidationError, _targetLanguage, _nativeLanguage,
                _selectedResourceId, _resources, _dedupMode, _isCommitting, _result, _resultFilter,
                FilteredItems(), _messages);
        }
    }

    /// <summary>Source OnInitializedAsync: the owner's resources for the existing-resource select.</summary>
    public async Task<bool> OpenAsync(string? expectedOwner)
    {
        if (_disposed || _open)
            return false;
        if (string.IsNullOrWhiteSpace(expectedOwner)
            || !_userScope.TryGetUserProfileId(out var active) || active != expectedOwner)
        {
            _logger.LogWarning("Import Content refused an absent or untrusted owner.");
            return false;
        }
        var stamp = _userScope.CaptureStamp();
        if (stamp.PublishedOwner != expectedOwner || !_userScope.IsCurrent(stamp))
        {
            _logger.LogWarning("Import Content refused a changed owner.");
            return false;
        }
        _owner = expectedOwner;
        _ownerStamp = stamp;
        _open = true;
        Notify(redraw: true);
        await LoadAvailableResourcesAsync();
        return TryOwner(out _);
    }

    // ---- Step 1 and 2: source and format hints (bound inputs, no redraw) ----

    public void SetRawText(string value)
    {
        if (!CanEdit() || _step > ImportContentStep.Source)
            return;
        _rawText = value ?? string.Empty;
        Notify(redraw: false);
    }

    public void SetContentType(ContentType value)
    {
        if (!CanEdit() || _step > ImportContentStep.Source || !Enum.IsDefined(value))
            return;
        _contentType = value;
        Notify(redraw: false);
    }

    public void SetDelimiter(string value)
    {
        if (!CanEdit() || _step > ImportContentStep.Source || !ImportDelimiters.All.Contains(value))
            return;
        _delimiter = value;
        Notify(redraw: false);
    }

    public void SetHasHeaderRow(bool value)
    {
        if (!CanEdit() || _step > ImportContentStep.Source)
            return;
        _hasHeaderRow = value;
        Notify(redraw: false);
    }

    /// <summary>Source Preview button: disabled while the text is empty or a parse runs.</summary>
    public bool CanPreview => CanEdit() && !_isParsing && !string.IsNullOrWhiteSpace(_rawText);

    /// <summary>Source ParseContent (ImportContent.razor:742).</summary>
    public async Task PreviewAsync()
    {
        if (!CanPreview || !TryOwner(out var owner))
            return;
        var operation = ++_operation;
        _isParsing = true;
        _showHarvestValidationError = false;
        _messages = [];
        Notify(redraw: true);
        var previewStarted = false;
        try
        {
            if (_contentType == ContentType.Auto)
            {
                var classification = await _import.ClassifyAsync(owner, _rawText, _lifetime.Token);
                if (!IsCurrent(operation))
                    return;
                var band = ClassifyConfidence(classification.Confidence);
                _autoDetect = new(classification.ContentType, classification.Confidence,
                    classification.Reasoning, band);
                _autoDetectConfirmed = false;
                _showTypeChooser = false;
                if (band == ImportConfidenceBand.High)
                {
                    _resolvedContentType = classification.ContentType;
                    _autoDetectConfirmed = true;
                    ApplyHarvestDefaults(_resolvedContentType);
                    _harvestStepReady = true;
                }
                else
                {
                    // Medium: the learner confirms first; Low: the learner picks (placeholder Vocabulary).
                    _resolvedContentType = band == ImportConfidenceBand.Medium
                        ? classification.ContentType : ContentType.Vocabulary;
                    _harvestStepReady = false;
                    _step = ImportContentStep.Harvest;
                    return;
                }
            }
            else
            {
                _resolvedContentType = _contentType;
                _autoDetect = null;
                ApplyHarvestDefaults(_resolvedContentType);
                _harvestStepReady = true;
            }
            previewStarted = true;
            await RunPreviewAsync(owner, operation);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (UnauthorizedAccessException ex)
        {
            OnOwnerRefused(ex, operation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse content");
            if (IsCurrent(operation))
                AddError("Import_ParseError", ex.Message);
        }
        finally
        {
            if (IsCurrent(operation) && !previewStarted)
            {
                _isParsing = false;
                Notify(redraw: true);
            }
        }
    }

    /// <summary>Source RunPreview (ImportContent.razor:808).</summary>
    private async Task RunPreviewAsync(string owner, long operation)
    {
        _isParsing = true;
        Notify(redraw: true);
        try
        {
            var request = new ContentImportRequest
            {
                RawText = _rawText,
                ContentType = _resolvedContentType,
                DelimiterOverride = _delimiter == ImportDelimiters.Auto ? null
                    : _delimiter == ImportDelimiters.Tab ? '\t' : _delimiter[0],
                HasHeaderRow = _hasHeaderRow,
                TargetLanguage = _targetLanguage,
                NativeLanguage = _nativeLanguage,
                HarvestTranscript = _harvestTranscript,
                HarvestSentences = _harvestSentences,
                HarvestPhrases = _harvestPhrases,
                HarvestWords = _harvestWords
            };
            var preview = await _import.PreviewAsync(owner, request, _lifetime.Token);
            if (!IsCurrent(operation))
                return;
            _preview = preview;
            // Source copies these fields of each row (not the source sentence) into its editable rows.
            _rows = preview.Rows.Select(row => new ImportRow
            {
                RowNumber = row.RowNumber,
                TargetLanguageTerm = row.TargetLanguageTerm,
                NativeLanguageTerm = row.NativeLanguageTerm,
                Status = row.Status,
                Error = row.Error,
                IsSelected = row.IsSelected,
                IsAiTranslated = row.IsAiTranslated,
                LexicalUnitType = row.LexicalUnitType,
                IsDuplicate = row.IsDuplicate,
                DuplicateReason = row.DuplicateReason
            }).ToList();
            _step = ImportContentStep.Preview;
            _selectAllRows = _rows.All(row => row.IsSelected);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (UnauthorizedAccessException ex)
        {
            OnOwnerRefused(ex, operation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse content");
            if (IsCurrent(operation))
                AddError("Import_ParseError", ex.Message);
        }
        finally
        {
            if (IsCurrent(operation))
            {
                _isParsing = false;
                Notify(redraw: true);
            }
        }
    }

    /// <summary>Source ApplyHarvestDefaults.</summary>
    private void ApplyHarvestDefaults(ContentType type)
    {
        (_harvestTranscript, _harvestSentences, _harvestPhrases, _harvestWords) = type switch
        {
            ContentType.Vocabulary => (false, false, false, true),
            ContentType.Phrases => (false, false, true, true),
            ContentType.Sentences => (false, true, false, true),
            ContentType.Transcript => (true, false, false, true),
            ContentType.Auto => (false, false, false, false),
            _ => (_harvestTranscript, _harvestSentences, _harvestPhrases, true)
        };
    }

    // ---- Auto-detect gate ----

    /// <summary>Source ConfirmAutoDetect.</summary>
    public Task ConfirmAutoDetectAsync()
    {
        if (!CanEdit() || _isParsing || _autoDetect is null || !TryOwner(out var owner))
            return Task.CompletedTask;
        _autoDetectConfirmed = true;
        ApplyHarvestDefaults(_resolvedContentType);
        _harvestStepReady = true;
        _messages = [];
        return RunPreviewAsync(owner, ++_operation);
    }

    public void OpenTypeChooser()
    {
        if (!CanEdit() || _autoDetect is null)
            return;
        _showTypeChooser = true;
        Notify(redraw: true);
    }

    public void CloseTypeChooser()
    {
        if (!CanEdit())
            return;
        _showTypeChooser = false;
        Notify(redraw: true);
    }

    /// <summary>Source OverrideContentType.</summary>
    public Task OverrideContentTypeAsync(ContentType type)
    {
        if (!CanEdit() || _isParsing || _autoDetect is null
            || type is ContentType.Auto || !Enum.IsDefined(type) || !TryOwner(out var owner))
            return Task.CompletedTask;
        _resolvedContentType = type;
        _autoDetectConfirmed = true;
        _showTypeChooser = false;
        ApplyHarvestDefaults(type);
        _harvestStepReady = true;
        _messages = [];
        return RunPreviewAsync(owner, ++_operation);
    }

    // ---- Harvest (bound checkboxes) ----

    public void SetHarvest(ImportHarvest harvest, bool value)
    {
        if (!CanEdit())
            return;
        switch (harvest)
        {
            case ImportHarvest.Transcript: _harvestTranscript = value; break;
            case ImportHarvest.Sentences: _harvestSentences = value; break;
            case ImportHarvest.Phrases: _harvestPhrases = value; break;
            case ImportHarvest.Words: _harvestWords = value; break;
            default: return;
        }
        Notify(redraw: false);
    }

    // ---- Preview rows ----

    /// <summary>A row's checkbox (error rows cannot be selected). Returns the selected count.</summary>
    public int SetRowSelected(int index, bool selected)
    {
        if (CanEdit() && index >= 0 && index < _rows.Count && _rows[index].Status != RowStatus.Error)
        {
            _rows[index].IsSelected = selected;
            Notify(redraw: false);
        }
        return _rows.Count(row => row.IsSelected);
    }

    public void SetRowTarget(int index, string value)
    {
        if (!CanEdit() || index < 0 || index >= _rows.Count)
            return;
        _rows[index].TargetLanguageTerm = value;
        Notify(redraw: false);
    }

    public void SetRowNative(int index, string value)
    {
        if (!CanEdit() || index < 0 || index >= _rows.Count)
            return;
        _rows[index].NativeLanguageTerm = value;
        Notify(redraw: false);
    }

    /// <summary>Source ToggleSelectAll: every row that is not an error follows the header checkbox.</summary>
    public void SetSelectAll(bool selected)
    {
        if (!CanEdit())
            return;
        _selectAllRows = selected;
        foreach (var row in _rows.Where(row => row.Status != RowStatus.Error))
            row.IsSelected = selected;
        Notify(redraw: true);
    }

    // ---- Target and duplicate handling ----

    public void SetTargetMode(ImportTargetMode mode)
    {
        if (!CanEdit() || !Enum.IsDefined(mode))
            return;
        _targetMode = mode;
        _showTitleValidationError = false;
        Notify(redraw: true);
    }

    /// <summary>The title field; typing clears its error (source ClearTitleError). True when it was shown.</summary>
    public bool SetNewResourceTitle(string value)
    {
        if (!CanEdit())
            return false;
        _newResourceTitle = value ?? string.Empty;
        var cleared = _showTitleValidationError;
        _showTitleValidationError = false;
        Notify(redraw: false);
        return cleared;
    }

    public void SetNewResourceDescription(string value)
    {
        if (!CanEdit())
            return;
        _newResourceDescription = value ?? string.Empty;
        Notify(redraw: false);
    }

    public void SetTargetLanguage(string value)
    {
        if (!CanEdit() || !Languages.Contains(value))
            return;
        _targetLanguage = value;
        Notify(redraw: false);
    }

    public void SetNativeLanguage(string value)
    {
        if (!CanEdit() || !Languages.Contains(value))
            return;
        _nativeLanguage = value;
        Notify(redraw: false);
    }

    /// <summary>The existing-resource select; empty is the placeholder option.</summary>
    public void SetSelectedResource(string value)
    {
        if (!CanEdit())
            return;
        value ??= string.Empty;
        if (value.Length > 0 && !_resources.Any(resource => resource.Id == value))
            return;
        _selectedResourceId = value;
        Notify(redraw: false);
    }

    public void SetDedupMode(DedupMode mode)
    {
        if (!CanEdit() || !Enum.IsDefined(mode))
            return;
        _dedupMode = mode;
        Notify(redraw: true);
    }

    // ---- Commit ----

    /// <summary>Source CommitImport (ImportContent.razor:974).</summary>
    public async Task CommitAsync()
    {
        if (!CanEdit() || _isParsing || _preview is null || _result is not null
            || !TryOwner(out var owner))
            return;
        _messages = [];
        if (!(_harvestTranscript || _harvestSentences || _harvestPhrases || _harvestWords))
        {
            _showHarvestValidationError = true;
            AddError("Import_HarvestValidationError");
            return;
        }
        _showHarvestValidationError = false;
        if (_targetMode == ImportTargetMode.New && string.IsNullOrWhiteSpace(_newResourceTitle))
        {
            _showTitleValidationError = true;
            AddError("Import_ResourceTitleRequired");
            return;
        }
        if (_targetMode == ImportTargetMode.Existing && string.IsNullOrWhiteSpace(_selectedResourceId))
        {
            AddError("Import_SelectResourceRequired");
            return;
        }
        if (!_rows.Any(row => row.IsSelected))
        {
            AddError("Import_NoRowsSelected");
            return;
        }
        if (_dedupMode == DedupMode.Update)
            _messages = _messages.Add(NativeMessage.Warning("Import_UpdateModeWarning"));

        var operation = ++_operation;
        _isCommitting = true;
        Notify(redraw: true);
        try
        {
            var commit = new ContentImportCommit
            {
                Preview = new ContentImportPreview
                {
                    Rows = _rows,
                    DetectedFormat = _preview.DetectedFormat,
                    DetectedContentType = _preview.DetectedContentType,
                    Warnings = _preview.Warnings,
                    SourceText = _preview.SourceText
                },
                Target = _targetMode == ImportTargetMode.New
                    ? new ImportTarget
                    {
                        Mode = ImportTargetMode.New,
                        NewResourceTitle = _newResourceTitle,
                        NewResourceDescription = _newResourceDescription,
                        TargetLanguage = _targetLanguage,
                        NativeLanguage = _nativeLanguage
                    }
                    : new ImportTarget
                    {
                        Mode = ImportTargetMode.Existing,
                        ExistingResourceId = _selectedResourceId
                    },
                DedupMode = _dedupMode,
                HarvestTranscript = _harvestTranscript,
                HarvestSentences = _harvestSentences,
                HarvestPhrases = _harvestPhrases,
                HarvestWords = _harvestWords
            };
            // The commit is one database transaction: it is not cancelled by leaving the page.
            var result = await _import.CommitAsync(owner, commit, CancellationToken.None);
            if (_disposed || operation != _operation)
                return;
            _result = result;
            _resultFilter = null;
            _step = ImportContentStep.Complete;
            _messages = _messages.Add(NativeMessage.Success("Import_ImportSuccess"));
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Import commit refused an absent or changed owner.");
            if (operation == _operation)
            {
                AddErrorSilently("Import_ProfileNotFound");
                Block();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to commit import");
            if (operation == _operation)
                AddErrorSilently("Import_CommitError", ex.Message);
        }
        finally
        {
            if (operation == _operation)
            {
                _isCommitting = false;
                Notify(redraw: true);
            }
        }
    }

    // ---- Results ----

    public void SetResultFilter(ImportItemStatus? status)
    {
        if (!_open || _result is null || status is { } value && !Enum.IsDefined(value))
            return;
        _resultFilter = status;
        Notify(redraw: true);
    }

    /// <summary>Source NavigateToVocabDetail: only a row with a word that did not fail opens.</summary>
    public static bool CanOpenWord(ContentImportItemResult item) =>
        item.Status != ImportItemStatus.Failed && !string.IsNullOrEmpty(item.VocabularyWordId);

    /// <summary>Source StartNewImport: every field back to its default; the resource list stays loaded.</summary>
    public void StartNewImport()
    {
        if (!_open || _isCommitting)
            return;
        ++_operation;
        _step = ImportContentStep.Source;
        _rawText = string.Empty;
        _contentType = ContentType.Vocabulary;
        _delimiter = ImportDelimiters.Auto;
        _hasHeaderRow = false;
        _preview = null;
        _rows = [];
        _selectAllRows = true;
        _targetMode = ImportTargetMode.New;
        _newResourceTitle = string.Empty;
        _newResourceDescription = string.Empty;
        _selectedResourceId = string.Empty;
        _dedupMode = DedupMode.Skip;
        _result = null;
        _resultFilter = null;
        _autoDetect = null;
        _autoDetectConfirmed = false;
        _showTypeChooser = false;
        _harvestTranscript = false;
        _harvestSentences = false;
        _harvestPhrases = false;
        _harvestWords = true;
        _showHarvestValidationError = false;
        _harvestStepReady = false;
        _isParsing = false;
        _messages = [];
        Notify(redraw: true);
    }

    /// <summary>The learner's next action clears the banners (Captain, 2026-11-02).</summary>
    public void DismissMessages()
    {
        if (_messages.IsEmpty)
            return;
        _messages = [];
        Notify(redraw: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _open = false;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    // ---- Helpers ----

    private async Task LoadAvailableResourcesAsync()
    {
        if (!TryOwner(out var owner))
            return;
        try
        {
            var resources = await _import.ListTargetResourcesAsync(owner, _lifetime.Token);
            if (_disposed || !TryOwner(out _))
                return;
            _resources = [.. resources];
            Notify(redraw: true);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (UnauthorizedAccessException ex)
        {
            OnOwnerRefused(ex, _operation);
        }
        catch (Exception ex)
        {
            // Source: logged only; the existing-resource list stays empty.
            _logger.LogError(ex, "Failed to load available resources");
        }
    }

    internal static ImportConfidenceBand ClassifyConfidence(float confidence) =>
        confidence >= 0.85f ? ImportConfidenceBand.High
        : confidence >= 0.70f ? ImportConfidenceBand.Medium
        : ImportConfidenceBand.Low;

    private ImmutableArray<ContentImportItemResult> FilteredItems() =>
        _result?.Items is not { } items ? []
        : _resultFilter is not { } filter ? [.. items]
        : [.. items.Where(item => item.Status == filter)];

    private bool CanEdit() => _open && !_blocked && !_disposed && !_isCommitting && _result is null;

    private bool IsCurrent(long operation) => !_disposed && operation == _operation;

    private bool TryOwner(out string owner)
    {
        owner = _owner ?? string.Empty;
        if (_disposed || !_open || _blocked || string.IsNullOrWhiteSpace(_owner))
            return false;
        if (_userScope.IsCurrent(_ownerStamp) && _userScope.TryGetUserProfileId(out var active)
            && active == _owner)
            return true;
        _logger.LogWarning("Import Content stopped: the device owner changed.");
        Block();
        Notify(redraw: true);
        return false;
    }

    private void OnOwnerRefused(UnauthorizedAccessException ex, long operation)
    {
        _logger.LogWarning(ex, "Import Content was refused for an absent or changed owner.");
        if (_disposed || operation != _operation)
            return;
        AddErrorSilently("Import_ProfileNotFound");
        Block();
        Notify(redraw: true);
    }

    private void Block()
    {
        _blocked = true;
        _isParsing = false;
    }

    private void AddError(string key, string? detail = null)
    {
        AddErrorSilently(key, detail);
        Notify(redraw: true);
    }

    /// <summary>Source toast text: the resource, then ": " and the exception message when there is one.</summary>
    private void AddErrorSilently(string key, string? detail = null) =>
        _messages = _messages.Add(detail is null
            ? NativeMessage.Error(key)
            : new NativeMessage(NativeMessageKind.Error, null, $"{_text(key)}: {detail}"));

    /// <summary>A bound input only updates the state (no event); every other change redraws the page.</summary>
    private void Notify(bool redraw)
    {
        if (_disposed || !redraw)
            return;
        Version++;
        Changed?.Invoke();
    }
}
