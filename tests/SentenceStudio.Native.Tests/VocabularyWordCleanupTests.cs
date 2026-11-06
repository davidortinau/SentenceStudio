using System.Reflection;
using System.Runtime.ExceptionServices;
using CoreSync;
using CoreSync.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Data;
using SentenceStudio.Native.Features.Settings;
using SentenceStudio.Native.Features.Vocabulary;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// The word editor's Delete and Find Duplicates (source VocabularyWordEdit.razor, and the
/// Vocabulary.razor duplicate manager it opens), on the shared store and VocabularyBrowser.
/// </summary>
public sealed class VocabularyWordCleanupTests
{
    private const string UserA = NativeSliceFixture.UserA;
    private const string UserB = NativeSliceFixture.UserB;

    [Fact]
    public async Task Delete_RemovesOnlyTheOwnersWordAndEverythingThatHangsOffIt()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA, known: true);
        await fixture.SeedAsync(UserB, known: true);
        await AddHistoryAndExampleAsync(fixture, UserA, 0);
        await AddHistoryAndExampleAsync(fixture, UserB, 0);
        fixture.Activate(UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var wordA = fixture.WordId(UserA, 0);
        var wordB = fixture.WordId(UserB, 0);

        Assert.Equal(NativeVocabularyDeleteStatus.NotFoundOrForbidden,
            await store.DeleteOwnedVocabularyAsync(UserA, wordB, default));
        Assert.Equal(NativeVocabularyDeleteStatus.NotFoundOrForbidden,
            await store.DeleteOwnedVocabularyAsync(UserB, wordB, default));
        Assert.Equal(NativeVocabularyDeleteStatus.Deleted,
            await store.DeleteOwnedVocabularyAsync(UserA, wordA, default));

        var a = await RowsAsync(fixture, wordA);
        Assert.Equal(new WordRows(0, 0, 0, 0, 0), a);
        Assert.Equal(new WordRows(1, 1, 1, 1, 1), await RowsAsync(fixture, wordB));
        Assert.Equal(new WordRows(1, 1, 1, 0, 0),
            await RowsAsync(fixture, fixture.WordId(UserA, 1)));
        Assert.True(fixture.Provider.GetRequiredService<NativePendingWrites>().IsPending(UserA));
        Assert.False(fixture.Provider.GetRequiredService<NativePendingWrites>().IsPending(UserB));

        // Absent owner: nothing is deleted.
        fixture.LoseOwner();
        Assert.Equal(NativeVocabularyDeleteStatus.NotFoundOrForbidden,
            await store.DeleteOwnedVocabularyAsync(UserA, fixture.WordId(UserA, 1), default));
        Assert.Equal(NativeVocabularyDeleteStatus.NotFoundOrForbidden,
            await store.DeleteOwnedVocabularyAsync(string.Empty, fixture.WordId(UserA, 1), default));
        Assert.Equal(1, (await RowsAsync(fixture, fixture.WordId(UserA, 1))).Words);
    }

    [Fact]
    public async Task Delete_RefusesAWordAnotherOwnerStillUses()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await fixture.SeedAsync(UserB);
        var shared = fixture.WordId(UserA, 0);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                Id = "mapping-shared-by-b", ResourceId = fixture.ResourceId(UserB),
                VocabularyWordId = shared
            });
            await db.SaveChangesAsync();
        }
        fixture.Activate(UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();

        Assert.Equal(NativeVocabularyDeleteStatus.UsedByOthers,
            await store.DeleteOwnedVocabularyAsync(UserA, shared, default));
        var rows = await RowsAsync(fixture, shared);
        Assert.Equal(1, rows.Words);
        Assert.Equal(2, rows.Mappings);

        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(UserA));
        await browser.OpenWordAsync(shared);
        // The owner may still edit it (nobody else can see it); only the delete is refused.
        Assert.Equal(NativeVocabularyDeleteBlock.UsedByOthers, browser.EditState.Detail!.DeleteBlock);
        Assert.True(browser.EditState.CanSave);
        Assert.False(browser.EditState.CanDelete);
        browser.RequestDelete();
        Assert.False(browser.IsDeleteConfirmationPresented);
    }

    [Fact]
    public async Task Browser_DeleteAsksFirstThenReturnsToTheListWithoutTheWord()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA, known: true);
        fixture.Activate(UserA);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(UserA));
        var wordId = fixture.WordId(UserA, 2);
        await browser.OpenWordAsync(wordId);
        Assert.True(browser.EditState.CanDelete);
        Assert.Equal($"VocabWordEdit_DeleteMessage", VocabularyWordCommands.DeleteMessage(
            browser.EditState.Detail!, key => key));
        Assert.Equal($"Delete {fixture.WordId(UserA, 2)}? target-{UserA}-2",
            VocabularyWordCommands.DeleteMessage(browser.EditState.Detail!,
                key => key == "VocabWordEdit_DeleteMessage" ? $"Delete {wordId}? {{0}}" : key));

        browser.RequestDelete();
        Assert.True(browser.IsDeleteConfirmationPresented);
        Assert.True(browser.EditState.ConfirmingDelete);
        browser.CancelDelete();
        Assert.False(browser.IsDeleteConfirmationPresented);
        Assert.Equal(1, (await RowsAsync(fixture, wordId)).Words);

        browser.RequestDelete();
        await browser.ConfirmDeleteAsync();

        Assert.False(browser.IsFormOpen);
        Assert.False(browser.IsDeleteConfirmationPresented);
        var list = browser.ListState;
        Assert.Equal("VocabWordEdit_DeletedSuccess", list.Feedback);
        Assert.False(list.FeedbackWarning);
        Assert.Equal(3, list.FilteredWords.Count);
        Assert.DoesNotContain(list.FilteredWords, word => word.Id == wordId);
        Assert.Equal(new WordRows(0, 0, 0, 0, 0), await RowsAsync(fixture, wordId));
    }

    [Fact]
    public async Task FindDuplicates_FocusesTheWordAndMergesOnlyTheOwnersCopies()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await fixture.SeedAsync(UserB);
        await AddCopiesAsync(fixture, UserA, "dup-a", "Korean", "Korean");
        await AddCopiesAsync(fixture, UserB, "dup-b", "Korean", "Korean");
        fixture.Activate(UserA);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(UserA));
        Assert.Equal(6, browser.ListState.FilteredWords.Count);

        await browser.OpenWordAsync("dup-a-1");
        Assert.True(browser.EditState.CanFindDuplicates);
        await browser.FindDuplicatesAsync();

        // Source: the editor gives way to the list's duplicate manager, focused on this word.
        Assert.False(browser.IsFormOpen);
        var panel = Assert.IsType<VocabularyDuplicatePanel>(browser.ListState.Duplicates);
        Assert.False(panel.Running);
        Assert.Equal("dup-a-1", panel.FocusedWordId);
        Assert.Equal("사과", panel.FocusLabel);
        var group = Assert.Single(panel.Groups);
        Assert.True(panel.IsFocused(group));
        Assert.True(group.CanMergeAutomatically);
        Assert.Equal(new[] { "dup-a-1", "dup-a-2" }, group.Words.Select(word => word.Id).Order());
        // dup-a-2 carries a memory aid, so it is the recommended keeper and listed first.
        Assert.Equal("dup-a-2", group.RecommendedKeeperId);
        Assert.Equal("dup-a-2", group.Words[0].Id);
        Assert.Equal("Their story", group.Words[0].MnemonicText);
        Assert.Equal(1, panel.TotalGroupCount);
        Assert.Equal(1, panel.ExtraRecordCount);

        await browser.MergeDuplicatesAsync(group.NormalizedTerm);

        var list = browser.ListState;
        Assert.Equal("Vocabulary_DuplicatesMergedSuccess", list.Feedback);
        Assert.Equal(5, list.FilteredWords.Count);
        Assert.Contains(list.FilteredWords, word => word.Id == "dup-a-2");
        Assert.DoesNotContain(list.FilteredWords, word => word.Id == "dup-a-1");
        var after = Assert.IsType<VocabularyDuplicatePanel>(list.Duplicates);
        Assert.Empty(after.Groups);
        Assert.Equal("Vocabulary_DuplicatesNoneForTerm", after.Status);
        Assert.Equal(new[] { "Vocabulary_DuplicatesMergedCount" }, after.Results);
        // The kept copy took over the merged copy's link; the other owner's copies stayed.
        Assert.Equal(new WordRows(0, 0, 0, 0, 0), await RowsAsync(fixture, "dup-a-1"));
        Assert.Equal(1, (await RowsAsync(fixture, "dup-a-2")).Words);
        Assert.Equal(1, (await RowsAsync(fixture, "dup-a-2")).Mappings);
        Assert.Equal(1, (await RowsAsync(fixture, "dup-b-1")).Words);
        Assert.Equal(1, (await RowsAsync(fixture, "dup-b-2")).Words);

        browser.CloseDuplicates();
        Assert.Null(browser.ListState.Duplicates);
    }

    [Fact]
    public async Task FindDuplicates_FromAnEditorElsewhere_HandsOverToTheVocabularyPagesFocusedPanel()
    {
        // An editor hosted outside the Vocabulary page (Import Content results) leaves its word
        // and hands the term and word over; the Vocabulary page opens the same focused panel.
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await fixture.SeedAsync(UserB);
        await AddCopiesAsync(fixture, UserA, "dup-a", "Korean", "Korean");
        fixture.Activate(UserA);
        var handedOver = new List<(string Term, string WordId)>();
        using var importEditor = new VocabularyBrowser(
            fixture.Provider.GetRequiredService<INativeResourceStore>(),
            fixture.UserScope, fixture.Preferences, new ScratchQuizMedia(),
            NullLogger<VocabularyBrowser>.Instance, key => key)
        {
            FindDuplicatesElsewhere = (term, wordId) => handedOver.Add((term, wordId))
        };
        Assert.True(await importEditor.OpenAsync(UserA));
        await importEditor.OpenWordAsync("dup-a-1");

        await importEditor.FindDuplicatesAsync();

        Assert.False(importEditor.IsFormOpen);
        Assert.Null(importEditor.ListState.Duplicates);
        Assert.Equal([("사과", "dup-a-1")], handedOver);

        using var vocabularyPage = Create(fixture);
        Assert.True(await vocabularyPage.OpenAsync(UserA));
        await vocabularyPage.OpenDuplicatesAsync(handedOver[0].Term, handedOver[0].WordId);
        var panel = Assert.IsType<VocabularyDuplicatePanel>(vocabularyPage.ListState.Duplicates);
        Assert.Equal(("dup-a-1", "사과"), (panel.FocusedWordId, panel.FocusLabel));
        Assert.True(panel.IsFocused(Assert.Single(panel.Groups)));

        // Another owner on the device opens nothing.
        fixture.Activate(UserB);
        using var other = Create(fixture);
        Assert.False(await other.OpenAsync(UserA));
        await other.OpenDuplicatesAsync("사과", "dup-a-1");
        Assert.Null(other.ListState.Duplicates);
    }

    [Fact]
    public async Task FindDuplicates_BlockedGroupExplainsAndMergesNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await AddCopiesAsync(fixture, UserA, "dup-a", "Korean", "Japanese");
        fixture.Activate(UserA);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(UserA));
        await browser.OpenWordAsync("dup-a-1");
        await browser.FindDuplicatesAsync();

        var group = Assert.Single(browser.ListState.Duplicates!.Groups);
        Assert.False(group.CanMergeAutomatically);
        Assert.Contains("language", group.MergeBlockedReason);

        await browser.MergeDuplicatesAsync(group.NormalizedTerm);
        Assert.Equal(group.MergeBlockedReason, browser.ListState.Feedback);
        Assert.True(browser.ListState.FeedbackWarning);
        Assert.Equal(1, (await RowsAsync(fixture, "dup-a-1")).Words);
        Assert.Equal(1, (await RowsAsync(fixture, "dup-a-2")).Words);

        // Source "Edit record": the copy opens in the editor, and the manager stays open.
        await browser.OpenWordAsync("dup-a-2");
        Assert.True(browser.IsFormOpen);
        Assert.NotNull(browser.ListState.Duplicates);
    }

    [Fact]
    public async Task DuplicateStore_IsOwnerScoped()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await fixture.SeedAsync(UserB);
        await AddCopiesAsync(fixture, UserA, "dup-a", "Korean", "Korean");
        await AddCopiesAsync(fixture, UserB, "dup-b", "Korean", "Korean");
        fixture.Activate(UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();

        var scan = await store.FindOwnedVocabularyDuplicatesAsync(UserA, null, 0, 20, default);
        Assert.True(scan.OwnerValid);
        var group = Assert.Single(scan.Groups);
        Assert.All(group.Words, word => Assert.StartsWith("dup-a-", word.Id));
        Assert.False((await store.FindOwnedVocabularyDuplicatesAsync(
            UserB, null, 0, 20, default)).OwnerValid);

        // A merge naming another owner's word merges nothing.
        Assert.Equal(NativeVocabularyMergeResult.None, await store.MergeOwnedVocabularyDuplicatesAsync(
            UserA, "dup-a-2", ["dup-b-1"], default));
        Assert.Equal(NativeVocabularyMergeResult.None, await store.MergeOwnedVocabularyDuplicatesAsync(
            UserB, "dup-b-2", ["dup-b-1"], default));
        Assert.Equal(1, (await RowsAsync(fixture, "dup-b-1")).Words);

        fixture.LoseOwner();
        Assert.False((await store.FindOwnedVocabularyDuplicatesAsync(
            UserA, null, 0, 20, default)).OwnerValid);
        Assert.Equal(NativeVocabularyMergeResult.None, await store.MergeOwnedVocabularyDuplicatesAsync(
            UserA, "dup-a-2", ["dup-a-1"], default));
        Assert.Equal(1, (await RowsAsync(fixture, "dup-a-1")).Words);
    }

    [Fact]
    public async Task Delete_IsTrackedForSyncWithItsCascadedRows()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA, known: true);
        await fixture.SeedAsync(UserB, known: true);
        await AddHistoryAndExampleAsync(fixture, UserA, 0);
        var sync = new SqliteSyncProvider(new SqliteSyncConfigurationBuilder(
                $"Data Source={fixture.DatabasePath}").ConfigureSyncTables().Build(),
            ProviderMode.Local);
        await sync.ApplyProvisionAsync();
        // A server that has already received everything (its anchor is the current version).
        var server = Guid.NewGuid();
        var initial = await sync.GetChangesAsync(server);
        await sync.SaveVersionForStoreAsync(server, initial.SourceAnchor.Version);
        fixture.Activate(UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var wordA = fixture.WordId(UserA, 0);

        Assert.Equal(NativeVocabularyDeleteStatus.Deleted,
            await store.DeleteOwnedVocabularyAsync(UserA, wordA, default));

        var changes = await sync.GetChangesAsync(server);
        var deletes = changes.Items.Where(item => item.ChangeType == ChangeType.Delete)
            .Select(item => (item.TableName, Id: item.Values["Id"].Value?.ToString()))
            .ToHashSet();
        Assert.Contains(("VocabularyWord", wordA), deletes);
        Assert.Contains(("ResourceVocabularyMapping", $"mapping-{UserA}-0"), deletes);
        Assert.Contains(("VocabularyProgress", $"progress-{UserA}-0"), deletes);
        Assert.Contains(("VocabularyLearningContext", $"context-{UserA}-0"), deletes);
        Assert.Contains(("ExampleSentence", $"example-{UserA}-0"), deletes);
        // Nothing of the other owner's changed.
        Assert.DoesNotContain(changes.Items, item =>
            item.Values.Values.Any(value => value.Value?.ToString()?.Contains(UserB) == true));
    }

    // Review 2026-11-05: AI example sentences carry the owner's UserProfileId and no resource.
    // One runtime rule (Captain 2026-11-06): a sentence is the learner's only when it is
    // recorded to them. One recorded to no one is no one's, wherever it sits (the learner's
    // resource or another's), and blocks the delete like another learner's sentence.
    [Fact]
    public async Task Delete_TakesAWordWithTheOwnersOwnSentencesButNotAnotherOwnersSentence()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA, known: true);
        await fixture.SeedAsync(UserB);
        var withOwnSentence = fixture.WordId(UserA, 0);
        var withForeignSentence = fixture.WordId(UserA, 1);
        var withUnownedSentence = fixture.WordId(UserA, 2);
        var withSentenceOnTheirResource = fixture.WordId(UserA, 3);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ExampleSentences.Add(AiSentence("ai-own", withOwnSentence, UserA));
            db.ExampleSentences.Add(AiSentence("ai-foreign", withForeignSentence, UserB));
            var unowned = AiSentence("legacy-unowned", withUnownedSentence, UserA);
            unowned.UserProfileId = null;
            unowned.LearningResourceId = fixture.ResourceId(UserA);
            db.ExampleSentences.Add(unowned);
            var onTheirs = AiSentence("legacy-on-theirs", withSentenceOnTheirResource, UserA);
            onTheirs.UserProfileId = null;
            onTheirs.LearningResourceId = fixture.ResourceId(UserB);
            db.ExampleSentences.Add(onTheirs);
            await db.SaveChangesAsync();
        }
        fixture.Activate(UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(UserA));

        var refusedWords = new[] { withForeignSentence, withUnownedSentence, withSentenceOnTheirResource };
        foreach (var refused in refusedWords)
        {
            await browser.OpenWordAsync(refused);
            // The word is still the owner's to edit; the sentence that is not theirs keeps it.
            Assert.Equal(NativeVocabularyDeleteBlock.UsedByOthers, browser.EditState.Detail!.DeleteBlock);
            Assert.True(browser.EditState.CanSave);
            Assert.Empty(browser.EditState.Detail!.Examples);
            Assert.Equal(NativeVocabularyDeleteStatus.UsedByOthers,
                await store.DeleteOwnedVocabularyAsync(UserA, refused, default));
        }

        await browser.OpenWordAsync(withOwnSentence);
        Assert.Equal(NativeVocabularyDeleteBlock.None, browser.EditState.Detail!.DeleteBlock);
        Assert.Single(browser.EditState.Detail!.Examples);
        Assert.True(browser.EditState.CanDelete);
        browser.RequestDelete();
        await browser.ConfirmDeleteAsync();

        Assert.False(browser.IsFormOpen);
        Assert.Null(browser.EditState.DeleteError);
        Assert.Equal("VocabWordEdit_DeletedSuccess", browser.ListState.Feedback);
        Assert.Equal(new WordRows(0, 0, 0, 0, 0), await RowsAsync(fixture, withOwnSentence));
        foreach (var kept in refusedWords)
        {
            var rows = await RowsAsync(fixture, kept);
            Assert.Equal(1, rows.Words);
            Assert.Equal(1, rows.Examples);
        }
    }

    // Review 2026-11-05: a committed delete whose sync tracking fails says so, as Save does.
    [Fact]
    public async Task Delete_WhoseSyncTrackingFails_StillDeletesAndWarns()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA, known: true);
        fixture.Activate(UserA);
        // The next MarkPending overflows its checked revision counter and throws.
        fixture.Preferences.Set("native_sync_revision_v1_" + UserA, long.MaxValue);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(UserA));
        var wordId = fixture.WordId(UserA, 0);
        await browser.OpenWordAsync(wordId);
        browser.RequestDelete();
        await browser.ConfirmDeleteAsync();

        Assert.False(browser.IsFormOpen);
        Assert.Equal("VocabWordEdit_DeletedSuccess NativeResources_SyncUnconfirmed",
            browser.ListState.Feedback);
        Assert.True(browser.ListState.FeedbackWarning);
        Assert.Equal(0, (await RowsAsync(fixture, wordId)).Words);
    }

    [Fact]
    public async Task Merge_WhoseSyncTrackingFails_StillMergesAndWarns()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await AddCopiesAsync(fixture, UserA, "dup-a", "Korean", "Korean");
        fixture.Activate(UserA);
        fixture.Preferences.Set("native_sync_revision_v1_" + UserA, long.MaxValue);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(UserA));
        await browser.OpenWordAsync("dup-a-1");
        await browser.FindDuplicatesAsync();

        await browser.MergeDuplicatesAsync(Assert.Single(browser.ListState.Duplicates!.Groups).NormalizedTerm);

        Assert.Equal("Vocabulary_DuplicatesMergedSuccess NativeResources_SyncUnconfirmed",
            browser.ListState.Feedback);
        Assert.True(browser.ListState.FeedbackWarning);
        Assert.Equal(0, (await RowsAsync(fixture, "dup-a-1")).Words);
        Assert.DoesNotContain(browser.ListState.FilteredWords, word => word.Id == "dup-a-1");
    }

    // Review 2026-11-05: a merge can keep every copy (another account still uses it) and still
    // move this owner's links: that is a saved change, not "no merge needed".
    [Fact]
    public async Task Merge_ThatMovesLinksButKeepsASharedCopy_IsASavedChange()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await fixture.SeedAsync(UserB);
        await AddCopiesAsync(fixture, UserA, "dup-a", "Korean", "Korean");
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                Id = "mapping-dup-a-1-by-b", ResourceId = fixture.ResourceId(UserB),
                VocabularyWordId = "dup-a-1"
            });
            await db.SaveChangesAsync();
        }
        fixture.Activate(UserA);
        var pending = fixture.Provider.GetRequiredService<NativePendingWrites>();
        Assert.False(pending.IsPending(UserA));
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(UserA));
        await browser.OpenWordAsync("dup-a-2");
        await browser.FindDuplicatesAsync();
        var group = Assert.Single(browser.ListState.Duplicates!.Groups);
        Assert.Equal("dup-a-2", group.RecommendedKeeperId);

        await browser.MergeDuplicatesAsync(group.NormalizedTerm);

        var list = browser.ListState;
        Assert.Equal("Vocabulary_DuplicatesMergedCount", list.Feedback);
        Assert.False(list.FeedbackWarning);
        // The list reloaded: the kept copy is still recorded to this owner (only the other
        // account's link holds it), so it stays in their list, now in none of their resources.
        Assert.Contains(list.FilteredWords, word => word.Id == "dup-a-1"
            && word.AssociatedResources.Count == 0);
        Assert.Contains(list.FilteredWords, word => word.Id == "dup-a-2");
        Assert.True(pending.IsPending(UserA));
        var kept = await RowsAsync(fixture, "dup-a-1");
        Assert.Equal(1, kept.Words);
        Assert.Equal(1, kept.Mappings);
        Assert.Equal(1, (await RowsAsync(fixture, "dup-a-2")).Mappings);
        Assert.False(pending.IsPending(UserB));
        // Nothing is left to move.
        Assert.Equal(NativeVocabularyMergeResult.None, await fixture.Provider
            .GetRequiredService<INativeResourceStore>()
            .MergeOwnedVocabularyDuplicatesAsync(UserA, "dup-a-2", ["dup-a-1"], default));
    }

    // One ownership rule (Captain 2026-11-06): a copy recorded to another owner sits in the
    // learner's resource but is not theirs, so it is never in their duplicate groups and its
    // memory aids are never sent to the device's duplicate manager.
    [Fact]
    public async Task DuplicateScan_NeverShowsAnotherOwnersCopy()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyWords.Add(new VocabularyWord {
                Id = "dup-own", UserProfileId = UserA, TargetLanguageTerm = "apple", NativeLanguageTerm = "apple",
                Language = "Korean", LexicalUnitType = LexicalUnitType.Word,
                CreatedAt = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc)
            });
            db.VocabularyWords.Add(new VocabularyWord {
                Id = "dup-legacy", TargetLanguageTerm = "Apple", NativeLanguageTerm = "apple",
                Language = "Korean", LexicalUnitType = LexicalUnitType.Word, UserProfileId = UserB,
                MnemonicText = "Their private story", MnemonicImageUri = "their.png",
                AudioPronunciationUri = "their.mp3",
                CreatedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc)
            });
            foreach (var id in new[] { "dup-own", "dup-legacy" })
                db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                    Id = $"mapping-{id}", ResourceId = fixture.ResourceId(UserA), VocabularyWordId = id
                });
            await db.SaveChangesAsync();
        }
        fixture.Activate(UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();

        var scan = await store.FindOwnedVocabularyDuplicatesAsync(UserA, "apple", 0, 20, default);
        Assert.True(scan.OwnerValid);
        Assert.Empty(scan.Groups);
        Assert.Equal(0, scan.TotalGroupCount);
        Assert.DoesNotContain("Their private story", System.Text.Json.JsonSerializer.Serialize(scan));
        // A merge naming the other owner's copy changes nothing.
        Assert.Equal(NativeVocabularyMergeResult.None,
            await store.MergeOwnedVocabularyDuplicatesAsync(UserA, "dup-own", ["dup-legacy"], default));
        Assert.Equal(1, (await RowsAsync(fixture, "dup-legacy")).Words);
    }

    // Review 2026-11-05: a rescan asked for while a merge runs waits for it, so the merge's
    // result line stays; the merge's own rescan already sees what was asked for.
    [Fact]
    public async Task Rescan_DuringAMerge_RunsAfterItAndKeepsTheMergeResult()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await AddCopiesAsync(fixture, UserA, "dup-a", "Korean", "Korean");
        fixture.Activate(UserA);
        var gated = GatedStore.Wrap(fixture, out var store);
        var finder = new VocabularyDuplicateFinder(store, NullLogger.Instance, key => key, new Host());
        await finder.OpenAsync(UserA, "사과", "dup-a-1", default);
        var group = Assert.Single(finder.Panel!.Groups);
        gated.MergeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var merging = finder.MergeAsync(UserA, group.NormalizedTerm, default);
        await gated.MergeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await finder.RescanAsync(UserA, default);
        Assert.True(finder.Panel!.Running);
        Assert.Equal("Vocabulary_MergingGroup", finder.Panel.Status);
        Assert.Equal(1, gated.Scans);

        gated.MergeGate.SetResult();
        var outcome = await merging.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(outcome);
        Assert.True(outcome.Changed);
        Assert.Equal("Vocabulary_DuplicatesMergedSuccess", outcome.Feedback);
        var panel = finder.Panel!;
        Assert.False(panel.Running);
        Assert.Equal(new[] { "Vocabulary_DuplicatesMergedCount" }, panel.Results);
        Assert.Empty(panel.Groups);
        // The open and the merge's own rescan, which started after the request.
        Assert.Equal(2, gated.Scans);
    }

    [Fact]
    public async Task Rescan_DuringAScan_RunsOnceMoreAfterIt()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await AddCopiesAsync(fixture, UserA, "dup-a", "Korean", "Korean");
        fixture.Activate(UserA);
        var gated = GatedStore.Wrap(fixture, out var store);
        var finder = new VocabularyDuplicateFinder(store, NullLogger.Instance, key => key, new Host());
        gated.ScanGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var opening = finder.OpenAsync(UserA, "사과", "dup-a-1", default);
        await gated.ScanEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await finder.RescanAsync(UserA, default);
        var gate = gated.ScanGate;
        gated.ScanGate = null;
        gate.SetResult();
        await opening.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, gated.Scans);
        Assert.False(finder.Panel!.Running);
        Assert.Single(finder.Panel.Groups);
    }

    // Review 2026-11-05: a failed scan ends "Scanning..." or "Merging..." with the error.
    [Fact]
    public async Task FailedScan_EndsTheStatusWithTheError()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await AddCopiesAsync(fixture, UserA, "dup-a", "Korean", "Korean");
        fixture.Activate(UserA);
        var gated = GatedStore.Wrap(fixture, out var store);
        static string Text(string key) => key == "Vocabulary_CleanupError" ? "Cleanup failed: {0}" : key;
        var finder = new VocabularyDuplicateFinder(store, NullLogger.Instance, Text, new Host());
        gated.FailScans = true;

        await finder.OpenAsync(UserA, "사과", "dup-a-1", default);
        Assert.False(finder.Panel!.Running);
        Assert.Equal("Cleanup failed: The scan broke.", finder.Panel.Status);

        gated.FailScans = false;
        await finder.RescanAsync(UserA, default);
        var group = Assert.Single(finder.Panel!.Groups);
        gated.FailScans = true;
        var outcome = await finder.MergeAsync(UserA, group.NormalizedTerm, default);

        Assert.True(outcome!.Changed);
        Assert.False(finder.Panel!.Running);
        Assert.Equal("Cleanup failed: The scan broke.", finder.Panel.Status);
        Assert.Equal(new[] { "Vocabulary_DuplicatesMergedCount" }, finder.Panel.Results);
    }

    // Source: "Found N group(s) of duplicate words with M extra record(s). Showing K."
    [Fact]
    public async Task Status_SaysHowManyGroupsAreShown()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await AddCopiesAsync(fixture, UserA, "dup-a", "Korean", "Korean");
        fixture.Activate(UserA);
        static string Text(string key) => key switch
        {
            "Vocabulary_DuplicatesFoundCount" =>
                "Found {0} group(s) of duplicate words with {1} extra record(s).",
            "NativeVocabulary_DuplicatesShowing" => "Showing {0}.",
            _ => key
        };
        var finder = new VocabularyDuplicateFinder(
            fixture.Provider.GetRequiredService<INativeResourceStore>(), NullLogger.Instance, Text,
            new Host());

        await finder.OpenAsync(UserA, null, null, default);

        Assert.Equal("Found 1 group(s) of duplicate words with 1 extra record(s). Showing 1.",
            finder.Panel!.Status);
    }

    // Review 2026-11-05: leaving the list while a scan or merge runs is not an owner change.
    [Fact]
    public async Task LeavingTheList_DuringAScanOrMerge_DoesNotReportAnOwnerChange()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await AddCopiesAsync(fixture, UserA, "dup-a", "Korean", "Korean");
        fixture.Activate(UserA);
        var gated = GatedStore.Wrap(fixture, out var store);
        var logger = new ListLogger<VocabularyBrowser>();
        using var browser = new VocabularyBrowser(store, fixture.UserScope, fixture.Preferences,
            new ScratchQuizMedia(), logger, key => key);
        Assert.True(await browser.OpenAsync(UserA));

        await browser.OpenWordAsync("dup-a-1");
        gated.ScanGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var finding = browser.FindDuplicatesAsync();
        await gated.ScanEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        browser.Close();
        gated.ScanGate.SetResult();
        await finding.WaitAsync(TimeSpan.FromSeconds(10));
        gated.ScanGate = null;
        Assert.False(browser.IsOpen);

        Assert.True(await browser.OpenAsync(UserA));
        await browser.OpenWordAsync("dup-a-1");
        await browser.FindDuplicatesAsync();
        var group = Assert.Single(browser.ListState.Duplicates!.Groups);
        gated.MergeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var merging = browser.MergeDuplicatesAsync(group.NormalizedTerm);
        await gated.MergeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        browser.Close();
        gated.MergeGate.SetResult();
        await merging.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(browser.IsOpen);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("owner", StringComparison.Ordinal));
        Assert.True(await browser.OpenAsync(UserA));
        Assert.False(browser.ListState.Blocked);
        Assert.Null(browser.ListState.ErrorKey);
        Assert.Null(browser.ListState.Duplicates);
    }

    // Review 2026-11-05: the save lets go of the editor before the manager scans again.
    [Fact]
    public async Task Save_FinishesBeforeTheManagerScansAgain()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await AddCopiesAsync(fixture, UserA, "dup-a", "Korean", "Korean");
        fixture.Activate(UserA);
        var gated = GatedStore.Wrap(fixture, out var store);
        using var browser = new VocabularyBrowser(store, fixture.UserScope, fixture.Preferences,
            new ScratchQuizMedia(), NullLogger<VocabularyBrowser>.Instance, key => key);
        Assert.True(await browser.OpenAsync(UserA));
        await browser.OpenWordAsync("dup-a-1");
        await browser.FindDuplicatesAsync();
        Assert.NotNull(browser.ListState.Duplicates);

        await browser.OpenWordAsync(fixture.WordId(UserA, 0));
        browser.ChangeDraft(nameof(NativeVocabularyEditDraft.NativeTerm), "a changed meaning");
        gated.ScanGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = browser.SaveAsync();
        await gated.ScanEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(browser.IsFormOpen);
        Assert.False(browser.IsNavigationBlocked);
        Assert.Equal("VocabWordEdit_UpdatedSuccess", browser.ListState.Feedback);
        Assert.True(browser.ListState.Duplicates!.Running);

        gated.ScanGate.SetResult();
        await saving.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(browser.ListState.Duplicates!.Running);
        Assert.Single(browser.ListState.Duplicates.Groups);
    }

    // One ownership rule (Captain 2026-11-06): a copy not recorded to the learner (another
    // learner's, or a legacy word recorded to nobody) sits in the learner's resource but is not
    // theirs, so it is never grouped with their word, and a merge naming it changes no row.
    [Theory]
    [InlineData(UserB)]
    [InlineData(null)]
    public async Task CopyNotRecordedToTheOwner_IsNeverGrouped_AndAMergeNamingItChangesNoRow(
        string? legacyOwner)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await fixture.SeedAsync(UserB);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyWords.Add(new VocabularyWord {
                Id = "dup-own", UserProfileId = UserA, TargetLanguageTerm = "사과", NativeLanguageTerm = "apple",
                Language = "Korean", LexicalUnitType = LexicalUnitType.Word,
                CreatedAt = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc)
            });
            db.VocabularyWords.Add(new VocabularyWord {
                Id = "dup-legacy", TargetLanguageTerm = "사과", NativeLanguageTerm = "apple",
                Language = "Korean", LexicalUnitType = LexicalUnitType.Word, UserProfileId = UserB,
                MnemonicText = "Their private story", MnemonicImageUri = "their.png",
                AudioPronunciationUri = "their.mp3",
                CreatedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc)
            });
            foreach (var id in new[] { "dup-own", "dup-legacy" })
                db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                    Id = $"mapping-{id}", ResourceId = fixture.ResourceId(UserA), VocabularyWordId = id
                });
            // A word recorded to no one is no one's even when only A links it: nothing infers A
            // from that link.
            await db.SaveChangesAsync();
            if (legacyOwner is null)
                await NativeSliceFixture.RecordToNoOneAsync(db, "dup-legacy");
        }
        fixture.Activate(UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var pending = fixture.Provider.GetRequiredService<NativePendingWrites>();
        var before = await SnapshotAsync(fixture, "dup-own", "dup-legacy");
        Assert.Equal(legacyOwner, before.Single(word => word.Id == "dup-legacy").Owner);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(UserA));
        await browser.OpenWordAsync("dup-own");
        await browser.FindDuplicatesAsync();

        Assert.Empty(browser.ListState.Duplicates!.Groups);
        Assert.DoesNotContain(browser.ListState.FilteredWords, word => word.Id == "dup-legacy");

        // Asked directly, the store refuses either keeper.
        Assert.Equal(NativeVocabularyMergeResult.None, await store.MergeOwnedVocabularyDuplicatesAsync(
            UserA, "dup-own", ["dup-legacy"], default));
        Assert.Equal(NativeVocabularyMergeResult.None, await store.MergeOwnedVocabularyDuplicatesAsync(
            UserA, "dup-legacy", ["dup-own"], default));

        // No field of the other row reached the owner's row, and that row is untouched.
        Assert.Equal(before, await SnapshotAsync(fixture, "dup-own", "dup-legacy"));
        var own = before.Single(word => word.Id == "dup-own");
        Assert.Null(own.Mnemonic);
        Assert.Null(own.Image);
        Assert.Null(own.Audio);
        Assert.False(pending.IsPending(UserA));
    }

    // The one ownership rule (Captain 2026-11-06), checked against what the server's owner-checked
    // sync does with the same rows (the shared rules it calls).
    // Whose the word is: its recorded owner only (VocabularyWordOwnership). A word recorded to no
    // one is no one's and not shown, whether only A links it ("unrecorded-word") or A and B do
    // ("shared-legacy-word"); the server never changes it from a device.
    // Edit: the recorded owner may always edit their word (nobody else can see it), as the server
    // applies the owner's update of their own word.
    // Delete: native refuses exactly what the server refuses, and says why: data that is not the
    // owner's still uses the word (another learner's link, progress, sentence, pair or phrase, or a
    // sentence recorded to no one, wherever it sits), or a Minimal Pairs answer, which the
    // database also refuses.
    [Theory]
    [InlineData("none", true, NativeVocabularyDeleteStatus.Deleted)]
    [InlineData("mapping", true, NativeVocabularyDeleteStatus.UsedByOthers)]
    [InlineData("progress", true, NativeVocabularyDeleteStatus.UsedByOthers)]
    [InlineData("sentence", true, NativeVocabularyDeleteStatus.UsedByOthers)]
    [InlineData("sentence-on-their-resource", true, NativeVocabularyDeleteStatus.UsedByOthers)]
    [InlineData("unowned-sentence", true, NativeVocabularyDeleteStatus.UsedByOthers)]
    [InlineData("pair", true, NativeVocabularyDeleteStatus.UsedByOthers)]
    [InlineData("phrase", true, NativeVocabularyDeleteStatus.UsedByOthers)]
    [InlineData("answer", true, NativeVocabularyDeleteStatus.UsedByAnswers)]
    [InlineData("unrecorded-word", false, NativeVocabularyDeleteStatus.NotFoundOrForbidden)]
    [InlineData("shared-legacy-word", false, NativeVocabularyDeleteStatus.NotFoundOrForbidden)]
    public async Task EditAndDelete_MatchWhatTheServerAcceptsForTheSameRows(
        string use, bool shown, NativeVocabularyDeleteStatus expectedDelete)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await fixture.SeedAsync(UserB);
        var wordId = fixture.WordId(UserA, 0);
        var ownOther = fixture.WordId(UserA, 1);
        var theirs = fixture.WordId(UserB, 0);
        var at = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            switch (use)
            {
                case "mapping":
                case "shared-legacy-word":
                    db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                        Id = "use-mapping", ResourceId = fixture.ResourceId(UserB), VocabularyWordId = wordId });
                    break;
                case "progress":
                    db.VocabularyProgresses.Add(new VocabularyProgress {
                        Id = "use-progress", VocabularyWordId = wordId, UserId = UserB });
                    break;
                case "sentence":
                    db.ExampleSentences.Add(AiSentence("use-sentence", wordId, UserB));
                    break;
                case "unowned-sentence":
                case "sentence-on-their-resource":
                    var sentence = AiSentence("use-unowned", wordId, UserA);
                    sentence.UserProfileId = null;
                    sentence.LearningResourceId = fixture.ResourceId(
                        use == "unowned-sentence" ? UserA : UserB);
                    db.ExampleSentences.Add(sentence);
                    break;
                case "pair":
                    db.MinimalPairs.Add(new MinimalPair {
                        Id = "use-pair", UserId = UserB, VocabularyWordAId = wordId,
                        VocabularyWordBId = theirs, CreatedAt = at, UpdatedAt = at });
                    break;
                case "phrase":
                    db.PhraseConstituents.Add(new PhraseConstituent {
                        Id = "use-phrase", PhraseWordId = theirs, ConstituentWordId = wordId });
                    break;
                case "answer":
                    // The owner's own pair, session and answer: nothing of another learner's.
                    db.MinimalPairs.Add(new MinimalPair {
                        Id = "own-pair", UserId = UserA, VocabularyWordAId = wordId,
                        VocabularyWordBId = ownOther, CreatedAt = at, UpdatedAt = at });
                    db.MinimalPairSessions.Add(new MinimalPairSession {
                        Id = "own-session", UserId = UserA, StartedAt = at, CreatedAt = at, UpdatedAt = at });
                    db.MinimalPairAttempts.Add(new MinimalPairAttempt {
                        Id = "own-answer", UserId = UserA, SessionId = "own-session", PairId = "own-pair",
                        PromptWordId = wordId, SelectedWordId = ownOther, CreatedAt = at });
                    break;
            }
            await db.SaveChangesAsync();
            if (use is "unrecorded-word" or "shared-legacy-word")
                await NativeSliceFixture.RecordToNoOneAsync(db, wordId);
        }
        fixture.Activate(UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();

        // The server, over the same rows: a word goes to (and is changed for) its recorded owner.
        bool serverUpdates, serverDeletes;
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var storedOwner = (await SyncRowOwners.GetStoredWordOwnersAsync(db, [wordId], default))
                .GetValueOrDefault(wordId);
            serverUpdates = storedOwner == UserA;
            serverDeletes = storedOwner == UserA
                && !(await VocabularyWordDependents.OthersDependOnAsync(db, [wordId], UserA, default))
                    .Contains(wordId)
                && !(await VocabularyWordDependents.AnswersStillUseAsync(db, [wordId], default))
                    .Contains(wordId);
        }
        Assert.Equal(shown, serverUpdates);
        Assert.Equal(use is "none", serverDeletes);

        var detail = await store.GetOwnedVocabularyAsync(UserA, wordId, default);
        Assert.Equal(shown, detail is not null);
        Assert.Equal(shown, (await store.ListOwnedVocabularyAsync(UserA, default)).Words
            .Any(word => word.Id == wordId));
        if (detail is not null)
        {
            Assert.Equal(expectedDelete == NativeVocabularyDeleteStatus.Deleted,
                VocabularyWordCommands.CanDelete(false, detail));
            Assert.Equal(expectedDelete switch
            {
                NativeVocabularyDeleteStatus.Deleted => NativeVocabularyDeleteBlock.None,
                NativeVocabularyDeleteStatus.UsedByAnswers => NativeVocabularyDeleteBlock.UsedByAnswers,
                _ => NativeVocabularyDeleteBlock.UsedByOthers
            }, detail.DeleteBlock);
            Assert.Equal(expectedDelete switch
            {
                NativeVocabularyDeleteStatus.UsedByOthers => "NativeVocabulary_DeleteUsedByOthers",
                NativeVocabularyDeleteStatus.UsedByAnswers => "NativeVocabulary_DeleteUsedByAnswers",
                _ => null
            }, VocabularyWordCommands.DeleteBlockedReason(detail, key => key));
            // Only sentences recorded to the learner are listed and counted as theirs.
            Assert.Empty(detail.Examples);
        }

        var deleted = await store.DeleteOwnedVocabularyAsync(UserA, wordId, default);
        Assert.Equal(expectedDelete, deleted);
        // Native deletes exactly what the server would.
        Assert.Equal(serverDeletes, deleted == NativeVocabularyDeleteStatus.Deleted);
        Assert.Equal(deleted == NativeVocabularyDeleteStatus.Deleted ? 0 : 1,
            (await RowsAsync(fixture, wordId)).Words);
        if (deleted == NativeVocabularyDeleteStatus.Deleted)
            return;

        var draft = detail?.Draft ?? new NativeVocabularyEditDraft(wordId, at, $"target-{UserA}-0",
            $"native-{UserA}-0", null, "Korean", null, null, null, LexicalUnitType.Word,
            [fixture.ResourceId(UserA)]);
        var saved = await store.SaveOwnedVocabularyAsync(UserA,
            draft with { NativeTerm = "changed" }, default);
        Assert.Equal(shown ? NativeVocabularySaveStatus.Saved
            : NativeVocabularySaveStatus.NotFoundOrForbidden, saved);
        // Native never saves an edit the server would not apply.
        Assert.True(!shown || serverUpdates);
        using var after = fixture.Scope();
        var stored = await after.ServiceProvider
            .GetRequiredService<ApplicationDbContext>().VocabularyWords.AsNoTracking()
            .SingleAsync(word => word.Id == wordId);
        Assert.Equal(shown ? "changed" : $"native-{UserA}-0", stored.NativeLanguageTerm);
        Assert.Equal(shown ? UserA : null, stored.UserProfileId);
    }

    // Review 4: Save may remove every link; the word stays the learner's and editable, and can
    // be deleted. Under the one ownership rule it also stays in their list.
    [Fact]
    public async Task SavingWithNoResources_KeepsTheWordEditableAndDeletable()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA, known: true);
        fixture.Activate(UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var wordId = fixture.WordId(UserA, 0);
        var detail = (await store.GetOwnedVocabularyAsync(UserA, wordId, default))!;
        Assert.Equal(NativeVocabularySaveStatus.Saved, await store.SaveOwnedVocabularyAsync(UserA,
            detail.Draft with { ResourceIds = [] }, default));

        var reopened = (await store.GetOwnedVocabularyAsync(UserA, wordId, default))!;
        Assert.Empty(reopened.Draft.ResourceIds);
        Assert.Equal(NativeVocabularyDeleteBlock.None, reopened.DeleteBlock);
        Assert.Contains((await store.ListOwnedVocabularyAsync(UserA, default)).Words,
            word => word.Id == wordId && word.AssociatedResources.Count == 0);
        Assert.Equal(NativeVocabularyDeleteStatus.Deleted,
            await store.DeleteOwnedVocabularyAsync(UserA, wordId, default));
    }

    /// <summary>
    /// The owner's word that no resource links and no progress holds (for example one added on
    /// the web without a resource, then synced) can be saved with no resource selected: under the
    /// owner rule it stays in their vocabulary, so the old "keep a resource" refusal is gone.
    /// </summary>
    [Fact]
    public async Task OwnersUnlinkedWordWithoutProgress_SavesWithNoResources()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA, wordCount: 0);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyWords.Add(new VocabularyWord {
                Id = "web-added", UserProfileId = UserA, TargetLanguageTerm = "웹", NativeLanguageTerm = "web",
                Language = "Korean", LexicalUnitType = LexicalUnitType.Word });
            await db.SaveChangesAsync();
        }
        fixture.Activate(UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var detail = (await store.GetOwnedVocabularyAsync(UserA, "web-added", default))!;
        Assert.Null(detail.Progress);
        Assert.Empty(detail.Draft.ResourceIds);
        Assert.Equal(NativeVocabularySaveStatus.Saved, await store.SaveOwnedVocabularyAsync(UserA,
            detail.Draft with { NativeTerm = "the web" }, default));
        Assert.Equal("VocabWordEdit_SaveFailed", VocabularyBrowser.SaveErrorKey(
            NativeVocabularySaveStatus.Invalid, detail, detail.Draft));
    }

    // Review 4: a refused delete maps the shared delete's reason to what is true now. Another
    // learner's progress that arrives after the editor opened blocks the delete: the message
    // says so and the open editor refreshes, instead of keeping Delete offered. The word stays
    // the owner's to edit.
    [Fact]
    public async Task RefusedDelete_AfterAnotherLearnersRowArrives_SaysWhyAndRefreshesTheEditor()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await fixture.SeedAsync(UserB);
        fixture.Activate(UserA);
        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(UserA));
        var wordId = fixture.WordId(UserA, 0);
        await browser.OpenWordAsync(wordId);
        Assert.True(browser.EditState.CanDelete);
        browser.RequestDelete();
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyProgresses.Add(new VocabularyProgress {
                Id = "arrived-by-sync", VocabularyWordId = wordId, UserId = UserB });
            await db.SaveChangesAsync();
        }

        await browser.ConfirmDeleteAsync();

        Assert.True(browser.IsFormOpen);
        Assert.Equal("NativeVocabulary_DeleteUsedByOthers", browser.EditState.DeleteError);
        Assert.Equal(NativeVocabularyDeleteBlock.UsedByOthers, browser.EditState.Detail!.DeleteBlock);
        Assert.False(browser.EditState.CanDelete);
        Assert.True(browser.EditState.CanSave);
        Assert.Equal(1, (await RowsAsync(fixture, wordId)).Words);
    }

    // A group blocked by the source's own check keeps the source's "Edit before merge" advice:
    // every copy is the learner's, so editing the records unblocks it.
    [Fact]
    public void BlockedGroup_ShowsTheSourceReasonAndEditAdvice()
    {
        var blocked = new NativeVocabularyDuplicateGroup("사과", "사과", [], false,
            "Language differs", "keeper");
        Assert.Equal("Language differs", VocabularyDuplicateFinder.BlockedReason(blocked, key => key));
        Assert.Equal(new VocabularyDuplicateBlock("NativeVocabulary_DuplicatesEditBeforeMerge",
                "NativeVocabulary_DuplicatesMergeUnavailable", "Language differs",
                "NativeVocabulary_DuplicatesMergeUnavailableHelp"),
            VocabularyDuplicateFinder.Blocked(blocked, key => key));
        Assert.Equal("NativeVocabulary_DuplicatesBlockedFallback", VocabularyDuplicateFinder
            .BlockedReason(blocked with { MergeBlockedReason = null }, key => key));
    }

    // Review 2026-11-05 (third pass): with no owner, `x.Owner != owner` stops matching rows that
    // name nobody, so the shared rule would find fewer uses. It refuses to run instead.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task OthersDependOn_WithNoOwner_FailsClosed(string? owner)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await Assert.ThrowsAsync<ArgumentException>(() => VocabularyWordDependents.OthersDependOnAsync(
            db, [fixture.WordId(UserA, 0)], owner!, default));
    }

    // Review 2026-11-05 (second pass): leaving the list (disposing the browser) while a delete
    // or the post-save rescan runs ends quietly, with no false error.
    [Fact]
    public async Task Dispose_DuringADelete_EndsQuietly()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA, known: true);
        fixture.Activate(UserA);
        var gated = GatedStore.Wrap(fixture, out var store);
        var logger = new ListLogger<VocabularyBrowser>();
        var browser = new VocabularyBrowser(store, fixture.UserScope, fixture.Preferences,
            new ScratchQuizMedia(), logger, key => key);
        Assert.True(await browser.OpenAsync(UserA));
        var wordId = fixture.WordId(UserA, 0);
        await browser.OpenWordAsync(wordId);
        browser.RequestDelete();
        gated.DeleteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var deleting = browser.ConfirmDeleteAsync();
        await gated.DeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        browser.Dispose();
        gated.DeleteGate.SetResult();
        await deleting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(logger.Messages);
        Assert.Null(browser.EditState.DeleteError);
        Assert.Equal(1, (await RowsAsync(fixture, wordId)).Words);
    }

    [Fact]
    public async Task Dispose_DuringThePostSaveRescan_EndsQuietly()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await AddCopiesAsync(fixture, UserA, "dup-a", "Korean", "Korean");
        fixture.Activate(UserA);
        var gated = GatedStore.Wrap(fixture, out var store);
        var logger = new ListLogger<VocabularyBrowser>();
        var browser = new VocabularyBrowser(store, fixture.UserScope, fixture.Preferences,
            new ScratchQuizMedia(), logger, key => key);
        Assert.True(await browser.OpenAsync(UserA));
        await browser.OpenWordAsync("dup-a-1");
        await browser.FindDuplicatesAsync();
        await browser.OpenWordAsync(fixture.WordId(UserA, 0));
        browser.ChangeDraft(nameof(NativeVocabularyEditDraft.NativeTerm), "a changed meaning");
        gated.ScanGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var saving = browser.SaveAsync();
        await gated.ScanEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        browser.Dispose();
        gated.ScanGate.SetResult();

        await saving.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(logger.Messages);
    }

    // Review 2026-11-05 (second pass): one badge rule for both heads, readable on every theme.
    [Fact]
    public void EncodingBadges_ReadAtFourPointFiveToOneOnEveryTheme()
    {
        foreach (var theme in ThemeCatalog.All)
            foreach (var dark in new[] { false, true })
            {
                var colors = NativeThemeColorTable.For(theme.Id, dark);
                foreach (var surface in new[] { colors.Body, colors.CardSurface })
                    foreach (var tone in Enum.GetValues<VocabularyEncodingTone>())
                    {
                        var (fill, text) = VocabularyEncodingBadge.Colors(tone, colors, surface);
                        var ratio = SrgbColor.Parse(text).ContrastRatio(SrgbColor.Parse(fill));
                        Assert.True(ratio >= VocabularyEncodingBadge.MinimumContrast,
                            $"{theme.Id} {(dark ? "dark" : "light")} {tone} on {surface}: {ratio:0.00}");
                    }
            }
        // The source's tones: success, warning and the theme's secondary at 25%.
        var seoul = NativeThemeColorTable.For(ThemeCatalog.DefaultThemeId, dark: false);
        Assert.Equal(NativeThemeColorTable.Blend(NativeThemeColorTable.SuccessHex, seoul.Body, 0.25),
            VocabularyEncodingBadge.Colors(VocabularyEncodingTone.Strong, seoul, seoul.Body).Fill);
        Assert.Equal(NativeThemeColorTable.Blend(NativeThemeColorTable.WarningHex, seoul.Body, 0.25),
            VocabularyEncodingBadge.Colors(VocabularyEncodingTone.Good, seoul, seoul.Body).Fill);
        // Cached by its inputs: asking again gives the same colours.
        Assert.Equal(VocabularyEncodingBadge.Colors(VocabularyEncodingTone.Good, seoul, seoul.Body),
            VocabularyEncodingBadge.Colors(VocabularyEncodingTone.Good, seoul with { }, seoul.Body));
        Assert.Equal(NativeThemeColorTable.Blend(seoul.Secondary, seoul.Body, 0.25),
            VocabularyEncodingBadge.Colors(VocabularyEncodingTone.Basic, seoul, seoul.Body).Fill);
    }

    private static VocabularyBrowser Create(NativeSliceFixture fixture) =>
        new(fixture.Provider.GetRequiredService<INativeResourceStore>(),
            fixture.UserScope, fixture.Preferences, new ScratchQuizMedia(),
            NullLogger<VocabularyBrowser>.Instance, key => key);

    private sealed record WordRows(
        int Words, int Mappings, int Progress, int Contexts, int Examples);

    private sealed record WordSnapshot(string Id, string? Owner, string? Target, string? Native,
        string? Mnemonic, string? Image, string? Audio, DateTime UpdatedAt, string Mappings);

    private static async Task<List<WordSnapshot>> SnapshotAsync(NativeSliceFixture fixture,
        params string[] ids)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var words = await db.VocabularyWords.AsNoTracking()
            .Where(word => ids.Contains(word.Id)).OrderBy(word => word.Id).ToListAsync();
        var mappings = await db.ResourceVocabularyMappings.AsNoTracking()
            .Where(mapping => ids.Contains(mapping.VocabularyWordId)).ToListAsync();
        return words.Select(word => new WordSnapshot(word.Id, word.UserProfileId,
                word.TargetLanguageTerm, word.NativeLanguageTerm, word.MnemonicText,
                word.MnemonicImageUri, word.AudioPronunciationUri, word.UpdatedAt,
                string.Join(",", mappings.Where(mapping => mapping.VocabularyWordId == word.Id)
                    .Select(mapping => mapping.Id + ">" + mapping.ResourceId).Order())))
            .ToList();
    }

    private static async Task<WordRows> RowsAsync(NativeSliceFixture fixture, string wordId)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var progressIds = await db.VocabularyProgresses.AsNoTracking()
            .Where(progress => progress.VocabularyWordId == wordId)
            .Select(progress => progress.Id).ToListAsync();
        return new WordRows(
            await db.VocabularyWords.CountAsync(word => word.Id == wordId),
            await db.ResourceVocabularyMappings.CountAsync(
                mapping => mapping.VocabularyWordId == wordId),
            progressIds.Count,
            await db.VocabularyLearningContexts.CountAsync(
                context => progressIds.Contains(context.VocabularyProgressId)),
            await db.ExampleSentences.CountAsync(example => example.VocabularyWordId == wordId));
    }

    private static async Task AddHistoryAndExampleAsync(
        NativeSliceFixture fixture, string owner, int index)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.VocabularyLearningContexts.Add(new VocabularyLearningContext {
            Id = $"context-{owner}-{index}", VocabularyProgressId = $"progress-{owner}-{index}",
            LearningResourceId = fixture.ResourceId(owner), Activity = "VocabularyQuiz",
            InputMode = "MultipleChoice", WasCorrect = true,
            LearnedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc)
        });
        db.ExampleSentences.Add(new ExampleSentence {
            Id = $"example-{owner}-{index}", VocabularyWordId = fixture.WordId(owner, index),
            UserProfileId = owner, LearningResourceId = fixture.ResourceId(owner),
            TargetSentence = $"sentence-{owner}-{index}", NativeSentence = "native sentence",
            CreatedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();
    }

    private static ExampleSentence AiSentence(string id, string wordId, string owner) => new()
    {
        Id = id, VocabularyWordId = wordId, UserProfileId = owner, LearningResourceId = null,
        TargetSentence = $"ai-{id}", NativeSentence = "native sentence",
        CreatedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc)
    };

    /// <summary>A list that is open for <see cref="UserA"/>; store work runs inline.</summary>
    private sealed class Host : IVocabularyCleanupHost
    {
        public bool TryOpenOwner(out string owner)
        {
            owner = UserA;
            return true;
        }

        public bool IsFormOpen => false;
        public bool IsDisposed => false;
        public bool IsSaving => false;
        public NativeVocabularyDetail? OpenDetail => null;
        public NativeVocabularyEditDraft? OpenDraft => null;
        public CancellationToken Lifetime => CancellationToken.None;
        public void LeaveWord() { }
        public void ShowListFeedback(string message, bool warning) { }
        public Task ReloadListAsync() => Task.CompletedTask;
        public Task RefreshOpenWordAsync() => Task.CompletedTask;
        public void PublishChanges(bool list = false, bool form = false, bool deleteDialog = false) { }
        public Task<T> OffUiAsync<T>(Func<Task<T>> work) => work();
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        internal IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return _messages.ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning)
                return;
            lock (_messages)
                _messages.Add(formatter(state, exception));
        }
    }

    /// <summary>
    /// The real store, with its duplicate scans and merges held at a gate (or failing) so a test
    /// can act while one runs.
    /// </summary>
    public class GatedStore : DispatchProxy
    {
        private INativeResourceStore _inner = null!;
        private int _scans;

        internal TaskCompletionSource? ScanGate { get; set; }
        internal TaskCompletionSource ScanEntered { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? DeleteGate { get; set; }
        internal TaskCompletionSource DeleteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? MergeGate { get; set; }
        internal TaskCompletionSource MergeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailScans { get; set; }
        internal int Scans => Volatile.Read(ref _scans);

        internal static GatedStore Wrap(NativeSliceFixture fixture, out INativeResourceStore store)
        {
            store = Create<INativeResourceStore, GatedStore>();
            var gated = (GatedStore)(object)store;
            gated._inner = fixture.Provider.GetRequiredService<INativeResourceStore>();
            return gated;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            return method.Name switch
            {
                nameof(INativeResourceStore.FindOwnedVocabularyDuplicatesAsync) => ScanAsync(method, args),
                nameof(INativeResourceStore.MergeOwnedVocabularyDuplicatesAsync) => MergeAsync(method, args),
                nameof(INativeResourceStore.DeleteOwnedVocabularyAsync) => DeleteAsync(method, args),
                _ => Forward(method, args)
            };
        }

        private async Task<NativeVocabularyDuplicateScan> ScanAsync(MethodInfo method, object?[]? args)
        {
            Interlocked.Increment(ref _scans);
            if (ScanGate is { } gate)
            {
                ScanEntered.TrySetResult();
                await gate.Task;
                ScanEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (FailScans)
                throw new InvalidOperationException("The scan broke.");
            return await (Task<NativeVocabularyDuplicateScan>)Forward(method, args)!;
        }

        private async Task<NativeVocabularyMergeResult> MergeAsync(MethodInfo method, object?[]? args)
        {
            if (MergeGate is { } gate)
            {
                MergeEntered.TrySetResult();
                await gate.Task;
            }
            return await (Task<NativeVocabularyMergeResult>)Forward(method, args)!;
        }

        private async Task<NativeVocabularyDeleteStatus> DeleteAsync(MethodInfo method, object?[]? args)
        {
            if (DeleteGate is { } gate)
            {
                DeleteEntered.TrySetResult();
                await gate.Task;
            }
            return await (Task<NativeVocabularyDeleteStatus>)Forward(method, args)!;
        }

        private object? Forward(MethodInfo method, object?[]? args)
        {
            try
            {
                return method.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    /// <summary>Two copies of 사과 / apple on the owner's resource; the second has a memory aid.</summary>
    private static async Task AddCopiesAsync(NativeSliceFixture fixture, string owner,
        string prefix, string firstLanguage, string secondLanguage)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var (index, language, story) in new[] {
            (1, firstLanguage, (string?)null), (2, secondLanguage, "Their story") })
        {
            db.VocabularyWords.Add(new VocabularyWord {
                Id = $"{prefix}-{index}", UserProfileId = owner,
                TargetLanguageTerm = index == 1 ? "사과" : " 사과 ",
                NativeLanguageTerm = "apple", Language = language,
                LexicalUnitType = LexicalUnitType.Word, MnemonicText = story,
                CreatedAt = new DateTime(2026, 11, 1, 9, index, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 11, 1, 9, index, 0, DateTimeKind.Utc)
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                Id = $"mapping-{prefix}-{index}", ResourceId = fixture.ResourceId(owner),
                VocabularyWordId = $"{prefix}-{index}"
            });
        }
        await db.SaveChangesAsync();
    }
}
