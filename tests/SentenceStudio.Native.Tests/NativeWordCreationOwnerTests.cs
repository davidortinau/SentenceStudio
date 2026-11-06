using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Every native path that creates a word records the learner who creates it (Captain
/// 2026-11-06: "every word should have an owner"); nothing infers an owner after the save any
/// more. Two learners share the device database, so a word is never recorded to the other one.
/// Reading's save-word path is covered by NativeReadingPresenterTests and the import commit by
/// NativeContentImportTests; the save guard's refusal by VocabularyWordOwnershipTests.
/// </summary>
public sealed class NativeWordCreationOwnerTests
{
    private const string UserA = NativeSliceFixture.UserA;
    private const string UserB = NativeSliceFixture.UserB;

    [Fact]
    public async Task NewResourceWithStagedWords_RecordsEachWordToItsCreator()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA, wordCount: 0);
        await fixture.SeedAsync(UserB, wordCount: 0);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var aWord = Guid.NewGuid().ToString();
        var bWord = Guid.NewGuid().ToString();

        fixture.Activate(UserA);
        Assert.Equal(NativeResourceMutationResult.Success, (await store.CreateAsync(UserA,
            new NativeResourceDraft("A's list", null, null, MediaType: "Vocabulary List",
                Language: "Korean", StagedVocabulary: [new NativeResourceVocabularyRow(aWord, "하나", "one")]),
            default)).Result);
        fixture.Activate(UserB);
        Assert.Equal(NativeResourceMutationResult.Success, (await store.CreateAsync(UserB,
            new NativeResourceDraft("B's list", null, null, MediaType: "Vocabulary List",
                Language: "Korean", StagedVocabulary: [new NativeResourceVocabularyRow(bWord, "둘", "two")]),
            default)).Result);

        Assert.Equal(UserA, await OwnerAsync(fixture, aWord));
        Assert.Equal(UserB, await OwnerAsync(fixture, bWord));
    }

    [Fact]
    public async Task QuickCreateAndImportIntoAResource_RecordTheWordsToTheirCreator()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA, wordCount: 0);
        await fixture.SeedAsync(UserB, wordCount: 0);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var created = Guid.NewGuid().ToString();
        var imported = Guid.NewGuid().ToString();

        fixture.Activate(UserA);
        Assert.Equal(NativeResourceMutationResult.Success, await store.CreateVocabularyAsync(UserA,
            fixture.ResourceId(UserA), new NativeResourceVocabularyRow(created, "셋", "three"),
            "Korean", default));
        fixture.Activate(UserB);
        var import = await store.ImportVocabularyAsync(UserB, fixture.ResourceId(UserB), "Korean",
            [new NativeResourceVocabularyRow(imported, "넷", "four")], default);
        Assert.Equal(NativeResourceMutationResult.Success, import.Result);

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var aWord = await db.VocabularyWords.AsNoTracking()
            .SingleAsync(word => word.TargetLanguageTerm == "셋");
        var bWord = await db.VocabularyWords.AsNoTracking()
            .SingleAsync(word => word.TargetLanguageTerm == "넷");
        Assert.Equal(UserA, aWord.UserProfileId);
        Assert.Equal(UserB, bWord.UserProfileId);
        Assert.False(await db.VocabularyWords.AnyAsync(word =>
            word.UserProfileId == null || word.UserProfileId == ""));
    }

    private static async Task<string?> OwnerAsync(NativeSliceFixture fixture, string wordId)
    {
        using var scope = fixture.Scope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyWords.AsNoTracking()
            .Where(word => word.Id == wordId)
            .Select(word => word.UserProfileId)
            .SingleAsync();
    }
}
