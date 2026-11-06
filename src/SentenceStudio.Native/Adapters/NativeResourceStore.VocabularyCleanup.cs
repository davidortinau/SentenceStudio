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
    /// removes the word, its resource links and, by cascade, its progress, history, example
    /// sentences and pairs. Only a word the owner may edit can go: one another learner's data
    /// still uses stays, by the rule the server applies to the delete
    /// (<see cref="VocabularyWordDependents"/>).
    /// </summary>
    public async Task<NativeVocabularyDeleteStatus> DeleteOwnedVocabularyAsync(
        string owner, string wordId, CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner) || string.IsNullOrWhiteSpace(wordId))
            return NativeVocabularyDeleteStatus.NotFoundOrForbidden;

        var stamp = userScope.CaptureStamp();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var word = await db.VocabularyWords.AsNoTracking()
            .Where(candidate => candidate.Id == wordId
                && OwnedVocabularyIds(db, owner).Contains(candidate.Id))
            .Select(candidate => new { candidate.Id, candidate.UserProfileId })
            .SingleOrDefaultAsync(cancellationToken);
        if (word is null)
            return NativeVocabularyDeleteStatus.NotFoundOrForbidden;
        if (word.UserProfileId != owner
            || !await IsEditableByOwnerAsync(db, owner, wordId, cancellationToken))
            return NativeVocabularyDeleteStatus.SharedReadOnly;

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return NativeVocabularyDeleteStatus.NotFoundOrForbidden;
        var removed = await scope.ServiceProvider
            .GetRequiredService<LearningResourceRepository>()
            .DeleteVocabularyWordAsync(wordId);
        // The repository reports success for a word it did not find, and swallows its own
        // failure, so the row itself is the proof.
        if (!removed || await db.VocabularyWords.AsNoTracking()
                .AnyAsync(candidate => candidate.Id == wordId, CancellationToken.None))
        {
            logger.LogWarning("Native vocabulary delete did not remove the word.");
            return NativeVocabularyDeleteStatus.Failed;
        }
        return TryMarkPending(owner, "Vocabulary delete")
            ? NativeVocabularyDeleteStatus.Deleted
            : NativeVocabularyDeleteStatus.DeletedSyncUnconfirmed;
    }

    /// <summary>
    /// Source FindDuplicateVocabularyGroupsAsync for the active owner. A copy not recorded to the
    /// owner (another learner's, or a legacy word recorded to nobody, which the server also
    /// treats as not theirs) shows no memory aids or audio; its strength, its place in the order,
    /// the recommended keeper and the group's title come from what is shown, so nothing hints
    /// at aids the learner cannot see. Such a group cannot merge here: the merge would copy
    /// that row's hidden aids into the keeper and delete a row the server keeps.
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
                focusTerm, skip, take, cancellationToken));
        if (!IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return new(false, [], 0, 0);

        var groups = scan.Groups.Select(group =>
            {
                var shown = VocabularyDuplicateGroup.OrderWordsByMergePreference(
                    group.Words.Select(info => AsShownTo(info, owner)));
                var anotherOwners = group.Words.Any(info => info.Word.UserProfileId != owner);
                var title = shown.FirstOrDefault()?.Word.TargetLanguageTerm?.Trim();
                return new NativeVocabularyDuplicateGroup(
                    group.NormalizedTerm,
                    string.IsNullOrEmpty(title) ? group.DisplayTerm : title,
                    shown.Select(info => DuplicateWord(info)).ToArray(),
                    group.CanMergeAutomatically && !anotherOwners, group.MergeBlockedReason,
                    shown.FirstOrDefault()?.Word.Id ?? string.Empty,
                    HasAnotherOwnersCopy: anotherOwners);
            })
            .ToArray();
        return new(true, groups, scan.TotalGroupCount, scan.ExtraRecordCount);
    }

    /// <summary>
    /// Source MergeDuplicateGroup (<see cref="LearningResourceRepository.MergeVocabularyWordsWithResultAsync"/>),
    /// which keeps every copy's history and refuses words shared outside the owner. Every word
    /// must be the owner's, linked to them and recorded to them; otherwise nothing is merged
    /// (a copy recorded to anyone else would give its hidden aids to the keeper).
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
        var expected = requested.Length;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var owned = await db.VocabularyWords.AsNoTracking()
                .Where(word => requested.Contains(word.Id)
                    && word.UserProfileId == owner
                    && OwnedVocabularyIds(db, owner).Contains(word.Id))
                .CountAsync(cancellationToken);
            if (owned != expected)
            {
                logger.LogWarning("Native duplicate merge refused words the owner does not hold.");
                return NativeVocabularyMergeResult.None;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return NativeVocabularyMergeResult.None;
        var merge = await WithResourcesAsync(resources =>
            resources.MergeVocabularyWordsWithResultAsync(keeperWordId, duplicates, cancellationToken));
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

    /// <summary>
    /// The copy as this owner may see it: another owner's copy loses its memory aids and audio,
    /// and its strength is what remains.
    /// </summary>
    private static VocabularyDuplicateWordInfo AsShownTo(
        VocabularyDuplicateWordInfo info, string owner)
    {
        var word = info.Word;
        if (word.UserProfileId == owner)
            return info;
        var shown = new VocabularyWord
        {
            Id = word.Id,
            TargetLanguageTerm = word.TargetLanguageTerm,
            NativeLanguageTerm = word.NativeLanguageTerm,
            Language = word.Language,
            LexicalUnitType = word.LexicalUnitType,
            CreatedAt = word.CreatedAt,
            UpdatedAt = word.UpdatedAt
        };
        return info with
        {
            Word = shown,
            EncodingScore = EncodingScoreHelper.CalculateWithLabel(shown, 0).Score
        };
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
