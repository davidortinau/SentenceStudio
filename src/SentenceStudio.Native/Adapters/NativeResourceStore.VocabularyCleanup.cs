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
                    // Empty when no copy can be kept (nothing to merge into).
                    group.RecommendedKeeperId, group.EffectiveRisk, group.Differences,
                    group.MergeParts);
            })
            .ToArray();
        return new(true, groups, scan.TotalGroupCount, scan.ExtraRecordCount,
            scan.SafeGroupCount, scan.SafeCopyCount, scan.ReviewGroupCount, scan.BlockedGroupCount);
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
                return NativeVocabularyMergeResult.None with { RefusalKey = "Vocabulary_MergeRefusedNotYours" };
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsActiveOwner(owner) || !userScope.IsCurrent(stamp))
            return NativeVocabularyMergeResult.None;
        var merge = await WithResourcesAsync(resources =>
            resources.MergeVocabularyWordsWithResultAsync(
                keeperWordId, duplicates, cancellationToken, owner));
        if (!merge.Changed)
            return new(merge.Deleted, false, false, merge.RefusalKey);
        return new(merge.Deleted, true, !TryMarkPending(owner, "Duplicate merge"));
    }

    /// <summary>
    /// Source MergeSafeDuplicateGroupsAsync for the active owner: every group the shared rules
    /// rate safe (nothing is lost), across all pages, in one step. The shared scan reads only the
    /// words recorded to the owner, the same rule as <see cref="OwnedVocabularyIds"/>.
    /// </summary>
    public async Task<NativeVocabularyMergeAllResult> MergeSafeOwnedVocabularyDuplicatesAsync(
        string owner, string? focusTerm, IReadOnlyCollection<string> leaveOut,
        IReadOnlyCollection<string> keepers, CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            return NativeVocabularyMergeAllResult.None;
        cancellationToken.ThrowIfCancellationRequested();

        // The merges are the owner's even if the account changes while they run, so their sync
        // is recorded for that owner (TryMarkPending is keyed by owner).
        var merged = await WithResourcesAsync(resources =>
            resources.MergeSafeDuplicateGroupsAsync(cancellationToken, owner, focusTerm, leaveOut, keepers));
        var refused = merged.Refused
            .Select(refusal => new NativeVocabularyMergeRefusal(refusal.DisplayTerm, refusal.RefusalKey))
            .ToArray();
        if (merged.MergedGroups == 0)
            return new(0, 0, refused, false);
        return new(merged.MergedGroups, merged.RemovedCopies, refused,
            !TryMarkPending(owner, "Duplicate merge all"));
    }

    /// <summary>
    /// Resolve with AI (shared <see cref="VocabularyDuplicateAiCleanup"/>) for the active owner:
    /// the prompt goes through the owner-bound gateway (fast tier, then reasoning for the unsure),
    /// in the owner's native and target languages, and the merges are made here with the shared
    /// rules, so they sync like any other.
    /// </summary>
    public async Task<NativeVocabularyDuplicateAiResult> ResolveOwnedVocabularyDuplicatesWithAiAsync(
        string owner, IReadOnlyCollection<string> leaveOut, IReadOnlyCollection<string> keepers,
        CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            return NativeVocabularyDuplicateAiResult.None;
        using var scope = scopes.CreateScope();
        var gateway = scope.ServiceProvider.GetService<IOwnerBoundNativeAiGateway>()
            ?? throw new InvalidOperationException("Duplicate resolution with AI requires an owner-bound AI gateway.");
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var languages = await db.UserProfiles.AsNoTracking().Where(profile => profile.Id == owner)
            .Select(profile => new { profile.NativeLanguage, profile.TargetLanguage })
            .SingleOrDefaultAsync(cancellationToken);
        var cleanup = new VocabularyDuplicateAiCleanup(
            scope.ServiceProvider.GetRequiredService<LearningResourceRepository>(),
            scope.ServiceProvider.GetRequiredService<ILogger<VocabularyDuplicateAiCleanup>>());
        var result = await cleanup.RunAsync(owner, languages?.NativeLanguage ?? "English",
            languages?.TargetLanguage ?? "Korean",
            (prompt, tier, ct) => gateway.SendPromptAsync<SentenceStudio.Services.DTOs.DuplicateResolutionResponse>(
                owner, prompt, tier, ct),
            leaveOut, keepers, cancellationToken);

        var changed = result.Rules.MergedGroups > 0 || result.Ai.Applied.Count > 0;
        return new NativeVocabularyDuplicateAiResult(
            result.Rules.RemovedCopies, result.Ai.MergedGroups, result.Ai.KeptApartGroups, result.Ai.RemovedCopies,
            result.Ai.NeedsYou.Select(open => new NativeVocabularyDuplicateAiSuggestion(open.Call.GroupId,
                open.Call.Usable, open.Call.Verdict, open.Call.Sets.Select(set => set.Meaning).ToArray(),
                open.Call.Reason, open.Call.Unanswered)).ToArray(),
            result.Rules.Refused.Concat(result.Ai.Refused)
                .Select(refusal => new NativeVocabularyMergeRefusal(refusal.DisplayTerm, refusal.RefusalKey)).ToArray(),
            changed && !TryMarkPending(owner, "Duplicate resolution with AI"), result.AiUnavailable);
    }

    /// <summary>The owner's standing duplicate decisions (shared GetStandingDuplicateDecisionsAsync).</summary>
    public async Task<IReadOnlyList<NativeVocabularyDuplicateDecision>> GetOwnedVocabularyDuplicateDecisionsAsync(
        string owner, CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            return [];
        var decisions = await WithResourcesAsync(resources =>
            resources.GetStandingDuplicateDecisionsAsync(cancellationToken, owner));
        return decisions.Select(decision => new NativeVocabularyDuplicateDecision(decision.Id, decision.Kind,
                decision.DisplayTerm, decision.Meanings, decision.DecidedBy, decision.Reason))
            .ToArray();
    }

    /// <summary>Undo (shared <see cref="LearningResourceRepository.UndoDuplicateDecisionAsync"/>).</summary>
    public async Task<NativeVocabularyMergeResult> UndoOwnedVocabularyDuplicateDecisionAsync(
        string owner, string decisionId, CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner) || string.IsNullOrWhiteSpace(decisionId))
            return NativeVocabularyMergeResult.None;
        var undone = await WithResourcesAsync(resources =>
            resources.UndoDuplicateDecisionAsync(decisionId, cancellationToken, owner));
        return undone
            ? new(0, true, !TryMarkPending(owner, "Duplicate undo"))
            : NativeVocabularyMergeResult.None;
    }

    /// <summary>Keep Apart (shared <see cref="LearningResourceRepository.KeepDuplicatesApartAsync"/>).</summary>
    public async Task<NativeVocabularyMergeResult> KeepOwnedVocabularyDuplicatesApartAsync(
        string owner, IReadOnlyCollection<string> wordIds, CancellationToken cancellationToken)
    {
        if (!IsActiveOwner(owner))
            return NativeVocabularyMergeResult.None;
        var kept = await WithResourcesAsync(resources => resources.KeepDuplicatesApartAsync(
            wordIds, VocabularyDuplicateDecider.Learner, null, cancellationToken, owner));
        return kept
            ? new(0, true, !TryMarkPending(owner, "Keep duplicates apart"))
            : NativeVocabularyMergeResult.None with { RefusalKey = "Vocabulary_DuplicatesKeepApartFailed" };
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
            !string.IsNullOrWhiteSpace(word.AudioPronunciationUri),
            string.IsNullOrWhiteSpace(word.Lemma) ? null : word.Lemma.Trim(),
            info.UsedByOthers, info.Mastery, info.Attempts);
    }
}
