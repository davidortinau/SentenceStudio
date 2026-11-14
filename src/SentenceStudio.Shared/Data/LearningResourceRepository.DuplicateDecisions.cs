using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Data;

/// <summary>
/// Duplicate decisions (docs/duplicate-merge-ai-plan.md): copies kept apart, the AI's confident
/// calls applied as merges and kept-apart records, and Undo. Every read and write is the
/// learner's own (the one ownership rule); an absent learner writes nothing.
/// </summary>
public partial class LearningResourceRepository
{
    /// <summary>
    /// Keeps the learner's copies of one term apart: records a kept-apart decision, so the
    /// duplicate scan stops showing them until a new copy of the term arrives. Every copy must be
    /// the learner's own word, all with one target term; otherwise nothing is recorded (logged)
    /// and it returns false. Keeping the same copies apart again restores a decision that was undone.
    /// </summary>
    public async Task<bool> KeepDuplicatesApartAsync(IReadOnlyCollection<string> copyIds,
        VocabularyDuplicateDecider decidedBy, string? reason, CancellationToken ct = default,
        string? userProfileId = null)
    {
        var userId = ResolveUserId(userProfileId);
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("KeepDuplicatesApartAsync called without an active user; nothing recorded.");
            return false;
        }
        var kept = await KeepApartAsync(userId,
            copyIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)
                .Select(id => new VocabularyDuplicateAiSet([id], string.Empty, null)).ToArray(),
            decidedBy, reason, ct);
        if (kept)
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
        return kept;
    }

    /// <summary>The learner's duplicate decisions, newest first (the report and its Undo).</summary>
    public async Task<IReadOnlyList<VocabularyDuplicateDecision>> GetDuplicateDecisionsAsync(
        CancellationToken ct = default, string? userProfileId = null)
    {
        var userId = ResolveUserId(userProfileId);
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("GetDuplicateDecisionsAsync called without an active user; returning none.");
            return [];
        }
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.VocabularyDuplicateDecisions.AsNoTracking()
            .Where(decision => decision.UserProfileId == userId)
            .OrderByDescending(decision => decision.DecidedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Applies the AI's confident calls (Captain 2026-11-14: they apply without asking, with a
    /// report and Undo). <paramref name="asked"/> are the groups as the AI saw them; a group whose
    /// copies changed since is not touched. A same-meaning set merges into its copy the learner
    /// has learned (as everywhere), which takes the AI's meaning and language; copies that mean
    /// different things are kept apart with their clarified meanings. Each merge runs in its own
    /// transaction with its decision record; a group that fails is reported and the rest still
    /// apply. Calls that are not sure are returned for the learner, untouched.
    /// </summary>
    public async Task<VocabularyDuplicateApplyResult> ApplyDuplicateResolutionAsync(
        IReadOnlyList<VocabularyDuplicateAiGroup> asked, IReadOnlyList<VocabularyDuplicateAiResult> results,
        CancellationToken ct = default, string? userProfileId = null)
    {
        var userId = ResolveUserId(userProfileId);
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("ApplyDuplicateResolutionAsync called without an active user; nothing applied.");
            return VocabularyDuplicateApplyResult.None;
        }

        Dictionary<string, VocabularyDuplicateGroup> current;
        using (var scope = _serviceProvider.CreateScope())
            current = (await ScanDuplicateGroupsAsync(
                    scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), userId, null, ct))
                .ToDictionary(group => group.NormalizedTerm, StringComparer.Ordinal);
        var askedById = asked.ToDictionary(group => group.GroupId, StringComparer.Ordinal);

        var applied = new List<VocabularyDuplicateApplied>();
        var needsYou = new List<VocabularyDuplicateAiResult>();
        var refused = new List<(string DisplayTerm, string RefusalKey)>();
        foreach (var result in results)
        {
            if (ct.IsCancellationRequested)
                break;
            var call = result.Call;
            askedById.TryGetValue(call.GroupId, out var seen);
            if (!call.IsSure)
            {
                needsYou.Add(result);
                continue;
            }
            if (seen is null || !current.TryGetValue(call.GroupId, out var group)
                || !group.Words.Select(info => info.Word.Id).Order(StringComparer.Ordinal)
                    .SequenceEqual(seen.Copies.Select(copy => copy.Id).Order(StringComparer.Ordinal)))
            {
                refused.Add((seen?.Term ?? call.GroupId, "Vocabulary_DuplicatesChangedSinceAi"));
                continue;
            }
            try
            {
                var outcome = await ApplyCallAsync(group, call, userId, ct);
                if (outcome.Applied is { } done)
                    applied.Add(done);
                else
                    refused.Add((group.DisplayTerm, outcome.RefusalKey ?? "Vocabulary_MergeFailed"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI duplicate resolution: the group {Term} was not applied.", group.NormalizedTerm);
                refused.Add((group.DisplayTerm, "Vocabulary_MergeFailed"));
            }
        }

        if (applied.Count > 0)
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
        _logger.LogInformation(
            "AI duplicate resolution: {Applied} groups applied ({Removed} copies removed), {NeedsYou} for the learner, {Refused} refused, cancelled {Cancelled}.",
            applied.Count, applied.Sum(item => item.RemovedCopies), needsYou.Count, refused.Count, ct.IsCancellationRequested);
        return new VocabularyDuplicateApplyResult(applied, needsYou, refused);
    }

    /// <summary>
    /// Undoes one of the learner's decisions. A kept-apart decision stops hiding its copies and
    /// their meanings go back. A merge brings the merged-away copies back as new words with their
    /// own fields and resource links, and the kept word gets its own fields and resources back;
    /// the practice history stays on the kept word (it cannot be split exactly). Returns false
    /// (logged) when the decision is not the learner's, is already undone, or its kept word is gone.
    /// </summary>
    public async Task<bool> UndoDuplicateDecisionAsync(string decisionId, CancellationToken ct = default,
        string? userProfileId = null)
    {
        var userId = ResolveUserId(userProfileId);
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(decisionId))
        {
            _logger.LogWarning("UndoDuplicateDecisionAsync needs an active user and a decision; nothing undone.");
            return false;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var undone = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);
            var decision = await db.VocabularyDuplicateDecisions
                .SingleOrDefaultAsync(row => row.Id == decisionId && row.UserProfileId == userId, ct);
            if (decision is null || decision.UndoneAt is not null)
                return false;
            var before = VocabularyDuplicateUndoRecord.FromJson(decision.Before);
            if (decision.Kind == VocabularyDuplicateDecisionKind.Merged
                && !await UndoMergeAsync(db, decision, before, userId, ct))
                return false;
            if (decision.Kind == VocabularyDuplicateDecisionKind.KeptApart && before is not null)
                await RestoreMeaningsAsync(db, before.Words, userId, ct);
            decision.UndoneAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        });
        if (undone)
            _syncService?.TriggerSyncAsync().ConfigureAwait(false);
        else
            _logger.LogWarning("UndoDuplicateDecisionAsync: decision {DecisionId} was not undone.", decisionId);
        return undone;
    }

    private async Task<(VocabularyDuplicateApplied? Applied, string? RefusalKey)> ApplyCallAsync(
        VocabularyDuplicateGroup group, VocabularyDuplicateAiCall call, string userId, CancellationToken ct)
    {
        if (group.EffectiveRisk == VocabularyDuplicateRisk.Blocked)
            return (null, group.MergeBlockedReason ?? "Vocabulary_MergeRefusedNothingToMove");
        // Merge order (the copy the learner has learned first) decides each set's keeper.
        var order = group.Words.Select((info, index) => (info, index))
            .ToDictionary(item => item.info.Word.Id, item => item, StringComparer.Ordinal);
        var decisions = new List<string>();
        var removed = 0;
        var remaining = new List<VocabularyDuplicateAiSet>();
        foreach (var set in call.Sets)
        {
            if (set.CopyIds.Count < 2)
            {
                remaining.Add(set);
                continue;
            }
            var copies = set.CopyIds.Select(id => order[id]).OrderBy(item => item.index).Select(item => item.info).ToList();
            var keeper = copies.FirstOrDefault(info => !info.UsedByOthers);
            if (keeper is null)
                return (null, "Vocabulary_DuplicatesAllShared");
            var others = copies.Where(info => info != keeper).Select(info => info.Word.Id).ToList();
            var (merged, decisionId) = await MergeAndRecordAsync(keeper.Word.Id, others, userId, group.NormalizedTerm,
                set.Meaning, set.Language, VocabularyDuplicateDecider.Ai, call.Reason, ct);
            if (!merged.Changed)
                return (null, merged.RefusalKey ?? "Vocabulary_MergeRefusedNothingToMove");
            decisions.Add(decisionId);
            removed += merged.Deleted;
            remaining.Add(new VocabularyDuplicateAiSet([keeper.Word.Id], set.Meaning, set.Language));
        }
        // What is left means different things: kept apart, each with its clarified meaning.
        if (remaining.Count > 1)
        {
            if (!await KeepApartAsync(userId, remaining, VocabularyDuplicateDecider.Ai, call.Reason, ct))
                return (null, "Vocabulary_MergeFailed");
            decisions.Add(SyncIds.VocabularyDuplicateDecision(userId, VocabularyDuplicateDecisionKind.KeptApart,
                group.NormalizedTerm, remaining.Select(set => set.CopyIds[0])));
        }
        return (new VocabularyDuplicateApplied(group.DisplayTerm, call.Verdict, decisions,
            call.Sets.Select(set => set.Meaning).ToArray(), removed, call.Reason), null);
    }

    /// <summary>
    /// Merges <paramref name="otherIds"/> into <paramref name="keeperId"/> and records it, in one
    /// serializable transaction: the copies' fields before the merge, then the merge, then the
    /// kept meaning and language, then the decision.
    /// </summary>
    private async Task<(VocabularyMergeResult Result, string DecisionId)> MergeAndRecordAsync(string keeperId,
        IReadOnlyList<string> otherIds, string userId, string termKey, string? meaning, string? language,
        VocabularyDuplicateDecider decidedBy, string? reason, CancellationToken ct)
    {
        var copyIds = otherIds.Prepend(keeperId).ToArray();
        var decisionId = SyncIds.VocabularyDuplicateDecision(userId, VocabularyDuplicateDecisionKind.Merged, termKey, copyIds);
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var result = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);
            var before = await SnapshotAsync(db, copyIds, userId, ct);
            var merged = await MergeWithinTransactionAsync(db, keeperId, otherIds.ToList(), userId, ct);
            if (!merged.Changed)
                return merged;
            var keeper = await db.VocabularyWords.OwnedBy(userId).SingleAsync(word => word.Id == keeperId, ct);
            Clarify(keeper, meaning, language);
            await RecordAsync(db, decisionId, userId, termKey, VocabularyDuplicateDecisionKind.Merged, copyIds,
                keeperId, decidedBy, reason, new VocabularyDuplicateUndoRecord(before).ToJson(), ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return merged;
        });
        return (result, decisionId);
    }

    /// <summary>
    /// Records <paramref name="sets"/> (one copy each) as kept apart, giving each copy its set's
    /// meaning and language when they are given and differ (the old values go in the undo record).
    /// Every copy must be the learner's own word, all of one term; otherwise nothing is written.
    /// </summary>
    private async Task<bool> KeepApartAsync(string userId, IReadOnlyList<VocabularyDuplicateAiSet> sets,
        VocabularyDuplicateDecider decidedBy, string? reason, CancellationToken ct)
    {
        var ids = sets.Select(set => set.CopyIds.Count == 1 ? set.CopyIds[0] : string.Empty)
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length < 2 || ids.Length != sets.Count)
        {
            _logger.LogWarning("Keeping duplicates apart needs two or more distinct copies; nothing recorded.");
            return false;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);
            var words = await db.VocabularyWords.OwnedBy(userId).Where(word => ids.Contains(word.Id)).ToListAsync(ct);
            var keys = words.Select(word => VocabularyDuplicatePolicy.NormalizeTargetTerm(word.TargetLanguageTerm))
                .Distinct(StringComparer.Ordinal).ToList();
            if (words.Count != ids.Length || keys.Count != 1 || keys[0].Length == 0)
            {
                _logger.LogWarning(
                    "Keeping duplicates apart: the {Count} copies are not all the learner's words of one term; nothing recorded.",
                    ids.Length);
                return false;
            }
            var byId = words.ToDictionary(word => word.Id, StringComparer.Ordinal);
            var changing = sets.Where(set => Changes(byId[set.CopyIds[0]], set.Meaning, set.Language))
                .Select(set => set.CopyIds[0]).ToArray();
            var before = changing.Length == 0 ? null
                : new VocabularyDuplicateUndoRecord(await SnapshotAsync(db, changing, userId, ct)).ToJson();
            foreach (var set in sets)
                Clarify(byId[set.CopyIds[0]], set.Meaning, set.Language);
            await RecordAsync(db, SyncIds.VocabularyDuplicateDecision(userId, VocabularyDuplicateDecisionKind.KeptApart,
                    keys[0], ids), userId, keys[0], VocabularyDuplicateDecisionKind.KeptApart, ids, null, decidedBy,
                reason, before, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            _logger.LogInformation("Kept {Count} copies of a term apart ({DecidedBy}), {Clarified} meanings clarified.",
                ids.Length, decidedBy, changing.Length);
            return true;
        });
    }

    private static async Task RecordAsync(ApplicationDbContext db, string id, string userId, string termKey,
        VocabularyDuplicateDecisionKind kind, IReadOnlyCollection<string> copyIds, string? keeperId,
        VocabularyDuplicateDecider decidedBy, string? reason, string? before, CancellationToken ct)
    {
        var decision = await db.VocabularyDuplicateDecisions.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (decision is null)
        {
            decision = new VocabularyDuplicateDecision
            {
                Id = id, UserProfileId = userId, TermKey = termKey, Kind = kind,
                CopyIds = SyncIds.DuplicateDecisionCopies(copyIds),
            };
            db.VocabularyDuplicateDecisions.Add(decision);
        }
        decision.KeeperId = keeperId;
        decision.DecidedBy = decidedBy;
        decision.Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        // Keeping copies apart again (with no new clarification) keeps the first undo record.
        decision.Before = before ?? decision.Before;
        decision.DecidedAt = DateTime.UtcNow;
        decision.UndoneAt = null;
    }

    private static bool Changes(VocabularyWord word, string? meaning, string? language) =>
        (!string.IsNullOrWhiteSpace(meaning) && !string.Equals(word.NativeLanguageTerm?.Trim(), meaning.Trim(), StringComparison.Ordinal))
        || (!string.IsNullOrWhiteSpace(language) && !string.Equals(word.Language?.Trim(), language.Trim(), StringComparison.Ordinal));

    private static void Clarify(VocabularyWord word, string? meaning, string? language)
    {
        if (!Changes(word, meaning, language))
            return;
        if (!string.IsNullOrWhiteSpace(meaning))
            word.NativeLanguageTerm = meaning.Trim();
        if (!string.IsNullOrWhiteSpace(language))
            word.Language = language.Trim();
        word.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>The learner's words among <paramref name="ids"/> as they are, with their own resources.</summary>
    private static async Task<IReadOnlyList<VocabularyDuplicateWordSnapshot>> SnapshotAsync(
        ApplicationDbContext db, IReadOnlyCollection<string> ids, string userId, CancellationToken ct)
    {
        var words = await db.VocabularyWords.AsNoTracking().OwnedBy(userId).Where(word => ids.Contains(word.Id))
            .ToListAsync(ct);
        var resources = (await db.ResourceVocabularyMappings.AsNoTracking()
                .Where(mapping => ids.Contains(mapping.VocabularyWordId) && mapping.Resource.UserProfileId == userId)
                .Select(mapping => new { mapping.VocabularyWordId, mapping.ResourceId })
                .ToListAsync(ct))
            .GroupBy(mapping => mapping.VocabularyWordId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(mapping => mapping.ResourceId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        return words.OrderBy(word => word.Id, StringComparer.Ordinal).Select(word => new VocabularyDuplicateWordSnapshot(
                word.Id, word.TargetLanguageTerm, word.NativeLanguageTerm, word.Lemma, word.Language, word.Tags,
                word.MnemonicText, word.MnemonicImageUri, word.AudioPronunciationUri, word.LexicalUnitType.ToString(),
                word.PartOfSpeech is { } partOfSpeech ? VocabularyPartOfSpeechTokens.ToToken(partOfSpeech) : null,
                word.CreatedAt, resources.GetValueOrDefault(word.Id) ?? []))
            .ToArray();
    }

    /// <summary>
    /// Puts back the meaning and language a kept-apart decision clarified, on each of the learner's
    /// words that still exists (other edits since are kept).
    /// </summary>
    private static async Task RestoreMeaningsAsync(ApplicationDbContext db,
        IReadOnlyList<VocabularyDuplicateWordSnapshot> snapshots, string userId, CancellationToken ct)
    {
        var ids = snapshots.Select(snapshot => snapshot.Id).ToArray();
        var words = await db.VocabularyWords.OwnedBy(userId).Where(word => ids.Contains(word.Id))
            .ToDictionaryAsync(word => word.Id, StringComparer.Ordinal, ct);
        foreach (var snapshot in snapshots)
        {
            if (!words.TryGetValue(snapshot.Id, out var word))
                continue;
            word.NativeLanguageTerm = snapshot.NativeLanguageTerm;
            word.Language = snapshot.Language;
            word.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static void Restore(VocabularyWord word, VocabularyDuplicateWordSnapshot snapshot)
    {
        word.TargetLanguageTerm = snapshot.TargetLanguageTerm;
        word.NativeLanguageTerm = snapshot.NativeLanguageTerm;
        word.Lemma = snapshot.Lemma;
        word.Language = snapshot.Language;
        word.Tags = snapshot.Tags;
        word.MnemonicText = snapshot.MnemonicText;
        word.MnemonicImageUri = snapshot.MnemonicImageUri;
        word.AudioPronunciationUri = snapshot.AudioPronunciationUri;
        word.LexicalUnitType = Enum.TryParse<LexicalUnitType>(snapshot.LexicalUnitType, out var kind) ? kind : LexicalUnitType.Unknown;
        word.PartOfSpeech = VocabularyPartOfSpeechTokens.FromToken(snapshot.PartOfSpeech);
        word.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// A merge undone: the kept word gets its fields and its own resources back, and each copy the
    /// merge removed comes back as a new word (a new ID, so no device mistakes it for the deleted
    /// row) with its fields and the learner's resources that still exist.
    /// </summary>
    private async Task<bool> UndoMergeAsync(ApplicationDbContext db, VocabularyDuplicateDecision decision,
        VocabularyDuplicateUndoRecord? before, string userId, CancellationToken ct)
    {
        var keeperSnapshot = before?.Words.FirstOrDefault(snapshot => snapshot.Id == decision.KeeperId);
        if (before is null || keeperSnapshot is null
            || await db.VocabularyWords.OwnedBy(userId).SingleOrDefaultAsync(word => word.Id == decision.KeeperId, ct)
                is not { } keeper)
        {
            _logger.LogWarning("Undo of merge {DecisionId}: its record or kept word is gone.", decision.Id);
            return false;
        }

        Restore(keeper, keeperSnapshot);
        var removed = before.Words.Where(snapshot => snapshot.Id != decision.KeeperId).ToList();
        var removedIds = removed.Select(snapshot => snapshot.Id).ToArray();
        // A copy another learner used stayed (the merge moved only the learner's links from it).
        var stayed = await db.VocabularyWords.OwnedBy(userId).Where(word => removedIds.Contains(word.Id))
            .ToDictionaryAsync(word => word.Id, StringComparer.Ordinal, ct);
        var ownResources = (await db.LearningResources.Where(resource => resource.UserProfileId == userId)
            .Select(resource => resource.Id).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var restored = new List<(VocabularyWord Word, IReadOnlyList<string> Resources)>();
        foreach (var snapshot in removed)
        {
            if (!stayed.TryGetValue(snapshot.Id, out var word))
            {
                word = new VocabularyWord { Id = Guid.NewGuid().ToString(), UserProfileId = userId, CreatedAt = snapshot.CreatedAt };
                db.VocabularyWords.Add(word);
            }
            Restore(word, snapshot);
            restored.Add((word, snapshot.ResourceIds));
        }

        // The learner's resources go back to the copies that held them; the kept word leaves the
        // ones it only gained in the merge.
        var keeperOwn = keeperSnapshot.ResourceIds.ToHashSet(StringComparer.Ordinal);
        var moved = restored.SelectMany(item => item.Resources).Where(ownResources.Contains)
            .Where(resource => !keeperOwn.Contains(resource)).ToHashSet(StringComparer.Ordinal);
        db.ResourceVocabularyMappings.RemoveRange(await db.ResourceVocabularyMappings
            .Where(mapping => mapping.VocabularyWordId == keeper.Id && moved.Contains(mapping.ResourceId))
            .ToListAsync(ct));
        foreach (var (word, resources) in restored)
        {
            foreach (var resource in resources.Where(ownResources.Contains))
            {
                if (!await db.ResourceVocabularyMappings.AnyAsync(
                        mapping => mapping.VocabularyWordId == word.Id && mapping.ResourceId == resource, ct))
                    db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
                    {
                        Id = Guid.NewGuid().ToString(), ResourceId = resource, VocabularyWordId = word.Id
                    });
            }
        }
        return true;
    }
}

/// <summary>One group the AI resolved: what it decided, the decisions recorded (for Undo) and why.</summary>
/// <param name="Meanings">The meaning of each set, in the AI's order.</param>
public sealed record VocabularyDuplicateApplied(
    string DisplayTerm, VocabularyDuplicateAiVerdict Verdict, IReadOnlyList<string> DecisionIds,
    IReadOnlyList<string> Meanings, int RemovedCopies, string Reason);

/// <summary>
/// What an AI duplicate pass did: the groups it applied, the calls left for the learner (not
/// sure enough, or unusable), and the groups it could not apply with their reasons.
/// </summary>
public sealed record VocabularyDuplicateApplyResult(
    IReadOnlyList<VocabularyDuplicateApplied> Applied,
    IReadOnlyList<VocabularyDuplicateAiResult> NeedsYou,
    IReadOnlyList<(string DisplayTerm, string RefusalKey)> Refused)
{
    public static VocabularyDuplicateApplyResult None { get; } = new([], [], []);

    public int MergedGroups => Applied.Count(item => item.Verdict != VocabularyDuplicateAiVerdict.DifferentWords);

    public int KeptApartGroups => Applied.Count(item => item.Verdict == VocabularyDuplicateAiVerdict.DifferentWords);

    public int RemovedCopies => Applied.Sum(item => item.RemovedCopies);
}
