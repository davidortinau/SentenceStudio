using System.Globalization;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Native.Features.Settings;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Features.Vocabulary;

/// <summary>
/// The open duplicate manager (source Vocabulary.razor cleanup panel with showDuplicatePanel):
/// what it shows after Find Duplicates, focused on the word the editor came from.
/// </summary>
internal sealed record VocabularyDuplicatePanel(
    bool Running, string Title, string Status,
    string? FocusLabel, string? FocusNormalizedTerm, string? FocusedWordId,
    IReadOnlyList<NativeVocabularyDuplicateGroup> Groups,
    int TotalGroupCount, int ExtraRecordCount,
    IReadOnlyList<string> Results)
{
    internal int RemainingGroupCount => Math.Max(0, TotalGroupCount - Groups.Count);

    internal bool IsFocused(NativeVocabularyDuplicateGroup group) =>
        !string.IsNullOrWhiteSpace(FocusNormalizedTerm)
        && group.NormalizedTerm == FocusNormalizedTerm;
}

/// <summary>What a merge did, for the list feedback (source toasts).</summary>
internal sealed record VocabularyDuplicateMergeOutcome(
    bool Changed, string Feedback, bool Warning);

/// <summary>
/// What the word-cleanup workflow needs from the <see cref="VocabularyBrowser"/> that hosts it
/// (both heads; iOS through its VocabularyCoordinator). The browser keeps the list and editor
/// state; the workflow decides the rest.
/// </summary>
internal interface IVocabularyCleanupHost
{
    /// <summary>
    /// The active owner while the list is open. False when the list was closed (nothing to do,
    /// nothing to report) or the owner changed (the host has blocked the view itself).
    /// </summary>
    bool TryOpenOwner(out string owner);

    bool IsFormOpen { get; }
    bool IsSaving { get; }
    NativeVocabularyDetail? OpenDetail { get; }
    NativeVocabularyEditDraft? OpenDraft { get; }

    /// <summary>Cancelled when the host is disposed; still readable after that.</summary>
    CancellationToken Lifetime { get; }

    /// <summary>The host was disposed: work cut short by that ends quietly.</summary>
    bool IsDisposed { get; }

    /// <summary>Leaves the editor and discards its draft (the source navigates away). No publish.</summary>
    void LeaveWord();

    /// <summary>The list's feedback line (the source's toast).</summary>
    void ShowListFeedback(string message, bool warning);

    Task ReloadListAsync();

    /// <summary>
    /// Tells the UI what changed. <paramref name="deleteDialog"/> is the delete confirmation alone,
    /// which the iOS scene updates in place without rebuilding the editor.
    /// </summary>
    void PublishChanges(bool list = false, bool form = false, bool deleteDialog = false);

    /// <summary>
    /// Runs store work off the UI thread and resumes on it: SQLite work is synchronous under
    /// its async API and would otherwise freeze the screen.
    /// </summary>
    Task<T> OffUiAsync<T>(Func<Task<T>> work);
}

/// <summary>
/// The word editor's Find Duplicates, the duplicate manager it opens, and Delete (source
/// VocabularyWordEdit OpenDuplicateManager, RequestDelete and ConfirmDelete; Vocabulary.razor
/// OpenDuplicateManagerAsync, ShowMoreDuplicates and MergeDuplicateGroup). One copy of the
/// workflow for both heads; each head keeps only its bindings.
/// </summary>
internal sealed class VocabularyWordCleanup
{
    private readonly INativeResourceStore _store;
    private readonly ILogger _logger;
    private readonly Func<string, string> _text;
    private readonly IVocabularyCleanupHost _host;
    private readonly VocabularyDuplicateFinder _duplicates;
    private bool _confirmingDelete;
    private bool _deleting;
    private string? _deleteError;

    internal VocabularyWordCleanup(INativeResourceStore store, ILogger logger,
        Func<string, string> text, IVocabularyCleanupHost host)
    {
        _store = store;
        _logger = logger;
        _text = text;
        _host = host;
        _duplicates = new VocabularyDuplicateFinder(store, logger, text, host);
    }

    internal VocabularyDuplicatePanel? Duplicates => _duplicates.Panel;
    internal bool ConfirmingDelete => _confirmingDelete;
    internal bool Deleting => _deleting;
    internal string? DeleteError => _deleteError;

