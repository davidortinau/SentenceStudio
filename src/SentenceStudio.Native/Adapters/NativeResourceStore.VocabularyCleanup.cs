using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Adapters;

/// <summary>
/// The word editor's Delete and Find Duplicates (source VocabularyWordEdit.razor and the
/// Vocabulary.razor duplicate manager), on the source repository paths so a delete or merge
/// syncs like one made on the web or in the MAUI apps.
/// </summary>
public sealed partial class NativeResourceStore
{
    /// <summary>
    /// Source ConfirmDelete (<see cref="LearningResourceRepository.DeleteVocabularyWordAsync"/>):
    /// removes the owner's word, its resource links and, by cascade, its progress, history,
    /// example sentences and pairs. A word not recorded to the owner is never touched; the
    /// shared delete then applies the server's rules in one transaction and says why it refused,
    /// so they are not run twice (review 4). A refusal is mapped to what the editor can explain:
    /// UsedByOthers when data that is not the owner's still uses the word, UsedByAnswers for a
    /// Minimal Pairs answer.
    /// </summary>
    public async Task<NativeVocabularyDeleteStatus> DeleteOwnedVocabularyAsync(
        string owner, string wordId, CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner) || string.IsNullOrWhiteSpace(wordId))
            return NativeVocabularyDeleteStatus.NotFoundOrForbidden;

        var stamp = userScope.CaptureStamp();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (!await OwnedVocabularyIds(db, owner).AnyAsync(id => id == wordId, cancellationToken))
            return NativeVocabularyDeleteStatus.NotFoundOrForbidden;

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return NativeVocabularyDeleteStatus.NotFoundOrForbidden;
        var removed = await scope.ServiceProvider
            .GetRequiredService<LearningResourceRepository>()
            .DeleteVocabularyWordAsync(wordId, owner, CancellationToken.None);
        switch (removed)
        {
            case VocabularyWordDeleteResult.Deleted:
                return TryMarkPending(owner, "Vocabulary delete")
                    ? NativeVocabularyDeleteStatus.Deleted
                    : NativeVocabularyDeleteStatus.DeletedSyncUnconfirmed;
            case VocabularyWordDeleteResult.RefusedAnswers:
                return NativeVocabularyDeleteStatus.UsedByAnswers;
            case VocabularyWordDeleteResult.RefusedShared:
                return NativeVocabularyDeleteStatus.UsedByOthers;
            case VocabularyWordDeleteResult.NotFound:
                return NativeVocabularyDeleteStatus.NotFoundOrForbidden;
            default:
                logger.LogWarning("Native vocabulary delete did not remove the word.");
                return NativeVocabularyDeleteStatus.Failed;
        }
    }

    /// <summary>
    /// Source FindDuplicateVocabularyGroupsAsync for the active owner, shown as the repository
    /// returns it: only the owner's own words, the same on every client. The order, the
    /// recommended keeper and the title come from what is shown.
    /// </summary>
    public async Task<NativeVocabularyDuplicateScan> FindOwnedVocabularyDuplicatesAsync(
        string owner, string? focusTerm, int skip, int take,
        CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            return new(false, [], 0, 0);

        var stamp = userScope.CaptureStamp();
        var scan = await WithResourcesAsync(resources =>
            resources.FindDuplicateVocabularyGroupsAsync(
                focusTerm, skip, take, cancellationToken, owner));
        if (!IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return new(false, [], 0, 0);

        var groups = scan.Groups.Select(group =>
            {
                var shown = group.Words;
                var title = shown.FirstOrDefault()?.Word.TargetLanguageTerm?.Trim();
                return new NativeVocabularyDuplicateGroup(
                    group.NormalizedTerm,
                    string.IsNullOrEmpty(title) ? group.DisplayTerm : title,
                    shown.Select(info => DuplicateWord(info)).ToArray(),
                    group.CanMergeAutomatically, group.MergeBlockedReason,
                    group.RecommendedKeeperId);
            })
            .ToArray();
        return new(true, groups, scan.TotalGroupCount, scan.ExtraRecordCount);
    }

    /// <summary>
    /// Source MergeDuplicateGroup (<see cref="LearningResourceRepository.MergeVocabularyWordsWithResultAsync"/>),
    /// which keeps every copy's history, refuses words shared outside the owner and changes
    /// nothing unless every word is the owner's by the one ownership rule. The device first
    /// checks that every word is recorded to the owner.
    /// </summary>
    public async Task<NativeVocabularyMergeResult> MergeOwnedVocabularyDuplicatesAsync(
        string owner, string keeperWordId, IReadOnlyList<string> duplicateWordIds,
        CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner) || string.IsNullOrWhiteSpace(keeperWordId)
            || duplicateWordIds is null)
            return NativeVocabularyMergeResult.None;
        var duplicates = duplicateWordIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && id != keeperWordId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (duplicates.Count == 0)
            return NativeVocabularyMergeResult.None;

        var stamp = userScope.CaptureStamp();
        var requested = duplicates.Append(keeperWordId).ToArray();
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var inVocabulary = await OwnedVocabularyIds(db, owner)
                .Where(id => requested.Contains(id))
                .Distinct()
                .CountAsync(cancellationToken);
            if (inVocabulary != requested.Length)
            {
                logger.LogWarning("Native duplicate merge refused words the owner does not hold.");
                return NativeVocabularyMergeResult.None;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return NativeVocabularyMergeResult.None;
        var merge = await WithResourcesAsync(resources =>
            resources.MergeVocabularyWordsWithResultAsync(
                keeperWordId, duplicates, cancellationToken, owner));
        if (!merge.Changed)
            return new(merge.Deleted, false, false);
        return new(merge.Deleted, true, !TryMarkPending(owner, "Duplicate merge"));
    }

    /// <summary>
    /// Records a committed change for sync. False (logged) when the tracking could not be
    /// written: the change is on this device but the learner must hear that its sync is unconfirmed.
    /// </summary>
    private bool TryMarkPending(string owner, string operation)
    {
        try
        {
            pendingWrites.MarkPending(owner);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Operation} committed but sync tracking was not confirmed.", operation);
            return false;
        }
    }

    private static NativeVocabularyDuplicateWord DuplicateWord(VocabularyDuplicateWordInfo info)
    {
        var word = info.Word;
        return new NativeVocabularyDuplicateWord(
            word.Id, word.TargetLanguageTerm ?? string.Empty,
            word.NativeLanguageTerm ?? string.Empty,
            word.Language, word.LexicalUnitType, info.ResourceCount,
            info.EncodingScore, EncodingScoreHelper.GetLabel(info.EncodingScore),
            string.IsNullOrWhiteSpace(word.MnemonicText) ? null : word.MnemonicText,
            !string.IsNullOrWhiteSpace(word.MnemonicImageUri),
            !string.IsNullOrWhiteSpace(word.AudioPronunciationUri));
    }
}
