using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// The Library rows' counts (Captain 2026-11-13: "Still missing counts"): each counts what its
/// page lists, for the one owner, on real SQLite with two learners.
/// </summary>
public sealed class NativeLibraryCountsTests
{
    private static readonly DateTime At = new(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CountsOnlyTheOwnersWordsActiveSkillsAndPairsOfTheirOwnWords()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyWords.AddRange(
                Word("a1", NativeSliceFixture.UserA), Word("a2", NativeSliceFixture.UserA),
                Word("a3", NativeSliceFixture.UserA), Word("b1", NativeSliceFixture.UserB));
            db.SkillProfiles.AddRange(
                new SkillProfile { Id = "s1", Title = "One", UserProfileId = NativeSliceFixture.UserA },
                new SkillProfile { Id = "s2", Title = "Two", UserProfileId = NativeSliceFixture.UserA },
                new SkillProfile { Id = "s3", Title = "Old", UserProfileId = NativeSliceFixture.UserA,
                    IsArchived = true },
                new SkillProfile { Id = "sb", Title = "Theirs", UserProfileId = NativeSliceFixture.UserB });
            db.MinimalPairs.AddRange(
                Pair("p1", "a1", "a2"),
                // A pair with another learner's word is not listed, so it is not counted.
                Pair("p2", "a1", "b1"));
            await db.SaveChangesAsync();
        }

        var counter = new NativeLibraryCounts(
            fixture.Provider.GetRequiredService<IServiceScopeFactory>());
        Assert.Equal(new NativeLibraryCountSet(3, 2, 1),
            await counter.CountAsync(NativeSliceFixture.UserA, CancellationToken.None));
        Assert.Equal(new NativeLibraryCountSet(1, 1, 0),
            await counter.CountAsync(NativeSliceFixture.UserB, CancellationToken.None));
        Assert.Null(await counter.CountAsync(" ", CancellationToken.None));
    }

    private static VocabularyWord Word(string id, string? owner) => new()
    {
        Id = id, UserProfileId = owner, TargetLanguageTerm = id, NativeLanguageTerm = id,
        Language = "Korean", LexicalUnitType = LexicalUnitType.Word, CreatedAt = At, UpdatedAt = At
    };

    private static MinimalPair Pair(string id, string a, string b) => new()
    {
        Id = id, UserId = NativeSliceFixture.UserA, VocabularyWordAId = a, VocabularyWordBId = b,
        CreatedAt = At, UpdatedAt = At
    };
}
