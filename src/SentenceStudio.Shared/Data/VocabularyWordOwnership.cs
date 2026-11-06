using Microsoft.EntityFrameworkCore;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

/// <summary>
/// Whose a <see cref="VocabularyWord"/> is (Captain 2026-11-06): "every word should have an owner
/// and only be visible and used by those owners." A word is a learner's only when it is recorded
/// to them (<see cref="VocabularyWord.UserProfileId"/>). Every client (web, MAUI, native), the
/// API's owner-checked sync and the Coach read, reuse, edit, merge and delete words by that rule
/// alone; a word recorded to no one (NULL or '') is no one's and shown to no one. Nothing is
/// inferred from links or progress at runtime: a word gets its owner when it is created (every
/// creation path sets it, and <see cref="ApplicationDbContext"/> refuses a new word without one).
/// Example sentences follow the same rule on their own <see cref="ExampleSentence.UserProfileId"/>.
/// The one-time startup <see cref="BackfillAsync"/> and scripts/assign-vocabulary-word-owners.sql
/// recorded the owners of legacy words.
/// </summary>
public static class VocabularyWordOwnership
{
    private const int UpdateBatchSize = 500;

    /// <summary>
    /// The learner's words: those recorded to <paramref name="owner"/>. The one place the rule is
    /// written as a query; a NULL or '' owner never equals a learner.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is null or blank (fail closed).</exception>
    public static IQueryable<VocabularyWord> OwnedBy(this IQueryable<VocabularyWord> words, string owner)
    {
        RequireOwner(owner);
        return words.Where(word => word.UserProfileId == owner);
    }

    /// <summary>The learner's example sentences: those recorded to <paramref name="owner"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is null or blank (fail closed).</exception>
    public static IQueryable<ExampleSentence> OwnedBy(this IQueryable<ExampleSentence> sentences, string owner)
    {
        RequireOwner(owner);
        return sentences.Where(sentence => sentence.UserProfileId == owner);
    }

    /// <summary>
    /// Whether a recorded owner names the learner: false for a NULL or '' recorded owner and for a
    /// blank <paramref name="owner"/>, so an absent identity never matches.
    /// </summary>
    public static bool IsLearners(string? recordedOwner, string? owner) =>
        !string.IsNullOrWhiteSpace(owner)
        && string.Equals(recordedOwner, owner, StringComparison.Ordinal);

    /// <summary>A recorded owner that names no one: NULL, '' or blank, the same everywhere.</summary>
    public static bool IsOwnerless(string? recordedOwner) => string.IsNullOrWhiteSpace(recordedOwner);

    /// <summary>
    /// The learner's words among <paramref name="ids"/>
    /// (<see cref="OwnedBy(IQueryable{VocabularyWord}, string)"/>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is null or blank (fail closed).</exception>
    public static async Task<HashSet<string>> OwnedByAsync(
        ApplicationDbContext db, IReadOnlyCollection<string> ids, string owner,
        CancellationToken cancellationToken = default)
    {
        RequireOwner(owner);
        if (ids.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);
        var wanted = ids.Distinct(StringComparer.Ordinal).ToArray();
        return (await db.VocabularyWords.AsNoTracking().OwnedBy(owner)
                .Where(word => wanted.Contains(word.Id))
                .Select(word => word.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Words a learner may see, link or reuse: only their own
    /// (<see cref="OwnedBy(IQueryable{VocabularyWord}, string)"/>), never another account's word
    /// or one recorded to no one.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="userId"/> is null or blank (fail closed).</exception>
    public static IQueryable<VocabularyWord> AvailableTo(ApplicationDbContext db, string userId) =>
        db.VocabularyWords.OwnedBy(userId);

    /// <summary>
    /// On a device, gives the signed-in learner each example sentence recorded to no one whose
    /// word is recorded to them and whose resource, if it has one, is theirs: the parent's owner,
    /// the rule of the SyncOwnersPhase3 migration and <see cref="StampExampleSentencesAsync"/>.
    /// Such sentences exist on MAUI installs upgraded from main: they were written on the device
    /// before sentences had owners or synced, and the migration could not take the owner from a
    /// word whose own owner only arrives with the first sync afterwards. Without this they would
    /// stay hidden (and unsynced) for good. Idempotent: a sentence with an owner is never changed,
    /// and nothing is deleted. Returns how many sentences took the owner.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is null or blank (fail closed).</exception>
    public static Task<int> ClaimOwnSentencesOnDeviceAsync(
        ApplicationDbContext db, string owner, CancellationToken cancellationToken = default)
    {
        RequireOwner(owner);
        return db.ExampleSentences
            .Where(sentence => (sentence.UserProfileId == null || sentence.UserProfileId == "")
                && db.VocabularyWords.Any(word => word.Id == sentence.VocabularyWordId
                    && word.UserProfileId == owner)
                && (sentence.LearningResourceId == null
                    || db.LearningResources.Any(resource => resource.Id == sentence.LearningResourceId
                        && resource.UserProfileId == owner)))
            .ExecuteUpdateAsync(set => set.SetProperty(sentence => sentence.UserProfileId, owner),
                cancellationToken);
    }

    private static void RequireOwner(string? owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("An owner is required to decide whose words these are.",
                nameof(owner));
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

    /// <summary>Stamps every ownerless word that has exactly one owner.</summary>
    public static Task<Result> BackfillAsync(
        ApplicationDbContext db, Action? assertSession = null,
        CancellationToken cancellationToken = default) =>
        StampAsync(db, null, assertSession, cancellationToken);

    /// <summary>
    /// Stamps the given ownerless words (or all ownerless words when <paramref name="wordIds"/>
    /// is null) that have exactly one owner. Only the one-time startup backfill runs it: saves and
    /// sync uploads never infer an owner (Captain 2026-11-06).
    /// </summary>
    private static async Task<Result> StampAsync(
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
    /// Only a profile can own a word: the backfill checks every planned owner, because progress
    /// left by deleted accounts exists. (<paramref name="wordIds"/> is always null now that saves
    /// no longer stamp; the backfill is left as it ran in production.)
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