    internal bool IsDeleteConfirmationPresented =>
        _host.IsFormOpen && (_confirmingDelete || _deleting);

    /// <summary>Source deleteConfirmMessage for the loaded word; null until it loads.</summary>
    internal string? DeleteMessage => _host.IsFormOpen && _host.OpenDetail is { } detail
        ? VocabularyWordCommands.DeleteMessage(detail, _text) : null;

    /// <summary>
    /// Source OpenDuplicateManager: leaves the editor (its draft is discarded, as the source
    /// navigates away) for the list's duplicate manager, focused on the draft's term and this word.
    /// </summary>
    internal async Task FindDuplicatesAsync()
    {
        var draft = _host.OpenDraft;
        if (!_host.IsFormOpen
            || !VocabularyWordCommands.CanFindDuplicates(_host.IsSaving || _deleting, draft)
            || string.IsNullOrWhiteSpace(draft!.TargetTerm)
            || !_host.TryOpenOwner(out var owner))
            return;
        var term = draft.TargetTerm.Trim();
        var wordId = draft.Id;
        ResetDelete();
        _host.LeaveWord();
        _host.PublishChanges(list: true, form: true);
        await _duplicates.OpenAsync(owner, term, wordId, _host.Lifetime);
    }

    /// <summary>Source ShowMoreDuplicates.</summary>
    internal Task ShowMoreDuplicatesAsync() =>
        _host.TryOpenOwner(out var owner)
            ? _duplicates.ShowMoreAsync(owner, _host.Lifetime)
            : Task.CompletedTask;

    /// <summary>Source MergeDuplicateGroup, then the list reloads (source LoadData).</summary>
    internal async Task MergeDuplicatesAsync(string normalizedTerm)
    {
        if (_host.IsFormOpen || !_host.TryOpenOwner(out var owner))
            return;
        var outcome = await _duplicates.MergeAsync(owner, normalizedTerm, _host.Lifetime);
        if (outcome is null || !_host.TryOpenOwner(out _))
            return;
        _host.ShowListFeedback(outcome.Feedback, outcome.Warning);
        if (outcome.Changed)
            await _host.ReloadListAsync();
        else
            _host.PublishChanges(list: true);
    }

    /// <summary>Source CloseCleanupPanel.</summary>
    internal void CloseDuplicates()
    {
        if (_host.TryOpenOwner(out _))
            _duplicates.Close();
    }

    /// <summary>
    /// Source: returning to the list after a save or delete reopens the manager, so it scans
    /// again. Call after the save or delete has finished, not while it holds the editor.
    /// </summary>
    internal async Task RescanDuplicatesAsync()
    {
        if (_duplicates.IsOpen && _host.TryOpenOwner(out var owner))
            await _duplicates.RescanAsync(owner, _host.Lifetime);
    }

    /// <summary>Source RequestDelete: shows the confirmation.</summary>
    internal void RequestDelete()
    {
        if (!_host.IsFormOpen || !VocabularyWordCommands.CanDelete(
                _host.IsSaving || _deleting, _host.OpenDetail)
            || !_host.TryOpenOwner(out _))
            return;
        _confirmingDelete = true;
        _deleteError = null;
        _host.PublishChanges(deleteDialog: true);
    }

    /// <summary>Source CancelDelete (Cancel, Close or the backdrop); ignored while deleting.</summary>
    internal void CancelDelete()
    {
        if (_deleting || !_confirmingDelete)
            return;
        _confirmingDelete = false;
        _host.PublishChanges(deleteDialog: true);
    }

