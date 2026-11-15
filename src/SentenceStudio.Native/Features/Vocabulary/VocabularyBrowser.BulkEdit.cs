using Microsoft.Extensions.Logging;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Features.Vocabulary;

/// <summary>
/// The rest of the source's bulk edit for the words chosen in select mode (Vocabulary.razor:
/// Set Language, Add Tags, Delete; Set Status is in VocabularyBrowser.Marks.cs). Each goes through
/// the store's owner-scoped write, so only the learner's own words change.
/// </summary>
internal partial class VocabularyBrowser
{
    private bool _askingBulkDelete;
    private bool _tagEntryOpen;
    private string _tagDraft = string.Empty;

    /// <summary>A confirmation or the tag entry is open over the list.</summary>
    private bool BulkPromptOpen => _askingStartOver || _askingBulkDelete || _tagEntryOpen;

    /// <summary>Source bulkConfirmDeleteMessage while Delete asks; null otherwise.</summary>
    internal string? BulkDeleteMessage => _listOpen && _selecting && _askingBulkDelete
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
            _text("Vocabulary_BulkConfirmDeleteMessage"), _selectedIds.Count)
        : null;

    /// <summary>The tag entry is open (the head shows its field).</summary>
    internal bool IsTagEntryOpen => _listOpen && _selecting && _tagEntryOpen;

    /// <summary>The tags typed so far (comma-separated).</summary>
    internal string TagDraft => _tagDraft;

    /// <summary>The tag entry holds at least one tag.</summary>
    internal bool CanAddTags => IsTagEntryOpen && !_marking && ParseTags(_tagDraft).Count > 0;

    /// <summary>Source ParseTagInput: comma-separated, trimmed, no empty or repeated tags.</summary>
    internal static IReadOnlyList<string> ParseTags(string? input) =>
        (input ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private bool CanStartBulkEdit => _selecting && !_marking && !_saving && !AnyDeleting && !_handingOff
        && _selectedIds.Count > 0 && !BulkPromptOpen;

    /// <summary>Moves on when a bulk choice was refused, so a head mounts its menu anew (a picker keeps no stale choice).</summary>
    private int _bulkRefusals;

    /// <summary>
    /// A bulk choice made with no word chosen says so (the iOS menu's items cannot follow the
    /// selection: the scene keeps the mounted toolbar). True when the choice may go on.
    /// </summary>
    private bool BulkEditMayStart()
    {
        if (CanStartBulkEdit)
            return true;
        if (_selecting && !_marking && !BulkPromptOpen && _selectedIds.Count == 0 && TryOwner(out _))
        {
            _feedback = _text("NativeVocabulary_SelectFirst");
            _feedbackWarning = true;
            _bulkRefusals++;
            Notify(VocabularyChanges.List);
        }
        return false;
    }

    /// <summary>Source Set Language: every chosen word gets the language, in one save.</summary>
    internal Task SetSelectedLanguageAsync(string language) =>
        !BulkEditMayStart() || !TryOwner(out var owner) ? Task.CompletedTask
            : BulkEditSelectedAsync((ids, token) => _store.SetOwnedVocabularyLanguageAsync(owner, ids, language, token),
                changed => string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    _text("Vocabulary_BulkLanguageSuccess"), changed, language),
                everyWordChanges: true);

    /// <summary>Source Add Tags: opens the entry for the tags to add.</summary>
    internal void OpenTagEntry()
    {
        if (!BulkEditMayStart() || !TryOwner(out _))
            return;
        _tagEntryOpen = true;
        _tagDraft = string.Empty;
        Notify(VocabularyChanges.List);
    }

    /// <summary>Typed into the tag entry: the field already shows it, so nothing redraws.</summary>
    internal void SetTagDraft(string value)
    {
        if (_tagEntryOpen)
            _tagDraft = value ?? string.Empty;
    }

    internal void CancelTagEntry()
    {
        if (!_tagEntryOpen || _marking)
            return;
        _tagEntryOpen = false;
        _tagDraft = string.Empty;
        Notify(VocabularyChanges.List);
    }

    /// <summary>Adds the typed tags to every chosen word (their own tags stay), in one save.</summary>
    internal Task AddTagsToSelectedAsync()
    {
        var typed = _tagDraft;
        var tags = ParseTags(typed);
        if (!_tagEntryOpen || tags.Count == 0 || _marking || _saving || AnyDeleting || _handingOff
            || _selectedIds.Count == 0 || !TryOwner(out var owner))
            return Task.CompletedTask;
        _tagEntryOpen = false;
        _tagDraft = string.Empty;
        // A word that already holds every tag does not change, so a count of zero is not a
        // failure here. A save that threw opens the entry again with what was typed.
        return BulkEditSelectedAsync((ids, token) => _store.AddOwnedVocabularyTagsAsync(owner, ids, tags, token),
            changed => string.Format(System.Globalization.CultureInfo.CurrentCulture,
                _text("Vocabulary_BulkTagsSuccess"), changed),
            everyWordChanges: false,
            failed: () =>
            {
                _tagEntryOpen = true;
                _tagDraft = typed;
            });
    }

    /// <summary>One bulk save for the chosen words: says what happened, leaves select mode, reloads.</summary>
    /// <param name="everyWordChanges">
    /// The save changes every word it is given (Set Language), so fewer changed than chosen is a
    /// partial result and none is a failure.
    /// </param>
    /// <param name="failed">What to restore when the save threw.</param>
    private async Task BulkEditSelectedAsync(
        Func<string[], CancellationToken, Task<NativeVocabularyMarkResult>> save, Func<int, string> success,
        bool everyWordChanges, Action? failed = null)
    {
        var ids = _selectedIds.ToArray();
        // A word open beside the list with nothing typed takes the new values; one being edited
        // keeps its draft.
        var openClean = _formOpen && !_creating && !HasUnsavedEdits;
        _marking = true;
        Notify(VocabularyChanges.List | VocabularyChanges.Form);
        var changed = false;
        try
        {
            var result = await OffUiAsync(() => save(ids, _lifetimeToken));
            if (_disposed || !TryOwner(out _))
                return;
            if (result.Changed == 0 && everyWordChanges)
            {
                _feedback = _text("NativeVocabulary_BulkFailed");
                _feedbackWarning = true;
                return;
            }
            changed = result.Changed > 0;
            var missed = everyWordChanges ? ids.Length - result.Changed : 0;
            var said = missed > 0
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    _text("Vocabulary_BulkUpdatePartial"), result.Changed, missed)
                : success(result.Changed);
            _feedback = result.SyncUnconfirmed ? $"{said} {_text("NativeResources_SyncUnconfirmed")}" : said;
            _feedbackWarning = result.SyncUnconfirmed || missed > 0;
            ResetSelection();
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native Vocabulary bulk edit failed.");
            _feedback = _text("NativeVocabulary_BulkFailed");
            _feedbackWarning = true;
            failed?.Invoke();
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
            await RefreshOpenWordAfterBulkEditAsync(ids, openClean);
        }
    }

    /// <summary>
    /// The word open beside the list was one of the changed ones: it shows what it holds now.
    /// With nothing typed its fields take the new values; a draft being edited is kept, with the
    /// word's new save stamp so its Save is not refused as a conflict.
    /// </summary>
    private async Task RefreshOpenWordAfterBulkEditAsync(string[] ids, bool openClean)
    {
        if (!_formOpen || _creating || _selectedWordId is not { } id
            || !ids.Contains(id, StringComparer.Ordinal) || !TryOwner(out var owner))
            return;
        var generation = Volatile.Read(ref _formGeneration);
        try
        {
            var fresh = await OffUiAsync(() => _store.GetOwnedVocabularyAsync(owner, id, _lifetimeToken));
            if (fresh is null || generation != Volatile.Read(ref _formGeneration)
                || !_formOpen || _selectedWordId != id || !TryOwner(out _))
                return;
            _detail = fresh;
            _draft = openClean || _draft is null ? fresh.Draft
                : _draft with { OriginalUpdatedAt = fresh.Draft.OriginalUpdatedAt };
            Notify(VocabularyChanges.Form);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Native Vocabulary word refresh after a bulk edit failed.");
        }
    }

    /// <summary>Source Delete: asks first, naming how many words.</summary>
    internal void RequestBulkDelete()
    {
        if (!BulkEditMayStart() || !TryOwner(out _))
            return;
        _askingBulkDelete = true;
        Notify(VocabularyChanges.List);
    }

    internal void CancelBulkDelete()
    {
        if (!_askingBulkDelete)
            return;
        _askingBulkDelete = false;
        Notify(VocabularyChanges.List);
    }

    /// <summary>
    /// Deletes each chosen word through the store's owner-scoped delete (source
    /// ConfirmBulkDelete): a word other data or Minimal Pairs answers still use stays, and the
    /// list says how many were deleted and how many stayed, and why.
    /// </summary>
    internal async Task ConfirmBulkDeleteAsync()
    {
        if (!_askingBulkDelete)
            return;
        _askingBulkDelete = false;
        if (!_selecting || _marking || _saving || AnyDeleting || _handingOff || _selectedIds.Count == 0
            || !TryOwner(out var owner))
        {
            Notify(VocabularyChanges.List);
            return;
        }
        var ids = _selectedIds.ToArray();
        _marking = true;
        Notify(VocabularyChanges.List | VocabularyChanges.Form);
        int deleted = 0, shared = 0, answers = 0, notFound = 0, failed = 0;
        var gone = new HashSet<string>(StringComparer.Ordinal);
        var unconfirmed = false;
        try
        {
            foreach (var id in ids)
            {
                NativeVocabularyDeleteStatus status;
                try
                {
                    status = await OffUiAsync(() => _store.DeleteOwnedVocabularyAsync(owner, id, _lifetimeToken));
                }
                catch (OperationCanceledException) when (_disposed)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Native Vocabulary bulk delete failed for one word.");
                    status = NativeVocabularyDeleteStatus.Failed;
                }
                if (_disposed || !TryOwner(out _))
                    return;
                switch (status)
                {
                    case NativeVocabularyDeleteStatus.Deleted: deleted++; gone.Add(id); break;
                    case NativeVocabularyDeleteStatus.DeletedSyncUnconfirmed:
                        deleted++; gone.Add(id); unconfirmed = true; break;
                    case NativeVocabularyDeleteStatus.UsedByOthers: shared++; break;
                    case NativeVocabularyDeleteStatus.UsedByAnswers: answers++; break;
                    case NativeVocabularyDeleteStatus.NotFoundOrForbidden: notFound++; break;
                    default: failed++; break;
                }
            }
            var parts = new List<string>();
            void Say(string key, int count)
            {
                if (count > 0)
                    parts.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture, _text(key), count));
            }
            Say("Vocabulary_BulkDeleteSuccess", deleted);
            Say("Vocabulary_BulkDeleteKept", shared);
            Say("Vocabulary_BulkDeleteKeptAnswers", answers);
            Say("Vocabulary_BulkDeleteNotFound", notFound);
            if (failed > 0)
                parts.Add(_text("Vocabulary_BulkDeleteFailedGeneric"));
            if (unconfirmed)
                parts.Add(_text("NativeResources_SyncUnconfirmed"));
            _feedback = string.Join(" ", parts);
            _feedbackWarning = shared + answers + notFound + failed > 0 || unconfirmed;
            // A deleted word open beside the list: the editor leaves it.
            if (_formOpen && !_creating && _selectedWordId is { } open && gone.Contains(open))
            {
                _cleanup.ResetDelete();
                LeaveWord();
            }
            // A failure keeps the words still there chosen, so the learner can try again.
            if (failed == 0)
                ResetSelection();
            else
                _selectedIds = _selectedIds.Where(id => !gone.Contains(id)).ToHashSet(StringComparer.Ordinal);
        }
        finally
        {
            _marking = false;
            if (!_disposed)
                Notify(VocabularyChanges.List | VocabularyChanges.Form);
        }
        if (deleted > 0 || notFound > 0)
        {
            await ReloadAsync();
            await _cleanup.RescanDuplicatesAsync();
        }
    }
}
