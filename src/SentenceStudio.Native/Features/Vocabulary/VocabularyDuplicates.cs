using System.Globalization;
using Microsoft.Extensions.Logging;
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
/// Platform-neutral Find Duplicates workflow (source OpenDuplicateManagerAsync,
/// LoadDuplicateGroupsAsync, ShowMoreDuplicates and MergeDuplicateGroup) over the owner-scoped
/// store, shared by the iOS VocabularyCoordinator and the Android VocabularyBrowser. The caller
/// checks its owner before each call and reloads its catalog after a merge.
/// </summary>
internal sealed class VocabularyDuplicateFinder
{
    /// <summary>Source duplicatePageSize.</summary>
    internal const int PageSize = 20;

    private readonly INativeResourceStore _store;
    private readonly ILogger _logger;
    private readonly Func<string, string> _text;
    private readonly Action _changed;
    private VocabularyDuplicatePanel? _panel;
    private long _generation;

    internal VocabularyDuplicateFinder(
        INativeResourceStore store, ILogger logger,
        Func<string, string> text, Action changed)
    {
        _store = store;
        _logger = logger;
        _text = text;
        _changed = changed;
    }

    internal VocabularyDuplicatePanel? Panel => _panel;
    internal bool IsOpen => _panel is not null;
    internal bool IsRunning => _panel?.Running == true;

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
        _changed();
        await LoadAsync(owner, append: false, results: [], cancellationToken);
    }

    /// <summary>Source ShowMoreDuplicates.</summary>
    internal Task ShowMoreAsync(string owner, CancellationToken cancellationToken)
    {
        if (_panel is not { Running: false } panel || panel.RemainingGroupCount == 0)
            return Task.CompletedTask;
        _panel = panel with { Running = true };
        _changed();
        return LoadAsync(owner, append: true, panel.Results, cancellationToken);
    }

    /// <summary>
    /// Scans again from the first page (source: the list reopens the manager on return from
    /// the editor and after each merge). Keeps the results already reported.
    /// </summary>
    internal Task RescanAsync(string owner, CancellationToken cancellationToken)
    {
        if (_panel is not { } panel)
            return Task.CompletedTask;
        _panel = panel with { Running = true };
        _changed();
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
            return new(false, group.MergeBlockedReason
                ?? _text("NativeVocabulary_DuplicatesBlockedFallback"), Warning: true);

        var generation = Interlocked.Increment(ref _generation);
        _panel = panel with {
            Running = true,
            Status = Format("Vocabulary_MergingGroup", group.NormalizedTerm)
        };
        _changed();
        var results = panel.Results.ToList();
        VocabularyDuplicateMergeOutcome outcome;
        try
        {
            var keeper = group.Words[0];
            var duplicates = group.Words.Skip(1).Select(word => word.Id).ToArray();
            // Off the UI thread: the merge folds every copy's history in one save.
            var removed = await Task.Run(() => _store.MergeOwnedVocabularyDuplicatesAsync(
                owner, keeper.Id, duplicates, cancellationToken), cancellationToken);
            results.Add(Format("Vocabulary_DuplicatesMergedCount", group.DisplayTerm, removed));
            outcome = removed > 0
                ? new(true, Format("Vocabulary_DuplicatesMergedSuccess", removed, group.DisplayTerm),
                    Warning: false)
                : new(false, Format("Vocabulary_DuplicatesNoMergeNeeded", group.DisplayTerm),
                    Warning: false);
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
        Interlocked.Increment(ref _generation);
        _panel = null;
        _changed();
    }

    /// <summary>Drops the panel without publishing (owner change, list closed).</summary>
    internal void Reset()
    {
        Interlocked.Increment(ref _generation);
        _panel = null;
    }

    private async Task LoadAsync(string owner, bool append, IReadOnlyList<string> results,
        CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);
        var label = _panel?.FocusLabel;
        var skip = append ? _panel?.Groups.Count ?? 0 : 0;
        try
        {
            // Off the UI thread: the scan reads every owned word.
            var scan = await Task.Run(() => _store.FindOwnedVocabularyDuplicatesAsync(
                owner, label, skip, PageSize, cancellationToken), cancellationToken);
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
                    ? Format("Vocabulary_DuplicatesFoundCount",
                        scan.TotalGroupCount, scan.ExtraRecordCount)
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
                    Results = [.. results, Format("Vocabulary_CleanupError", ex.Message)]
                };
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation))
                _changed();
        }
    }

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
}
