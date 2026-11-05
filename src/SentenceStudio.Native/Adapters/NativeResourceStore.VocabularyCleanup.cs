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
    /// sentences and pairs. Only a word the owner may edit can go: one another account still
    /// uses (links, progress or sentences) stays, as the server would refuse its delete.
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
            || !await VocabularyIdsWithExclusiveCurrentUse(db, owner)
                .AnyAsync(id => id == wordId, cancellationToken))
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
        try
        {
            pendingWrites.MarkPending(owner);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Vocabulary delete committed but sync tracking was not confirmed.");
        }
        return NativeVocabularyDeleteStatus.Deleted;
    }

    /// <summary>
    /// Source FindDuplicateVocabularyGroupsAsync for the active owner. Copies keep the
    /// source's merge-preference order and recommended keeper; a copy recorded to another
    /// owner shows no memory aids, and its strength counts its terms only.
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

        var groups = scan.Groups.Select(group => new NativeVocabularyDuplicateGroup(
                group.NormalizedTerm, group.DisplayTerm,
                VocabularyDuplicateGroup.OrderWordsByMergePreference(group.Words)
                    .Select(info => DuplicateWord(info, owner)).ToArray(),
                group.CanMergeAutomatically, group.MergeBlockedReason,
                group.RecommendedKeeperId))
            .ToArray();
        return new(true, groups, scan.TotalGroupCount, scan.ExtraRecordCount);
    }

    /// <summary>
    /// Source MergeDuplicateGroup (<see cref="LearningResourceRepository.MergeVocabularyWordsAsync"/>),
    /// which keeps every copy's history and refuses words shared outside the owner. Every word
    /// must be the owner's; otherwise nothing is merged.
    /// </summary>
    public async Task<int> MergeOwnedVocabularyDuplicatesAsync(
        string owner, string keeperWordId, IReadOnlyList<string> duplicateWordIds,
        CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner) || string.IsNullOrWhiteSpace(keeperWordId)
            || duplicateWordIds is null)
            return 0;
        var duplicates = duplicateWordIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && id != keeperWordId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (duplicates.Count == 0)
            return 0;

        var stamp = userScope.CaptureStamp();
        var requested = duplicates.Append(keeperWordId).ToArray();
        var expected = requested.Length;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var owned = await db.VocabularyWords.AsNoTracking()
                .Where(word => requested.Contains(word.Id)
                    && OwnedVocabularyIds(db, owner).Contains(word.Id))
                .CountAsync(cancellationToken);
            if (owned != expected)
            {
                logger.LogWarning("Native duplicate merge refused words the owner does not hold.");
                return 0;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return 0;
        var removed = await WithResourcesAsync(resources =>
            resources.MergeVocabularyWordsAsync(keeperWordId, duplicates, cancellationToken));
        if (removed > 0)
        {
            try
            {
                pendingWrites.MarkPending(owner);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Duplicate merge committed but sync tracking was not confirmed.");
            }
        }
        return removed;
    }

    private static NativeVocabularyDuplicateWord DuplicateWord(
        VocabularyDuplicateWordInfo info, string owner)
    {
        var word = info.Word;
        var ownsMemoryAids = word.UserProfileId == owner;
        var score = ownsMemoryAids ? info.EncodingScore
            : EncodingScoreHelper.CalculateWithLabel(new VocabularyWord {
                TargetLanguageTerm = word.TargetLanguageTerm,
                NativeLanguageTerm = word.NativeLanguageTerm
            }, 0).Score;
        return new NativeVocabularyDuplicateWord(
            word.Id, word.TargetLanguageTerm ?? string.Empty,
            word.NativeLanguageTerm ?? string.Empty,
            word.Language, word.LexicalUnitType, info.ResourceCount,
            score, EncodingScoreHelper.GetLabel(score),
            ownsMemoryAids && !string.IsNullOrWhiteSpace(word.MnemonicText)
                ? word.MnemonicText : null,
            ownsMemoryAids && !string.IsNullOrWhiteSpace(word.MnemonicImageUri),
            !string.IsNullOrWhiteSpace(word.AudioPronunciationUri));
    }
}
