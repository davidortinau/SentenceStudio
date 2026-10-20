using Microsoft.Extensions.Logging;
using SentenceStudio.Data;

namespace SentenceStudio.Native;

public sealed partial class NativeResourcePresenter
{
    private int _creatingStarter;

    public async Task<NativeResourceCreateResult> CreateStarterAsync(
        CancellationToken cancellationToken = default)
    {
        if (!TryCurrentOwner(out var owner))
        {
            Block();
            return new(NativeResourceMutationResult.NotFoundOrForbidden);
        }
        var stamp = userScope.CaptureStamp();
        if (!SelectOwner(owner, stamp))
            return new(NativeResourceMutationResult.NotFoundOrForbidden);
        if (Interlocked.CompareExchange(ref _creatingStarter, 1, 0) != 0)
            return new(NativeResourceMutationResult.Invalid);

        _list = _list with { StarterBusy = true, ErrorKey = null,
            LastStarterResult = null };
        var committed = false;
        try
        {
            var created = await store.CreateStarterAsync(owner, cancellationToken);
            committed = created.Result == NativeResourceMutationResult.Success;
            if (committed)
                pendingWrites.MarkPending(owner);
            if (!IsCurrentOwnerStamp(owner, stamp))
            {
                Block();
                return new(NativeResourceMutationResult.NotFoundOrForbidden);
            }
            if (committed)
            {
                _syncPhase = NativeResourceSyncPhase.Pending;
                _syncErrorKey = null;
            }
            if (committed || created.Result == NativeResourceMutationResult.AlreadyPresent)
            {
                var rows = await store.ListAsync(owner, cancellationToken);
                if (!IsCurrentOwnerStamp(owner, stamp))
                {
                    Block();
                    return new(NativeResourceMutationResult.NotFoundOrForbidden);
                }
                if (committed && !rows.Any(row => row.Id == created.ResourceId))
                    throw new InvalidOperationException(
                        "A created starter resource was not owner-visible after reload.");
                _all = rows;
                ReconcileFilters();
                ProjectList();
            }
            _list = _list with {
                StarterBusy = false,
                LastStarterResult = created,
                ErrorKey = created.Result is NativeResourceMutationResult.Success
                    or NativeResourceMutationResult.AlreadyPresent
                    ? null : created.ErrorKey ?? "NativeResources_StarterFailed"
            };
            return created;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentOwnerStamp(owner, stamp))
                _list = _list with {
                    ErrorKey = committed ? "NativeResources_SavedReloadError" : null,
                    Sync = _syncPhase
                };
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Native starter resource creation or reload failed.");
            var failed = new NativeResourceCreateResult(
                NativeResourceMutationResult.Failed,
                ErrorKey: committed ? "NativeResources_SavedReloadError"
                    : "NativeResources_StarterFailed");
            if (IsCurrentOwnerStamp(owner, stamp))
                _list = _list with {
                    ErrorKey = failed.ErrorKey,
                    LastStarterResult = failed,
                    Sync = _syncPhase
                };
            return failed;
        }
        finally
        {
            if (IsCurrentOwnerStamp(owner, stamp))
                _list = _list with { StarterBusy = false };
            Volatile.Write(ref _creatingStarter, 0);
        }
    }

    public async Task<NativeResourceImportResult> GenerateVocabularyAsync(
        CancellationToken cancellationToken = default)
    {
        if (_detail.Resource is not { IsSmartResource: false } resource
            || _detail.Phase is not (NativeResourceDetailPhase.Ready
                or NativeResourceDetailPhase.SaveError)
            || _detail.Reloading || Volatile.Read(ref _saving) != 0)
            return new(0, 0, NativeResourceMutationResult.Invalid,
                "ResourceEdit_GenerateFailed");
        var activeDraft = _detail.Draft;
        var draft = activeDraft ?? DraftFor(resource);
        if (string.IsNullOrWhiteSpace(draft.Transcript))
            return new(0, 0, NativeResourceMutationResult.Invalid,
                "ResourceEdit_NoTranscript");
        if (string.IsNullOrWhiteSpace(draft.Title))
            return new(0, 0, NativeResourceMutationResult.Invalid,
                "ResourceEdit_TitleRequired");
        if (!TryCurrentOwner(out var owner) || _owner != owner)
        {
            Block();
            return new(0, 0, NativeResourceMutationResult.NotFoundOrForbidden);
        }
        if (Interlocked.CompareExchange(ref _vocabularyMutation, 1, 0) != 0)
            return new(0, 0, NativeResourceMutationResult.Invalid,
                "ResourceEdit_GenerateFailed");
        var stamp = userScope.CaptureStamp();
        var contentSaved = false;
        _detail = _detail with { VocabularyBusy = true, ErrorKey = null };
        try
        {
            var generated = await store.GenerateVocabularyFromDraftAsync(
                owner, resource.Id, draft, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentOwnerStamp(owner, stamp)
                || _detail.Resource?.Id != resource.Id
                || !ReferenceEquals(activeDraft, _detail.Draft))
            {
                Block();
                return new(0, 0, NativeResourceMutationResult.NotFoundOrForbidden);
            }

            var original = resource.OriginalContent ?? new ResourceContentSnapshot(
                resource.OriginalMetadata, resource.MediaType, resource.Language,
                resource.MediaUrl, resource.Transcript, resource.Translation);
            var content = ContentFromDraft(resource, draft);
            var saved = await store.SaveContentAsync(
                owner, resource.Id, original, content, cancellationToken);
            if (!IsCurrentOwnerStamp(owner, stamp))
            {
                Block();
                return new(0, 0, NativeResourceMutationResult.NotFoundOrForbidden);
            }
            if (saved != ResourceMetadataUpdateResult.Success)
            {
                var (phase, error, outcome) = saved switch
                {
                    ResourceMetadataUpdateResult.Conflict =>
                        (NativeResourceDetailPhase.Conflict,
                            "NativeResources_Conflict", NativeResourceMutationResult.Failed),
                    ResourceMetadataUpdateResult.NotFoundOrForbidden =>
                        (NativeResourceDetailPhase.NotFound,
                            "ResourceEdit_NotFound",
                            NativeResourceMutationResult.NotFoundOrForbidden),
                    ResourceMetadataUpdateResult.SmartReadOnly =>
                        (NativeResourceDetailPhase.ReadOnly,
                            "ResourceEdit_AutoUpdated",
                            NativeResourceMutationResult.SmartReadOnly),
                    _ => (NativeResourceDetailPhase.SaveError,
                        "NativeResources_SaveInvalid", NativeResourceMutationResult.Invalid)
                };
                _detail = _detail with {
                    Phase = phase, ErrorKey = error, LastSaveResult = saved
                };
                return new(0, 0, outcome, error);
            }

            contentSaved = true;
            pendingWrites.MarkPending(owner);
            _syncPhase = NativeResourceSyncPhase.Pending;
            _syncErrorKey = null;
            if (!IsCurrentOwnerStamp(owner, stamp))
            {
                Block();
                return new(0, 0, NativeResourceMutationResult.NotFoundOrForbidden);
            }

            var import = await store.ImportVocabularyAsync(
                owner, resource.Id, content.Language, generated.Words,
                cancellationToken);
            if (!IsCurrentOwnerStamp(owner, stamp))
            {
                Block();
                return new(0, 0, NativeResourceMutationResult.NotFoundOrForbidden);
            }
            var result = import with {
                GeneratedTotal = generated.GeneratedTotal,
                Skipped = import.Skipped + generated.FilteredCount
            };
            if (import.Result != NativeResourceMutationResult.Success)
            {
                _detail = _detail with {
                    Phase = NativeResourceDetailPhase.LoadError,
                    ErrorKey = "NativeResources_GenerationPartial",
                    Sync = _syncPhase
                };
                return result with {
                    Result = NativeResourceMutationResult.Failed,
                    ErrorKey = "NativeResources_GenerationPartial"
                };
            }

            var reloaded = await store.GetAsync(owner, resource.Id, cancellationToken);
            var rows = await store.ListAsync(owner, cancellationToken);
            if (!IsCurrentOwnerStamp(owner, stamp))
            {
                Block();
                return new(0, 0, NativeResourceMutationResult.NotFoundOrForbidden);
            }
            if (reloaded is null)
                throw new InvalidOperationException("Generated vocabulary was saved but resource reload failed.");
            _all = rows;
            ReconcileFilters();
            ProjectList();
            _detail = new(NativeResourceDetailPhase.Ready, reloaded,
                activeDraft is null ? null : DraftFor(reloaded),
                Sync: _syncPhase, LastImport: result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentOwnerStamp(owner, stamp))
                _detail = _detail with {
                    Phase = contentSaved
                        ? NativeResourceDetailPhase.LoadError : _detail.Phase,
                    ErrorKey = contentSaved ? "NativeResources_GenerationPartial" : null,
                    Sync = _syncPhase
                };
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Native resource generation failed ({FailureType}).",
                ex.GetType().Name);
            if (IsCurrentOwnerStamp(owner, stamp))
                _detail = _detail with {
                    Phase = contentSaved
                        ? NativeResourceDetailPhase.LoadError : _detail.Phase,
                    ErrorKey = contentSaved
                        ? "NativeResources_GenerationPartial"
                        : "ResourceEdit_GenerateFailed",
                    Sync = _syncPhase
                };
            return new(0, 0, NativeResourceMutationResult.Failed,
                contentSaved
                    ? "NativeResources_GenerationPartial"
                    : "ResourceEdit_GenerateFailed");
        }
        finally
        {
            if (IsCurrentOwnerStamp(owner, stamp))
                _detail = _detail with { VocabularyBusy = false };
            Volatile.Write(ref _vocabularyMutation, 0);
        }
    }
}
