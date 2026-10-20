using Microsoft.Extensions.Logging;

namespace SentenceStudio.Native;

public sealed partial class NativeResourcePresenter
{
    private int _deleting;

    public bool RequestDelete()
    {
        if (!TryCurrentOwner(out var owner) || _owner != owner
            || _detail.Resource is not { IsSmartResource: false }
            || _detail.Phase is not (NativeResourceDetailPhase.Ready
                or NativeResourceDetailPhase.SaveError)
            || _detail.Reloading || Volatile.Read(ref _saving) != 0
            || Volatile.Read(ref _vocabularyMutation) != 0)
            return false;
        _detail = _detail with { ShowDeleteConfirmation = true, ErrorKey = null };
        return true;
    }

    public void CancelDelete()
    {
        if (Volatile.Read(ref _deleting) == 0)
            _detail = _detail with { ShowDeleteConfirmation = false };
    }

    public async Task<NativeResourceMutationResult> ConfirmDeleteAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_detail.ShowDeleteConfirmation || _detail.Resource is not {
                IsSmartResource: false } resource
            || !TryCurrentOwner(out var owner) || _owner != owner)
            return NativeResourceMutationResult.Invalid;
        if (Interlocked.CompareExchange(ref _deleting, 1, 0) != 0)
            return NativeResourceMutationResult.Invalid;

        var ownerStamp = userScope.CaptureStamp();
        _detail = _detail with { Phase = NativeResourceDetailPhase.Deleting, ErrorKey = null };
        var committed = false;
        try
        {
            var result = await store.DeleteAsync(owner, resource.Id, cancellationToken);
            if (result == NativeResourceMutationResult.Success)
            {
                committed = true;
                pendingWrites.MarkPending(owner);
            }
            if (!IsCurrentOwnerStamp(owner, ownerStamp))
            {
                Block();
                return NativeResourceMutationResult.NotFoundOrForbidden;
            }
            if (result != NativeResourceMutationResult.Success)
            {
                _detail = _detail with {
                    Phase = NativeResourceDetailPhase.Ready,
                    ShowDeleteConfirmation = false,
                    ErrorKey = result == NativeResourceMutationResult.Failed
                        ? "NativeResources_DeleteFailed" : MutationErrorKey(result)
                };
                return result;
            }

            _syncPhase = NativeResourceSyncPhase.Pending;
            _syncErrorKey = null;
            var rows = await store.ListAsync(owner, cancellationToken);
            if (!IsCurrentOwnerStamp(owner, ownerStamp))
            {
                Block();
                return NativeResourceMutationResult.NotFoundOrForbidden;
            }
            _all = rows;
            ReconcileFilters();
            ProjectList();
            _detail = new(NativeResourceDetailPhase.Idle,
                Sync: _syncPhase, SyncErrorKey: _syncErrorKey);
            return NativeResourceMutationResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentOwnerStamp(owner, ownerStamp))
                _detail = _detail with {
                    Phase = committed
                        ? NativeResourceDetailPhase.LoadError
                        : NativeResourceDetailPhase.Ready,
                    ShowDeleteConfirmation = false,
                    ErrorKey = committed
                        ? "NativeResources_SavedReloadError" : null,
                    Sync = _syncPhase
                };
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Native resource delete or reload failed ({FailureType}).",
                ex.GetType().Name);
            if (IsCurrentOwnerStamp(owner, ownerStamp))
                _detail = _detail with {
                    Phase = _syncPhase == NativeResourceSyncPhase.Pending
                        ? NativeResourceDetailPhase.LoadError
                        : NativeResourceDetailPhase.Ready,
                    ShowDeleteConfirmation = false,
                    ErrorKey = _syncPhase == NativeResourceSyncPhase.Pending
                        ? "NativeResources_SavedReloadError"
                        : "NativeResources_DeleteFailed"
                };
            return NativeResourceMutationResult.Failed;
        }
        finally
        {
            Volatile.Write(ref _deleting, 0);
        }
    }
}