    /// <summary>
    /// Source ConfirmDelete: deletes the word, then returns to the list with the source's
    /// success message (with a warning when its sync could not be recorded). A refused or
    /// failed delete keeps the editor open and says why.
    /// </summary>
    internal async Task ConfirmDeleteAsync()
    {
        if (!_host.IsFormOpen || !_confirmingDelete || _deleting || _host.IsSaving
            || _host.OpenDetail is not { } detail || !_host.TryOpenOwner(out var owner))
        {
            // The confirmation may already show its pending state; take it down.
            if (_confirmingDelete && !_deleting)
            {
                _confirmingDelete = false;
                _host.PublishChanges(deleteDialog: true);
            }
            return;
        }
        _deleting = true;
        _deleteError = null;
        // The confirmation shows its pending state; the editor under it stays as it is.
        _host.PublishChanges(deleteDialog: true);
        var deleted = false;
        try
        {
            var result = await _host.OffUiAsync(() => _store.DeleteOwnedVocabularyAsync(
                owner, detail.Word.Id, _host.Lifetime));
            if (result is NativeVocabularyDeleteStatus.Deleted
                or NativeVocabularyDeleteStatus.DeletedSyncUnconfirmed)
            {
                deleted = true;
                var unconfirmed = result == NativeVocabularyDeleteStatus.DeletedSyncUnconfirmed;
                var feedback = _text("VocabWordEdit_DeletedSuccess");
                _host.ShowListFeedback(unconfirmed
                    ? $"{feedback} {_text("NativeResources_SyncUnconfirmed")}" : feedback,
                    unconfirmed);
                _host.LeaveWord();
            }
            else if (_host.TryOpenOwner(out _))
                _deleteError = VocabularyWordCommands.DeleteError(result, detail, _text);
        }
        catch (OperationCanceledException) when (_host.IsDisposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native Vocabulary delete failed.");
            _deleteError = VocabularyWordCommands.DeleteError(
                NativeVocabularyDeleteStatus.Failed, detail, _text);
        }
        finally
        {
            _deleting = false;
            _confirmingDelete = false;
            _host.PublishChanges(list: true, form: true, deleteDialog: true);
        }
        if (deleted)
        {
            await _host.ReloadListAsync();
            await RescanDuplicatesAsync();
        }
    }

    /// <summary>A newly opened or closed word starts with no confirmation or delete error.</summary>
    internal void ResetDelete()
    {
        _confirmingDelete = false;
        _deleteError = null;
    }

    /// <summary>The list closed or its owner changed: everything goes, without publishing.</summary>
    internal void Reset()
    {
        _duplicates.Reset();
        ResetDelete();
    }
}

/// <summary>
/// Platform-neutral Find Duplicates scan state (source OpenDuplicateManagerAsync,
/// LoadDuplicateGroupsAsync, ShowMoreDuplicates and MergeDuplicateGroup) over the owner-scoped
/// store. Used through <see cref="VocabularyWordCleanup"/>.
/// </summary>
internal sealed class VocabularyDuplicateFinder
{
    /// <summary>Source duplicatePageSize.</summary>
    internal const int PageSize = 20;

    private readonly INativeResourceStore _store;
    private readonly ILogger _logger;
    private readonly Func<string, string> _text;
    private readonly IVocabularyCleanupHost _host;
    private VocabularyDuplicatePanel? _panel;
    private long _generation;
    private bool _rescanPending;

    internal VocabularyDuplicateFinder(
        INativeResourceStore store, ILogger logger,
        Func<string, string> text, IVocabularyCleanupHost host)
    {
        _store = store;
        _logger = logger;
        _text = text;
        _host = host;
    }

    internal VocabularyDuplicatePanel? Panel => _panel;
    internal bool IsOpen => _panel is not null;

    /// <summary>Source OpenDuplicateManagerAsync(focusTerm, focusWordId).</summary>
    internal async Task OpenAsync(string owner, string? focusTerm, string? focusWordId,
        CancellationToken cancellationToken)
    {
        var label = string.IsNullOrWhiteSpace(focusTerm) ? null : focusTerm.Trim();
        _panel = new VocabularyDuplicatePanel(true,
            label is null ? _text("Vocabulary_DuplicatesTitle")
                : Format("Vocabulary_DuplicatesFor", label),
            label is null ? _text("Vocabulary_DuplicatesScanning")
                : Format("Vocabulary_DuplicatesScanningForTerm", label),
            label, VocabularyDuplicatePolicy.NormalizeTargetTerm(focusTerm),
            string.IsNullOrWhiteSpace(focusWordId) ? null : focusWordId,
            [], 0, 0, []);
        Changed();
        await LoadAsync(owner, append: false, results: [], cancellationToken);
    }

    /// <summary>Source ShowMoreDuplicates.</summary>
    internal Task ShowMoreAsync(string owner, CancellationToken cancellationToken)
    {
        if (_panel is not { Running: false } panel || panel.RemainingGroupCount == 0)
            return Task.CompletedTask;
        _panel = panel with { Running = true };
        Changed();
        return LoadAsync(owner, append: true, panel.Results, cancellationToken);
    }

