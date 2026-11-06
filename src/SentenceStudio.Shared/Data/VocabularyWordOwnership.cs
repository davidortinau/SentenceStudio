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

    /// <summary>
    /// The one runtime rule for "this word is the learner's" (Captain 2026-11-06), used by every
    /// client (web, MAUI, native), the API's owner-checked sync and the Coach for delete, merge,
    /// edit and memory-aid visibility: the word is recorded to the learner
    /// (<see cref="VocabularyWord.UserProfileId"/>). Nothing is inferred from links or progress
    /// at runtime. A word recorded to no one is no one's: read-only for everyone, its memory aids
    /// hidden, until the one-time backfill (scripts/assign-vocabulary-word-owners.sql) records
    /// its single user. Example sentences follow the same rule on their own
    /// <see cref="ExampleSentence.UserProfileId"/>. Returns the learner's words among
    /// <paramref name="ids"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is null or blank (fail closed).</exception>
    public static async Task<HashSet<string>> OwnedByAsync(
        ApplicationDbContext db, IReadOnlyCollection<string> ids, string owner,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("An owner is required to decide whose words these are.",
                nameof(owner));
        if (ids.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);
        var wanted = ids.Distinct(StringComparer.Ordinal).ToArray();
        return (await db.VocabularyWords.AsNoTracking()
                .Where(word => wanted.Contains(word.Id) && word.UserProfileId == owner)
                .Select(word => word.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Outcome of one stamping pass.</summary>
    /// <param name="Stamped">Word id to the owner it was given.</param>
    /// <param name="Shared">Ownerless words linked to more than one account.</param>
    /// <param name="Unlinked">Ownerless words linked to no account.</param>
    public sealed record Result(
        IReadOnlyDictionary<string, string> Stamped, int Shared, int Unlinked)
    {
        /// <summary>Ownerless example sentences of the stamped words that took the same owner.</summary>
        public int SentencesStamped { get; init; }

        /// <summary>
        /// Ownerless words whose only link names an account that is not a profile (for example
        /// progress left behind by a deleted account). Left ownerless: only a profile can own a word.
        /// </summary>
        public int OwnerNotAProfile { get; init; }
    }

    private sealed record OwnerLink(string WordId, string Owner);

    /// <summary>
    /// Words a learner may see, link, or reuse: words they own, or ownerless legacy words
    /// no other account uses that are either already this learner's (by resource link or
    /// progress) or carry nothing of anyone else's: no memory aids, and no example sentence
    /// except the learner's own. Never another account's word. Linking an unlinked ownerless
    /// word stamps it, and its ownerless sentences, with the linker's owner (see
    /// <see cref="Stamp"/>), so one holding memory aids, ownerless sentences or another
    /// learner's sentences is offered to no one.
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
                        && (word.MnemonicImageUri == null || word.MnemonicImageUri == "")
                        // EF keeps C# null semantics: a null or '' owner differs from userId.
                        && !db.ExampleSentences.Any(sentence => sentence.VocabularyWordId == word.Id
                            && sentence.UserProfileId != userId)));

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
        var candidates = stamped.Values.Distinct(StringComparer.Ordinal).ToArray();
        var notAProfile = OwnersMustBeProfiles(db, wordIds)
            ? DropOwnersWithoutProfile(stamped, await db.UserProfiles.AsNoTracking()
                .Where(profile => candidates.Contains(profile.Id))
                .Select(profile => profile.Id).ToListAsync(cancellationToken))
            : 0;
        var sentences = 0;

        foreach (var batch in Batches(stamped))
        {
            assertSession?.Invoke();
            var ids = batch.Ids;
            await db.VocabularyWords
                .Where(word => ids.Contains(word.Id) && word.UserProfileId == null)
                .ExecuteUpdateAsync(set => set.SetProperty(
                    word => word.UserProfileId, batch.Owner), cancellationToken);
            sentences += await OwnerlessSentencesOf(db, ids)
                .ExecuteUpdateAsync(set => set.SetProperty(
                    sentence => sentence.UserProfileId, batch.Owner), cancellationToken);
        }

        return new(stamped, shared, Math.Max(0, ownerlessCount - stamped.Count - shared - notAProfile))
        {
            SentencesStamped = sentences,
            OwnerNotAProfile = notAProfile
        };
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
        var candidates = stamped.Values.Distinct(StringComparer.Ordinal).ToArray();
        var notAProfile = OwnersMustBeProfiles(db, wordIds)
            ? DropOwnersWithoutProfile(stamped, db.UserProfiles.AsNoTracking()
                .Where(profile => candidates.Contains(profile.Id))
                .Select(profile => profile.Id).ToList())
            : 0;
        var sentences = 0;

        foreach (var batch in Batches(stamped))
        {
            var ids = batch.Ids;
            db.VocabularyWords
                .Where(word => ids.Contains(word.Id) && word.UserProfileId == null)
                .ExecuteUpdate(set => set.SetProperty(word => word.UserProfileId, batch.Owner));
            sentences += OwnerlessSentencesOf(db, ids)
                .ExecuteUpdate(set => set.SetProperty(sentence => sentence.UserProfileId, batch.Owner));
        }

        return new(stamped, shared, Math.Max(0, ownerlessCount - stamped.Count - shared - notAProfile))
        {
            SentencesStamped = sentences,
            OwnerNotAProfile = notAProfile
        };
    }

    /// <summary>
    /// Gives every ownerless example sentence the owner its resource, or else its word, has
    /// (SyncOwnersPhase3's rule). The migration can only use word owners that already exist, and
    /// on the server they are stamped after it, at startup; this runs right after that step, so
    /// sentences whose word has just gained an owner are not left hidden. Idempotent: a sentence
    /// with an owner is never changed, and one with no provable owner stays ownerless.
    /// </summary>
    /// <returns>How many sentences gained an owner.</returns>
    public static async Task<int> StampExampleSentencesAsync(
        ApplicationDbContext db, Action? assertSession = null,
        CancellationToken cancellationToken = default)
    {
        assertSession?.Invoke();
        var fromResource = await db.ExampleSentences
            .Where(sentence => (sentence.UserProfileId == null || sentence.UserProfileId == "")
                && db.LearningResources.Any(resource => resource.Id == sentence.LearningResourceId
                    && resource.UserProfileId != null && resource.UserProfileId != ""))
            .ExecuteUpdateAsync(set => set.SetProperty(sentence => sentence.UserProfileId,
                sentence => db.LearningResources
                    .Where(resource => resource.Id == sentence.LearningResourceId)
                    .Select(resource => resource.UserProfileId)
                    .First()), cancellationToken);
        assertSession?.Invoke();
        var fromWord = await db.ExampleSentences
            .Where(sentence => (sentence.UserProfileId == null || sentence.UserProfileId == "")
                && db.VocabularyWords.Any(word => word.Id == sentence.VocabularyWordId
                    && word.UserProfileId != null && word.UserProfileId != ""))
            .ExecuteUpdateAsync(set => set.SetProperty(sentence => sentence.UserProfileId,
                sentence => db.VocabularyWords
                    .Where(word => word.Id == sentence.VocabularyWordId)
                    .Select(word => word.UserProfileId)
                    .First()), cancellationToken);
        return fromResource + fromWord;
    }

    /// <summary>
    /// The ownerless example sentences of words just given an owner, outside any owned resource
    /// (a sentence in a resource follows the resource's owner, which the migration already set).
    /// </summary>
    private static IQueryable<ExampleSentence> OwnerlessSentencesOf(
        ApplicationDbContext db, string[] wordIds) =>
        db.ExampleSentences.Where(sentence => wordIds.Contains(sentence.VocabularyWordId)
            && (sentence.UserProfileId == null || sentence.UserProfileId == "")
            && !db.LearningResources.Any(resource => resource.Id == sentence.LearningResourceId
                && resource.UserProfileId != null && resource.UserProfileId != ""));

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

    /// <summary>
    /// Only a profile can own a word. The server checks every planned owner, because progress
    /// left by deleted accounts exists there. A device's save stamps only words it has just
    /// created for the signed-in learner, whose profile row can arrive later with the first
    /// sync, so a device save skips the check. A full backfill checks wherever it runs.
    /// </summary>
    private static bool OwnersMustBeProfiles(
        ApplicationDbContext db, IReadOnlyCollection<string>? wordIds) =>
        wordIds is null || !db.Database.IsSqlite();

    /// <summary>Removes planned stamps whose owner is not an existing profile; returns how many.</summary>
    private static int DropOwnersWithoutProfile(
        Dictionary<string, string> stamped, IReadOnlyCollection<string> profileIds)
    {
        var profiles = profileIds.ToHashSet(StringComparer.Ordinal);
        var orphaned = stamped.Where(pair => !profiles.Contains(pair.Value))
            .Select(pair => pair.Key).ToList();
        foreach (var wordId in orphaned)
            stamped.Remove(wordId);
        return orphaned.Count;
    }

    private static IEnumerable<(string Owner, string[] Ids)> Batches(
        Dictionary<string, string> stamped) =>
        stamped.GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .SelectMany(owner => owner.Select(pair => pair.Key)
                .Chunk(UpdateBatchSize)
                .Select(ids => (owner.Key, ids)));
}
