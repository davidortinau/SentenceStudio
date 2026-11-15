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

    /// <summary>Source Set Language: every chosen word gets the language, in one save.</summary>
    internal Task SetSelectedLanguageAsync(string language) =>
        !CanStartBulkEdit || !TryOwner(out var owner) ? Task.CompletedTask
            : BulkEditSelectedAsync((ids, token) => _store.SetOwnedVocabularyLanguageAsync(owner, ids, language, token),
                changed => string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    _text("Vocabulary_BulkLanguageSuccess"), changed, language));

    /// <summary>Source Add Tags: opens the entry for the tags to add.</summary>
    internal void OpenTagEntry()
    {
        if (!CanStartBulkEdit || !TryOwner(out _))
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
        var tags = ParseTags(_tagDraft);
        if (!_tagEntryOpen || tags.Count == 0 || _marking || !TryOwner(out var owner))
            return Task.CompletedTask;
        _tagEntryOpen = false;
        _tagDraft = string.Empty;
        return BulkEditSelectedAsync((ids, token) => _store.AddOwnedVocabularyTagsAsync(owner, ids, tags, token),
            changed => string.Format(System.Globalization.CultureInfo.CurrentCulture,
                _text("Vocabulary_BulkTagsSuccess"), changed));
    }

    /// <summary>One bulk save for the chosen words: says what happened, leaves select mode, reloads.</summary>
    private async Task BulkEditSelectedAsync(
        Func<string[], CancellationToken, Task<NativeVocabularyMarkResult>> save, Func<int, string> success)
    {
        var ids = _selectedIds.ToArray();
        _marking = true;
        Notify(VocabularyChanges.List | VocabularyChanges.Form);
        var changed = false;
        try
        {
            var result = await OffUiAsync(() => save(ids, _lifetimeToken));
            if (_disposed || !TryOwner(out _))
                return;
            if (result.Changed == 0)
            {
                _feedback = _text("NativeVocabulary_BulkFailed");
                _feedbackWarning = true;
                return;
            }
            changed = true;
            _feedback = result.SyncUnconfirmed
                ? $"{success(result.Changed)} {_text("NativeResources_SyncUnconfirmed")}" : success(result.Changed);
            _feedbackWarning = result.SyncUnconfirmed;
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
            // A word open beside the list shows what it holds now (its draft stays).
            await ((IVocabularyCleanupHost)this).RefreshOpenWordAsync();
        }
    }

    /// <summary>Source Delete: asks first, naming how many words.</summary>
    internal void RequestBulkDelete()
    {
        if (!CanStartBulkEdit || !TryOwner(out _))
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
            // A failure keeps the choice, so the learner can try again.
            if (failed == 0)
                ResetSelection();
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
