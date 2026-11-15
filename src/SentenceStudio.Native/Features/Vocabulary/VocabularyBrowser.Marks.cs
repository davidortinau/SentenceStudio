using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Features.Vocabulary;

/// <summary>
/// The learner's marks (docs/known-words-plan.md, Captain 2026-11-14): Mark Known, Practice
/// again and Start over, from the word editor and for many words chosen in the list's select
/// mode (source Vocabulary.razor bulk status and VocabularyWordEdit.razor status buttons). Both
/// go through the store's one owner-scoped save, so every client applies the same rule.
/// </summary>
internal partial class VocabularyBrowser
{
    private bool _selecting;
    private IReadOnlySet<string> _selectedIds = new HashSet<string>(StringComparer.Ordinal);
    private bool _marking;

    /// <summary>Start Over for the chosen words is asking to confirm.</summary>
    private bool _askingStartOver;

    /// <summary>The confirmation's message while it asks (source bulk delete asks the same way); null otherwise.</summary>
    internal string? StartOverSelectedMessage => _listOpen && _selecting && _askingStartOver
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
            _text("Vocabulary_StartOverConfirm"), _selectedIds.Count)
        : null;

    /// <summary>
    /// The status menu's choice. Mark Known and Practice Again apply at once; Start Over, which
    /// sets every chosen word's score to zero, asks first.
    /// </summary>
    internal Task RequestMarkSelectedAsync(VocabularyLearnerAction action)
    {
        if (!BulkEditMayStart())
            return Task.CompletedTask;
        if (action != VocabularyLearnerAction.StartOver)
            return MarkSelectedAsync(action);
        if (!_selecting || _marking || _selectedIds.Count == 0 || BulkPromptOpen || !TryOwner(out _))
            return Task.CompletedTask;
        _askingStartOver = true;
        Notify(VocabularyChanges.List);
        return Task.CompletedTask;
    }

    /// <summary>The confirmation's Start Over.</summary>
    internal Task ConfirmStartOverSelectedAsync()
    {
        if (!_askingStartOver)
            return Task.CompletedTask;
        _askingStartOver = false;
        return MarkSelectedAsync(VocabularyLearnerAction.StartOver);
    }

    /// <summary>Cancel (or the dialog dismissed) while it asks.</summary>
    internal void CancelStartOverSelected()
    {
        if (!_askingStartOver)
            return;
        _askingStartOver = false;
        Notify(VocabularyChanges.List);
    }

    private void ResetSelection()
    {
        _askingStartOver = false;
        _askingBulkDelete = false;
        _tagEntryOpen = false;
        _tagDraft = string.Empty;
        _selecting = false;
        _selectedIds = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Enters or leaves the list's select mode; leaving clears the choice.</summary>
    internal void SetSelecting(bool selecting)
    {
        if (!_listOpen || _marking || _selecting == selecting || !TryOwner(out _))
            return;
        ResetSelection();
        _selecting = selecting;
        Notify(VocabularyChanges.List);
    }

    /// <summary>A row tapped in select mode: chosen, or no longer chosen.</summary>
    internal void ToggleSelected(string wordId)
    {
        if (!_selecting || _marking || BulkPromptOpen || !TryOwner(out _)
            || !_catalog.Words.Any(word => word.Id == wordId))
            return;
        var next = new HashSet<string>(_selectedIds, StringComparer.Ordinal);
        if (!next.Remove(wordId))
            next.Add(wordId);
        _selectedIds = next;
        Notify(VocabularyChanges.List);
    }

    /// <summary>
    /// The chosen words as the head's own selection control reports them (SwiftUI List
    /// selection): only words in the list are kept.
    /// </summary>
    internal void SetSelected(IEnumerable<string> wordIds)
    {
        if (!_selecting || _marking || !TryOwner(out _))
            return;
        var known = _catalog.Words.Select(word => word.Id).ToHashSet(StringComparer.Ordinal);
        var next = wordIds.Where(known.Contains).ToHashSet(StringComparer.Ordinal);
        if (next.SetEquals(_selectedIds))
            return;
        _selectedIds = next;
        Notify(VocabularyChanges.List);
    }

    /// <summary>Source Select All: every word the filters show now.</summary>
    internal void SelectAllFiltered()
    {
        if (!_selecting || _marking || !TryOwner(out _) || _filters is null)
            return;
        _selectedIds = _filters.Filter(_catalog.Words, _sortField, _sortDescending)
            .Select(word => word.Id).ToHashSet(StringComparer.Ordinal);
        Notify(VocabularyChanges.List);
    }

    /// <summary>
    /// Applies the mark to every chosen word in one save, says what happened (source
    /// BulkUpdateSuccess or BulkUpdatePartial), leaves select mode and reloads the list.
    /// </summary>
    internal async Task MarkSelectedAsync(VocabularyLearnerAction action)
    {
        if (!_selecting || _marking || _saving || AnyDeleting || _handingOff
            || _selectedIds.Count == 0 || !TryOwner(out var owner))
            return;
        var ids = _selectedIds.ToArray();
        _marking = true;
        Notify(VocabularyChanges.List | VocabularyChanges.Form);
        var changed = false;
        try
        {
            var result = await OffUiAsync(() => _store.ApplyLearnerActionAsync(
                owner, ids, action, _lifetimeToken));
            if (_disposed || !TryOwner(out _))
                return;
            if (result.Changed == 0)
            {
                _feedback = _text(result.RefusalKey ?? "Vocabulary_MarkRefusedNotYours");
                _feedbackWarning = true;
                return;
            }
            changed = true;
            var missed = ids.Length - result.Changed;
            var feedback = missed == 0
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    _text("Vocabulary_BulkUpdateSuccess"), result.Changed, _text(StatusKey(action)))
                : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    _text("Vocabulary_BulkUpdatePartial"), result.Changed, missed);
            _feedback = result.SyncUnconfirmed
                ? $"{feedback} {_text("NativeResources_SyncUnconfirmed")}" : feedback;
            _feedbackWarning = result.SyncUnconfirmed || missed > 0;
            ResetSelection();
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native Vocabulary bulk mark failed.");
            _feedback = _text("NativeVocabulary_MarkFailed");
            _feedbackWarning = true;
        }
        finally
        {
            _marking = false;
            if (!_disposed)
                Notify(VocabularyChanges.List | VocabularyChanges.Form);
        }
        if (changed)
        {
            await ReloadAsync();
            // A word open beside the list shows its new status.
            await ((IVocabularyCleanupHost)this).RefreshOpenWordAsync();
        }
    }

    /// <summary>
    /// The editor's status action on the open word. The learner's draft stays; the word's status,
    /// progress and the list row are read again.
    /// </summary>
    internal async Task MarkOpenWordAsync(VocabularyLearnerAction action)
    {
        if (!_formOpen || _creating || _marking || _saving || AnyDeleting || _handingOff
            || _detail is null || _selectedWordId is not { } id || !TryOwner(out var owner))
            return;
        _marking = true;
        var hadError = _editErrorKey is not null;
        _editErrorKey = null;
        // Only the Learning Progress card changes, so the editor keeps its scroll position;
        // a message at the top of the page needs the whole page.
        Notify((hadError ? VocabularyChanges.Form : VocabularyChanges.Progress) | VocabularyChanges.List);
        var changed = false;
        var generation = Volatile.Read(ref _formGeneration);
        try
        {
            var result = await OffUiAsync(() => _store.ApplyLearnerActionAsync(
                owner, [id], action, _lifetimeToken));
            if (_disposed || !TryOwner(out _))
                return;
            if (result.Changed == 0)
                _editErrorKey = result.RefusalKey ?? "Vocabulary_MarkRefusedNotYours";
            else
            {
                changed = true;
                if (result.SyncUnconfirmed)
                    _editErrorKey = "NativeResources_SyncUnconfirmed";
                // The word's new status and progress; the learner's draft stays.
                var fresh = await OffUiAsync(() => _store.GetOwnedVocabularyAsync(owner, id, _lifetimeToken));
                if (_disposed || !TryOwner(out _))
                    return;
                if (fresh is not null && generation == Volatile.Read(ref _formGeneration)
                    && _formOpen && _selectedWordId == id)
                    _detail = fresh;
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native Vocabulary word mark failed.");
            _editErrorKey = "NativeVocabulary_MarkFailed";
        }
        finally
        {
            _marking = false;
            if (!_disposed)
                Notify((_editErrorKey is not null ? VocabularyChanges.Form : VocabularyChanges.Progress)
                    | VocabularyChanges.List);
        }
        if (changed)
            await ReloadAsync();
    }

    /// <summary>The resource key of the status an action leaves a word in ("Updated 3 word(s) to Known").</summary>
    internal static string StatusKey(VocabularyLearnerAction action) => action switch
    {
        VocabularyLearnerAction.MarkKnown => "Vocabulary_Known",
        VocabularyLearnerAction.PracticeAgain => "Vocabulary_Learning",
        _ => "Vocabulary_Unknown"
    };

    /// <summary>The resource key of an action's name.</summary>
    internal static string ActionKey(VocabularyLearnerAction action) => action switch
    {
        VocabularyLearnerAction.MarkKnown => "Vocabulary_MarkKnown",
        VocabularyLearnerAction.PracticeAgain => "Vocabulary_PracticeAgain",
        _ => "Vocabulary_StartOver"
    };
}