    /// <summary>
    /// Scans again from the first page, keeping the results already reported. While a scan or
    /// merge runs, the rescan waits for it and runs after, so the merge's result is kept.
    /// </summary>
    internal Task RescanAsync(string owner, CancellationToken cancellationToken)
    {
        if (_panel is not { } panel)
            return Task.CompletedTask;
        if (panel.Running)
        {
            _rescanPending = true;
            return Task.CompletedTask;
        }
        _panel = panel with { Running = true };
        Changed();
        return LoadAsync(owner, append: false, panel.Results, cancellationToken);
    }

    /// <summary>
    /// Source MergeDuplicateGroup: keeps the recommended copy (first by merge preference) and
    /// folds the others into it, then scans again. Null when the group cannot merge or a scan
    /// or merge is already running.
    /// </summary>
    internal async Task<VocabularyDuplicateMergeOutcome?> MergeAsync(
        string owner, string normalizedTerm, CancellationToken cancellationToken)
    {
        if (_panel is not { Running: false } panel
            || panel.Groups.FirstOrDefault(group => group.NormalizedTerm == normalizedTerm)
                is not { } group)
            return null;
        if (!group.CanMergeAutomatically || group.Words.Count < 2)
            return new(false, BlockedReason(group, _text), Warning: true);

        var generation = Volatile.Read(ref _generation);
        _panel = panel with {
            Running = true,
            Status = Format("Vocabulary_MergingGroup", group.NormalizedTerm)
        };
        Changed();
        var results = panel.Results.ToList();
        VocabularyDuplicateMergeOutcome outcome;
        try
        {
            var keeper = group.Words[0];
            var duplicates = group.Words.Skip(1).Select(word => word.Id).ToArray();
            var merge = await _host.OffUiAsync(() => _store.MergeOwnedVocabularyDuplicatesAsync(
                owner, keeper.Id, duplicates, cancellationToken));
            results.Add(Format("Vocabulary_DuplicatesMergedCount", group.DisplayTerm, merge.Removed));
            // Source: removed copies, else "no merge was needed". Links and history can move
            // while every copy is kept (other data still uses it); that is a change, not a no-op.
            var feedback = merge.Removed > 0
                ? Format("Vocabulary_DuplicatesMergedSuccess", merge.Removed, group.DisplayTerm)
                : merge.Changed
                    ? Format("Vocabulary_DuplicatesMergedCount", group.DisplayTerm, 0)
                    : Format("Vocabulary_DuplicatesNoMergeNeeded", group.DisplayTerm);
            outcome = merge.SyncUnconfirmed
                ? new(true, $"{feedback} {_text("NativeResources_SyncUnconfirmed")}", Warning: true)
                : new(merge.Changed, feedback, Warning: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native duplicate merge failed.");
            var message = Format("Vocabulary_DuplicatesMergeError", group.DisplayTerm, ex.Message);
            results.Add(message);
            outcome = new(false, message, Warning: true);
        }
        if (generation != Volatile.Read(ref _generation) || _panel is null)
            return outcome;
        await LoadAsync(owner, append: false, results, cancellationToken);
        return outcome;
    }

    /// <summary>Source CloseCleanupPanel.</summary>
    internal void Close()
    {
        if (_panel is null)
            return;
        Reset();
        Changed();
    }

    /// <summary>Drops the panel without publishing (owner change, list closed).</summary>
    internal void Reset()
    {
        Interlocked.Increment(ref _generation);
        _rescanPending = false;
        _panel = null;
    }

    private async Task LoadAsync(string owner, bool append, IReadOnlyList<string> results,
        CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);
        // A full scan sees everything committed before it starts; only a rescan asked for
        // while it runs needs another.
        if (!append)
            _rescanPending = false;
        var label = _panel?.FocusLabel;
        var skip = append ? _panel?.Groups.Count ?? 0 : 0;
        try
        {
            var scan = await _host.OffUiAsync(() => _store.FindOwnedVocabularyDuplicatesAsync(
                owner, label, skip, PageSize, cancellationToken));
            if (generation != Volatile.Read(ref _generation) || _panel is not { } panel)
                return;
            if (!scan.OwnerValid)
            {
                _panel = null;
                return;
            }
            var groups = append ? panel.Groups.Concat(scan.Groups).ToArray() : scan.Groups;
            _panel = panel with {
                Running = false,
                Groups = groups,
                TotalGroupCount = scan.TotalGroupCount,
                ExtraRecordCount = scan.ExtraRecordCount,
                Results = results,
                Status = scan.TotalGroupCount > 0
                    ? $"{Format("Vocabulary_DuplicatesFoundCount", scan.TotalGroupCount, scan.ExtraRecordCount)} "
                        + Format("NativeVocabulary_DuplicatesShowing", groups.Count)
                    : label is null ? _text("Vocabulary_DuplicatesNone")
                        : Format("Vocabulary_DuplicatesNoneForTerm", label)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Native duplicate scan failed.");
            if (generation == Volatile.Read(ref _generation) && _panel is { } failed)
                _panel = failed with {
                    Running = false,
                    Results = results,
                    // The scan is over: the status says why, not "Scanning..." or "Merging...".
                    Status = Format("Vocabulary_CleanupError", ex.Message)
                };
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation))
                Changed();
        }
        if (_rescanPending && generation == Volatile.Read(ref _generation)
            && _panel is { Running: false } done)
        {
            _panel = done with { Running = true };
            Changed();
            await LoadAsync(owner, append: false, done.Results, cancellationToken);
        }
    }

    private void Changed() => _host.PublishChanges(list: true);

    /// <summary>Source GetMemoryAidSummary.</summary>
    internal static string MemoryAidSummary(
        NativeVocabularyDuplicateWord word, Func<string, string> text)
    {
        var aids = new List<string>();
        if (!string.IsNullOrWhiteSpace(word.MnemonicText))
        {
            var trimmed = word.MnemonicText.Trim();
            aids.Add(string.Format(CultureInfo.CurrentCulture,
                text("NativeVocabulary_DuplicatesMemoryAid"),
                trimmed.Length <= 120 ? trimmed : $"{trimmed[..120]}..."));
        }
        if (word.HasImageAid)
            aids.Add(text("NativeVocabulary_DuplicatesImageAid"));
        if (word.HasAudio)
            aids.Add(text("NativeVocabulary_DuplicatesAudio"));
        return aids.Count > 0 ? string.Join(" | ", aids)
            : text("NativeVocabulary_DuplicatesNoMemoryAids");
    }

    /// <summary>
    /// Why a group cannot merge: a copy not recorded to the learner first (the device refuses
    /// it, see <see cref="NativeVocabularyDuplicateGroup.HasAnotherOwnersCopy"/>), then the
    /// source's reason.
    /// </summary>
    internal static string BlockedReason(NativeVocabularyDuplicateGroup group,
        Func<string, string> text) =>
        group.HasAnotherOwnersCopy ? text("NativeVocabulary_DuplicatesAnotherOwner")
            : group.MergeBlockedReason ?? text("NativeVocabulary_DuplicatesBlockedFallback");

    /// <summary>Source focus note (the resource carries the web's &lt;strong&gt; markup).</summary>
    internal static string FocusNote(string label, Func<string, string> text) =>
        string.Format(CultureInfo.CurrentCulture,
            text("Vocabulary_DuplicatesFocused")
                .Replace("<strong>", string.Empty, StringComparison.Ordinal)
                .Replace("</strong>", string.Empty, StringComparison.Ordinal),
            label);

    /// <summary>Source GetEncodingLabel keys.</summary>
    internal static string EncodingKey(string label) => label switch
    {
        "Strong" => "Vocabulary_Strong",
        "Good" => "Vocabulary_Good",
        _ => "Vocabulary_Basic"
    };

    private string Format(string key, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, _text(key), values);
}

