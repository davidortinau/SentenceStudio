using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

// VocabularyWord.UserProfileId records who owns a word and its memory aids.
public sealed class VocabularyWordOwnershipTests
{
    [Fact]
    public async Task WordSavedWithItsResourceLink_IsOwnedByThatResourcesOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);

        Assert.Equal(NativeSliceFixture.UserA, await OwnerAsync(fixture, fixture.WordId(NativeSliceFixture.UserA, 0)));
        Assert.Equal(NativeSliceFixture.UserA, await OwnerAsync(fixture, fixture.WordId(NativeSliceFixture.UserA, 1)));
        Assert.Equal(NativeSliceFixture.UserB, await OwnerAsync(fixture, fixture.WordId(NativeSliceFixture.UserB, 0)));
    }

    [Fact]
    public async Task OnDevices_ExistingWordGainingALinkStaysOwnerless_UntilTheServerStamps()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            db.VocabularyWords.AddRange(
                new VocabularyWord { Id = "linked-later", TargetLanguageTerm = "나중", NativeLanguageTerm = "later" },
                new VocabularyWord { Id = "progress-later", TargetLanguageTerm = "진도", NativeLanguageTerm = "progress" });
            await db.SaveChangesAsync();
        }
        Assert.Null(await OwnerAsync(fixture, "linked-later"));

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

        // A SQLite store sees only its own account's links, so it never infers an
        // owner for an existing word; the word stays ownerless (fails closed).
        Assert.Null(await OwnerAsync(fixture, "linked-later"));
        Assert.Null(await OwnerAsync(fixture, "progress-later"));

        // The server's routine (full view) stamps it, e.g. after the upload applies.
        using (var scope = fixture.Scope())
            await VocabularyWordOwnership.StampAsync(Db(scope), ["linked-later", "progress-later"]);
        Assert.Equal(NativeSliceFixture.UserA, await OwnerAsync(fixture, "linked-later"));
        Assert.Equal(NativeSliceFixture.UserA, await OwnerAsync(fixture, "progress-later"));
    }

    [Fact]
    public async Task DetachedUpdateWithBlankOwner_KeepsTheStoredOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        var id = fixture.WordId(NativeSliceFixture.UserA, 0);

        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            // Mirrors callers that Update() a word object built outside this context.
            db.VocabularyWords.Update(new VocabularyWord {
                Id = id, TargetLanguageTerm = "edited elsewhere",
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

    [Fact]
    public async Task Backfill_StampsOnlySingleOwnerWords_AndLeavesSharedAndUnlinkedOwnerless()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 0);
        using (var scope = fixture.Scope())
        {
            var db = Db(scope);
            db.VocabularyWords.AddRange(
                new VocabularyWord { Id = "legacy-a", TargetLanguageTerm = "가", NativeLanguageTerm = "a" },
                new VocabularyWord { Id = "legacy-shared", TargetLanguageTerm = "나", NativeLanguageTerm = "b" },
                new VocabularyWord { Id = "legacy-unlinked", TargetLanguageTerm = "다", NativeLanguageTerm = "c",
                    MnemonicText = "nobody can be shown this" });
            await db.SaveChangesAsync();
            // Links written by raw SQL, as CoreSync does, bypass the save hook.
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
            // A unlinks the word; the global row and A's memory aid remain.
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
        }
        var repository = fixture.Provider.GetRequiredService<LearningResourceRepository>();

        Assert.Null(await repository.GetWordByTargetTermAsync(target, NativeSliceFixture.UserB));
        Assert.Null(await repository.GetWordByNativeTermAsync(native, NativeSliceFixture.UserB));
        Assert.Equal(aWord, (await repository.GetWordByTargetTermAsync(target, NativeSliceFixture.UserA))?.Id);
        Assert.Equal(aWord, (await repository.GetWordByNativeTermAsync(native, NativeSliceFixture.UserA))?.Id);
    }

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
            db.VocabularyWords.AddRange(
                new VocabularyWord {
                    Id = "ownerless-orphan", TargetLanguageTerm = "주인없음", NativeLanguageTerm = "nobody's",
                    MnemonicText = "whose story?" },
                // Unused, ownerless, and holding no memory aids: nothing private to take.
                new VocabularyWord {
                    Id = "plain-orphan", TargetLanguageTerm = "평범", NativeLanguageTerm = "plain" });
            await db.SaveChangesAsync();
        }
        var repository = fixture.Provider.GetRequiredService<LearningResourceRepository>();
        var bResource = fixture.ResourceId(NativeSliceFixture.UserB);

        var orphans = await repository.GetOrphanedVocabularyWordsAsync(NativeSliceFixture.UserB);
        Assert.Equal(new[] { "plain-orphan", bWord }.Order(),
            orphans.Select(word => word.Id).Order());

        Assert.False(await repository.AddVocabularyToResourceAsync(bResource, aWord, NativeSliceFixture.UserB));
        Assert.False(await repository.AddVocabularyToResourceAsync(bResource, "ownerless-orphan", NativeSliceFixture.UserB));
        Assert.False(await repository.BulkAssociateWordsWithResourceAsync(bResource, [aWord], NativeSliceFixture.UserB));
        Assert.True(await repository.AddVocabularyToResourceAsync(bResource, bWord, NativeSliceFixture.UserB));
        Assert.True(await repository.AddVocabularyToResourceAsync(bResource, "plain-orphan", NativeSliceFixture.UserB));

        using var check = fixture.Scope();
        var rows = Db(check);
        Assert.False(await rows.ResourceVocabularyMappings.AnyAsync(mapping =>
            mapping.VocabularyWordId == aWord || mapping.VocabularyWordId == "ownerless-orphan"));
        Assert.Equal(NativeSliceFixture.UserA, await OwnerAsync(fixture, aWord));
        Assert.Null(await OwnerAsync(fixture, "ownerless-orphan"));
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
