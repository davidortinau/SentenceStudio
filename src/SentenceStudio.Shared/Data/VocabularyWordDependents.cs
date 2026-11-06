using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Data;

/// <summary>
/// The rules for which of an owner's words other rows still use. The API's owner-checked sync
/// applies a device's word delete only when <see cref="OthersDependOnAsync"/> and
/// <see cref="AnswersStillUseAsync(ApplicationDbContext, string[], IReadOnlyDictionary{string, HashSet{string}}, CancellationToken)"/>
/// leave the word out (NativeOwnerSyncProvider); the shared delete
/// (LearningResourceRepository.DeleteVocabularyWordAsync) and the native word editor apply the
/// same rules, so no client deletes a word the server keeps. Whose a word or sentence is comes
/// from <see cref="VocabularyWordOwnership"/>.
/// </summary>
public static class VocabularyWordDependents
{
    /// <summary>
    /// Words among <paramref name="ids"/> that someone other than <paramref name="owner"/>
    /// links, practises or wrote a sentence for: a link from a resource that is not the
    /// owner's, another learner's progress, or an example sentence not recorded to the owner (one
    /// recorded to no one counts as someone else's, as everywhere: the recorded owner decides).
    /// The native editor offers editing only for words this leaves out.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is null or blank (fail closed).</exception>
    public static async Task<HashSet<string>> OthersUseAsync(
        ApplicationDbContext db, string[] ids, string owner, CancellationToken ct)
    {
        RequireOwner(owner);
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (ids.Length == 0)
            return used;
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
        // EF keeps C# null semantics: a sentence recorded to no one differs from the owner.
        used.UnionWith(await db.ExampleSentences.AsNoTracking()
            .Where(example => ids.Contains(example.VocabularyWordId) && example.UserProfileId != owner)
            .Select(example => example.VocabularyWordId).ToListAsync(ct));
        used.IntersectWith(ids);
        return used;
    }

    /// <summary>
    /// Words among <paramref name="ids"/> that a row of another learner, or a row nobody owns,
    /// still uses: deleting the word would cascade into, or clear a link of, that row. That is
    /// <see cref="OthersUseAsync"/>, plus minimal pairs that do not name the owner and phrase
    /// links whose phrase word is not the owner's.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="owner"/> is null or blank. Comparing with no owner would stop rows that
    /// name nobody from counting, so the rule refuses to run instead of finding fewer uses.
    /// </exception>
    public static async Task<HashSet<string>> OthersDependOnAsync(
        ApplicationDbContext db, string[] ids, string owner, CancellationToken ct)
    {
        var used = await OthersUseAsync(db, ids, owner, ct);
        used.UnionWith(await OthersPairsOrPhrasesUseAsync(db, ids, owner, ct));
        return used;
    }

    /// <summary>
    /// Words among <paramref name="ids"/> that a minimal pair not naming the owner, or a phrase
    /// whose phrase word is not the owner's, uses: the part of
    /// <see cref="OthersDependOnAsync"/> that blocks a delete but not an edit (editing the
    /// word changes neither row).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is null or blank (fail closed).</exception>
    public static async Task<HashSet<string>> OthersPairsOrPhrasesUseAsync(
        ApplicationDbContext db, string[] ids, string owner, CancellationToken ct)
    {
        RequireOwner(owner);
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (ids.Length == 0)
            return used;
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

    private static void RequireOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("An owner is required to decide whose rows use a word.",
                nameof(owner));
    }

    private static readonly IReadOnlyDictionary<string, HashSet<string>> NothingDeleted =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
}
