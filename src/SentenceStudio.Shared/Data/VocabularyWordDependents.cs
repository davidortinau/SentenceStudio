using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Data;

/// <summary>
/// The rules for which of an owner's words other rows still use, so a delete is refused before
/// it runs. The API's owner-checked sync applies a device's word delete only when both sets
/// leave the word out (NativeOwnerSyncProvider), and the native word editor offers Delete on
/// the same rules, so a device never deletes a word the server keeps.
/// </summary>
public static class VocabularyWordDependents
{
    /// <summary>
    /// Words among <paramref name="ids"/> that a row of another learner, or a row nobody owns,
    /// still uses: deleting the word would cascade into, or clear a link of, that row. A
    /// mapping counts unless its resource is the owner's; progress, example sentences and
    /// minimal pairs unless they name the owner (a row naming nobody counts as another's); a
    /// phrase link unless its phrase word is the owner's.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="owner"/> is null or blank. Comparing with no owner would stop rows that
    /// name nobody from counting, so the rule refuses to run instead of finding fewer uses.
    /// </exception>
    public static async Task<HashSet<string>> OthersDependOnAsync(
        ApplicationDbContext db, string[] ids, string owner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("An owner is required to decide whose rows use a word.",
                nameof(owner));
        var used = new HashSet<string>(StringComparer.Ordinal);
        used.UnionWith(await (
            from mapping in db.ResourceVocabularyMappings.AsNoTracking()
            join resource in db.LearningResources.AsNoTracking()
                on mapping.ResourceId equals resource.Id into resources
            from resource in resources.DefaultIfEmpty()
            where ids.Contains(mapping.VocabularyWordId)
                && (resource == null || resource.UserProfileId != owner)
            select mapping.VocabularyWordId).ToListAsync(ct));
        used.UnionWith(await db.VocabularyProgresses.AsNoTracking()
            .Where(progress => ids.Contains(progress.VocabularyWordId) && progress.UserId != owner)
            .Select(progress => progress.VocabularyWordId).ToListAsync(ct));
        used.UnionWith(await db.ExampleSentences.AsNoTracking()
            .Where(example => ids.Contains(example.VocabularyWordId) && example.UserProfileId != owner)
            .Select(example => example.VocabularyWordId).ToListAsync(ct));
        var pairs = await db.MinimalPairs.AsNoTracking()
            .Where(pair => (ids.Contains(pair.VocabularyWordAId) || ids.Contains(pair.VocabularyWordBId))
                && pair.UserId != owner)
            .Select(pair => new { pair.VocabularyWordAId, pair.VocabularyWordBId }).ToListAsync(ct);
        used.UnionWith(pairs.SelectMany(pair => new[] { pair.VocabularyWordAId, pair.VocabularyWordBId }));
        used.UnionWith(await (
            from phrase in db.PhraseConstituents.AsNoTracking()
            join word in db.VocabularyWords.AsNoTracking() on phrase.PhraseWordId equals word.Id
            where phrase.ConstituentWordId != null && ids.Contains(phrase.ConstituentWordId)
                && word.UserProfileId != owner
            select phrase.ConstituentWordId!).ToListAsync(ct));
        // The pair query returns both words of a pair; only the asked-for ones are answers.
        used.IntersectWith(ids);
        return used;
    }

    /// <summary>
    /// Words among <paramref name="ids"/> that a minimal pair answer still references and that
    /// the same change does not remove first: an answer, pair or session whose ID is listed in
    /// <paramref name="deleted"/> under "MinimalPairAttempt", "MinimalPair" or
    /// "MinimalPairSession". An answer references its prompt and chosen words with ON DELETE
    /// RESTRICT, the only restricting references among synced tables (PostgreSQL catalog,
    /// 2026-11-04; B2), so deleting such a word fails on every provider. The answer may be
    /// another learner's or the owner's own.
    /// </summary>
    public static async Task<HashSet<string>> AnswersStillUseAsync(
        ApplicationDbContext db, string[] ids,
        IReadOnlyDictionary<string, HashSet<string>> deleted, CancellationToken ct)
    {
        var answers = await db.MinimalPairAttempts.AsNoTracking()
            .Where(answer => ids.Contains(answer.PromptWordId) || ids.Contains(answer.SelectedWordId))
            .Select(answer => new { answer.Id, answer.PairId, answer.SessionId,
                answer.PromptWordId, answer.SelectedWordId })
            .ToListAsync(ct);
        bool Removed(string table, string id) =>
            deleted.TryGetValue(table, out var removed) && removed.Contains(id);
        var used = answers
            .Where(answer => !Removed("MinimalPairAttempt", answer.Id)
                && !Removed("MinimalPair", answer.PairId)
                && !Removed("MinimalPairSession", answer.SessionId))
            .SelectMany(answer => new[] { answer.PromptWordId, answer.SelectedWordId })
            .ToHashSet(StringComparer.Ordinal);
        // An answer names two words; only the asked-for ones are answers.
        used.IntersectWith(ids);
        return used;
    }

    /// <summary>Words among <paramref name="ids"/> that a minimal pair answer references.</summary>
    public static Task<HashSet<string>> AnswersStillUseAsync(
        ApplicationDbContext db, string[] ids, CancellationToken ct) =>
        AnswersStillUseAsync(db, ids, NothingDeleted, ct);

    private static readonly IReadOnlyDictionary<string, HashSet<string>> NothingDeleted =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
}
