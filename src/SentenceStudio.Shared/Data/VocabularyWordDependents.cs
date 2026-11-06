using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Data;

/// <summary>
/// The one rule for which of an owner's words another learner's data still uses. The API's
/// owner-checked sync applies a device's word delete only when this set leaves the word out
/// (NativeOwnerSyncProvider), and the native word editor offers edit and delete on the same
/// rule, so a device never changes a word the server would keep.
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
    public static async Task<HashSet<string>> OthersDependOnAsync(
        ApplicationDbContext db, string[] ids, string owner, CancellationToken ct)
    {
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
}
