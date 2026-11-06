using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

// VocabularyWord.UserProfileId records who owns a word (Captain 2026-11-06): a word gets its owner
// when it is created, a new word without one is refused, and nothing infers an owner after a save.
public sealed class VocabularyWordOwnershipTests
{
    [Fact]
    public async Task WordsKeepTheOwnerTheyWereCreatedWith()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);

        Assert.Equal(NativeSliceFixture.UserA, await OwnerAsync(fixture, fixture.WordId(NativeSliceFixture.UserA, 0)));
        Assert.Equal(NativeSliceFixture.UserA, await OwnerAsync(fixture, fixture.WordId(NativeSliceFixture.UserA, 1)));
        Assert.Equal(NativeSliceFixture.UserB, await OwnerAsync(fixture, fixture.WordId(NativeSliceFixture.UserB, 0)));
    }

    /// <summary>
    /// Fail closed: a new word must name the learner who creates it. Before, a device save
    /// stamped a new word from a link or progress row saved with it, and a word saved without
    /// either was kept with no owner (hidden from everyone, and never synced).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NewWordWithoutAnOwner_IsRefused_AndNothingInTheSaveIsWritten(string? owner)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = "no-owner", UserProfileId = owner, TargetLanguageTerm = "새", NativeLanguageTerm = "new"
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = "m-no-owner", ResourceId = fixture.ResourceId(NativeSliceFixture.UserA),
                VocabularyWordId = "no-owner"
            });
            var refused = await Assert.ThrowsAsync<VocabularyWordOwnerRequiredException>(
                () => db.SaveChangesAsync());
            Assert.Equal(new[] { "no-owner" }, refused.WordIds);
            Assert.Throws<VocabularyWordOwnerRequiredException>(() => db.SaveChanges());
        }

        using var check = fixture.Scope();
        Assert.False(await Db(check).VocabularyWords.AnyAsync(word => word.Id == "no-owner"));
        Assert.False(await Db(check).ResourceVocabularyMappings.AnyAsync(mapping => mapping.Id == "m-no-owner"));
    }

    [Fact]
    public async Task SavingALinkOrProgress_NeverInfersAnOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            db.VocabularyWords.AddRange(
                new VocabularyWord { Id = "linked-later", UserProfileId = NativeSliceFixture.UserA,
                    TargetLanguageTerm = "나중", NativeLanguageTerm = "later" },
                new VocabularyWord { Id = "progress-later", UserProfileId = NativeSliceFixture.UserA,
                    TargetLanguageTerm = "진도", NativeLanguageTerm = "progress" });
            await db.SaveChangesAsync();
            // Legacy words recorded to no one, as a device holds them from before owners.
            await NativeSliceFixture.RecordToNoOneAsync(db, "linked-later", "progress-later");
        }

        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                ResourceId = fixture.ResourceId(NativeSliceFixture.UserA),
                VocabularyWordId = "linked-later"
            });
            db.VocabularyProgresses.Add(new VocabularyProgress {
                Id = "progress-for-later", VocabularyWordId = "progress-later",
                UserId = NativeSliceFixture.UserA
            });
            await db.SaveChangesAsync();
        }

        Assert.Null(await OwnerAsync(fixture, "linked-later"));
        Assert.Null(await OwnerAsync(fixture, "progress-later"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task DetachedUpdateWithBlankOwner_KeepsTheStoredOwner(string? blank)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        var id = fixture.WordId(NativeSliceFixture.UserA, 0);

        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            // Mirrors callers that Update() a word object built outside this context.
            db.VocabularyWords.Update(new VocabularyWord {
                Id = id, UserProfileId = blank, TargetLanguageTerm = "edited elsewhere",
                NativeLanguageTerm = "edited", Language = "Korean"
            });
            await db.SaveChangesAsync();
        }

        using var check = fixture.Scope();
        var stored = await Db(check).VocabularyWords.AsNoTracking().SingleAsync(word => word.Id == id);
        Assert.Equal("edited elsewhere", stored.TargetLanguageTerm);
        Assert.Equal(NativeSliceFixture.UserA, stored.UserProfileId);
    }

    [Fact]
    public async Task LaterForeignLink_NeverChangesTheRecordedOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 0);
        var id = fixture.WordId(NativeSliceFixture.UserA, 0);
        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                ResourceId = fixture.ResourceId(NativeSliceFixture.UserB), VocabularyWordId = id
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(NativeSliceFixture.UserA, await OwnerAsync(fixture, id));
    }

    /// <summary>
    /// A device creates a word for the signed-in learner before that learner's profile row has
    /// synced (Android and MAUI get it with the first sync): the save names the learner, which
    /// is all the guard asks.
    /// </summary>
    [Fact]
    public async Task NewWordNamingTheLearner_SavesEvenBeforeTheProfileRowHasSynced()
    {
        using var fixture = new NativeSliceFixture();
        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            db.LearningResources.Add(new LearningResource
            {
                Id = "resource-pending", UserProfileId = "profile-not-synced-yet",
                Language = "Korean", MediaType = "Vocabulary List", Title = "Offline list"
            });
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = "new-offline", UserProfileId = "profile-not-synced-yet",
                TargetLanguageTerm = "새", NativeLanguageTerm = "new"
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = "m-offline", ResourceId = "resource-pending", VocabularyWordId = "new-offline"
            });
            await db.SaveChangesAsync();
        }

        using (var check = fixture.Scope())
            Assert.False(await Db(check).UserProfiles.AnyAsync(profile => profile.Id == "profile-not-synced-yet"));
        Assert.Equal("profile-not-synced-yet", await OwnerAsync(fixture, "new-offline"));
    }

    [Fact]
    public async Task Backfill_StampsOnlySingleOwnerWords_AndLeavesSharedAndUnlinkedOwnerless()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 0);
        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            await AddLegacyWordsAsync(db,
                new VocabularyWord { Id = "legacy-a", TargetLanguageTerm = "가", NativeLanguageTerm = "a" },
                new VocabularyWord { Id = "legacy-shared", TargetLanguageTerm = "나", NativeLanguageTerm = "b" },
                new VocabularyWord { Id = "legacy-unlinked", TargetLanguageTerm = "다", NativeLanguageTerm = "c",
                    MnemonicText = "nobody can be shown this" });
            // Links written by raw SQL, as CoreSync does.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ResourceVocabularyMapping (Id, ResourceId, VocabularyWordId)
                VALUES ('m-a', {0}, 'legacy-a'), ('m-shared-a', {0}, 'legacy-shared'),
                       ('m-shared-b', {1}, 'legacy-shared')
                """,
                fixture.ResourceId(NativeSliceFixture.UserA),
                fixture.ResourceId(NativeSliceFixture.UserB));
        }

        VocabularyWordOwnership.Result result;
        using (var scope = fixture.Scope())
            result = await VocabularyWordOwnership.BackfillAsync(Db(scope));

        Assert.Equal(NativeSliceFixture.UserA, Assert.Single(result.Stamped).Value);
        Assert.Equal(1, result.Shared);
        Assert.Equal(1, result.Unlinked);
        Assert.Equal(NativeSliceFixture.UserA, await OwnerAsync(fixture, "legacy-a"));
        Assert.Null(await OwnerAsync(fixture, "legacy-shared"));
        Assert.Null(await OwnerAsync(fixture, "legacy-unlinked"));

        using (var scope = fixture.Scope())
            Assert.Empty((await VocabularyWordOwnership.BackfillAsync(Db(scope))).Stamped);
    }

    [Fact]
    public async Task AddingAWord_NeverAdoptsAnotherAccountsUnlinkedWord()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 0);
        var aWord = fixture.WordId(NativeSliceFixture.UserA, 0);
        var term = $"target-{NativeSliceFixture.UserA}-0";
        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            var word = await db.VocabularyWords.SingleAsync(row => row.Id == aWord);
            word.MnemonicText = "A's private memory story";
            // A unlinks the word; the row and A's memory aid remain, still A's.
            db.ResourceVocabularyMappings.RemoveRange(
                db.ResourceVocabularyMappings.Where(mapping => mapping.VocabularyWordId == aWord));
            await db.SaveChangesAsync();
        }

        var repository = fixture.Provider.GetRequiredService<LearningResourceRepository>();
        Assert.Equal(ResourceVocabularyWriteStatus.Success,
            await repository.CreateAndAttachOwnedVocabularyAsync(
                fixture.ResourceId(NativeSliceFixture.UserB),
                new VocabularyWord { TargetLanguageTerm = term, NativeLanguageTerm = "B's meaning" },
                "Korean", NativeSliceFixture.UserB));

        using (var check = fixture.Scope())
        {
            var db = Db(check);
            var bWord = await db.ResourceVocabularyMappings
                .Where(mapping => mapping.ResourceId == fixture.ResourceId(NativeSliceFixture.UserB))
                .Join(db.VocabularyWords, mapping => mapping.VocabularyWordId, word => word.Id,
                    (_, word) => word)
                .SingleAsync();
            Assert.NotEqual(aWord, bWord.Id);
            Assert.Equal(NativeSliceFixture.UserB, bWord.UserProfileId);
            Assert.Null(bWord.MnemonicText);
            var aStored = await db.VocabularyWords.AsNoTracking().SingleAsync(word => word.Id == aWord);
            Assert.Equal(NativeSliceFixture.UserA, aStored.UserProfileId);
            Assert.Equal("A's private memory story", aStored.MnemonicText);
            Assert.False(await db.ResourceVocabularyMappings.AnyAsync(mapping => mapping.VocabularyWordId == aWord));
        }

        // The owner re-adding their own unlinked word reuses it.
        Assert.Equal(ResourceVocabularyWriteStatus.Success,
            await repository.CreateAndAttachOwnedVocabularyAsync(
                fixture.ResourceId(NativeSliceFixture.UserA),
                new VocabularyWord { TargetLanguageTerm = term, NativeLanguageTerm = "again" },
                "Korean", NativeSliceFixture.UserA));
        using var final = fixture.Scope();
        Assert.True(await Db(final).ResourceVocabularyMappings.AnyAsync(mapping =>
            mapping.VocabularyWordId == aWord
            && mapping.ResourceId == fixture.ResourceId(NativeSliceFixture.UserA)));
    }

    /// <summary>
    /// Reusing an existing word when adding one: only the learner's own word is reused. An
    /// ownerless word with the same term is never reused or linked; a new word recorded to the
    /// learner is created, and the ownerless row stays exactly as it was.
    /// </summary>
    [Fact]
    public async Task AddingAWord_NeverReusesAnOwnerlessWord()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        using (var scope = fixture.Scope())
            await AddLegacyWordsAsync(Db(scope), new VocabularyWord {
                Id = "ownerless-same-term", TargetLanguageTerm = "같은", NativeLanguageTerm = "same",
                MnemonicText = "whose story?" });

        var repository = fixture.Provider.GetRequiredService<LearningResourceRepository>();
        Assert.Equal(ResourceVocabularyWriteStatus.Success,
            await repository.CreateAndAttachOwnedVocabularyAsync(
                fixture.ResourceId(NativeSliceFixture.UserA),
                new VocabularyWord { TargetLanguageTerm = "같은", NativeLanguageTerm = "same" },
                "Korean", NativeSliceFixture.UserA));

        using var check = fixture.Scope();
        var db = Db(check);
        var linked = await db.ResourceVocabularyMappings
            .Where(mapping => mapping.ResourceId == fixture.ResourceId(NativeSliceFixture.UserA))
            .Join(db.VocabularyWords, mapping => mapping.VocabularyWordId, word => word.Id,
                (_, word) => word)
            .SingleAsync();
        Assert.NotEqual("ownerless-same-term", linked.Id);
        Assert.Equal(NativeSliceFixture.UserA, linked.UserProfileId);
        Assert.Null(linked.MnemonicText);
        var legacy = await db.VocabularyWords.AsNoTracking().SingleAsync(word => word.Id == "ownerless-same-term");
        Assert.Null(legacy.UserProfileId);
        Assert.Equal("whose story?", legacy.MnemonicText);
    }

    [Fact]
    public async Task TermLookups_ReturnOnlyTheLearnersOwnWords()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 0);
        var aWord = fixture.WordId(NativeSliceFixture.UserA, 0);
        var target = $"target-{NativeSliceFixture.UserA}-0";
        var native = $"native-{NativeSliceFixture.UserA}-0";
        using (var scope = fixture.Scope())
        {
            // A unlinks the word; it stays A's.
            var db = Db(scope);
            db.ResourceVocabularyMappings.RemoveRange(
                db.ResourceVocabularyMappings.Where(mapping => mapping.VocabularyWordId == aWord));
            await db.SaveChangesAsync();
            await AddLegacyWordsAsync(db, new VocabularyWord {
                Id = "ownerless-lookup", TargetLanguageTerm = "주인없음", NativeLanguageTerm = "nobody's" });
        }
        var repository = fixture.Provider.GetRequiredService<LearningResourceRepository>();

        Assert.Null(await repository.GetWordByTargetTermAsync(target, NativeSliceFixture.UserB));
        Assert.Null(await repository.GetWordByNativeTermAsync(native, NativeSliceFixture.UserB));
        Assert.Equal(aWord, (await repository.GetWordByTargetTermAsync(target, NativeSliceFixture.UserA))?.Id);
        Assert.Equal(aWord, (await repository.GetWordByNativeTermAsync(native, NativeSliceFixture.UserA))?.Id);
        Assert.Null(await repository.GetWordByTargetTermAsync("주인없음", NativeSliceFixture.UserA));
        Assert.Null(await repository.GetWordByNativeTermAsync("nobody's", NativeSliceFixture.UserA));
    }

    /// <summary>
    /// Under the one ownership rule an ownerless word is offered to no one, whatever it holds
    /// (before 2026-11-06 an unused ownerless word with no memory aids or sentences was offered
    /// and linking it stamped it).
    /// </summary>
    [Fact]
    public async Task OrphanListAndLinking_OfferOnlyTheLearnersOwnUnlinkedWords()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);
        var aWord = fixture.WordId(NativeSliceFixture.UserA, 0);
        var bWord = fixture.WordId(NativeSliceFixture.UserB, 0);
        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            db.ResourceVocabularyMappings.RemoveRange(db.ResourceVocabularyMappings
                .Where(mapping => mapping.VocabularyWordId == aWord || mapping.VocabularyWordId == bWord));
            await db.SaveChangesAsync();
            await AddLegacyWordsAsync(db,
                new VocabularyWord {
                    Id = "ownerless-orphan", TargetLanguageTerm = "주인없음", NativeLanguageTerm = "nobody's",
                    MnemonicText = "whose story?" },
                new VocabularyWord {
                    Id = "plain-orphan", TargetLanguageTerm = "평범", NativeLanguageTerm = "plain" });
            await InsertSentenceAsync(db, "s-own", "plain-orphan", resourceId: null, owner: NativeSliceFixture.UserB);
        }
        var repository = fixture.Provider.GetRequiredService<LearningResourceRepository>();
        var bResource = fixture.ResourceId(NativeSliceFixture.UserB);

        var orphans = await repository.GetOrphanedVocabularyWordsAsync(NativeSliceFixture.UserB);
        Assert.Equal(new[] { bWord }, orphans.Select(word => word.Id));

        Assert.False(await repository.AddVocabularyToResourceAsync(bResource, aWord, NativeSliceFixture.UserB));
        Assert.False(await repository.AddVocabularyToResourceAsync(bResource, "ownerless-orphan", NativeSliceFixture.UserB));
        Assert.False(await repository.AddVocabularyToResourceAsync(bResource, "plain-orphan", NativeSliceFixture.UserB));
        Assert.False(await repository.BulkAssociateWordsWithResourceAsync(bResource, [aWord], NativeSliceFixture.UserB));
        Assert.False(await repository.BulkAssociateWordsWithResourceAsync(bResource, ["plain-orphan"], NativeSliceFixture.UserB));
        Assert.True(await repository.AddVocabularyToResourceAsync(bResource, bWord, NativeSliceFixture.UserB));

        using var check = fixture.Scope();
        var rows = Db(check);
        Assert.False(await rows.ResourceVocabularyMappings.AnyAsync(mapping =>
            mapping.VocabularyWordId == aWord || mapping.VocabularyWordId == "ownerless-orphan"
            || mapping.VocabularyWordId == "plain-orphan"));
        Assert.Equal(NativeSliceFixture.UserA, await OwnerAsync(fixture, aWord));
        Assert.Null(await OwnerAsync(fixture, "ownerless-orphan"));
        Assert.Null(await OwnerAsync(fixture, "plain-orphan"));
    }

    /// <summary>
    /// Found rehearsing the deploy on a production copy (2026-11-04): SyncOwnersPhase3 copies a
    /// sentence's owner from its word, but the server stamps word owners after the migration, so
    /// every example sentence stayed ownerless and the server hid them all. Stamping a word now
    /// gives its ownerless sentences the same owner.
    /// </summary>
    [Fact]
    public async Task StampingAWord_GivesItsOwnerlessExampleSentencesTheSameOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 0);
        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            await AddLegacyWordsAsync(db, new VocabularyWord { Id = "legacy-a", TargetLanguageTerm = "가", NativeLanguageTerm = "a" });
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO ResourceVocabularyMapping (Id, ResourceId, VocabularyWordId) VALUES ('m-a', {0}, 'legacy-a')",
                fixture.ResourceId(NativeSliceFixture.UserA));
            await InsertSentenceAsync(db, "s-plain", "legacy-a", resourceId: null, owner: null);
            await InsertSentenceAsync(db, "s-owned", "legacy-a", resourceId: null, owner: NativeSliceFixture.UserB);
        }

        VocabularyWordOwnership.Result result;
        using (var scope = fixture.Scope())
            result = await VocabularyWordOwnership.BackfillAsync(Db(scope));

        Assert.Equal(1, result.SentencesStamped);
        Assert.Equal(NativeSliceFixture.UserA, await OwnerAsync(fixture, "legacy-a"));
        Assert.Equal(NativeSliceFixture.UserA, await SentenceOwnerAsync(fixture, "s-plain"));
        Assert.Equal(NativeSliceFixture.UserB, await SentenceOwnerAsync(fixture, "s-owned"));
    }

    /// <summary>
    /// The startup pass: an ownerless sentence takes its resource's owner, or else its word's,
    /// and one with neither stays ownerless (never guessed). A second run changes nothing.
    /// </summary>
    [Fact]
    public async Task ExampleSentenceBackfill_UsesTheResourceOwnerThenTheWordOwner_AndChangesNothingTwice()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 0);
        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            db.VocabularyWords.Add(
                new VocabularyWord { Id = "owned-a", UserProfileId = NativeSliceFixture.UserA,
                    TargetLanguageTerm = "가", NativeLanguageTerm = "a" });
            await db.SaveChangesAsync();
            await AddLegacyWordsAsync(db,
                new VocabularyWord { Id = "unowned", TargetLanguageTerm = "나", NativeLanguageTerm = "b" });
            await InsertSentenceAsync(db, "s-word", "owned-a", resourceId: null, owner: null);
            await InsertSentenceAsync(db, "s-resource", "unowned", fixture.ResourceId(NativeSliceFixture.UserB), owner: null);
            await InsertSentenceAsync(db, "s-none", "unowned", resourceId: null, owner: "");
        }

        using (var scope = fixture.Scope())
            Assert.Equal(2, await VocabularyWordOwnership.StampExampleSentencesAsync(Db(scope)));

        Assert.Equal(NativeSliceFixture.UserA, await SentenceOwnerAsync(fixture, "s-word"));
        Assert.Equal(NativeSliceFixture.UserB, await SentenceOwnerAsync(fixture, "s-resource"));
        Assert.True(string.IsNullOrEmpty(await SentenceOwnerAsync(fixture, "s-none")));

        using (var scope = fixture.Scope())
            Assert.Equal(0, await VocabularyWordOwnership.StampExampleSentencesAsync(Db(scope)));
    }

    /// <summary>
    /// Found rehearsing on the 2026-11-05 production dump: 28 words were linked only by progress
    /// rows naming accounts that no longer exist, and the backfill stamped those IDs as owners.
    /// Only a profile can own a word, so such words stay ownerless. A word shared by a learner
    /// and a deleted account still counts as shared.
    /// </summary>
    [Fact]
    public async Task Backfill_NeverStampsAnOwnerThatIsNotAProfile()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            await AddLegacyWordsAsync(db,
                new VocabularyWord { Id = "ghost-only", TargetLanguageTerm = "유령", NativeLanguageTerm = "ghost" },
                new VocabularyWord { Id = "ghost-and-a", TargetLanguageTerm = "함께", NativeLanguageTerm = "together" });
            db.VocabularyProgresses.AddRange(
                new VocabularyProgress { VocabularyWordId = "ghost-only", UserId = "deleted-account" },
                new VocabularyProgress { VocabularyWordId = "ghost-and-a", UserId = "deleted-account" });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO ResourceVocabularyMapping (Id, ResourceId, VocabularyWordId) VALUES ('m-a', {0}, 'ghost-and-a')",
                fixture.ResourceId(NativeSliceFixture.UserA));
        }

        VocabularyWordOwnership.Result result;
        using (var scope = fixture.Scope())
            result = await VocabularyWordOwnership.BackfillAsync(Db(scope));

        Assert.Empty(result.Stamped);
        Assert.Equal(1, result.OwnerNotAProfile);
        Assert.Equal(1, result.Shared);
        Assert.Equal(0, result.Unlinked);
        Assert.Null(await OwnerAsync(fixture, "ghost-only"));
        Assert.Null(await OwnerAsync(fixture, "ghost-and-a"));
    }

    /// <summary>
    /// Review finding (2026-11-05): a sentence whose resource belongs to B keeps B as its
    /// owner even when its word is stamped to A. The word pass skips it; the sentence pass
    /// gives it the resource owner first.
    /// </summary>
    [Fact]
    public async Task SentenceOnAnotherLearnersResource_TakesTheResourceOwner_NotTheWordOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 0);
        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            await AddLegacyWordsAsync(db, new VocabularyWord { Id = "linked-a", TargetLanguageTerm = "가", NativeLanguageTerm = "a" });
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO ResourceVocabularyMapping (Id, ResourceId, VocabularyWordId) VALUES ('m-a', {0}, 'linked-a')",
                fixture.ResourceId(NativeSliceFixture.UserA));
            await InsertSentenceAsync(db, "s-b-resource", "linked-a", fixture.ResourceId(NativeSliceFixture.UserB), owner: null);
        }

        VocabularyWordOwnership.Result result;
        using (var scope = fixture.Scope())
            result = await VocabularyWordOwnership.BackfillAsync(Db(scope));
        Assert.Equal(NativeSliceFixture.UserA, await OwnerAsync(fixture, "linked-a"));
        Assert.Equal(0, result.SentencesStamped);
        Assert.True(string.IsNullOrEmpty(await SentenceOwnerAsync(fixture, "s-b-resource")));

        using (var scope = fixture.Scope())
            Assert.Equal(1, await VocabularyWordOwnership.StampExampleSentencesAsync(Db(scope)));
        Assert.Equal(NativeSliceFixture.UserB, await SentenceOwnerAsync(fixture, "s-b-resource"));
    }

    /// <summary>
    /// Data preservation for item 2 (Captain 2026-11-06): a MAUI install upgraded from main holds
    /// the learner's own example sentences with no owner (written before sentences had owners, never
    /// synced; the migration could not take the owner from a word whose owner arrives only with the
    /// first sync). Once their word is recorded to the signed-in learner, each takes the learner as
    /// owner when its resource is theirs or absent, the parent's-owner rule of SyncOwnersPhase3.
    /// Nothing else changes, and a second run changes nothing.
    /// </summary>
    [Fact]
    public async Task DeviceClaim_GivesTheLearnerOnlyOwnerlessSentencesOfTheirWordsOnTheirResources()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);
        var aWord = fixture.WordId(NativeSliceFixture.UserA, 0);
        var bWord = fixture.WordId(NativeSliceFixture.UserB, 0);
        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            await AddLegacyWordsAsync(db, new VocabularyWord {
                Id = "ownerless-word", TargetLanguageTerm = "없음", NativeLanguageTerm = "none" });
            await InsertSentenceAsync(db, "a-no-resource", aWord, resourceId: null, owner: null);
            await InsertSentenceAsync(db, "a-blank-owner", aWord, resourceId: null, owner: "");
            await InsertSentenceAsync(db, "a-own-resource", aWord, fixture.ResourceId(NativeSliceFixture.UserA), owner: null);
            await InsertSentenceAsync(db, "a-word-b-resource", aWord, fixture.ResourceId(NativeSliceFixture.UserB), owner: null);
            await InsertSentenceAsync(db, "a-already-b", aWord, resourceId: null, owner: NativeSliceFixture.UserB);
            await InsertSentenceAsync(db, "b-word", bWord, resourceId: null, owner: null);
            await InsertSentenceAsync(db, "ownerless-word-sentence", "ownerless-word", resourceId: null, owner: null);
        }

        using (var scope = fixture.Scope())
            Assert.Equal(3, await VocabularyWordOwnership.ClaimOwnSentencesOnDeviceAsync(
                Db(scope), NativeSliceFixture.UserA));

        Assert.Equal(NativeSliceFixture.UserA, await SentenceOwnerAsync(fixture, "a-no-resource"));
        Assert.Equal(NativeSliceFixture.UserA, await SentenceOwnerAsync(fixture, "a-blank-owner"));
        Assert.Equal(NativeSliceFixture.UserA, await SentenceOwnerAsync(fixture, "a-own-resource"));
        Assert.Null(await SentenceOwnerAsync(fixture, "a-word-b-resource"));
        Assert.Equal(NativeSliceFixture.UserB, await SentenceOwnerAsync(fixture, "a-already-b"));
        Assert.Null(await SentenceOwnerAsync(fixture, "b-word"));
        Assert.Null(await SentenceOwnerAsync(fixture, "ownerless-word-sentence"));

        using (var scope = fixture.Scope())
        {
            Assert.Equal(0, await VocabularyWordOwnership.ClaimOwnSentencesOnDeviceAsync(
                Db(scope), NativeSliceFixture.UserA));
            Assert.Equal(7, await Db(scope).ExampleSentences.CountAsync());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task DeviceClaim_WithNoLearner_RefusesAndChangesNothing(string? owner)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        using (var scope = fixture.Scope())
            await InsertSentenceAsync(Db(scope), "s", fixture.WordId(NativeSliceFixture.UserA, 0),
                resourceId: null, owner: null);

        using (var scope = fixture.Scope())
            await Assert.ThrowsAsync<ArgumentException>(() =>
                VocabularyWordOwnership.ClaimOwnSentencesOnDeviceAsync(Db(scope), owner!));
        Assert.Null(await SentenceOwnerAsync(fixture, "s"));
    }

    /// <summary>
    /// Saves words as legacy rows recorded to no one: the save guard refuses a new ownerless
    /// word, so each is saved with a placeholder owner that is then cleared outside SaveChanges.
    /// </summary>
    private static async Task AddLegacyWordsAsync(ApplicationDbContext db, params VocabularyWord[] words)
    {
        foreach (var word in words)
            word.UserProfileId = "legacy-placeholder";
        db.VocabularyWords.AddRange(words);
        await db.SaveChangesAsync();
        await NativeSliceFixture.RecordToNoOneAsync(db, words.Select(word => word.Id).ToArray());
        foreach (var word in words)
            db.Entry(word).State = EntityState.Detached;
    }

    /// <summary>Writes a sentence the way CoreSync and old rows arrive: raw, past every save hook.</summary>
    private static Task InsertSentenceAsync(
        ApplicationDbContext db, string id, string wordId, string? resourceId, string? owner) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ExampleSentence (Id, VocabularyWordId, LearningResourceId, TargetSentence, IsCore,
                CreatedAt, UpdatedAt, Source, Status, Register, IsFlagged, UserProfileId)
            VALUES (@id, @word, @resource, '문장', 0, '2026-11-04 00:00:00', '2026-11-04 00:00:00', 0, 0, 0, 0, @owner)
            """,
            new Microsoft.Data.Sqlite.SqliteParameter("@id", id),
            new Microsoft.Data.Sqlite.SqliteParameter("@word", wordId),
            new Microsoft.Data.Sqlite.SqliteParameter("@resource", (object?)resourceId ?? DBNull.Value),
            new Microsoft.Data.Sqlite.SqliteParameter("@owner", (object?)owner ?? DBNull.Value));

    private static async Task<string?> SentenceOwnerAsync(NativeSliceFixture fixture, string sentenceId)
    {
        using var scope = fixture.Scope();
        return await Db(scope).ExampleSentences.AsNoTracking()
            .Where(sentence => sentence.Id == sentenceId)
            .Select(sentence => sentence.UserProfileId)
            .SingleAsync();
    }

    private static ApplicationDbContext Db(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    private static async Task<string?> OwnerAsync(NativeSliceFixture fixture, string wordId)
    {
        using var scope = fixture.Scope();
        return await Db(scope).VocabularyWords.AsNoTracking()
            .Where(word => word.Id == wordId)
            .Select(word => word.UserProfileId)
            .SingleAsync();
    }
}