/// <summary>Source GetEncodingBadgeClass: Strong is success, Good warning, Basic secondary.</summary>
internal enum VocabularyEncodingTone
{
    Basic, Good, Strong
}

/// <summary>
/// The duplicate manager's encoding badge colours for both heads (source
/// GetEncodingBadgeClass: "bg-{tone} bg-opacity-25 text-{tone}", with Strong success, Good
/// warning and Basic the theme's secondary and secondary text). The tone's text colour moves
/// toward the body text only as far as it needs to read at 4.5:1 on its tint; the label names
/// the strength as well.
/// </summary>
internal static class VocabularyEncodingBadge
{
    /// <summary><c>--bs-success</c> and <c>--bs-warning</c> (app.css), the heads' Success and Warning roles.</summary>
    internal const string SuccessHex = "#12B886";
    internal const string WarningHex = "#FFC233";
    internal const double MinimumContrast = 4.5;

    /// <summary>Fill and text, as hex, for a badge on <paramref name="surfaceHex"/>.</summary>
    internal static (string Fill, string Text) Colors(VocabularyEncodingTone tone,
        NativeThemeColors theme, string surfaceHex)
    {
        var toneHex = tone switch
        {
            VocabularyEncodingTone.Strong => SuccessHex,
            VocabularyEncodingTone.Good => WarningHex,
            _ => theme.Secondary
        };
        var fill = NativeThemeColorTable.Blend(toneHex, surfaceHex, 0.25);
        var preferred = tone == VocabularyEncodingTone.Basic ? theme.SecondaryText : toneHex;
        return (fill, Readable(preferred, theme.Text, fill));
    }

