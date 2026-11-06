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
                Id = orphanId, UserProfileId = NativeSliceFixture.UserA,
                TargetLanguageTerm = "progress-only",
                NativeLanguageTerm = "unmapped", Language = "Korean",
                MnemonicText = "A's progress-only memory story",
                MnemonicImageUri = "https://images.example.invalid/a-progress-only"
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
        // Terms + owned mnemonic text + image = 4/6.
        Assert.Contains(catalog.Words, word => word.Id == orphanId
            && word.AssociatedResources.Count == 0
            && word.EncodingLabel == "Good");
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
        // Review 4: a word recorded to the learner stays theirs to edit without a link
        // (Save may remove every link: the word stays theirs).
        Assert.Equal("A's progress-only memory story", progressOnlyDetail.Draft.MnemonicText);
        Assert.Equal("https://images.example.invalid/a-progress-only",
            progressOnlyDetail.Draft.MnemonicImageUri);
        Assert.Equal(NativeVocabularySaveStatus.Saved,
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
            MnemonicText = "a memory aid", LexicalUnitType = LexicalUnitType.Phrase,
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
        Assert.Equal("a memory aid", reopened.Draft.MnemonicText);
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
        Assert.Equal(4, await dbAfter.ResourceVocabularyMappings.CountAsync(mapping =>
            mapping.ResourceId == fixture.ResourceId(NativeSliceFixture.UserB)));
        Assert.Equal(4, await dbAfter.VocabularyProgresses.CountAsync(progress =>
            progress.UserId == NativeSliceFixture.UserB));
        Assert.Equal(0, await dbAfter.VocabularyProgresses.CountAsync(progress =>
            progress.UserId == NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task PrivateWord_WithOwnResourceExample_KeepsMnemonicEditAndRestart()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        var id = fixture.WordId(NativeSliceFixture.UserA, 0);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Phase 3 records every sentence's owner (the migration backfills it from the
            // resource; save paths stamp it). A sentence naming nobody counts as another
            // learner's, as on the server: see VocabularyWordCleanupTests.
            db.ExampleSentences.Add(new ExampleSentence {
                VocabularyWordId = id,
                UserProfileId = NativeSliceFixture.UserA,
                LearningResourceId = fixture.ResourceId(NativeSliceFixture.UserA),
                Source = ExampleSentenceSource.UserAuthored,
                TargetSentence = "owned resource example"
            });
            await db.SaveChangesAsync();
        }

        fixture.Activate(NativeSliceFixture.UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var detail = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, id, default))!;
        Assert.Equal("opaque-photo-0", detail.Draft.MnemonicImageUri);
        Assert.Equal("owned resource example",
            Assert.Single(detail.Examples).TargetSentence);
        Assert.Equal(NativeVocabularySaveStatus.Saved,
            await store.SaveOwnedVocabularyAsync(NativeSliceFixture.UserA,
                detail.Draft with {
                    NativeTerm = "private edit",
                    MnemonicText = "private owner cue"
                }, default));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var restored = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, id, default))!;
        Assert.Equal("private edit", restored.Draft.NativeTerm);
        Assert.Equal("private owner cue", restored.Draft.MnemonicText);
        Assert.Equal("opaque-photo-0", restored.Draft.MnemonicImageUri);
        Assert.Single(restored.Examples);
        Assert.Equal(NativeResourceSyncPhase.Pending,
            (await fixture.Resources.LoadListAsync()).Sync);
    }

    // Rai's 2026-10-08 case: A writes memory aids, unlinks the word, and B ends up linked to it.
    // The recorded owner decides (Captain 2026-11-06): the word stays A's, and B never sees it.
    [Fact]
    public async Task UnlinkThenForeignLink_TheWordStaysTheRecordedOwnersAndIsHiddenFromTheOther()
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
                Id = "b-linked-to-a-word",
                ResourceId = fixture.ResourceId(NativeSliceFixture.UserB),
                VocabularyWordId = id
            });
            await db.SaveChangesAsync();
        }

        fixture.Activate(NativeSliceFixture.UserB);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var catalog = await store.ListOwnedVocabularyAsync(NativeSliceFixture.UserB, default);
        Assert.True(catalog.OwnerValid);
        Assert.Empty(catalog.Words);
        Assert.DoesNotContain(aCue, JsonSerializer.Serialize(catalog));
        Assert.Null(await store.GetOwnedVocabularyAsync(NativeSliceFixture.UserB, id, default));
        Assert.False(await store.OwnsVocabularyAsync(NativeSliceFixture.UserB, id, default));
        Assert.Empty((await store.GetAsync(NativeSliceFixture.UserB,
            fixture.ResourceId(NativeSliceFixture.UserB), default))!.Vocabulary);

        fixture.Activate(NativeSliceFixture.UserA);
        var forA = (await store.GetOwnedVocabularyAsync(NativeSliceFixture.UserA, id, default))!;
        Assert.Equal(aCue, forA.Draft.MnemonicText);
        Assert.Equal(aImage, forA.Draft.MnemonicImageUri);
        Assert.Empty(forA.Draft.ResourceIds);
        Assert.Contains((await store.ListOwnedVocabularyAsync(NativeSliceFixture.UserA, default)).Words,
            word => word.Id == id && word.AssociatedResources.Count == 0);

        using var checkedScope = fixture.Scope();
        var stored = await checkedScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>().VocabularyWords
            .AsNoTracking().SingleAsync(word => word.Id == id);
        Assert.Equal(NativeSliceFixture.UserA, stored.UserProfileId);
        Assert.Equal(aCue, stored.MnemonicText);
        Assert.Equal(aImage, stored.MnemonicImageUri);
    }

    /// <summary>
    /// A's word that B's resource also links (legacy data) is A's alone to edit: nobody else can
    /// see it, so the 2026-10-20 "shared, read-only" state is gone. B's link stays as it is, and
    /// a save still cannot attach a resource that is not A's.
    /// </summary>
    [Fact]
    public async Task WordAnotherLearnersResourceLinks_StaysEditableByItsOwner_AndForeignResourcesAreRefused()
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
        Assert.All(shared.AvailableResources, resource =>
            Assert.NotEqual(fixture.ResourceId(NativeSliceFixture.UserB), resource.Id));
        Assert.Equal([fixture.ResourceId(NativeSliceFixture.UserA)], shared.Draft.ResourceIds);
        // B's link is data that is not A's: deleting the word would remove it.
        Assert.Equal(NativeVocabularyDeleteBlock.UsedByOthers, shared.DeleteBlock);
        Assert.Equal(NativeVocabularySaveStatus.Saved,
            await store.SaveOwnedVocabularyAsync(
                NativeSliceFixture.UserA,
                shared.Draft with { NativeTerm = "A's edit" }, default));
        Assert.Equal(NativeVocabularyDeleteStatus.UsedByOthers,
            await store.DeleteOwnedVocabularyAsync(NativeSliceFixture.UserA, sharedId, default));

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
        Assert.Null(await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserB, sharedId, default));
        Assert.Equal(NativeVocabularySaveStatus.NotFoundOrForbidden,
            await store.SaveOwnedVocabularyAsync(
                NativeSliceFixture.UserB, shared.Draft with { NativeTerm = "B's overwrite" }, default));
        Assert.Equal(NativeVocabularyDeleteStatus.NotFoundOrForbidden,
            await store.DeleteOwnedVocabularyAsync(NativeSliceFixture.UserB, sharedId, default));
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
        var sharedAfter = await dbAfter.VocabularyWords.SingleAsync(word => word.Id == sharedId);
        Assert.Equal("A's edit", sharedAfter.NativeLanguageTerm);
        Assert.Equal(NativeSliceFixture.UserA, sharedAfter.UserProfileId);
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
            // Each sentence is recorded to its resource's owner, as the phase 3 migration
            // backfills and every save stamps; only a recorded sentence is the learner's.
            db.ExampleSentences.AddRange(
                new ExampleSentence {
                    VocabularyWordId = id, UserProfileId = NativeSliceFixture.UserA,
                    LearningResourceId = fixture.ResourceId(NativeSliceFixture.UserA),
                    TargetSentence = "owned example"
                },
                new ExampleSentence {
                    VocabularyWordId = id, UserProfileId = NativeSliceFixture.UserB,
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
    public async Task LinkedFromAnotherResource_ReferenceSentencesAreTheOwnersOnly_AndTheOtherLearnerSeesNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);
        var sharedId = fixture.WordId(NativeSliceFixture.UserA, 0);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var storedWord = await db.VocabularyWords.SingleAsync(word => word.Id == sharedId);
            storedWord.MnemonicText = "A's memory story";
            storedWord.MnemonicImageUri = "https://images.example.invalid/a?private=1";
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                Id = "shared-example-mapping",
                ResourceId = fixture.ResourceId(NativeSliceFixture.UserB),
                VocabularyWordId = sharedId
            });
            // Recorded as the phase 3 migration backfills from the resource; the third names
            // nobody and so is no one's.
            db.ExampleSentences.AddRange(
                new ExampleSentence {
                    VocabularyWordId = sharedId, UserProfileId = NativeSliceFixture.UserA,
                    LearningResourceId = fixture.ResourceId(NativeSliceFixture.UserA),
                    TargetSentence = "example attributed to A"
                },
                new ExampleSentence {
                    VocabularyWordId = sharedId, UserProfileId = NativeSliceFixture.UserB,
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
        // A sentence recorded to no one is no one's, so it is listed for no one.
        Assert.Equal("example attributed to A",
            Assert.Single(forA.Examples).TargetSentence);
        Assert.Equal("A's memory story", forA.Draft.MnemonicText);
        Assert.Equal("https://images.example.invalid/a?private=1", forA.Draft.MnemonicImageUri);
        // Terms + A's text + image + one owned example = 5/6.
        Assert.Equal("Strong", forA.Word.EncodingLabel);
        Assert.Equal(NativeVocabularyDeleteBlock.UsedByOthers, forA.DeleteBlock);

        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Null(await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserB, sharedId, default));
        var bCatalog = await store.ListOwnedVocabularyAsync(NativeSliceFixture.UserB, default);
        Assert.DoesNotContain(bCatalog.Words, word => word.Id == sharedId);
        Assert.DoesNotContain("A's memory story", JsonSerializer.Serialize(bCatalog));
        Assert.DoesNotContain("a?private=1", JsonSerializer.Serialize(bCatalog));

        using var verified = fixture.Scope();
        var rows = verified.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(3, await rows.ExampleSentences.CountAsync(example =>
            example.VocabularyWordId == sharedId));
        Assert.Equal(0, await rows.VocabularyProgresses.CountAsync());
        Assert.Equal(2, await rows.ResourceVocabularyMappings.CountAsync(mapping =>
            mapping.VocabularyWordId == sharedId));
        var unchangedWord = await rows.VocabularyWords.AsNoTracking()
            .SingleAsync(word => word.Id == sharedId);
        Assert.Equal("A's memory story", unchangedWord.MnemonicText);
        Assert.Equal("https://images.example.invalid/a?private=1",
            unchangedWord.MnemonicImageUri);
        Assert.Equal(NativeSliceFixture.UserA, unchangedWord.UserProfileId);
    }

    [Fact]
    public async Task UnattributedExample_NeverCounts_AndOnlyTheOwnerSeesTheWord()
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

        // A sentence recorded to no one counts for no one. A owns the word: terms + text +
        // seeded image = 4/6.
        fixture.Activate(NativeSliceFixture.UserA);
        var word = Assert.Single((await store.ListOwnedVocabularyAsync(
            NativeSliceFixture.UserA, default)).Words);
        Assert.Equal("Good", word.EncodingLabel);
        var detail = await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, sharedId, default);
        Assert.NotNull(detail);
        Assert.Equal("one more encoding cue", detail.Draft.MnemonicText);
        Assert.Empty(detail.Examples);
        Assert.Equal("Good", detail.Word.EncodingLabel);

        // B's progress on A's word does not make it B's: B sees nothing of it.
        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Empty((await store.ListOwnedVocabularyAsync(NativeSliceFixture.UserB, default)).Words);
        Assert.Null(await store.GetOwnedVocabularyAsync(NativeSliceFixture.UserB, sharedId, default));

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
    public async Task ForeignProgressOnly_NeverListsTheWordForThatLearner_AndItsOwnerStillEditsIt()
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
        Assert.Empty((await store.ListOwnedVocabularyAsync(
            NativeSliceFixture.UserB, default)).Words);

        fixture.Activate(NativeSliceFixture.UserA);
        var detail = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, id, default))!;
        // B's progress is data that is not A's: A may edit the word but not delete it.
        Assert.Equal(NativeVocabularyDeleteBlock.UsedByOthers, detail.DeleteBlock);
        Assert.Equal(NativeVocabularySaveStatus.Saved,
            await store.SaveOwnedVocabularyAsync(NativeSliceFixture.UserA,
                detail.Draft with { NativeTerm = "A's own edit" }, default));
    }

    [Fact]
    public async Task ForeignProgressOnly_NeverShowsTheWordOrItsMemoryAidsToThatLearner()
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

        fixture.Activate(NativeSliceFixture.UserA);
        var entry = Assert.Single((await store.ListOwnedVocabularyAsync(
            NativeSliceFixture.UserA, default)).Words);
        Assert.Equal(id, entry.Id);
        // Owner: terms + text + image = 4/6.
        Assert.Equal("Good", entry.EncodingLabel);
        var ownerDetail = (await store.GetOwnedVocabularyAsync(NativeSliceFixture.UserA, id, default))!;
        Assert.Equal(privateCue, ownerDetail.Draft.MnemonicText);
        Assert.Equal(signedUri, ownerDetail.Draft.MnemonicImageUri);

        fixture.Activate(NativeSliceFixture.UserB);
        var catalog = await store.ListOwnedVocabularyAsync(NativeSliceFixture.UserB, default);
        Assert.True(catalog.OwnerValid);
        Assert.Empty(catalog.Words);
        Assert.DoesNotContain(privateCue, JsonSerializer.Serialize(catalog));
        Assert.DoesNotContain(signedUri, JsonSerializer.Serialize(catalog));
        Assert.Null(await store.GetOwnedVocabularyAsync(NativeSliceFixture.UserB, id, default));
        Assert.Equal(NativeVocabularySaveStatus.NotFoundOrForbidden,
            await store.SaveOwnedVocabularyAsync(NativeSliceFixture.UserB,
                ownerDetail.Draft with { NativeTerm = "not writable", MnemonicText = "B overwrites" },
                default));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserB);
        store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        Assert.Null(await store.GetOwnedVocabularyAsync(NativeSliceFixture.UserB, id, default));
        using var checkedScope = fixture.Scope();
        var checkedDb = checkedScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();
        var stored = await checkedDb.VocabularyWords.AsNoTracking()
            .SingleAsync(word => word.Id == id);
        Assert.Equal(privateCue, stored.MnemonicText);
        Assert.Equal(signedUri, stored.MnemonicImageUri);
        Assert.Equal($"native-{NativeSliceFixture.UserA}-0", stored.NativeLanguageTerm);
        Assert.Single(await checkedDb.ResourceVocabularyMappings
            .Where(mapping => mapping.VocabularyWordId == id).ToListAsync());
        Assert.Single(await checkedDb.VocabularyProgresses
            .Where(progress => progress.VocabularyWordId == id).ToListAsync());
    }

    // One runtime rule (Captain 2026-11-06): a sentence is the learner's only when it is
    // recorded to them; one recorded to no one (no resource, or a resource that cannot be
    // found) or to another learner is not shown. The word stays its owner's to edit (nobody else
    // can see it), but the sentence is data that is not theirs, so the word cannot be deleted.
    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("unresolvable")]
    public async Task UnattributedOrForeignExample_IsHidden_AndBlocksOnlyTheDelete(
        string exampleLink)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 0);
        const string otherOwnedResource = "owner-a-other-resource";
        const string privateCue = "private note";
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
                TargetSentence = "context with no verified author"
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
        var detail = (await store.GetOwnedVocabularyAsync(
            NativeSliceFixture.UserA, id, default))!;
        Assert.Empty(detail.Examples);
        Assert.Equal(privateCue, detail.Draft.MnemonicText);
        Assert.Equal(signedUri, detail.Draft.MnemonicImageUri);
        // Owner's text + image count; the unattributed or foreign example does not.
        Assert.Equal("Good", detail.Word.EncodingLabel);
        Assert.Equal(NativeVocabularyDeleteBlock.UsedByOthers, detail.DeleteBlock);
        Assert.Equal(NativeVocabularyDeleteStatus.UsedByOthers,
            await store.DeleteOwnedVocabularyAsync(NativeSliceFixture.UserA, id, default));
        Assert.Equal(NativeVocabularySaveStatus.Saved,
            await store.SaveOwnedVocabularyAsync(NativeSliceFixture.UserA,
                detail.Draft with {
                    NativeTerm = "the owner's translation",
                    MnemonicText = "the owner's replacement",
                    ResourceIds = [otherOwnedResource]
                }, default));

        using var checkedScope = fixture.Scope();
        var checkedDb = checkedScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();
        var stored = await checkedDb.VocabularyWords.AsNoTracking()
            .SingleAsync(word => word.Id == id);
        Assert.Equal("the owner's replacement", stored.MnemonicText);
        Assert.Equal("the owner's translation", stored.NativeLanguageTerm);
        Assert.Equal(NativeSliceFixture.UserA, stored.UserProfileId);
        Assert.Equal(otherOwnedResource,
            Assert.Single(await checkedDb.ResourceVocabularyMappings
                .Where(mapping => mapping.VocabularyWordId == id)
                .ToListAsync()).ResourceId);
        Assert.Equal(1, await checkedDb.ExampleSentences.CountAsync(example =>
            example.VocabularyWordId == id));
    }

    /// <summary>
    /// Two learners on one device database (the strict rule, Captain 2026-11-06): a word
    /// recorded to no one and another learner's word, both linked into A's resource, are never
    /// listed, opened, saved, deleted, offered as duplicates, shown in the resource's word list or
    /// quizzed for A; A's own word that no resource links is listed and opened. Review finding 10:
    /// an ownerless word is not shown at all, so no "shared with another profile" reason can be
    /// given for it.
    /// </summary>
    [Fact]
    public async Task OwnerlessAndForeignWordsInTheLearnersResource_AreHiddenEverywhere_AndOwnUnlinkedWordsShow()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);
        var foreignId = fixture.WordId(NativeSliceFixture.UserB, 0);
        const string ownerlessId = "ownerless-in-a-resource";
        const string ownerlessCopyId = "ownerless-copy-of-foreign";
        const string unlinkedId = "a-own-unlinked";
        var aResource = fixture.ResourceId(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyWords.AddRange(
                new VocabularyWord {
                    Id = ownerlessId, UserProfileId = NativeSliceFixture.UserA,
                    TargetLanguageTerm = "주인없음", NativeLanguageTerm = "nobody's", Language = "Korean",
                    MnemonicText = "whose story?"
                },
                new VocabularyWord {
                    Id = ownerlessCopyId, UserProfileId = NativeSliceFixture.UserA,
                    TargetLanguageTerm = $"target-{NativeSliceFixture.UserB}-0",
                    NativeLanguageTerm = $"native-{NativeSliceFixture.UserB}-0", Language = "Korean"
                },
                new VocabularyWord {
                    Id = unlinkedId, UserProfileId = NativeSliceFixture.UserA,
                    TargetLanguageTerm = "혼자", NativeLanguageTerm = "alone", Language = "Korean"
                });
            db.ResourceVocabularyMappings.AddRange(
                new ResourceVocabularyMapping { Id = "a-links-ownerless", ResourceId = aResource,
                    VocabularyWordId = ownerlessId },
                new ResourceVocabularyMapping { Id = "a-links-ownerless-copy", ResourceId = aResource,
                    VocabularyWordId = ownerlessCopyId },
                new ResourceVocabularyMapping { Id = "a-links-foreign", ResourceId = aResource,
                    VocabularyWordId = foreignId });
            await db.SaveChangesAsync();
            await NativeSliceFixture.RecordToNoOneAsync(db, ownerlessId, ownerlessCopyId);
        }
        fixture.Activate(NativeSliceFixture.UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();

        var catalog = await store.ListOwnedVocabularyAsync(NativeSliceFixture.UserA, default);
        Assert.Equal([unlinkedId], catalog.Words.Select(word => word.Id));
        Assert.DoesNotContain("whose story?", JsonSerializer.Serialize(catalog));
        Assert.NotNull(await store.GetOwnedVocabularyAsync(NativeSliceFixture.UserA, unlinkedId, default));
        foreach (var hidden in new[] { ownerlessId, ownerlessCopyId, foreignId })
        {
            Assert.Null(await store.GetOwnedVocabularyAsync(NativeSliceFixture.UserA, hidden, default));
            Assert.False(await store.OwnsVocabularyAsync(NativeSliceFixture.UserA, hidden, default));
            var draft = new NativeVocabularyEditDraft(hidden, DateTime.UtcNow, "overwrite", "overwrite",
                null, null, null, "taken", null, LexicalUnitType.Word, [aResource]);
            Assert.Equal(NativeVocabularySaveStatus.NotFoundOrForbidden,
                await store.SaveOwnedVocabularyAsync(NativeSliceFixture.UserA, draft, default));
            Assert.Equal(NativeVocabularyDeleteStatus.NotFoundOrForbidden,
                await store.DeleteOwnedVocabularyAsync(NativeSliceFixture.UserA, hidden, default));
        }
        var resource = (await store.GetAsync(NativeSliceFixture.UserA, aResource, default))!;
        Assert.Empty(resource.Vocabulary);
        Assert.Equal(0, resource.VocabularyCount);
        // The ownerless copy shares B's term, but neither is A's: no duplicate group for A.
        var duplicates = await store.FindOwnedVocabularyDuplicatesAsync(
            NativeSliceFixture.UserA, null, 0, 20, default);
        Assert.True(duplicates.OwnerValid);
        Assert.Empty(duplicates.Groups);
        Assert.Equal(NativeVocabularyMergeResult.None,
            await store.MergeOwnedVocabularyDuplicatesAsync(
                NativeSliceFixture.UserA, unlinkedId, [ownerlessId], default));

        // A quiz on A's resource has no words to ask: the hidden words are never quizzed.
        var launch = await fixture.Provider.GetRequiredService<NativeQuizLaunchService>()
            .CreateChooseOwnAsync(NativeSliceFixture.UserA, [aResource], skillId: null);
        Assert.NotNull(launch);
        using (var quiz = fixture.Quiz)
            Assert.Equal(NativePhase.Empty, (await quiz.OpenAsync(launch)).Phase);
        Assert.Null(await fixture.Provider.GetRequiredService<NativeQuizLaunchService>()
            .CreateChooseOwnAsync(NativeSliceFixture.UserA, [], skillId: null,
                focusVocabularyIds: [ownerlessId]));

        using var verified = fixture.Scope();
        var rows = verified.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        // Nothing hidden was changed or deleted.
        Assert.Equal(3, await rows.ResourceVocabularyMappings.CountAsync(mapping => mapping.ResourceId == aResource));
        var ownerless = await rows.VocabularyWords.AsNoTracking().SingleAsync(word => word.Id == ownerlessId);
        Assert.Null(ownerless.UserProfileId);
        Assert.Equal("whose story?", ownerless.MnemonicText);
        Assert.Equal(NativeSliceFixture.UserB,
            (await rows.VocabularyWords.AsNoTracking().SingleAsync(word => word.Id == foreignId)).UserProfileId);
        Assert.Equal(0, await rows.VocabularyProgresses.CountAsync(progress =>
            progress.UserId == NativeSliceFixture.UserA));
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
}
