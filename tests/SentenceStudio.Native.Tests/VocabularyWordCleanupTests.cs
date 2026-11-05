using CoreSync;
using CoreSync.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Features.Vocabulary;
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

        Assert.Equal(NativeVocabularyDeleteStatus.SharedReadOnly,
            await store.DeleteOwnedVocabularyAsync(UserA, shared, default));
        var rows = await RowsAsync(fixture, shared);
        Assert.Equal(1, rows.Words);
        Assert.Equal(2, rows.Mappings);

        using var browser = Create(fixture);
        Assert.True(await browser.OpenAsync(UserA));
        await browser.OpenWordAsync(shared);
        Assert.True(browser.EditState.Detail!.IsSharedWithAnotherOwner);
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
        Assert.Equal(0, await store.MergeOwnedVocabularyDuplicatesAsync(
            UserA, "dup-a-2", ["dup-b-1"], default));
        Assert.Equal(0, await store.MergeOwnedVocabularyDuplicatesAsync(
            UserB, "dup-b-2", ["dup-b-1"], default));
        Assert.Equal(1, (await RowsAsync(fixture, "dup-b-1")).Words);

        fixture.LoseOwner();
        Assert.False((await store.FindOwnedVocabularyDuplicatesAsync(
            UserA, null, 0, 20, default)).OwnerValid);
        Assert.Equal(0, await store.MergeOwnedVocabularyDuplicatesAsync(
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

    private static VocabularyBrowser Create(NativeSliceFixture fixture) =>
        new(fixture.Provider.GetRequiredService<INativeResourceStore>(),
            fixture.UserScope, fixture.Preferences, new ScratchQuizMedia(),
            NullLogger<VocabularyBrowser>.Instance, key => key);

    private sealed record WordRows(
        int Words, int Mappings, int Progress, int Contexts, int Examples);

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
                Id = $"{prefix}-{index}", TargetLanguageTerm = index == 1 ? "사과" : " 사과 ",
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
