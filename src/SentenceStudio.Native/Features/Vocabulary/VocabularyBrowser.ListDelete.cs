using Microsoft.Extensions.Logging;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Features.Vocabulary;

/// <summary>
/// The list's swipe Delete (Captain 2026-11-12: Library lists are native lists with swipe
/// Delete and Study). It asks first, like the editor's Delete (source showDeleteConfirm and its
/// message), then removes the word through the same owner-scoped store delete, so the server's
/// rules (a word others or Minimal Pairs answers use is kept) and sync apply unchanged.
/// </summary>
internal partial class VocabularyBrowser
{
    /// <summary>The word whose swipe Delete is asking to confirm (the dialog shows only for this).</summary>
    private NativeVocabularyEntry? _askingListDelete;

    /// <summary>A confirmed list delete is running (the dialog is already gone).</summary>
    private bool _listDeleting;

    /// <summary>A delete from the list or the editor is running.</summary>
    private bool AnyDeleting => _cleanup.Deleting || _listDeleting;

    /// <summary>The list's delete confirmation is asking (never while the delete runs).</summary>
    internal bool IsListDeletePresented => _listOpen && _askingListDelete is not null;

    /// <summary>Source deleteConfirmMessage for the swiped word; null when none is asked.</summary>
    internal string? ListDeleteMessage => IsListDeletePresented
        ? VocabularyWordCommands.DeleteMessage(_askingListDelete!.TargetTerm, _text) : null;

    /// <summary>The swiped word's id while its confirmation asks.</summary>
    internal string? PendingListDeleteId => IsListDeletePresented ? _askingListDelete!.Id : null;

    /// <summary>
    /// A row's swipe Delete: asks to confirm. Nothing else may be saving or deleting, and the
    /// editor's own confirmation must not be showing (one presentation at a time).
    /// </summary>
    internal void RequestListDelete(string wordId)
    {
        if (!_listOpen || _saving || AnyDeleting || _handingOff || _askingListDelete is not null
            || _cleanup.ConfirmingDelete || !TryOwner(out _)
            || _catalog.Words.FirstOrDefault(word => word.Id == wordId) is not { } word)
            return;
        _askingListDelete = word;
        Notify(VocabularyChanges.List);
    }

    /// <summary>
    /// A row's swipe Study: the resources the word belongs to, for Study's Choose My Own (it
    /// selects the ones it offers). Empty for a word in no resource or not in the list.
    /// </summary>
    internal IReadOnlyList<string> StudyResourceIds(string wordId) =>
        _listOpen && TryOwner(out _)
        && _catalog.Words.FirstOrDefault(word => word.Id == wordId) is { } word
            ? word.AssociatedResources.Select(resource => resource.Id).ToArray()
            : [];

    /// <summary>Cancel (or the dialog dismissed) while it asks.</summary>
    internal void CancelListDelete()
    {
        if (_askingListDelete is null)
            return;
        _askingListDelete = null;
        Notify(VocabularyChanges.List);
    }

    /// <summary>
    /// Deletes the confirmed word. The dialog goes at once (the delete is no longer asking).
    /// The list says what happened (source DeletedSuccess, with a warning when its sync could
    /// not be recorded, or why the delete was refused) and drops the row straight away. When the
    /// word was open in the editor beside the list, the editor leaves it, or, after a refusal,
    /// shows what the word allows now.
    /// </summary>
    internal async Task ConfirmListDeleteAsync()
    {
        if (_askingListDelete is not { } word)
            return;
        _askingListDelete = null;
        if (!_listOpen || _listDeleting || _saving || _cleanup.Deleting || _handingOff
            || !TryOwner(out var owner))
        {
            Notify(VocabularyChanges.List);
            return;
        }
        _listDeleting = true;
        // DeleteDialog too: an editor open beside the list holds its Save while this runs.
        Notify(VocabularyChanges.List | VocabularyChanges.DeleteDialog);
        var deleted = false;
        var refused = false;
        var leftWord = false;
        var openWord = _formOpen && !_creating
            && string.Equals(_selectedWordId, word.Id, StringComparison.Ordinal);
        try
        {
            var result = await OffUiAsync(() => _store.DeleteOwnedVocabularyAsync(
                owner, word.Id, _lifetimeToken));
            var committed = result is NativeVocabularyDeleteStatus.Deleted
                or NativeVocabularyDeleteStatus.DeletedSyncUnconfirmed;
            if (_disposed || !TryOwner(out _))
            {
                if (committed)
                    _logger.LogWarning(
                        "Native Vocabulary list delete committed ({Status}) after the active owner changed; the list is blocked.",
                        result);
                return;
            }
            if (committed)
            {
                deleted = true;
                var unconfirmed = result == NativeVocabularyDeleteStatus.DeletedSyncUnconfirmed;
                var feedback = _text("VocabWordEdit_DeletedSuccess");
                _feedback = unconfirmed
                    ? $"{feedback} {_text("NativeResources_SyncUnconfirmed")}" : feedback;
                _feedbackWarning = unconfirmed;
                // The row goes now, not after the reload: it can no longer be swiped.
                _catalog = _catalog with { Words = _catalog.Words
                    .Where(entry => entry.Id != word.Id).ToArray() };
                RememberWordCount(owner);
                if (openWord && _formOpen
                    && string.Equals(_selectedWordId, word.Id, StringComparison.Ordinal))
                {
                    _cleanup.ResetDelete();
                    LeaveWord();
                    leftWord = true;
                }
            }
            else
            {
                _feedback = VocabularyWordCommands.DeleteError(result, word.TargetTerm, _text);
                _feedbackWarning = true;
                refused = result is NativeVocabularyDeleteStatus.UsedByOthers
                    or NativeVocabularyDeleteStatus.UsedByAnswers;
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native Vocabulary list delete failed.");
            _feedback = VocabularyWordCommands.DeleteError(
                NativeVocabularyDeleteStatus.Failed, word.TargetTerm, _text);
            _feedbackWarning = true;
        }
        finally
        {
            _listDeleting = false;
            if (!_disposed)
                Notify(VocabularyChanges.List | VocabularyChanges.DeleteDialog
                    | (leftWord ? VocabularyChanges.Form : VocabularyChanges.None));
        }
        if (deleted)
        {
            await ReloadAsync();
            await _cleanup.RescanDuplicatesAsync();
        }
        else if (refused && openWord)
            // As the editor's Delete does after a refusal (review 4): its Delete block is now.
            await ((IVocabularyCleanupHost)this).RefreshOpenWordAsync();
    }
}