    /// <summary>
    /// <paramref name="preferred"/> mixed toward <paramref name="bodyText"/> in tenths until it
    /// reads at <see cref="MinimumContrast"/> on <paramref name="fill"/>; black or white if
    /// even the body text does not.
    /// </summary>
    internal static string Readable(string preferred, string bodyText, string fill)
    {
        var background = SrgbColor.Parse(fill);
        for (var step = 0; step <= 10; step++)
        {
            var candidate = NativeThemeColorTable.Blend(bodyText, preferred, step / 10.0);
            if (SrgbColor.Parse(candidate).ContrastRatio(background) >= MinimumContrast)
                return candidate;
        }
        return SrgbColor.Parse("#000000").ContrastRatio(background)
            >= SrgbColor.Parse("#FFFFFF").ContrastRatio(background) ? "#000000" : "#FFFFFF";
    }
}

/// <summary>
/// The editor's Find Duplicates and Delete rules and messages (source VocabularyWordEdit
/// OpenDuplicateManager, RequestDelete and ConfirmDelete), shared by both heads.
/// </summary>
internal static class VocabularyWordCommands
{
    /// <summary>
    /// Source: disabled only while saving; with a blank term the action does nothing
    /// (OpenDuplicateManager returns), so the enabled state never lags typing.
    /// </summary>
    internal static bool CanFindDuplicates(bool busy, NativeVocabularyEditDraft? draft) =>
        !busy && draft is not null;

    /// <summary>
    /// Source Delete is disabled while saving. A word shared with another owner cannot be
    /// deleted here (the server would keep it, and its links belong to someone else).
    /// </summary>
    internal static bool CanDelete(bool busy, NativeVocabularyDetail? detail) =>
        !busy && detail is { IsSharedWithAnotherOwner: false };

    /// <summary>Source deleteConfirmMessage, with the saved term.</summary>
    internal static string DeleteMessage(NativeVocabularyDetail detail, Func<string, string> text) =>
        string.Format(CultureInfo.CurrentCulture, text("VocabWordEdit_DeleteMessage"),
            detail.Word.TargetTerm);

    /// <summary>The message for a refused or failed delete.</summary>
    internal static string DeleteError(NativeVocabularyDeleteStatus status,
        NativeVocabularyDetail detail, Func<string, string> text) => status switch
    {
        NativeVocabularyDeleteStatus.SharedReadOnly => text("NativeVocabulary_SharedReadOnly"),
        NativeVocabularyDeleteStatus.NotFoundOrForbidden => text("VocabWordEdit_NotFound"),
        _ => string.Format(CultureInfo.CurrentCulture, text("Common_DeleteFailed"),
            detail.Word.TargetTerm)
    };

    /// <summary>Source GetEncodingBadgeClass tone for an encoding label.</summary>
    internal static VocabularyEncodingTone EncodingTone(string label) => label switch
    {
        "Strong" => VocabularyEncodingTone.Strong,
        "Good" => VocabularyEncodingTone.Good,
        _ => VocabularyEncodingTone.Basic
    };
}
