using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeVocabularyStoreTests
{
    [Fact]
    public async Task OwnedBrowse_UnionsMappedAndProgressOnlyWordsWithoutReadCreatingProgress()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        const string orphanId = "owned-progress-only";
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyWords.Add(new VocabularyWord {
                Id = orphanId, TargetLanguageTerm = "progress-only",
                NativeLanguageTerm = "unmapped", Language = "Korean",
                MnemonicText = "author not attributable",
                MnemonicImageUri = "https://images.example.invalid/unknown-owner"
            });
            db.VocabularyProgresses.Add(new VocabularyProgress {
                Id = "progress-owned-only", VocabularyWordId = orphanId,
                UserId = NativeSliceFixture.UserA
            });
            await db.SaveChangesAsync();
        }
        fixture.Activate(NativeSliceFixture.UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();

        var catalog = await store.ListOwnedVocabularyAsync(
            NativeSliceFixture.UserA, default);
        Assert.True(catalog.OwnerValid);
        Assert.Equal(5, catalog.Words.Count);
        Assert.Contains(catalog.Words, word => word.Id == orphanId
            && word.AssociatedResources.Count == 0
            && word.EncodingLabel == "Basic");
        Assert.DoesNotContain(catalog.Words,
            word => word.Id == fixture.WordId(NativeSliceFixture.UserB, 0));
        Assert.All(catalog.Resources, resource =>
            Assert.NotEqual(fixture.ResourceId(NativeSliceFixture.UserB), resource.Id));
        var detail = await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, fixture.WordId(NativeSliceFixture.UserA, 0),
            default);
        Assert.NotNull(detail);
        Assert.Null(detail.Progress);
        var progressOnlyDetail = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, orphanId, default))!;
        Assert.True(progressOnlyDetail.IsSharedWithAnotherOwner);
        await AssertHidesStoredMemoryAids(fixture, progressOnlyDetail);
        Assert.Equal(NativeVocabularySaveStatus.SharedReadOnly,
            await store.SaveOwnedVocabularyAsync(
                NativeSliceFixture.UserA, progressOnlyDetail.Draft, default));
        Assert.Null(await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, fixture.WordId(NativeSliceFixture.UserB, 0),
            default));

        using var verified = fixture.Scope();
        var rows = verified.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await rows.VocabularyProgresses.CountAsync());
    }

    [Fact]
    public async Task OwnedEdit_SavesSameIdAndOnlyOwnedAssociationsAfterRestart()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB, known: true);
        const string secondResource = "owned-second-resource";
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LearningResources.Add(new LearningResource {
                Id = secondResource, UserProfileId = NativeSliceFixture.UserA,
                Title = "Second owned", Language = "Korean",
                MediaType = "Vocabulary List"
            });
            await db.SaveChangesAsync();
        }
        fixture.Activate(NativeSliceFixture.UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var id = fixture.WordId(NativeSliceFixture.UserA, 0);
        var opened = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, id, default))!;
        var draft = opened.Draft with {
            TargetTerm = "  수정한 단어  ", NativeTerm = "  edited  ",
            Lemma = "root", Tags = "one, two",
            LexicalUnitType = LexicalUnitType.Phrase,
            ResourceIds = [secondResource]
        };

        Assert.Equal(NativeVocabularySaveStatus.Saved,
            await store.SaveOwnedVocabularyAsync(
                NativeSliceFixture.UserA, draft, default));
        Assert.Equal(NativeResourceSyncPhase.Pending,
            (await fixture.Resources.LoadListAsync()).Sync);
        var reopened = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, id, default))!;
        Assert.Equal(id, reopened.Word.Id);
        Assert.Equal("수정한 단어", reopened.Word.TargetTerm);
        Assert.Equal("edited", reopened.Word.NativeTerm);
        Assert.Equal("root", reopened.Draft.Lemma);
        Assert.Equal("one, two", reopened.Draft.Tags);
        await AssertHidesStoredMemoryAids(fixture, reopened);
        Assert.Equal(LexicalUnitType.Phrase, reopened.Word.LexicalUnitType);
        Assert.Equal([secondResource], reopened.Draft.ResourceIds);

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        Assert.Equal(NativeResourceSyncPhase.Pending,
            (await fixture.Resources.LoadListAsync()).Sync);
        var restored = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, id, default))!;
        Assert.Equal("edited", restored.Word.NativeTerm);
        Assert.Equal([secondResource], restored.Draft.ResourceIds);
        using var checkedScope = fixture.Scope();
        var dbAfter = checkedScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();
        Assert.Single(await dbAfter.ResourceVocabularyMappings.Where(mapping =>
            mapping.VocabularyWordId == id).ToListAsync());
        var storedWord = await dbAfter.VocabularyWords.AsNoTracking()
            .SingleAsync(word => word.Id == id);
        Assert.Null(storedWord.MnemonicText);
        Assert.Equal("opaque-photo-0", storedWord.MnemonicImageUri);
        Assert.Equal(4, await dbAfter.ResourceVocabularyMappings.CountAsync(mapping =>
            mapping.ResourceId == fixture.ResourceId(NativeSliceFixture.UserB)));
        Assert.Equal(4, await dbAfter.VocabularyProgresses.CountAsync(progress =>
            progress.UserId == NativeSliceFixture.UserB));
        Assert.Equal(0, await dbAfter.VocabularyProgresses.CountAsync(progress =>
            progress.UserId == NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task ExclusiveWord_EditsBaseFieldsButNeverShowsOrWritesMemoryAids()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        var id = fixture.WordId(NativeSliceFixture.UserA, 0);
        const string ownCue = "a cue this owner may or may not have written";
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var word = await db.VocabularyWords.SingleAsync(row => row.Id == id);
            word.MnemonicText = ownCue;
            word.AudioPronunciationUri = "audio/own-word.mp3";
            db.ExampleSentences.Add(new ExampleSentence {
                VocabularyWordId = id,
                LearningResourceId = fixture.ResourceId(NativeSliceFixture.UserA),
                Source = ExampleSentenceSource.UserAuthored,
                TargetSentence = "owned resource example"
            });
            await db.SaveChangesAsync();
        }

        fixture.Activate(NativeSliceFixture.UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var catalog = await store.ListOwnedVocabularyAsync(
            NativeSliceFixture.UserA, default);
        // Terms + audio + owned example = 4/6 (0.667) stays "Good": with memory
        // aids excluded, "Strong" (>= 0.67) is unreachable on native by design.
        Assert.Equal("Good", Assert.Single(catalog.Words).EncodingLabel);
        var detail = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, id, default))!;
        Assert.False(detail.IsSharedWithAnotherOwner);
        Assert.Equal("Good", detail.Word.EncodingLabel);
        await AssertHidesStoredMemoryAids(fixture, detail);
        Assert.DoesNotContain(ownCue, JsonSerializer.Serialize(detail));
        Assert.DoesNotContain("opaque-photo-0", JsonSerializer.Serialize(detail));
        Assert.Equal("owned resource example",
            Assert.Single(detail.Examples).TargetSentence);
        Assert.Equal(NativeVocabularySaveStatus.Saved,
            await store.SaveOwnedVocabularyAsync(NativeSliceFixture.UserA,
                detail.Draft with {
                    NativeTerm = "private edit"
                }, default));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var restored = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, id, default))!;
        Assert.False(restored.IsSharedWithAnotherOwner);
        Assert.Equal("private edit", restored.Draft.NativeTerm);
        await AssertHidesStoredMemoryAids(fixture, restored);
        Assert.Single(restored.Examples);
        Assert.Equal(NativeResourceSyncPhase.Pending,
            (await fixture.Resources.LoadListAsync()).Sync);
        using var checkedScope = fixture.Scope();
        var stored = await checkedScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>().VocabularyWords
            .AsNoTracking().SingleAsync(word => word.Id == id);
        Assert.Equal("private edit", stored.NativeLanguageTerm);
        Assert.Equal(ownCue, stored.MnemonicText);
        Assert.Equal("opaque-photo-0", stored.MnemonicImageUri);
    }

    // Rai's 2026-10-08 RED case: A writes memory aids, unlinks the word, and B's
    // reuse makes B the sole current owner. Sole current use is not authorship.
    [Fact]
    public async Task UnlinkThenReuse_NeverExposesOrOverwritesPriorOwnersMemoryAids()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 0);
        var id = fixture.WordId(NativeSliceFixture.UserA, 0);
        const string aCue = "A's private memory story";
        const string aImage = "https://images.example.invalid/a?private-signed-token=owner-a";
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var word = await db.VocabularyWords.SingleAsync(row => row.Id == id);
            word.MnemonicText = aCue;
            word.MnemonicImageUri = aImage;
            db.ResourceVocabularyMappings.Remove(await db.ResourceVocabularyMappings
                .SingleAsync(mapping => mapping.VocabularyWordId == id));
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                Id = "b-reuses-a-word",
                ResourceId = fixture.ResourceId(NativeSliceFixture.UserB),
                VocabularyWordId = id
            });
            await db.SaveChangesAsync();
        }

        fixture.Activate(NativeSliceFixture.UserB);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var catalog = await store.ListOwnedVocabularyAsync(
            NativeSliceFixture.UserB, default);
        var entry = Assert.Single(catalog.Words);
        Assert.Equal(id, entry.Id);
        Assert.Equal("Basic", entry.EncodingLabel);
        Assert.DoesNotContain(aCue, JsonSerializer.Serialize(catalog));
        Assert.DoesNotContain(aImage, JsonSerializer.Serialize(catalog));

        var detail = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserB, id, default))!;
        Assert.False(detail.IsSharedWithAnotherOwner);
        Assert.Equal("Basic", detail.Word.EncodingLabel);
        await AssertHidesStoredMemoryAids(fixture, detail);
        Assert.DoesNotContain(aCue, JsonSerializer.Serialize(detail));
        Assert.DoesNotContain(aImage, JsonSerializer.Serialize(detail));
        Assert.Equal(NativeVocabularySaveStatus.Saved,
            await store.SaveOwnedVocabularyAsync(NativeSliceFixture.UserB,
                detail.Draft with {
                    NativeTerm = "B's translation"
                }, default));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserB);
        store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var restored = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserB, id, default))!;
        Assert.Equal("B's translation", restored.Draft.NativeTerm);
        await AssertHidesStoredMemoryAids(fixture, restored);
        using var checkedScope = fixture.Scope();
        var stored = await checkedScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>().VocabularyWords
            .AsNoTracking().SingleAsync(word => word.Id == id);
        Assert.Equal("B's translation", stored.NativeLanguageTerm);
        Assert.Equal(aCue, stored.MnemonicText);
        Assert.Equal(aImage, stored.MnemonicImageUri);
    }

    [Fact]
    public async Task SharedBaseWordAndForeignSelectedResource_RefuseMutation()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        const string sharedMapping = "shared-b-mapping";
        var sharedId = fixture.WordId(NativeSliceFixture.UserA, 0);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                Id = sharedMapping, ResourceId = fixture.ResourceId(NativeSliceFixture.UserB),
                VocabularyWordId = sharedId
            });
            await db.SaveChangesAsync();
        }
        fixture.Activate(NativeSliceFixture.UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var shared = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, sharedId, default))!;
        Assert.True(shared.IsSharedWithAnotherOwner);
        Assert.All(shared.AvailableResources, resource =>
            Assert.NotEqual(fixture.ResourceId(NativeSliceFixture.UserB), resource.Id));
        Assert.Equal(NativeVocabularySaveStatus.SharedReadOnly,
            await store.SaveOwnedVocabularyAsync(
                NativeSliceFixture.UserA,
                shared.Draft with { NativeTerm = "never written" }, default));

        var privateId = fixture.WordId(NativeSliceFixture.UserA, 1);
        var privateWord = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, privateId, default))!;
        Assert.Equal(NativeVocabularySaveStatus.NotFoundOrForbidden,
            await store.SaveOwnedVocabularyAsync(
                NativeSliceFixture.UserA, privateWord.Draft with {
                    NativeTerm = "no foreign attachment",
                    ResourceIds = [fixture.ResourceId(NativeSliceFixture.UserB)]
                }, default));
        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Null(await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, privateId, default));
        Assert.Equal(NativeVocabularySaveStatus.NotFoundOrForbidden,
            await store.SaveOwnedVocabularyAsync(
                NativeSliceFixture.UserA, privateWord.Draft, default));
        fixture.LoseOwner();
        Assert.False((await store.ListOwnedVocabularyAsync(
            NativeSliceFixture.UserA, default)).OwnerValid);
        Assert.Null(await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, sharedId, default));
        Assert.Equal(NativeVocabularySaveStatus.NotFoundOrForbidden,
            await store.SaveOwnedVocabularyAsync(
                NativeSliceFixture.UserA, privateWord.Draft, default));

        using var verified = fixture.Scope();
        var dbAfter = verified.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.NotEqual("never written", (await dbAfter.VocabularyWords
            .SingleAsync(word => word.Id == sharedId)).NativeLanguageTerm);
        Assert.NotEqual("no foreign attachment", (await dbAfter.VocabularyWords
            .SingleAsync(word => word.Id == privateId)).NativeLanguageTerm);
        Assert.Equal(sharedId, (await dbAfter.ResourceVocabularyMappings
            .SingleAsync(mapping => mapping.Id == sharedMapping)).VocabularyWordId);
    }

    [Fact]
    public async Task OwnedDetail_HistoryAndExamplesNeverExposeForeignResourceContent()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var id = fixture.WordId(NativeSliceFixture.UserA, 0);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyProgresses.Add(new VocabularyProgress {
                Id = "owned-history-progress", UserId = NativeSliceFixture.UserA,
                VocabularyWordId = id
            });
            db.VocabularyLearningContexts.Add(new VocabularyLearningContext {
                Id = "owned-history-foreign-source",
                VocabularyProgressId = "owned-history-progress",
                LearningResourceId = fixture.ResourceId(NativeSliceFixture.UserB),
                Activity = "VocabularyQuiz", WasCorrect = true
            });
            db.ExampleSentences.AddRange(
                new ExampleSentence {
                    VocabularyWordId = id,
                    LearningResourceId = fixture.ResourceId(NativeSliceFixture.UserA),
                    TargetSentence = "owned example"
                },
                new ExampleSentence {
                    VocabularyWordId = id,
                    LearningResourceId = fixture.ResourceId(NativeSliceFixture.UserB),
                    TargetSentence = "foreign example"
                });
            await db.SaveChangesAsync();
        }
        fixture.Activate(NativeSliceFixture.UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var detail = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, id, default))!;
        Assert.Equal("owned example", Assert.Single(detail.Examples).TargetSentence);
        Assert.Null(Assert.Single(detail.History).ResourceTitle);
        Assert.All(detail.AvailableResources, resource =>
            Assert.NotEqual(fixture.ResourceId(NativeSliceFixture.UserB), resource.Id));
    }

    [Fact]
    public async Task SharedWord_ReferenceSentencesRequireTheCurrentOwnersResource()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);
        var sharedId = fixture.WordId(NativeSliceFixture.UserA, 0);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var storedWord = await db.VocabularyWords.SingleAsync(word => word.Id == sharedId);
            storedWord.MnemonicText = "not attributable to either owner";
            storedWord.MnemonicImageUri = "https://images.example.invalid/shared?private=1";
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                Id = "shared-example-mapping",
                ResourceId = fixture.ResourceId(NativeSliceFixture.UserB),
                VocabularyWordId = sharedId
            });
            db.ExampleSentences.AddRange(
                new ExampleSentence {
                    VocabularyWordId = sharedId,
                    LearningResourceId = fixture.ResourceId(NativeSliceFixture.UserA),
                    TargetSentence = "example attributed to A"
                },
                new ExampleSentence {
                    VocabularyWordId = sharedId,
                    LearningResourceId = fixture.ResourceId(NativeSliceFixture.UserB),
                    TargetSentence = "example attributed to B"
                },
                new ExampleSentence {
                    VocabularyWordId = sharedId,
                    Source = ExampleSentenceSource.UserAuthored,
                    TargetSentence = "authorship cannot be established"
                });
            await db.SaveChangesAsync();
        }
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();

        fixture.Activate(NativeSliceFixture.UserA);
        var forA = await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, sharedId, default);
        Assert.NotNull(forA);
        Assert.True(forA.IsSharedWithAnotherOwner);
        Assert.Equal("example attributed to A",
            Assert.Single(forA.Examples).TargetSentence);
        await AssertHidesStoredMemoryAids(fixture, forA);
        Assert.Equal("Good", forA.Word.EncodingLabel);

        fixture.Activate(NativeSliceFixture.UserB);
        var forB = await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserB, sharedId, default);
        Assert.NotNull(forB);
        Assert.True(forB.IsSharedWithAnotherOwner);
        Assert.Equal("example attributed to B",
            Assert.Single(forB.Examples).TargetSentence);
        await AssertHidesStoredMemoryAids(fixture, forB);
        Assert.Equal("Good", forB.Word.EncodingLabel);
        Assert.Contains((await store.ListOwnedVocabularyAsync(
            NativeSliceFixture.UserB, default)).Words, word => word.Id == sharedId);

        using var verified = fixture.Scope();
        var rows = verified.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(3, await rows.ExampleSentences.CountAsync(example =>
            example.VocabularyWordId == sharedId));
        Assert.Equal(0, await rows.VocabularyProgresses.CountAsync());
        Assert.Equal(2, await rows.ResourceVocabularyMappings.CountAsync(mapping =>
            mapping.VocabularyWordId == sharedId));
        var unchangedWord = await rows.VocabularyWords.AsNoTracking()
            .SingleAsync(word => word.Id == sharedId);
        Assert.Equal("not attributable to either owner", unchangedWord.MnemonicText);
        Assert.Equal("https://images.example.invalid/shared?private=1",
            unchangedWord.MnemonicImageUri);
    }

    [Fact]
    public async Task UnattributedExample_AloneDoesNotImproveEncodingForEitherSharedWordOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        var sharedId = fixture.WordId(NativeSliceFixture.UserA, 0);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.VocabularyWords.SingleAsync(word => word.Id == sharedId))
                .MnemonicText = "one more encoding cue";
            db.VocabularyProgresses.Add(new VocabularyProgress {
                Id = "second-owner-progress", VocabularyWordId = sharedId,
                UserId = NativeSliceFixture.UserB
            });
            db.ExampleSentences.Add(new ExampleSentence {
                VocabularyWordId = sharedId,
                Source = ExampleSentenceSource.UserAuthored,
                TargetSentence = "unattributed context"
            });
            await db.SaveChangesAsync();
        }
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();

        foreach (var owner in new[] {
            NativeSliceFixture.UserA, NativeSliceFixture.UserB
        })
        {
            fixture.Activate(owner);
            var word = Assert.Single((await store.ListOwnedVocabularyAsync(
                owner, default)).Words);
            Assert.Equal("Basic", word.EncodingLabel);
            var detail = await store.GetOwnedVocabularyAsync(
                owner, sharedId, default);
            Assert.NotNull(detail);
            Assert.True(detail.IsSharedWithAnotherOwner);
            await AssertHidesStoredMemoryAids(fixture, detail);
            Assert.Empty(detail.Examples);
            Assert.Equal("Basic", detail.Word.EncodingLabel);
        }

        fixture.LoseOwner();
        Assert.False((await store.ListOwnedVocabularyAsync(
            NativeSliceFixture.UserA, default)).OwnerValid);
        Assert.Null(await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, sharedId, default));
        using var verified = fixture.Scope();
        var rows = verified.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await rows.ExampleSentences.CountAsync(example =>
            example.VocabularyWordId == sharedId));
        Assert.Equal(1, await rows.VocabularyProgresses.CountAsync());
    }

    [Fact]
    public async Task ForeignProgressOnlyShare_ListsForThatOwnerButRefusesBaseWordEdit()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        var id = fixture.WordId(NativeSliceFixture.UserA, 0);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyProgresses.Add(new VocabularyProgress {
                Id = "foreign-progress-only",
                UserId = NativeSliceFixture.UserB,
                VocabularyWordId = id
            });
            await db.SaveChangesAsync();
        }
        fixture.Activate(NativeSliceFixture.UserB);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var catalog = await store.ListOwnedVocabularyAsync(
            NativeSliceFixture.UserB, default);
        Assert.Equal(id, Assert.Single(catalog.Words).Id);
        Assert.Empty(Assert.Single(catalog.Words).AssociatedResources);

        fixture.Activate(NativeSliceFixture.UserA);
        var detail = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, id, default))!;
        Assert.True(detail.IsSharedWithAnotherOwner);
        Assert.Equal(NativeVocabularySaveStatus.SharedReadOnly,
            await store.SaveOwnedVocabularyAsync(NativeSliceFixture.UserA,
                detail.Draft with { NativeTerm = "must not overwrite" }, default));
    }

    [Fact]
    public async Task ForeignProgressOnlyShare_RedactsPrivateMnemonicsForBothOwners()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        var id = fixture.WordId(NativeSliceFixture.UserA, 0);
        const string privateCue = "A's private memory story";
        const string signedUri = "https://images.example.invalid/photo?private-signed-token=owner-a";
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var word = await db.VocabularyWords.SingleAsync(row => row.Id == id);
            word.MnemonicText = privateCue;
            word.MnemonicImageUri = signedUri;
            db.VocabularyProgresses.Add(new VocabularyProgress {
                Id = "progress-for-second-owner",
                UserId = NativeSliceFixture.UserB,
                VocabularyWordId = id
            });
            await db.SaveChangesAsync();
        }
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        foreach (var owner in new[] {
            NativeSliceFixture.UserA, NativeSliceFixture.UserB
        })
        {
            fixture.Activate(owner);
            var catalog = await store.ListOwnedVocabularyAsync(owner, default);
            Assert.True(catalog.OwnerValid);
            var entry = Assert.Single(catalog.Words);
            Assert.Equal(id, entry.Id);
            Assert.Equal("Basic", entry.EncodingLabel);
            Assert.DoesNotContain(privateCue, JsonSerializer.Serialize(catalog));
            Assert.DoesNotContain(signedUri, JsonSerializer.Serialize(catalog));

            var detail = (await store.GetOwnedVocabularyAsync(owner, id, default))!;
            Assert.True(detail.IsSharedWithAnotherOwner);
            Assert.Equal(entry.TargetTerm, detail.Word.TargetTerm);
            await AssertHidesStoredMemoryAids(fixture, detail);
            Assert.Equal("Basic", detail.Word.EncodingLabel);
            Assert.DoesNotContain(privateCue, JsonSerializer.Serialize(detail));
            Assert.DoesNotContain(signedUri, JsonSerializer.Serialize(detail));
            Assert.Equal(NativeVocabularySaveStatus.SharedReadOnly,
                await store.SaveOwnedVocabularyAsync(owner,
                    detail.Draft with { NativeTerm = "not writable" }, default));
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserB);
        store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var restored = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserB, id, default))!;
        await AssertHidesStoredMemoryAids(fixture, restored);
        using var checkedScope = fixture.Scope();
        var checkedDb = checkedScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();
        var stored = await checkedDb.VocabularyWords.AsNoTracking()
            .SingleAsync(word => word.Id == id);
        Assert.Equal(privateCue, stored.MnemonicText);
        Assert.Equal(signedUri, stored.MnemonicImageUri);
        Assert.Single(await checkedDb.ResourceVocabularyMappings
            .Where(mapping => mapping.VocabularyWordId == id).ToListAsync());
        Assert.Single(await checkedDb.VocabularyProgresses
            .Where(progress => progress.VocabularyWordId == id).ToListAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("unresolvable")]
    public async Task UnattributedOrForeignExampleOnly_RefusesBaseWordAndMappingWrites(
        string exampleLink)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 0);
        const string otherOwnedResource = "owner-a-other-resource";
        const string privateCue = "private note before refusal";
        const string signedUri = "https://images.example.invalid/private?token=owner-a";
        var id = fixture.WordId(NativeSliceFixture.UserA, 0);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.LearningResources.Add(new LearningResource {
                Id = otherOwnedResource, UserProfileId = NativeSliceFixture.UserA,
                Title = "Other owned resource", MediaType = "Vocabulary List"
            });
            var word = await db.VocabularyWords.SingleAsync(row => row.Id == id);
            word.MnemonicText = privateCue;
            word.MnemonicImageUri = signedUri;
            db.ExampleSentences.Add(new ExampleSentence {
                VocabularyWordId = id,
                LearningResourceId = exampleLink switch {
                    "foreign" => fixture.ResourceId(NativeSliceFixture.UserB),
                    "unresolvable" => "resource-with-no-resolvable-owner",
                    _ => null
                },
                Source = exampleLink == "missing"
                    ? ExampleSentenceSource.UserAuthored : ExampleSentenceSource.Unknown,
                TargetSentence = "private context with no verified author"
            });
            if (exampleLink == "unresolvable")
            {
                await db.Database.OpenConnectionAsync();
                try
                {
                    await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
                    await db.SaveChangesAsync();
                }
                finally
                {
                    await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
                    await db.Database.CloseConnectionAsync();
                }
            }
            else
                await db.SaveChangesAsync();
        }

        fixture.Activate(NativeSliceFixture.UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var beforeSync = (await fixture.Resources.LoadListAsync()).Sync;
        var detail = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, id, default))!;
        Assert.Empty(detail.Examples);
        Assert.True(detail.IsSharedWithAnotherOwner);
        await AssertHidesStoredMemoryAids(fixture, detail);
        Assert.Equal("Basic", detail.Word.EncodingLabel);
        Assert.Equal(NativeVocabularySaveStatus.SharedReadOnly,
            await store.SaveOwnedVocabularyAsync(NativeSliceFixture.UserA,
                detail.Draft with {
                    TargetTerm = "not a new global term",
                    NativeTerm = "not a new translation",
                    ResourceIds = [otherOwnedResource]
                }, default));
        Assert.Equal(beforeSync, (await fixture.Resources.LoadListAsync()).Sync);
        using var checkedScope = fixture.Scope();
        var checkedDb = checkedScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();
        var stored = await checkedDb.VocabularyWords.AsNoTracking()
            .SingleAsync(word => word.Id == id);
        Assert.Equal(privateCue, stored.MnemonicText);
        Assert.Equal(signedUri, stored.MnemonicImageUri);
        Assert.Equal($"target-{NativeSliceFixture.UserA}-0", stored.TargetLanguageTerm);
        Assert.Equal($"native-{NativeSliceFixture.UserA}-0", stored.NativeLanguageTerm);
        Assert.Equal(fixture.ResourceId(NativeSliceFixture.UserA),
            Assert.Single(await checkedDb.ResourceVocabularyMappings
                .Where(mapping => mapping.VocabularyWordId == id)
                .ToListAsync()).ResourceId);
        Assert.Equal(0, await checkedDb.VocabularyProgresses.CountAsync());
        Assert.Equal(1, await checkedDb.ExampleSentences.CountAsync(example =>
            example.VocabularyWordId == id));
    }

    [Fact]
    public async Task DuplicateCheckIsOwnerScopedAndConcurrentEditRefusesStaleDraft()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);
        fixture.Activate(NativeSliceFixture.UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var id = fixture.WordId(NativeSliceFixture.UserA, 0);
        var opened = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, id, default))!;
        Assert.Equal(NativeVocabularySaveStatus.Invalid,
            await store.SaveOwnedVocabularyAsync(NativeSliceFixture.UserA,
                opened.Draft with { NativeTerm = "   " }, default));
        Assert.Equal(NativeVocabularySaveStatus.Duplicate,
            await store.SaveOwnedVocabularyAsync(NativeSliceFixture.UserA,
                opened.Draft with {
                    TargetTerm = $"  TARGET-{NativeSliceFixture.UserA}-1 ",
                    NativeTerm = $" native-{NativeSliceFixture.UserA}-1 "
                }, default));
        Assert.Equal(NativeVocabularySaveStatus.Saved,
            await store.SaveOwnedVocabularyAsync(NativeSliceFixture.UserA,
                opened.Draft with {
                    TargetTerm = $"target-{NativeSliceFixture.UserB}-0",
                    NativeTerm = $"native-{NativeSliceFixture.UserB}-0"
                }, default));

        var fresh = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, id, default))!;
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var changed = await db.VocabularyWords.SingleAsync(word => word.Id == id);
            changed.UpdatedAt = fresh.Draft.OriginalUpdatedAt.AddSeconds(5);
            await db.SaveChangesAsync();
        }
        Assert.Equal(NativeVocabularySaveStatus.Conflict,
            await store.SaveOwnedVocabularyAsync(NativeSliceFixture.UserA,
                fresh.Draft with { NativeTerm = "stale overwrite" }, default));
        using var verified = fixture.Scope();
        var dbAfter = verified.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.NotEqual("stale overwrite", (await dbAfter.VocabularyWords
            .SingleAsync(word => word.Id == id)).NativeLanguageTerm);
        Assert.Equal($"native-{NativeSliceFixture.UserB}-0",
            (await dbAfter.VocabularyWords.SingleAsync(word =>
                word.Id == fixture.WordId(NativeSliceFixture.UserB, 0))).NativeLanguageTerm);
    }

    // The native draft has no memory-aid fields; prove the stored values of this
    // word never appear anywhere in what the store returned.
    private static async Task AssertHidesStoredMemoryAids(
        NativeSliceFixture fixture, NativeVocabularyDetail detail)
    {
        using var scope = fixture.Scope();
        var stored = await scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>().VocabularyWords
            .AsNoTracking().SingleAsync(word => word.Id == detail.Word.Id);
        var json = JsonSerializer.Serialize(detail);
        foreach (var value in new[] { stored.MnemonicText, stored.MnemonicImageUri })
            if (!string.IsNullOrWhiteSpace(value))
                Assert.DoesNotContain(value, json);
    }
}
