using Microsoft.EntityFrameworkCore;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

/// <summary>
/// Records who owns a <see cref="VocabularyWord"/>. A word with no owner gets one only
/// when exactly one account links to it, through that account's resources or its own
/// progress. Words linked to several accounts, or to none, keep a null owner; nothing is
/// guessed, deleted, or reassigned. Used by startup backfills and by
/// <see cref="ApplicationDbContext"/> after saves that add words, links, or progress.
/// </summary>
public static class VocabularyWordOwnership
{
    private const int UpdateBatchSize = 500;

    /// <summary>Outcome of one stamping pass.</summary>
    /// <param name="Stamped">Word id to the owner it was given.</param>
    /// <param name="Shared">Ownerless words linked to more than one account.</param>
    /// <param name="Unlinked">Ownerless words linked to no account.</param>
    public sealed record Result(
        IReadOnlyDictionary<string, string> Stamped, int Shared, int Unlinked);

    private sealed record OwnerLink(string WordId, string Owner);

    /// <summary>
    /// Words a learner may see, link, or reuse: words they own, or ownerless legacy words
    /// no other account uses that are either already this learner's (by resource link or
    /// progress) or carry no memory aids. Never another account's word, and never an
    /// ownerless word holding someone's memory aids.
    /// </summary>
    public static IQueryable<VocabularyWord> AvailableTo(ApplicationDbContext db, string userId) =>
        db.VocabularyWords.Where(word =>
            word.UserProfileId == userId
            || word.UserProfileId == null
                && !db.ResourceVocabularyMappings.Any(mapping =>
                    mapping.VocabularyWordId == word.Id
                    && !db.LearningResources.Any(resource =>
                        resource.Id == mapping.ResourceId && resource.UserProfileId == userId))
                && !db.VocabularyProgresses.Any(progress =>
                    progress.VocabularyWordId == word.Id && progress.UserId != userId)
                && (db.ResourceVocabularyMappings.Any(mapping => mapping.VocabularyWordId == word.Id)
                    || db.VocabularyProgresses.Any(progress => progress.VocabularyWordId == word.Id)
                    || (word.MnemonicText == null || word.MnemonicText == "")
                        && (word.MnemonicImageUri == null || word.MnemonicImageUri == "")));

    /// <summary>Stamps every ownerless word that has exactly one owner.</summary>
    public static Task<Result> BackfillAsync(
        ApplicationDbContext db, Action? assertSession = null,
        CancellationToken cancellationToken = default) =>
        StampAsync(db, null, assertSession, cancellationToken);

    /// <summary>
    /// Stamps the given ownerless words (or all ownerless words when <paramref name="wordIds"/>
    /// is null) that have exactly one owner.
    /// </summary>
    public static async Task<Result> StampAsync(
        ApplicationDbContext db, IReadOnlyCollection<string>? wordIds,
        Action? assertSession = null, CancellationToken cancellationToken = default)
    {
        if (wordIds is { Count: 0 })
            return Empty;

        var ownerless = OwnerlessIds(db, wordIds);
        var links = await MappingLinks(db, ownerless).ToListAsync(cancellationToken);
        links.AddRange(await ProgressLinks(db, ownerless).ToListAsync(cancellationToken));
        var ownerlessCount = await ownerless.CountAsync(cancellationToken);
        var (stamped, shared) = Plan(links);

        foreach (var batch in Batches(stamped))
        {
            assertSession?.Invoke();
            var ids = batch.Ids;
            await db.VocabularyWords
                .Where(word => ids.Contains(word.Id) && word.UserProfileId == null)
                .ExecuteUpdateAsync(set => set.SetProperty(
                    word => word.UserProfileId, batch.Owner), cancellationToken);
        }

        return new(stamped, shared, Math.Max(0, ownerlessCount - stamped.Count - shared));
    }

    /// <summary>Synchronous twin of <see cref="StampAsync"/> for <c>SaveChanges()</c>.</summary>
    public static Result Stamp(ApplicationDbContext db, IReadOnlyCollection<string> wordIds)
    {
        if (wordIds.Count == 0)
            return Empty;

        var ownerless = OwnerlessIds(db, wordIds);
        var links = MappingLinks(db, ownerless).ToList();
        links.AddRange(ProgressLinks(db, ownerless).ToList());
        var ownerlessCount = ownerless.Count();
        var (stamped, shared) = Plan(links);

        foreach (var batch in Batches(stamped))
        {
            var ids = batch.Ids;
            db.VocabularyWords
                .Where(word => ids.Contains(word.Id) && word.UserProfileId == null)
                .ExecuteUpdate(set => set.SetProperty(word => word.UserProfileId, batch.Owner));
        }

        return new(stamped, shared, Math.Max(0, ownerlessCount - stamped.Count - shared));
    }

    private static readonly Result Empty =
        new(new Dictionary<string, string>(StringComparer.Ordinal), 0, 0);

    private static IQueryable<string> OwnerlessIds(
        ApplicationDbContext db, IReadOnlyCollection<string>? wordIds)
    {
        var words = db.VocabularyWords.AsNoTracking()
            .Where(word => word.UserProfileId == null);
        if (wordIds is not null)
        {
            var ids = wordIds.ToArray();
            words = words.Where(word => ids.Contains(word.Id));
        }
        return words.Select(word => word.Id);
    }

    private static IQueryable<OwnerLink> MappingLinks(
        ApplicationDbContext db, IQueryable<string> ownerless) =>
        from mapping in db.ResourceVocabularyMappings.AsNoTracking()
        join resource in db.LearningResources.AsNoTracking()
            on mapping.ResourceId equals resource.Id
        where ownerless.Contains(mapping.VocabularyWordId)
            && resource.UserProfileId != null && resource.UserProfileId != ""
        select new OwnerLink(mapping.VocabularyWordId, resource.UserProfileId!);

    private static IQueryable<OwnerLink> ProgressLinks(
        ApplicationDbContext db, IQueryable<string> ownerless) =>
        db.VocabularyProgresses.AsNoTracking()
            .Where(progress => ownerless.Contains(progress.VocabularyWordId)
                && progress.UserId != null && progress.UserId != "")
            .Select(progress => new OwnerLink(progress.VocabularyWordId, progress.UserId));

    private static (Dictionary<string, string> Stamped, int Shared) Plan(
        IEnumerable<OwnerLink> links)
    {
        var stamped = new Dictionary<string, string>(StringComparer.Ordinal);
        var shared = 0;
        foreach (var word in links.GroupBy(link => link.WordId, StringComparer.Ordinal))
        {
            var owners = word.Select(link => link.Owner)
                .Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if (owners.Length == 1)
                stamped[word.Key] = owners[0];
            else
                shared++;
        }
        return (stamped, shared);
    }

    private static IEnumerable<(string Owner, string[] Ids)> Batches(
        Dictionary<string, string> stamped) =>
        stamped.GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .SelectMany(owner => owner.Select(pair => pair.Key)
                .Chunk(UpdateBatchSize)
                .Select(ids => (owner.Key, ids)));
}
