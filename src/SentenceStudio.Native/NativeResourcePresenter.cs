using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Common;
using SentenceStudio.Data;
using SentenceStudio.Services;

namespace SentenceStudio.Native;

public sealed partial class NativeResourcePresenter(
    DeviceUserScopeProvider userScope,
    IPreferencesService preferences,
    INativeResourceStore store,
    INativeSyncReadiness sync,
    NativePendingWrites pendingWrites,
    ILogger<NativeResourcePresenter> logger)
{
    private static readonly StringComparer FilterComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly ImmutableArray<string> TypeCatalog = Constants.MediaTypes.ToImmutableArray();
    private static readonly ImmutableArray<string> LanguageCatalog = Constants.Languages.ToImmutableArray();
    private const string ViewModeKey = "resources-view-mode";
    private IReadOnlyList<NativeResourceItem> _all = [];
    private string? _owner;
    private DeviceUserScopeProvider.ScopeStamp? _ownerStamp;
    private string _searchText = string.Empty;
    private string? _mediaType;
    private string? _language;
    private NativeResourceViewMode _viewMode = NativeResourceViewMode.Grid;
    private NativeResourceSyncPhase _syncPhase = NativeResourceSyncPhase.Unconfirmed;
    private string? _syncErrorKey;
    private NativeResourceListState _list = new(NativeResourceListPhase.Idle);
    private NativeResourceDetailState _detail = new(NativeResourceDetailPhase.Idle);
    private readonly object _detailLoadGate = new();
    private long _detailLoadGeneration;
    private long _listLoadGeneration;
    private long _syncGeneration;
    private Task<NativeResourceSyncPhase>? _synchronization;
    private CancellationToken _syncCancellationToken;
    private int _saving;

    public NativeResourceListState List =>
        _owner is not null && !IsCurrentOwnerStamp(_owner, _ownerStamp)
            ? new(NativeResourceListPhase.Blocked) : _list;

    public NativeResourceDetailState Detail =>
        _owner is not null && !IsCurrentOwnerStamp(_owner, _ownerStamp)
            ? new(NativeResourceDetailPhase.Blocked) : _detail;

    public async Task<NativeResourceListState> LoadListAsync(
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentOwner(out var owner))
            return Block();
        var ownerStamp = userScope.CaptureStamp();
        if (!SelectOwner(owner, ownerStamp))
            return List;
        await LoadListCoreAsync(owner, ownerStamp, cancellationToken);
        return List;
    }

    /// <summary>
    /// Refreshes only for the current owner and latest sync attempt; a newer sync
    /// or list read prevents this refresh from publishing stale rows.
    /// </summary>
    public Task<bool> RefreshAfterSyncAsync(
        Task<NativeResourceSyncPhase> synchronization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(synchronization);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<bool>(cancellationToken);
        string owner;
        DeviceUserScopeProvider.ScopeStamp ownerStamp;
        lock (_detailLoadGate)
        {
            if (!IsLatestSynchronizationLocked(synchronization))
                return Task.FromResult(false);
            owner = _owner!;
            ownerStamp = _ownerStamp!.Value;
        }
        return LoadListCoreAsync(owner, ownerStamp, cancellationToken, synchronization);
    }

    private async Task<bool> LoadListCoreAsync(
        string owner, DeviceUserScopeProvider.ScopeStamp ownerStamp,
        CancellationToken cancellationToken,
        Task<NativeResourceSyncPhase>? synchronization = null)
    {
        NativeResourceListState previous;
        long generation;
        lock (_detailLoadGate)
        {
            if (!IsCurrentOwnerStamp(owner, ownerStamp)
                || synchronization is not null
                    && !IsLatestSynchronizationLocked(synchronization))
                return false;
            previous = _list;
            generation = ++_listLoadGeneration;
            _list = _list with {
                Phase = NativeResourceListPhase.Loading, Items = [], ErrorKey = null };
        }
        try
        {
            var rows = await store.ListAsync(owner, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_detailLoadGate)
            {
                if (!CanApplyList(owner, ownerStamp, generation,
                    cancellationToken, synchronization))
                    return false;
                _all = rows;
                ReconcileFilters();
                ProjectList();
                return true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_detailLoadGate)
            {
                if (generation == _listLoadGeneration
                    && IsCurrentOwnerStamp(owner, ownerStamp)
                    && (synchronization is null
                        || IsLatestSynchronizationLocked(synchronization)))
                {
                    _list = previous.Phase == NativeResourceListPhase.Loading
                        ? new(NativeResourceListPhase.Idle, Sync: _syncPhase,
                            SyncErrorKey: _syncErrorKey)
                        : previous with {
                            Sync = _syncPhase, SyncErrorKey = _syncErrorKey };
                    if (CanProjectList())
                        ProjectList();
                }
            }
            throw;
        }
        catch (Exception ex)
        {
            lock (_detailLoadGate)
            {
                if (!CanApplyList(owner, ownerStamp, generation,
                    cancellationToken, synchronization))
                    return false;
                logger.LogError(ex, "Native resource list load failed.");
                _list = previous.Phase is NativeResourceListPhase.Populated
                    or NativeResourceListPhase.Empty or NativeResourceListPhase.NoMatches
                    ? previous with { ErrorKey = "NativeResources_LoadError",
                        Sync = _syncPhase, SyncErrorKey = _syncErrorKey }
                    : _list with {
                        Phase = NativeResourceListPhase.LoadError,
                        Items = [],
                        ErrorKey = "NativeResources_LoadError"
                    };
                return true;
            }
        }
    }

    public NativeResourceListState Search(string? text)
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
            return Block();
        _searchText = text ?? string.Empty;
        return CanProjectList()
            ? ProjectList()
            : _list = _list with { SearchText = _searchText };
    }

    public NativeResourceListState Filter(string? mediaType, string? language)
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
            return Block();
        var selectedType = mediaType is null ? null
            : TypeCatalog.FirstOrDefault(option => FilterComparer.Equals(option, mediaType))
                ?? throw new ArgumentException("The selected resource type is not in the catalog.", nameof(mediaType));
        var selectedLanguage = language is null ? null
            : LanguageCatalog.FirstOrDefault(option => FilterComparer.Equals(option, language))
                ?? throw new ArgumentException("The selected resource language is not in the catalog.", nameof(language));
        _mediaType = selectedType;
        _language = selectedLanguage;
        return CanProjectList()
            ? ProjectList()
            : _list = _list with {
                MediaType = _mediaType, Language = _language };
    }

    public NativeResourceListState SetViewMode(NativeResourceViewMode viewMode)
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
            return Block();
        if (!Enum.IsDefined(viewMode))
            throw new ArgumentOutOfRangeException(nameof(viewMode));
        _viewMode = viewMode;
        preferences.Set(ViewModeKey, viewMode == NativeResourceViewMode.Grid ? "grid" : "list");
        return _list = _list with { ViewMode = viewMode };
    }

    public NativeResourceDetailState BeginCreate()
    {
        if (!TryCurrentOwner(out var owner))
        {
            Block();
            return _detail;
        }
        if (!SelectOwner(owner, userScope.CaptureStamp()))
            return Detail;
        if (Volatile.Read(ref _saving) != 0)
            return _detail;
        _detailLoadGeneration++;
        return _detail = new(NativeResourceDetailPhase.Creating,
            Draft: new NativeResourceDraft(string.Empty, null, null,
                MediaType: TypeCatalog[0], Language: LanguageCatalog[0],
                StagedVocabulary: []),
            Sync: _syncPhase, SyncErrorKey: _syncErrorKey);
    }

    public async Task<NativeResourceDetailState> OpenAsync(
        string resourceId, CancellationToken cancellationToken = default)
    {
        if (!TryCurrentOwner(out var owner))
        {
            Block();
            return _detail;
        }
        var ownerStamp = userScope.CaptureStamp();
        if (!SelectOwner(owner, ownerStamp))
            return Detail;
        if (_detail.Draft is not null)
        {
            logger.LogDebug("Native resource detail refresh deferred while an edit is open.");
            return _detail;
        }
        long generation;
        lock (_detailLoadGate)
        {
            generation = ++_detailLoadGeneration;
            _detail = new(NativeResourceDetailPhase.Loading, Sync: _syncPhase,
                SyncErrorKey: _syncErrorKey);
            if (string.IsNullOrWhiteSpace(resourceId))
                return _detail = _detail with {
                    Phase = NativeResourceDetailPhase.NotFound,
                    ErrorKey = "ResourceEdit_NotFound"
                };
        }
        try
        {
            var resource = await store.GetAsync(owner, resourceId, cancellationToken);
            lock (_detailLoadGate)
            {
                if (generation != _detailLoadGeneration)
                    return _detail;
                if (!IsCurrentOwner(owner) || !userScope.IsCurrent(ownerStamp))
                {
                    Block();
                    return _detail;
                }
                return _detail = resource is null
                    ? _detail with {
                        Phase = NativeResourceDetailPhase.NotFound,
                        ErrorKey = "ResourceEdit_NotFound"
                    }
                    : new(NativeResourceDetailPhase.Ready, resource,
                        Sync: _syncPhase, SyncErrorKey: _syncErrorKey);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_detailLoadGate)
            {
                if (generation != _detailLoadGeneration)
                    return _detail;
                if (!IsCurrentOwner(owner) || !userScope.IsCurrent(ownerStamp))
                {
                    Block();
                    return _detail;
                }
                logger.LogError(ex, "Native resource detail load failed.");
                return _detail = _detail with {
                    Phase = NativeResourceDetailPhase.LoadError,
                    ErrorKey = "NativeResources_LoadError"
                };
            }
        }
    }

    public void InvalidateDetail()
    {
        lock (_detailLoadGate)
        {
            _detailLoadGeneration++;
            _detail = new(NativeResourceDetailPhase.Idle);
        }
    }

    public bool BeginEdit()
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner
            || _detail.Resource is not { } resource
            || _detail.Phase != NativeResourceDetailPhase.Ready
            || Volatile.Read(ref _vocabularyMutation) != 0)
            return false;
        if (resource.IsSmartResource)
        {
            _detail = _detail with {
                Phase = NativeResourceDetailPhase.ReadOnly,
                ErrorKey = "ResourceEdit_AutoUpdated"
            };
            return false;
        }
        _detail = _detail with {
            Draft = DraftFor(resource),
            ErrorKey = null,
            LastSaveResult = null
        };
        return true;
    }

    public NativeResourceDetailState ChangeDraft(NativeResourceField field, string? value)
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
        {
            Block();
            return _detail;
        }
        if (_detail.Draft is not { } draft || Volatile.Read(ref _saving) != 0
            || Volatile.Read(ref _vocabularyMutation) != 0
            || _detail.Reloading)
            throw new InvalidOperationException("No editable resource draft is active.");
        if (field == NativeResourceField.Language
            && _detail.Resource is not null
            && !draft.StagedVocabulary.IsDefaultOrEmpty
            && !string.Equals(draft.Language, value, StringComparison.Ordinal))
            return _detail = _detail with {
                ErrorKey = "NativeResources_SaveLanguageBeforeQuickAdd"
            };
        var refusal = _detail.Phase is NativeResourceDetailPhase.Conflict
            or NativeResourceDetailPhase.NotFound or NativeResourceDetailPhase.ReadOnly
            || _detail.LastSaveResult == ResourceMetadataUpdateResult.ValidationFailed;
        _detail = _detail with {
            Draft = field switch
            {
                NativeResourceField.Title => draft with {
                    Title = value ?? string.Empty, TitleDirty = true },
                NativeResourceField.Description => draft with {
                    Description = value ?? string.Empty, DescriptionDirty = true },
                NativeResourceField.Tags => draft with {
                    Tags = value ?? string.Empty, TagsDirty = true },
                NativeResourceField.MediaType => draft with {
                    MediaType = SelectCatalogValue(TypeCatalog, value), MediaTypeDirty = true },
                NativeResourceField.Language => draft with {
                    Language = SelectCatalogValue(LanguageCatalog, value), LanguageDirty = true },
                NativeResourceField.MediaUrl => draft with {
                    MediaUrl = value ?? string.Empty, MediaUrlDirty = true },
                NativeResourceField.Transcript => draft with {
                    Transcript = value ?? string.Empty, TranscriptDirty = true },
                NativeResourceField.Translation => draft with {
                    Translation = value ?? string.Empty, TranslationDirty = true },
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            },
            Phase = refusal ? _detail.Phase
                : _detail.Resource is null
                    ? NativeResourceDetailPhase.Creating
                    : NativeResourceDetailPhase.Ready,
            ErrorKey = refusal ? _detail.ErrorKey : null,
            LastSaveResult = refusal ? _detail.LastSaveResult : null
        };
        if (field == NativeResourceField.Language)
        {
            Interlocked.Increment(ref _vocabularySearchGeneration);
            _detail = _detail with { VocabularySearch = null };
            if (_detail.Resource is null && _detail.Draft is { } stagedDraft
                && !stagedDraft.StagedVocabulary.IsDefaultOrEmpty)
                _detail = _detail with {
                    Draft = stagedDraft with {
                        StagedVocabulary = stagedDraft.StagedVocabulary
                            .Select(row => row with { Language = stagedDraft.Language })
                            .ToImmutableArray()
                    }
                };
        }
        return _detail;
    }

    public void CancelEdit()
    {
        if (Volatile.Read(ref _saving) != 0
            || Volatile.Read(ref _vocabularyMutation) != 0)
            return;
        if (_detail.Phase == NativeResourceDetailPhase.LoadError
            && _detail.Resource is null)
            return;
        if (_detail.Resource is null)
        {
            _detail = new(NativeResourceDetailPhase.Idle, Sync: _syncPhase,
                SyncErrorKey: _syncErrorKey);
            return;
        }
        _detail = _detail with {
            Draft = null,
            Phase = _detail.Resource?.IsSmartResource == true
                ? NativeResourceDetailPhase.ReadOnly
                : _detail.Resource is null
                    ? _detail.Phase : NativeResourceDetailPhase.Ready,
            ErrorKey = null,
            LastSaveResult = null,
            Reloading = false
        };
    }

    private static string SelectCatalogValue(ImmutableArray<string> catalog, string? value) =>
        catalog.FirstOrDefault(option => FilterComparer.Equals(option, value))
        ?? throw new ArgumentException("The selected value is not in the resource catalog.");

    private static NativeResourceDraft DraftFor(NativeResourceDetail resource) =>
        new(resource.Title, resource.OriginalMetadata.Description, resource.OriginalMetadata.Tags,
            MediaType: resource.MediaType, Language: resource.Language,
            MediaUrl: resource.MediaUrl, Transcript: resource.Transcript,
            Translation: resource.Translation);

    private static ResourceContentUpdate ContentFromDraft(
        NativeResourceDetail resource, NativeResourceDraft draft)
    {
        var content = resource.OriginalContent ?? new ResourceContentSnapshot(
            resource.OriginalMetadata, resource.MediaType, resource.Language,
            resource.MediaUrl, resource.Transcript, resource.Translation);
        return new ResourceContentUpdate(draft.Title,
            draft.DescriptionDirty ? draft.Description : content.Metadata.Description,
            draft.TagsDirty ? draft.Tags : content.Metadata.Tags,
            draft.MediaTypeDirty ? draft.MediaType : content.MediaType,
            draft.LanguageDirty ? draft.Language : content.Language,
            draft.MediaUrlDirty ? draft.MediaUrl : content.MediaUrl,
            draft.TranscriptDirty ? draft.Transcript : content.Transcript,
            draft.TranslationDirty ? draft.Translation : content.Translation);
    }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
        {
            Block();
            return false;
        }
        var ownerStamp = userScope.CaptureStamp();
        if (!IsCurrentOwnerStamp(owner, ownerStamp))
        {
            Block();
            return false;
        }
        var resource = _detail.Resource;
        var draft = _detail.Draft;
        var creating = resource is null && _detail.Phase is
            (NativeResourceDetailPhase.Creating or NativeResourceDetailPhase.SaveError);
        if (draft is null || _detail.Reloading
            || !creating && (resource?.IsSmartResource != false
                || _detail.Phase is not (NativeResourceDetailPhase.Ready
                    or NativeResourceDetailPhase.SaveError))
            || _detail.LastSaveResult == ResourceMetadataUpdateResult.ValidationFailed)
            return false;
        if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0)
            return false;
        if (string.IsNullOrWhiteSpace(draft.Title))
        {
            _detail = _detail with {
                Phase = NativeResourceDetailPhase.SaveError,
                ErrorKey = creating ? "ResourceAdd_TitleRequired"
                    : "ResourceEdit_TitleRequired"
            };
            Volatile.Write(ref _saving, 0);
            return false;
        }
        if (!creating && !draft.StagedVocabulary.IsDefaultOrEmpty
            && draft.StagedVocabulary.Any(row => row.Language != draft.Language))
        {
            _detail = _detail with {
                Phase = NativeResourceDetailPhase.SaveError,
                ErrorKey = "NativeResources_SaveInvalid"
            };
            Volatile.Write(ref _saving, 0);
            return false;
        }

        var committed = false;
        string? committedResourceId = null;
        _detail = _detail with {
            Phase = NativeResourceDetailPhase.Saving, ErrorKey = null,
            LastSaveResult = null };
        try
        {
            if (!IsCurrentOwnerStamp(owner, ownerStamp))
            {
                Block();
                return false;
            }
            string resourceId;
            ResourceMetadataUpdateResult result;
            string? createErrorKey = null;
            if (creating)
            {
                var created = await store.CreateAsync(owner, draft, cancellationToken);
                result = created.Result switch
                {
                    NativeResourceMutationResult.Success => ResourceMetadataUpdateResult.Success,
                    NativeResourceMutationResult.Invalid => ResourceMetadataUpdateResult.ValidationFailed,
                    NativeResourceMutationResult.NotFoundOrForbidden =>
                        ResourceMetadataUpdateResult.NotFoundOrForbidden,
                    _ => ResourceMetadataUpdateResult.Conflict
                };
                resourceId = created.ResourceId ?? string.Empty;
                createErrorKey = created.ErrorKey;
                if (result == ResourceMetadataUpdateResult.Success
                    && string.IsNullOrWhiteSpace(resourceId))
                    throw new InvalidOperationException("Resource create reported success without an ID.");
            }
            else
            {
                var original = resource!.OriginalMetadata;
                var content = resource.OriginalContent ?? new ResourceContentSnapshot(
                    original, resource.MediaType, resource.Language, resource.MediaUrl,
                    resource.Transcript, resource.Translation);
                result = await store.SaveContentAsync(
                    owner, resource.Id, content,
                    ContentFromDraft(resource, draft),
                    cancellationToken);
                resourceId = resource.Id;
            }
            if (result == ResourceMetadataUpdateResult.Success)
            {
                committed = true;
                committedResourceId = resourceId;
                pendingWrites.MarkPending(owner);
            }
            if (!IsCurrentOwnerStamp(owner, ownerStamp))
            {
                Block();
                return false;
            }
            if (result != ResourceMetadataUpdateResult.Success)
            {
                var (phase, error) = result switch
                {
                    ResourceMetadataUpdateResult.NotFoundOrForbidden =>
                        (NativeResourceDetailPhase.NotFound, "ResourceEdit_NotFound"),
                    ResourceMetadataUpdateResult.SmartReadOnly =>
                        (NativeResourceDetailPhase.ReadOnly, "ResourceEdit_AutoUpdated"),
                    ResourceMetadataUpdateResult.ValidationFailed =>
                        (NativeResourceDetailPhase.SaveError, "NativeResources_SaveInvalid"),
                    ResourceMetadataUpdateResult.Conflict =>
                        (NativeResourceDetailPhase.Conflict, "NativeResources_Conflict"),
                    _ => throw new ArgumentOutOfRangeException(nameof(result), result, null)
                };
                _detail = _detail with {
                    Phase = creating && phase == NativeResourceDetailPhase.NotFound
                        ? NativeResourceDetailPhase.SaveError : phase,
                    ErrorKey = createErrorKey ?? error,
                    LastSaveResult = creating ? null : result
                };
                return false;
            }
            _syncPhase = NativeResourceSyncPhase.Pending;
            _syncErrorKey = null;

            if (!creating && !draft.StagedVocabulary.IsDefaultOrEmpty)
            {
                foreach (var word in draft.StagedVocabulary)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var linked = word.IsExisting
                        ? await store.AttachVocabularyAsync(
                            owner, resourceId, word.Id, draft.Language!, cancellationToken)
                        : await store.CreateVocabularyAsync(
                            owner, resourceId, word, draft.Language!, cancellationToken);
                    if (!IsCurrentOwnerStamp(owner, ownerStamp))
                    {
                        Block();
                        return false;
                    }
                    if (linked is NativeResourceMutationResult.Success
                        or NativeResourceMutationResult.AlreadyPresent)
                        continue;
                    logger.LogWarning(
                        "A resource was saved but a staged vocabulary association failed ({Result}).",
                        linked);
                    _detail = new(NativeResourceDetailPhase.LoadError,
                        Resource: resource, Draft: draft,
                        ErrorKey: "NativeResources_SavedReloadError",
                        Sync: _syncPhase)
                    {
                        CommittedResourceId = resourceId
                    };
                    return false;
                }
            }

            var reloaded = await store.GetAsync(owner, resourceId, cancellationToken);
            var rows = await store.ListAsync(owner, cancellationToken);
            if (!IsCurrentOwnerStamp(owner, ownerStamp))
            {
                Block();
                return false;
            }
            if (reloaded is null)
                throw new InvalidOperationException("A saved resource was not owner-visible after reload.");
            _all = rows;
            ReconcileFilters();
            ProjectList();
            _detail = new(NativeResourceDetailPhase.Ready, reloaded,
                Sync: _syncPhase);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentOwnerStamp(owner, ownerStamp))
                _detail = committed
                    ? new(NativeResourceDetailPhase.LoadError,
                        Resource: !creating && !draft.StagedVocabulary.IsDefaultOrEmpty
                            ? resource : null,
                        Draft: !creating && !draft.StagedVocabulary.IsDefaultOrEmpty
                            ? draft : null,
                        ErrorKey: "NativeResources_SavedReloadError", Sync: _syncPhase)
                    {
                        CommittedResourceId = committedResourceId
                    }
                    : _detail with {
                        Phase = creating ? NativeResourceDetailPhase.Creating
                            : NativeResourceDetailPhase.Ready,
                        Draft = draft,
                        ErrorKey = null };
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Native resource save or reload failed.");
            if (!IsCurrentOwnerStamp(owner, ownerStamp))
            {
                Block();
                return false;
            }
            _detail = committed
                ? new(NativeResourceDetailPhase.LoadError,
                    Resource: !creating && !draft.StagedVocabulary.IsDefaultOrEmpty
                        ? resource : null,
                    Draft: !creating && !draft.StagedVocabulary.IsDefaultOrEmpty
                        ? draft : null,
                    ErrorKey: "NativeResources_SavedReloadError",
                    Sync: _syncPhase)
                {
                    CommittedResourceId = committedResourceId
                }
                : _detail with {
                    Phase = NativeResourceDetailPhase.SaveError,
                    ErrorKey = "NativeResources_SaveError",
                    Sync = _syncPhase,
                    Draft = draft
                };
            return false;
        }
        finally
        {
            Volatile.Write(ref _saving, 0);
        }
    }

    public async Task<bool> ReloadDraftAsync(CancellationToken cancellationToken = default)
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner)
        {
            Block();
            return false;
        }
        var ownerStamp = userScope.CaptureStamp();
        if (!IsCurrentOwnerStamp(owner, ownerStamp))
        {
            Block();
            return false;
        }
        if (_detail is not { Resource: { } resource, Draft: { } draft }
            || _detail.Reloading || Volatile.Read(ref _saving) != 0
            || (_detail.Phase is not (NativeResourceDetailPhase.Conflict
                    or NativeResourceDetailPhase.NotFound
                    or NativeResourceDetailPhase.ReadOnly
                    or NativeResourceDetailPhase.SaveError)
                && (_detail.Phase != NativeResourceDetailPhase.LoadError
                    || _detail.ErrorKey is not
                        ("NativeResources_GenerationPartial"
                            or "NativeResources_SavedReloadError"))))
            return false;

        _detail = _detail with { Reloading = true };
        try
        {
            var current = await store.GetAsync(owner, resource.Id, cancellationToken);
            if (!IsCurrentOwnerStamp(owner, ownerStamp))
            {
                Block();
                return false;
            }
            if (!ReferenceEquals(_detail.Draft, draft))
                return false;
            if (current is null)
            {
                _detail = _detail with {
                    Phase = NativeResourceDetailPhase.NotFound,
                    ErrorKey = "ResourceEdit_NotFound",
                    LastSaveResult = ResourceMetadataUpdateResult.NotFoundOrForbidden,
                    Reloading = false
                };
                return false;
            }
            _detail = current.IsSmartResource
                ? new(NativeResourceDetailPhase.ReadOnly, current, Sync: _syncPhase,
                    SyncErrorKey: _syncErrorKey)
                : new(NativeResourceDetailPhase.Ready, current,
                    _detail.ErrorKey == "NativeResources_SavedReloadError"
                        && !draft.StagedVocabulary.IsDefaultOrEmpty
                        ? DraftFor(current) with {
                            StagedVocabulary = draft.StagedVocabulary }
                        : DraftFor(current),
                    Sync: _syncPhase, SyncErrorKey: _syncErrorKey);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentOwnerStamp(owner, ownerStamp)
                && ReferenceEquals(_detail.Draft, draft))
                _detail = _detail with { Reloading = false };
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Native resource conflict reload failed.");
            if (!IsCurrentOwnerStamp(owner, ownerStamp))
            {
                Block();
                return false;
            }
            if (ReferenceEquals(_detail.Draft, draft))
                _detail = _detail with {
                    Reloading = false, ErrorKey = "NativeResources_ReloadError"
                };
            return false;
        }
    }

    /// <summary>
    /// Overlapping callers share one attempt and its initiating cancellation token.
    /// Completion permits a new retry; invalidation or owner change detaches late results.
    /// </summary>
    public Task<NativeResourceSyncPhase> SynchronizeAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<NativeResourceSyncPhase>(cancellationToken);
        if (!TryCurrentOwner(out var owner))
        {
            Block();
            return Task.FromResult(NativeResourceSyncPhase.Unconfirmed);
        }
        var ownerStamp = userScope.CaptureStamp();
        if (!SelectOwner(owner, ownerStamp))
            return Task.FromResult(NativeResourceSyncPhase.Unconfirmed);
        lock (_detailLoadGate)
        {
            if (!IsCurrentOwnerStamp(owner, ownerStamp))
                return Task.FromResult(NativeResourceSyncPhase.Unconfirmed);
            if (_synchronization is { IsCompleted: false } inflight
                && !_syncCancellationToken.IsCancellationRequested)
                return inflight;
            var revision = pendingWrites.Revision(owner);
            var generation = ++_syncGeneration;
            _syncCancellationToken = cancellationToken;
            return _synchronization = SynchronizeCoreAsync(
                owner, ownerStamp, revision, generation, cancellationToken);
        }
    }

    public bool IsLatestSynchronization(Task<NativeResourceSyncPhase> synchronization)
    {
        ArgumentNullException.ThrowIfNull(synchronization);
        lock (_detailLoadGate)
            return IsLatestSynchronizationLocked(synchronization);
    }

    /// <summary>Detaches pending sync and list reads when the UI owner is disposed.</summary>
    public void InvalidateSync()
    {
        lock (_detailLoadGate)
        {
            _syncGeneration++;
            _listLoadGeneration++;
            _synchronization = null;
            _syncCancellationToken = default;
        }
    }

    private async Task<NativeResourceSyncPhase> SynchronizeCoreAsync(
        string owner, DeviceUserScopeProvider.ScopeStamp ownerStamp,
        long revision, long generation, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await sync.SynchronizeAsync(owner, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_detailLoadGate)
            {
                if (!CanApplySync(owner, ownerStamp, generation, cancellationToken))
                    return NativeResourceSyncPhase.Unconfirmed;
                if (outcome == NativeSyncOutcome.Confirmed)
                    pendingWrites.MarkConfirmedIfUnchanged(owner, revision);
                _syncPhase = pendingWrites.IsPending(owner)
                    ? NativeResourceSyncPhase.Pending
                    : outcome == NativeSyncOutcome.Confirmed
                        ? NativeResourceSyncPhase.Confirmed
                        : NativeResourceSyncPhase.Unconfirmed;
                _syncErrorKey = outcome == NativeSyncOutcome.Confirmed
                    ? null : "NativeResources_SyncUnconfirmed";
                _list = _list with { Sync = _syncPhase, SyncErrorKey = _syncErrorKey };
                _detail = _detail with { Sync = _syncPhase, SyncErrorKey = _syncErrorKey };
                return _syncPhase;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_detailLoadGate)
            {
                if (!CanApplySync(owner, ownerStamp, generation, cancellationToken))
                    return NativeResourceSyncPhase.Unconfirmed;
                logger.LogWarning(ex, "Native resource sync could not be confirmed.");
                _syncPhase = pendingWrites.IsPending(owner)
                    ? NativeResourceSyncPhase.Pending : NativeResourceSyncPhase.Unconfirmed;
                _syncErrorKey = "NativeResources_SyncUnconfirmed";
                _list = _list with { Sync = _syncPhase, SyncErrorKey = _syncErrorKey };
                _detail = _detail with { Sync = _syncPhase, SyncErrorKey = _syncErrorKey };
                return _syncPhase;
            }
        }
    }

    private bool CanApplySync(
        string owner, DeviceUserScopeProvider.ScopeStamp ownerStamp,
        long generation, CancellationToken cancellationToken) =>
        generation == _syncGeneration && !cancellationToken.IsCancellationRequested
        && IsCurrentOwnerStamp(owner, ownerStamp);

    private bool CanApplyList(
        string owner, DeviceUserScopeProvider.ScopeStamp ownerStamp,
        long generation, CancellationToken cancellationToken,
        Task<NativeResourceSyncPhase>? synchronization) =>
        generation == _listLoadGeneration && !cancellationToken.IsCancellationRequested
        && IsCurrentOwnerStamp(owner, ownerStamp)
        && (synchronization is null
            || IsLatestSynchronizationLocked(synchronization));

    private bool IsLatestSynchronizationLocked(Task<NativeResourceSyncPhase> synchronization) =>
        ReferenceEquals(_synchronization, synchronization)
        && !_syncCancellationToken.IsCancellationRequested
        && _owner is { } owner && IsCurrentOwnerStamp(owner, _ownerStamp);

    private bool IsCurrentOwnerStamp(
        string owner, DeviceUserScopeProvider.ScopeStamp? stamp) =>
        _owner == owner && stamp is { } current && _ownerStamp == current
        && userScope.IsCurrent(current) && IsCurrentOwner(owner);

    private NativeResourceListState ProjectList()
    {
        var matching = _all.Where(item =>
            (_mediaType is null || FilterComparer.Equals(item.MediaType, _mediaType))
            && (_language is null || FilterComparer.Equals(item.Language, _language))
            && (string.IsNullOrWhiteSpace(_searchText)
                || Contains(item.Title, _searchText)
                || Contains(item.Description, _searchText)
                || Contains(item.Tags, _searchText)
                || Contains(item.Language, _searchText)))
            .ToImmutableArray();
        var phase = _all.Count == 0 ? NativeResourceListPhase.Empty
            : matching.Length == 0 ? NativeResourceListPhase.NoMatches
            : NativeResourceListPhase.Populated;
        return _list = new(phase, matching, TypeCatalog, LanguageCatalog,
            _searchText, _mediaType, _language, Sync: _syncPhase,
            SyncErrorKey: _syncErrorKey, ViewMode: _viewMode);
    }

    private bool CanProjectList() =>
        _list.Phase is NativeResourceListPhase.Populated
            or NativeResourceListPhase.Empty
            or NativeResourceListPhase.NoMatches;

    private void ReconcileFilters()
    {
        if (_mediaType is not null)
            _mediaType = TypeCatalog.FirstOrDefault(option =>
                FilterComparer.Equals(option, _mediaType));
        if (_language is not null)
            _language = LanguageCatalog.FirstOrDefault(option =>
                FilterComparer.Equals(option, _language));
    }

    private bool SelectOwner(
        string owner, DeviceUserScopeProvider.ScopeStamp ownerStamp)
    {
        lock (_detailLoadGate)
        {
            if (!IsCurrentOwner(owner) || !userScope.IsCurrent(ownerStamp))
                return false;
            if (_owner != owner || _ownerStamp != ownerStamp)
            {
                _detailLoadGeneration++;
                _listLoadGeneration++;
                _syncGeneration++;
                _synchronization = null;
                _syncCancellationToken = default;
                _owner = owner;
                _ownerStamp = ownerStamp;
                _all = [];
                _searchText = string.Empty;
                _mediaType = null;
                _language = null;
                _viewMode = preferences.Get(ViewModeKey, "grid") == "list"
                    ? NativeResourceViewMode.List : NativeResourceViewMode.Grid;
                _list = new(NativeResourceListPhase.Idle, ViewMode: _viewMode);
                _detail = new(NativeResourceDetailPhase.Idle);
                _syncPhase = NativeResourceSyncPhase.Unconfirmed;
                _syncErrorKey = null;
            }
            if (pendingWrites.IsPending(owner))
                _syncPhase = NativeResourceSyncPhase.Pending;
            return true;
        }
    }

    private NativeResourceListState Block()
    {
        lock (_detailLoadGate)
        {
            _detailLoadGeneration++;
            _listLoadGeneration++;
            _syncGeneration++;
            _synchronization = null;
            _syncCancellationToken = default;
            _owner = null;
            _ownerStamp = null;
            _all = [];
            _syncPhase = NativeResourceSyncPhase.Unconfirmed;
            _syncErrorKey = null;
            _detail = new(NativeResourceDetailPhase.Blocked);
            return _list = new(NativeResourceListPhase.Blocked);
        }
    }

    private bool TryCurrentOwner(out string owner)
    {
        if (userScope.TryGetUserProfileId(out owner)
            && !string.IsNullOrWhiteSpace(owner)
            && string.Equals(
                preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                owner, StringComparison.Ordinal))
            return true;
        owner = string.Empty;
        return false;
    }

    private bool IsCurrentOwner(string owner) =>
        TryCurrentOwner(out var active) && active == owner;

    private static bool Contains(string? text, string search) =>
        text?.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase) == true;
}
