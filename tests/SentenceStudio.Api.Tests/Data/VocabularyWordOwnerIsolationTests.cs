using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Data;

/// <summary>
/// The one ownership rule (Captain 2026-11-06: "every word should have an owner and only be
/// visible and used by those owners") on the shared repository the web app runs on PostgreSQL
/// and the MAUI apps run on SQLite, with two learners: A sees, reuses, edits, merges and deletes
/// only words recorded to A; B's word that A's resource links and words recorded to no one are
/// hidden, and their rows stay exactly as they were. Words get their owner at creation, never by
/// inference after a save. With no active learner nothing is read or written (B66, B69).
/// </summary>
public abstract class VocabularyWordOwnerIsolationScenarios
{
    protected const string UserA = "owner-a";
    protected const string UserB = "owner-b";
    private const string ResourceA = "resource-a";
    private const string ResourceB = "resource-b";
    private static readonly DateTime At = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A fresh database with the schema for one test, as the registration of its
    /// <see cref="ApplicationDbContext"/>.
    /// </summary>
    protected abstract Task<Action<IServiceCollection>> CreateDatabaseAsync();

    private readonly List<ServiceProvider> _providers = [];

    private ServiceProvider Provider(Action<IServiceCollection> database, string? activeUser = null)
    {
        var services = new ServiceCollection();
        database(services);
        services.AddSingleton<IPreferencesService>(new ActiveUser(activeUser));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    protected async Task DisposeProvidersAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();
        _providers.Clear();
    }

    /// <summary>The shared repository as the web and MAUI hosts build it, for one active learner.</summary>
    private LearningResourceRepository Repository(Action<IServiceCollection> database, string? activeUser) =>
        new(Provider(database, activeUser), NullLogger<LearningResourceRepository>.Instance, fileSystem: null!);

    private ApplicationDbContext Context(Action<IServiceCollection> database) =>
        Provider(database).GetRequiredService<ApplicationDbContext>();

    protected async Task OwnDelete_RemovesOnlyTheLearnersWordAndRows()
    {
        var database = await SeedAsync();
        var before = await SnapshotOfBAsync(database);

        Assert.Equal(VocabularyWordDeleteResult.Deleted,
            await Repository(database, UserA).DeleteVocabularyWordAsync("a-own"));

        await using var db = Context(database);
        Assert.False(await db.VocabularyWords.AnyAsync(word => word.Id == "a-own"));
        Assert.False(await db.ResourceVocabularyMappings.AnyAsync(m => m.VocabularyWordId == "a-own"));
        Assert.Equal(before, await SnapshotOfBAsync(database));
    }

    protected async Task Delete_RefusesWhatTheServerWouldKeep_AndChangesNoRow()
    {
        var database = await SeedAsync();
        var before = await SnapshotOfBAsync(database);
        var repository = Repository(database, UserA);

        // A's word that B practises; A's word with A's own answer.
        Assert.Equal(VocabularyWordDeleteResult.RefusedShared,
            await repository.DeleteVocabularyWordAsync("a-used-by-b"));
        Assert.Equal(VocabularyWordDeleteResult.RefusedAnswers,
            await repository.DeleteVocabularyWordAsync("a-answered"));
        // B's words, even the one A's resource links, and words recorded to no one are not A's
        // to see (the one ownership rule, Captain 2026-11-06).
        foreach (var id in new[] { "b-linked-by-a", "b-private", "pear-legacy", "persimmon-legacy", "no-such-word" })
            Assert.Equal(VocabularyWordDeleteResult.NotFound, await repository.DeleteVocabularyWordAsync(id));

        await using var db = Context(database);
        foreach (var id in new[] { "a-used-by-b", "b-linked-by-a", "a-answered", "b-private", "pear-legacy", "persimmon-legacy" })
            Assert.True(await db.VocabularyWords.AnyAsync(word => word.Id == id), id);
        Assert.Equal(2, await db.ResourceVocabularyMappings.CountAsync(m => m.VocabularyWordId == "b-linked-by-a"));
        Assert.Equal(before, await SnapshotOfBAsync(database));
    }

    // Duplicate groups hold only the learner's own words: B's copy and words recorded to no one
    // are not shown, so they neither block a group nor show their memory aids.
    protected async Task Duplicates_ShowAndMergeOnlyTheLearnersOwnCopies()
    {
        var database = await SeedAsync();
        var before = await SnapshotOfBAsync(database);
        var repository = Repository(database, UserA);

        var scan = await repository.FindDuplicateVocabularyGroupsAsync();
        var group = Assert.Single(scan.Groups);
        Assert.Equal("포도", group.NormalizedTerm);
        Assert.True(group.CanMergeAutomatically);
        Assert.All(group.Words, info => Assert.Equal(UserA, info.Word.UserProfileId));
        Assert.DoesNotContain(scan.Groups.SelectMany(g => g.Words), info =>
            info.Word.Id is "dup-b" or "pear-legacy" or "persimmon-legacy");

        // Asked directly, a merge naming a word that is not A's is refused either way round.
        Assert.Equal(VocabularyMergeResult.None,
            await repository.MergeVocabularyWordsWithResultAsync("dup-a", ["dup-b"]));
        Assert.Equal(VocabularyMergeResult.None,
            await repository.MergeVocabularyWordsWithResultAsync("dup-b", ["dup-a"]));
        Assert.Equal(VocabularyMergeResult.None,
            await repository.MergeVocabularyWordsWithResultAsync("pear-a", ["pear-legacy"]));
        Assert.Equal(VocabularyMergeResult.None, await Repository(database, null)
            .MergeVocabularyWordsWithResultAsync("persimmon-a", ["persimmon-legacy"], default, UserA));

        await using (var db = Context(database))
        {
            var keeper = await db.VocabularyWords.AsNoTracking().SingleAsync(word => word.Id == "dup-a");
            Assert.Null(keeper.MnemonicText);
            Assert.Null(keeper.MnemonicImageUri);
            Assert.Null(keeper.AudioPronunciationUri);
            Assert.Null((await db.VocabularyWords.AsNoTracking()
                .SingleAsync(word => word.Id == "pear-legacy")).UserProfileId);
            Assert.Null((await db.VocabularyWords.AsNoTracking()
                .SingleAsync(word => word.Id == "persimmon-a")).MnemonicText);
            Assert.Equal("A's own story from before owners", (await db.VocabularyWords.AsNoTracking()
                .SingleAsync(word => word.Id == "persimmon-legacy")).MnemonicText);
        }
        Assert.Equal(before, await SnapshotOfBAsync(database));

        // The learner's own copies still merge (named learner, explicitly).
        Assert.Equal(1, (await Repository(database, null).MergeVocabularyWordsWithResultAsync(
            "grape-1", ["grape-2"], default, UserA)).Deleted);
    }

    // Review finding 9: the merge decides whose a sentence is by its recorded owner, not by the
    // resource it came from. A's own sentence with no resource moves to the keeper (before, it
    // marked the copy as holding an unowned artifact and the copy never merged); B's sentence on
    // A's resource keeps the copy and is never folded into A's keeper.
    protected async Task Merge_DecidesSentenceOwnersByTheirRecordedOwner()
    {
        var database = await SeedAsync();
        await using (var db = Context(database))
        {
            Word(db, "kiwi-1", "키위", UserA, ResourceA);
            Word(db, "kiwi-2", "키위", UserA, ResourceA);
            Word(db, "melon-1", "멜론", UserA, ResourceA);
            Word(db, "melon-2", "멜론", UserA, ResourceA);
            db.ExampleSentences.Add(new ExampleSentence {
                Id = "a-sentence-no-resource", VocabularyWordId = "kiwi-2", UserProfileId = UserA,
                TargetSentence = "키위가 맛있어요.", CreatedAt = At, UpdatedAt = At });
            db.ExampleSentences.Add(new ExampleSentence {
                Id = "b-sentence-on-a-resource", VocabularyWordId = "melon-2", UserProfileId = UserB,
                LearningResourceId = ResourceA, TargetSentence = "멜론을 샀어요.", CreatedAt = At, UpdatedAt = At });
            await db.SaveChangesAsync();
        }
        var repository = Repository(database, null);

        var kiwi = await repository.MergeVocabularyWordsWithResultAsync("kiwi-1", ["kiwi-2"], default, UserA);
        Assert.Equal(1, kiwi.Deleted);
        var melon = await repository.MergeVocabularyWordsWithResultAsync("melon-1", ["melon-2"], default, UserA);
        Assert.Equal(0, melon.Deleted);

        await using var check = Context(database);
        Assert.Equal("kiwi-1", (await check.ExampleSentences.AsNoTracking()
            .SingleAsync(sentence => sentence.Id == "a-sentence-no-resource")).VocabularyWordId);
        Assert.False(await check.VocabularyWords.AnyAsync(word => word.Id == "kiwi-2"));
        var bSentence = await check.ExampleSentences.AsNoTracking()
            .SingleAsync(sentence => sentence.Id == "b-sentence-on-a-resource");
        Assert.Equal("melon-2", bSentence.VocabularyWordId);
        Assert.Equal(UserB, bSentence.UserProfileId);
        Assert.True(await check.VocabularyWords.AnyAsync(word => word.Id == "melon-2"));
    }

    // The editor opens only a word recorded to the learner. Another learner's word, even one
    // A's resource links, and a word recorded to no one are not found, and never saved.
    protected async Task Editor_OpensOnlyTheLearnersOwnWord_AndNeverSavesAnother()
    {
        var database = await SeedAsync();
        var before = await SnapshotOfBAsync(database);
        var repository = Repository(database, null);

        foreach (var id in new[] { "dup-b", "b-private", "b-linked-by-a", "persimmon-legacy", "pear-legacy" })
            Assert.Null(await repository.GetVocabularyWordForLearnerAsync(id, UserA));
        Assert.NotNull(await repository.GetVocabularyWordForLearnerAsync("a-own", UserA));
        Assert.Null(await repository.GetVocabularyWordForLearnerAsync("a-own", null));

        foreach (var id in new[] { "dup-b", "persimmon-legacy" })
        {
            var forged = new VocabularyWord {
                Id = id, TargetLanguageTerm = "덮어쓰기", NativeLanguageTerm = "A overwrites",
                MnemonicText = "A's text on someone else's word", UserProfileId = UserA };
            Assert.Equal(VocabularyWordSaveResult.NotLearnersOwn,
                await repository.SaveLearnersVocabularyWordAsync(forged, UserA));
            Assert.Equal(-1, await repository.SaveWordAsync(forged, UserA));
            Assert.False(await repository.UpdateVocabularyWordAsync(forged, UserA));
        }
        var own = (await repository.GetVocabularyWordForLearnerAsync("a-own", UserA))!;
        own.NativeLanguageTerm = "edited by A";
        Assert.Equal(VocabularyWordSaveResult.Saved,
            await repository.SaveLearnersVocabularyWordAsync(own, UserA));

        await using (var db = Context(database))
        {
            Assert.Equal("edited by A", (await db.VocabularyWords.AsNoTracking()
                .SingleAsync(word => word.Id == "a-own")).NativeLanguageTerm);
            Assert.Equal("A's own story from before owners", (await db.VocabularyWords.AsNoTracking()
                .SingleAsync(word => word.Id == "persimmon-legacy")).MnemonicText);
        }
        Assert.Equal(before, await SnapshotOfBAsync(database));
    }

    // Review finding 8 (the page side reloads on NotFound): each outcome is counted.
    protected async Task BulkDelete_RemovesOnlyTheLearnersFreeWords()
    {
        var database = await SeedAsync();
        var before = await SnapshotOfBAsync(database);
        var result = await Repository(database, null).BulkDeleteVocabularyWordsAsync(
            ["a-own", "a-used-by-b", "b-linked-by-a", "a-answered", "persimmon-legacy"], UserA);
        Assert.Equal(new VocabularyBulkDeleteResult(Deleted: 1, KeptShared: 1, KeptAnswers: 1,
            NotFound: 2, Failed: 0), result);
        await using (var db = Context(database))
        {
            Assert.False(await db.VocabularyWords.AnyAsync(word => word.Id == "a-own"));
            foreach (var id in new[] { "a-used-by-b", "b-linked-by-a", "a-answered", "persimmon-legacy" })
            {
                Assert.True(await db.VocabularyWords.AnyAsync(word => word.Id == id), id);
                // A kept or hidden word keeps every link, the learner's included.
                Assert.True(await db.ResourceVocabularyMappings.AnyAsync(m =>
                    m.VocabularyWordId == id && m.ResourceId == ResourceA), id);
            }
        }
        Assert.Equal(before, await SnapshotOfBAsync(database));
    }

    // The Coach's remove-word acts only on a word recorded to the learner (moot review finding 7:
    // a word recorded to no one is no longer found, so it is never half-removed); a hidden word
    // keeps its rows, A's links included.
    protected async Task CoachRemoval_ActsOnlyOnTheLearnersOwnWords()
    {
        var database = await SeedAsync();
        var before = await SnapshotOfBAsync(database);

        foreach (var (wordId, found) in new[] { ("persimmon-legacy", false), ("b-linked-by-a", false), ("a-own", true) })
        {
            await using var appDb = Context(database);
            var handler = new SentenceStudio.Api.Coach.Operations.Handlers.CoachVocabularyRemovalHandler(
                new SentenceStudio.Api.Coach.Operations.CoachWriteOwnership(appDb),
                Repository(database, null));
            var args = System.Text.Json.JsonSerializer.Serialize(
                new SentenceStudio.Api.Coach.Operations.Handlers.CoachVocabularyRemovalArgs(wordId),
                SentenceStudio.Api.Coach.Persistence.CoachNormalizedJson.Options);
            if (found)
                await handler.ExecuteAsync(UserA, args, default);
            else
                AssertNotFoundOrNotOwned(await Assert.ThrowsAsync<SentenceStudio.Api.Coach.Tools.CoachToolException>(
                    () => handler.ExecuteAsync(UserA, args, default)));
        }

        await using (var db = Context(database))
        {
            Assert.False(await db.VocabularyWords.AnyAsync(word => word.Id == "a-own"));
            Assert.True(await db.ResourceVocabularyMappings.AnyAsync(m => m.VocabularyWordId == "persimmon-legacy"));
            Assert.Equal(2, await db.ResourceVocabularyMappings.CountAsync(m => m.VocabularyWordId == "b-linked-by-a"));
        }
        Assert.Equal(before, await SnapshotOfBAsync(database));
    }

    // Review of bf701c42, finding 4: when the delete keeps the learner's word (another learner
    // practises it; the learner's own Minimal Pairs answer uses it), the Coach unlinks it from
    // the learner's resources and says so: it stays in their vocabulary, and why, in the
    // learner's interface language. A free word of theirs is deleted, as the delete rule allows.
    protected async Task CoachRemoval_KeptWord_SaysItStaysInTheVocabularyAndWhy()
    {
        var database = await SeedAsync();
        var before = await SnapshotOfBAsync(database);
        await using (var db = Context(database))
            await db.UserProfiles.Where(profile => profile.Id == UserA)
                .ExecuteUpdateAsync(set => set.SetProperty(profile => profile.DisplayLanguage, "Korean"));
        var korean = System.Globalization.CultureInfo.GetCultureInfo("ko");
        string Text(string key) => SentenceStudio.LocalizationManager.Instance.GetString(key, korean);

        async Task<SentenceStudio.Api.Coach.Operations.CoachWriteExecution> RemoveAsync(string wordId)
        {
            await using var appDb = Context(database);
            var handler = new SentenceStudio.Api.Coach.Operations.Handlers.CoachVocabularyRemovalHandler(
                new SentenceStudio.Api.Coach.Operations.CoachWriteOwnership(appDb),
                Repository(database, null));
            return await handler.ExecuteAsync(UserA, System.Text.Json.JsonSerializer.Serialize(
                new SentenceStudio.Api.Coach.Operations.Handlers.CoachVocabularyRemovalArgs(wordId),
                SentenceStudio.Api.Coach.Persistence.CoachNormalizedJson.Options), default);
        }

        var shared = await RemoveAsync("a-used-by-b");
        Assert.Equal(string.Format(Text("Coach_VocabularyRemoval_Unlinked"), "셋"), shared.Summary);
        Assert.Equal([Text("VocabWordEdit_DeleteRefusedShared")], shared.Lines);
        var answered = await RemoveAsync("a-answered");
        Assert.Equal(string.Format(Text("Coach_VocabularyRemoval_Unlinked"), "넷"), answered.Summary);
        Assert.Equal([Text("NativeVocabulary_DeleteUsedByAnswers")], answered.Lines);
        // Asked again, nothing is left to unlink: it still stays, and the reply says only that.
        var again = await RemoveAsync("a-used-by-b");
        Assert.Equal(string.Format(Text("Coach_VocabularyRemoval_Kept"), "셋"), again.Summary);
        var deleted = await RemoveAsync("a-own");
        Assert.Equal("Removed “하나” from your vocabulary", deleted.Summary);

        await using (var db = Context(database))
        {
            foreach (var id in new[] { "a-used-by-b", "a-answered" })
            {
                Assert.True(await db.VocabularyWords.AnyAsync(word => word.Id == id && word.UserProfileId == UserA), id);
                Assert.False(await db.ResourceVocabularyMappings.AnyAsync(m => m.VocabularyWordId == id), id);
            }
            Assert.False(await db.VocabularyWords.AnyAsync(word => word.Id == "a-own"));
        }
        Assert.Equal(before, await SnapshotOfBAsync(database));
    }

    // Review finding 11: the refusal is the Coach's specific not-found-or-not-owned refusal,
    // raised before any preview, not any exception. B's word that A's resource links, and a
    // word recorded to no one, are refused; A's own word is edited.
    protected async Task CoachEdit_RefusesWordsNotRecordedToTheLearner()
    {
        var database = await SeedAsync();
        var before = await SnapshotOfBAsync(database);
        foreach (var (wordId, allowed) in new[] { ("dup-b", false), ("persimmon-legacy", false), ("a-own", true) })
        {
            await using var appDb = Context(database);
            var handler = new SentenceStudio.Api.Coach.Operations.Handlers.CoachVocabularyEditHandler(
                new SentenceStudio.Api.Coach.Operations.CoachWriteOwnership(appDb),
                Repository(database, null));
            var args = System.Text.Json.JsonSerializer.Serialize(
                new SentenceStudio.Api.Coach.Operations.Handlers.CoachVocabularyEditArgs(
                    wordId, null, "edited by the Coach", null, "the Coach's story"),
                SentenceStudio.Api.Coach.Persistence.CoachNormalizedJson.Options);
            if (allowed)
                await handler.ExecuteAsync(UserA, args, default);
            else
                AssertNotFoundOrNotOwned(await Assert.ThrowsAsync<SentenceStudio.Api.Coach.Tools.CoachToolException>(
                    () => handler.PrepareAsync(UserA, args, default)));
        }
        await using (var db = Context(database))
        {
            Assert.Equal("edited by the Coach", (await db.VocabularyWords.AsNoTracking()
                .SingleAsync(word => word.Id == "a-own")).NativeLanguageTerm);
            Assert.Equal("A's own story from before owners", (await db.VocabularyWords.AsNoTracking()
                .SingleAsync(word => word.Id == "persimmon-legacy")).MnemonicText);
        }
        Assert.Equal(before, await SnapshotOfBAsync(database));
    }

    private static void AssertNotFoundOrNotOwned(SentenceStudio.Api.Coach.Tools.CoachToolException refusal)
    {
        Assert.Equal(SentenceStudio.Api.Coach.Tools.CoachToolFailureKind.InvalidArgument, refusal.Kind);
        Assert.Equal("No such item for this learner.", refusal.Reason);
    }

    // Every read the web and MAUI pages use shows only words recorded to the learner: an
    // ownerless word and B's word that A's resource links are not shown; A's own word with no
    // resource is (it is still A's).
    protected async Task Reads_ShowOnlyTheLearnersOwnWords()
    {
        var database = await SeedAsync();
        await using (var db = Context(database))
        {
            db.VocabularyWords.Add(new VocabularyWord {
                Id = "a-unlinked", TargetLanguageTerm = "일곱", NativeLanguageTerm = "seven",
                Language = "Korean", UserProfileId = UserA, CreatedAt = At, UpdatedAt = At });
            await db.SaveChangesAsync();
        }
        var repository = Repository(database, UserA);
        string[] hidden = ["dup-b", "b-linked-by-a", "b-private", "pear-legacy", "persimmon-legacy"];

        var all = (await repository.GetAllVocabularyWordsWithResourcesAsync(UserA)).Select(w => w.Id).ToList();
        Assert.Contains("a-own", all);
        Assert.Contains("a-unlinked", all);
        Assert.DoesNotContain(all, hidden.Contains);
        Assert.DoesNotContain((await repository.GetAllVocabularyWordsAsync()).Select(w => w.Id), hidden.Contains);

        var resource = await repository.GetResourceAsync(ResourceA, UserA);
        Assert.NotNull(resource);
        Assert.Contains(resource.Vocabulary, word => word.Id == "a-own");
        Assert.DoesNotContain(resource.Vocabulary, word => hidden.Contains(word.Id));
        Assert.DoesNotContain((await repository.GetVocabularyWordsByResourceAsync(ResourceA, UserA))
            .Select(w => w.Id), hidden.Contains);
        Assert.Equal(new[] { "a-unlinked" }, (await repository.GetOrphanedVocabularyWordsAsync(UserA))
            .Select(w => w.Id).ToArray());
        Assert.Null(await repository.GetWordByTargetTermAsync("다섯", UserA));
        Assert.Equal("persimmon-a", (await repository.GetWordByTargetTermAsync("감", UserA))?.Id);
        Assert.Empty(await repository.GetResourcesForVocabularyWordAsync("b-linked-by-a", UserA));
        Assert.Null(await repository.GetVocabularyWordByIdAsync("dup-b", UserA));
        Assert.Null(await repository.FindDuplicateVocabularyWordAsync("다섯", "다섯 meaning"));

        // A cannot link a word that is not theirs, even by ID.
        Assert.False(await repository.AddVocabularyToResourceAsync(ResourceA, "b-private", UserA));
        Assert.False(await repository.BulkAssociateWordsWithResourceAsync(ResourceA, ["b-private"], UserA));
    }

    // Saving a resource never removes a link (review of bf701c42, finding 1): not one the page
    // left out of its list, and not one to a word the learner cannot see. Unlinking is its own
    // owner-scoped path. Rows hidden by the rule stay as they are.
    protected async Task SaveResource_KeepsLinksToHiddenWords()
    {
        var database = await SeedAsync();
        var linkedBefore = await LinkedToResourceAAsync(database);
        var repository = Repository(database, UserA);
        var resource = await repository.GetResourceAsync(ResourceA, UserA);
        Assert.NotNull(resource);
        resource.Vocabulary.RemoveAll(word => word.Id == "a-other");
        resource.Title = "A renamed";

        Assert.Equal(ResourceA, await repository.SaveResourceAsync(resource, UserA));

        Assert.Equal(linkedBefore, await LinkedToResourceAAsync(database));
        Assert.True(await repository.RemoveVocabularyFromResourceAsync(ResourceA, "a-other", UserA));
        Assert.DoesNotContain("a-other", await LinkedToResourceAAsync(database));
    }

    // Review of bf701c42, finding 1 (data loss): a caller that loads a resource without its
    // words (the Coach's rename and its undo) and saves it must not unlink them. Before, the
    // save replaced the learner's links with the empty list, and the deletes synced everywhere.
    protected async Task SaveResource_LoadedWithoutItsWords_KeepsEveryLink()
    {
        var database = await SeedAsync();
        var linkedBefore = await LinkedToResourceAAsync(database);
        Assert.Contains("a-own", linkedBefore);
        LearningResource resource;
        await using (var db = Context(database))
            resource = await db.LearningResources.AsNoTracking().SingleAsync(r => r.Id == ResourceA);
        Assert.Empty(resource.Vocabulary);
        resource.Title = "A renamed";

        Assert.Equal(ResourceA, await Repository(database, null).SaveResourceAsync(resource, UserA));

        Assert.Equal(linkedBefore, await LinkedToResourceAAsync(database));
        await using var check = Context(database);
        Assert.Equal("A renamed", (await check.LearningResources.AsNoTracking()
            .SingleAsync(r => r.Id == ResourceA)).Title);
    }

    // Finding 1 as the learner meets it: renaming a resource through Sam, then undoing the
    // rename, keeps every word of the resource linked.
    protected async Task CoachResourceEdit_AndItsUndo_KeepEveryLink()
    {
        var database = await SeedAsync();
        var linkedBefore = await LinkedToResourceAAsync(database);
        var args = System.Text.Json.JsonSerializer.Serialize(
            new SentenceStudio.Api.Coach.Operations.Handlers.CoachResourceEditArgs(ResourceA, Title: "Renamed by Sam"),
            SentenceStudio.Api.Coach.Persistence.CoachNormalizedJson.Options);
        SentenceStudio.Api.Coach.Operations.CoachWriteExecution executed;
        await using (var appDb = Context(database))
        {
            var handler = new SentenceStudio.Api.Coach.Operations.Handlers.CoachResourceEditHandler(
                Repository(database, null), new SentenceStudio.Api.Coach.Operations.CoachWriteOwnership(appDb));
            executed = await handler.ExecuteAsync(UserA, args, default);
        }
        Assert.Equal(linkedBefore, await LinkedToResourceAAsync(database));

        await using (var appDb = Context(database))
        {
            var handler = new SentenceStudio.Api.Coach.Operations.Handlers.CoachResourceEditHandler(
                Repository(database, null), new SentenceStudio.Api.Coach.Operations.CoachWriteOwnership(appDb));
            await handler.UndoAsync(UserA, args, executed.PriorStateJson!, default);
        }
        Assert.Equal(linkedBefore, await LinkedToResourceAAsync(database));
        await using var check = Context(database);
        Assert.Equal("A", (await check.LearningResources.AsNoTracking()
            .SingleAsync(r => r.Id == ResourceA)).Title);
    }

    // Review of bf701c42, finding 2: the word editor shows only the parts recorded to the
    // learner, so its save may add or remove only those links. A link to a part recorded to
    // another learner or to no one (or with no part) is not shown and stays, when the parts
    // change and when the phrase becomes a Word. Another learner's phrase is never changed,
    // and a part that is not the learner's is never linked.
    protected async Task PhraseParts_ChangeOnlyTheLinksTheLearnerSees()
    {
        var database = await SeedAsync();
        await using (var db = Context(database))
        {
            foreach (var (id, part) in new[] {
                ("pc-own", (string?)"a-other"), ("pc-b", "dup-b"), ("pc-nobody", "persimmon-legacy"),
                ("pc-none", null) })
                db.PhraseConstituents.Add(new PhraseConstituent {
                    Id = id, PhraseWordId = "a-own", ConstituentWordId = part, CreatedAt = At });
            db.PhraseConstituents.Add(new PhraseConstituent {
                Id = "pc-b-phrase", PhraseWordId = "b-private", ConstituentWordId = "b-linked-by-a", CreatedAt = At });
            await db.SaveChangesAsync();
        }
        var before = await SnapshotOfBAsync(database);
        var repository = Repository(database, null);
        string[] hidden = ["pc-b", "pc-nobody", "pc-none"];

        // The learner swaps their own part, and names words that are not theirs to link.
        Assert.True(await repository.SetPhraseConstituentsForLearnerAsync(
            "a-own", ["pear-a", "b-private", "pear-legacy"], UserA));
        var parts = await PhrasePartsAsync(database, "a-own");
        Assert.Equal(["pear-a"], parts.Where(row => !hidden.Contains(row.Id)).Select(row => row.Part).ToArray());
        Assert.All(hidden, id => Assert.Contains(parts, row => row.Id == id));

        // The phrase becomes a Word: only its visible parts go.
        Assert.True(await repository.SetPhraseConstituentsForLearnerAsync("a-own", [], UserA));
        Assert.Equal(hidden, (await PhrasePartsAsync(database, "a-own")).Select(row => row.Id)
            .Order(StringComparer.Ordinal).ToArray());

        // Another learner's phrase, or no learner: nothing changes.
        Assert.False(await repository.SetPhraseConstituentsForLearnerAsync("b-private", [], UserA));
        Assert.False(await repository.SetPhraseConstituentsForLearnerAsync("a-own", ["a-other"], null));
        Assert.Equal(["b-linked-by-a"], (await PhrasePartsAsync(database, "b-private")).Select(row => row.Part).ToArray());
        Assert.Equal(hidden, (await PhrasePartsAsync(database, "a-own")).Select(row => row.Id)
            .Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(before, await SnapshotOfBAsync(database));
    }

    private async Task<List<(string Id, string? Part)>> PhrasePartsAsync(
        Action<IServiceCollection> database, string phraseWordId)
    {
        await using var db = Context(database);
        return (await db.PhraseConstituents.AsNoTracking()
                .Where(link => link.PhraseWordId == phraseWordId)
                .Select(link => new { link.Id, link.ConstituentWordId }).ToListAsync())
            .Select(row => (row.Id, row.ConstituentWordId)).ToList();
    }

    private async Task<string[]> LinkedToResourceAAsync(Action<IServiceCollection> database)
    {
        await using var db = Context(database);
        return (await db.ResourceVocabularyMappings.AsNoTracking()
                .Where(m => m.ResourceId == ResourceA).Select(m => m.VocabularyWordId).ToListAsync())
            .Order(StringComparer.Ordinal).ToArray();
    }

    // Item 3 (Captain 2026-11-06): words get their owner when they are created. A new word with
    // no owner is refused, nothing is inferred after a save (a link or progress no longer
    // records a legacy word to the learner), and a blank owner on an update never overwrites
    // the stored one.
    protected async Task Saves_NeverGuessOwners_AndRefuseNewOwnerlessWords()
    {
        var database = await SeedAsync();
        await using (var db = Context(database))
        {
            db.VocabularyWords.Add(new VocabularyWord { Id = "no-owner", TargetLanguageTerm = "여덟" });
            var refused = await Assert.ThrowsAsync<VocabularyWordOwnerRequiredException>(() => db.SaveChangesAsync());
            Assert.Equal(new[] { "no-owner" }, refused.WordIds);
        }
        await using (var db = Context(database))
            Assert.False(await db.VocabularyWords.AnyAsync(word => word.Id == "no-owner"));
        await using (var db = Context(database))
        {
            db.VocabularyWords.Add(new VocabularyWord { Id = "blank-owner", TargetLanguageTerm = "아홉", UserProfileId = "" });
            Assert.Throws<VocabularyWordOwnerRequiredException>(() => db.SaveChanges());
        }

        await using (var db = Context(database))
        {
            // A link and progress on a word recorded to no one record nobody.
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                Id = "a-links-legacy", ResourceId = ResourceA, VocabularyWordId = "pear-legacy" });
            db.VocabularyProgresses.Add(new VocabularyProgress {
                Id = "a-practises-legacy", VocabularyWordId = "persimmon-legacy", UserId = UserA });
            await db.SaveChangesAsync();
        }
        await using (var db = Context(database))
        {
            // A blank owner on an updated object leaves the stored owner.
            db.VocabularyWords.Update(new VocabularyWord {
                Id = "a-own", TargetLanguageTerm = "하나", NativeLanguageTerm = "one, again",
                UserProfileId = "", CreatedAt = At, UpdatedAt = At });
            await db.SaveChangesAsync();
        }
        await using (var check = Context(database))
        {
            Assert.Null((await check.VocabularyWords.AsNoTracking().SingleAsync(w => w.Id == "pear-legacy")).UserProfileId);
            Assert.Null((await check.VocabularyWords.AsNoTracking().SingleAsync(w => w.Id == "persimmon-legacy")).UserProfileId);
            var own = await check.VocabularyWords.AsNoTracking().SingleAsync(w => w.Id == "a-own");
            Assert.Equal(UserA, own.UserProfileId);
            Assert.Equal("one, again", own.NativeLanguageTerm);
        }

        // The repository's create path records the creator.
        var created = new VocabularyWord { Id = "a-created", TargetLanguageTerm = "열", NativeLanguageTerm = "ten" };
        Assert.True(await Repository(database, null).SaveWordAsync(created, UserA) > 0);
        Assert.Equal(-1, await Repository(database, null).SaveWordAsync(
            new VocabularyWord { Id = "nobody-created", TargetLanguageTerm = "열하나" }, null));
        await using (var check = Context(database))
        {
            Assert.Equal(UserA, (await check.VocabularyWords.AsNoTracking().SingleAsync(w => w.Id == "a-created")).UserProfileId);
            Assert.False(await check.VocabularyWords.AnyAsync(w => w.Id == "nobody-created"));
        }
    }

    // Bulk edits change only the learner's own words, whatever IDs the page sends.
    protected async Task BulkEdits_ChangeOnlyTheLearnersOwnWords()
    {
        var database = await SeedAsync();
        var before = await SnapshotOfBAsync(database);
        var repository = Repository(database, null);
        string[] ids = ["a-own", "dup-b", "persimmon-legacy"];

        Assert.Equal(1, await repository.BulkSetLanguageAsync([.. ids], "Japanese", UserA));
        Assert.Equal(1, await repository.BulkAddTagsAsync([.. ids], ["bulk"], UserA));
        Assert.Equal(1, await repository.BulkUpdateLemmasAsync(ids.ToDictionary(id => id, _ => "lemma"), UserA));
        Assert.Equal(0, await repository.BulkSetLanguageAsync([.. ids], "Japanese", null));

        await using var db = Context(database);
        var own = await db.VocabularyWords.AsNoTracking().SingleAsync(w => w.Id == "a-own");
        Assert.Equal(("Japanese", "bulk", "lemma"), (own.Language, own.Tags, own.Lemma));
        var legacy = await db.VocabularyWords.AsNoTracking().SingleAsync(w => w.Id == "persimmon-legacy");
        Assert.Equal(("Korean", (string?)null, (string?)null), (legacy.Language, legacy.Tags, legacy.Lemma));
        Assert.Equal(before, await SnapshotOfBAsync(database));
    }

    // Review finding 4: a term swap tells a refusal (not the learner's word) from a technical
    // failure, so the Fix Swapped page never reports a database error as an ownership refusal.
    protected async Task TermSwap_TellsRefusalsFromFailures()
    {
        var database = await SeedAsync();
        void FailingDatabase(IServiceCollection services)
        {
            database(services);
            services.ConfigureDbContext<ApplicationDbContext>(options =>
                options.AddInterceptors(new FailVocabularyWordUpdates()));
        }
        var repository = Repository(database, null);
        Assert.Equal(VocabularyWordTermsUpdateResult.NotLearnersOwn,
            await repository.UpdateVocabularyWordTermsAsync("dup-b", "x", "y", UserA));
        Assert.Equal(VocabularyWordTermsUpdateResult.NotLearnersOwn,
            await repository.UpdateVocabularyWordTermsAsync("persimmon-legacy", "x", "y", UserA));
        Assert.Equal(VocabularyWordTermsUpdateResult.NotLearnersOwn,
            await repository.UpdateVocabularyWordTermsAsync("a-own", "x", "y", null));
        Assert.Equal(VocabularyWordTermsUpdateResult.Updated,
            await repository.UpdateVocabularyWordTermsAsync("a-own", "one", "하나", UserA));
        Assert.Equal(VocabularyWordTermsUpdateResult.Failed,
            await Repository(FailingDatabase, null).UpdateVocabularyWordTermsAsync("a-other", "two", "둘", UserA));
        await using var db = Context(database);
        Assert.Equal("둘", (await db.VocabularyWords.AsNoTracking().SingleAsync(w => w.Id == "a-other")).TargetLanguageTerm);
    }

    /// <summary>A database failure on any word update (the technical failure a page must report).</summary>
    private sealed class FailVocabularyWordUpdates : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            IsWordUpdate(command) ? throw new InvalidOperationException("Simulated database failure.") : ValueTask.FromResult(result);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            IsWordUpdate(command) ? throw new InvalidOperationException("Simulated database failure.") : ValueTask.FromResult(result);

        private static bool IsWordUpdate(DbCommand command) =>
            command.CommandText.Contains("UPDATE \"VocabularyWord\"", StringComparison.Ordinal);
    }

    // Quiz hints come only from the learner's own sentences on their own words.
    protected async Task QuizHints_UseOnlyTheLearnersOwnSentences()
    {
        var database = await SeedAsync();
        await using (var db = Context(database))
        {
            foreach (var (id, word, owner) in new[] {
                ("hint-a", "a-own", (string?)UserA), ("hint-b-on-a", "a-own", UserB),
                ("hint-nobody", "a-own", null), ("hint-on-b-word", "dup-b", UserA) })
                db.ExampleSentences.Add(new ExampleSentence {
                    Id = id, VocabularyWordId = word, UserProfileId = owner, LearningResourceId = ResourceA,
                    TargetSentence = $"{id} 문장.", Status = ExampleSentenceStatus.Curated,
                    CreatedAt = At, UpdatedAt = At });
            await db.SaveChangesAsync();
        }
        var scope = Provider(database, UserA).CreateScope();
        var sentences = new ExampleSentenceRepository(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), scope.ServiceProvider,
            NullLogger<ExampleSentenceRepository>.Instance);
        var hints = await sentences.GetQuizHintsForWordsAsync(UserA, ["a-own", "dup-b"], null);
        Assert.Equal(new[] { "hint-a" }, hints.Select(hint => hint.ExampleSentenceId).ToArray());
    }

    protected async Task AbsentLearner_ReadsAndWritesNothing()
    {
        var database = await SeedAsync();
        var before = await SnapshotOfBAsync(database);
        foreach (var absent in new[] { null, "" })
        {
            var repository = Repository(database, absent);
            Assert.Equal(VocabularyWordDeleteResult.NotFound,
                await repository.DeleteVocabularyWordAsync("a-own"));
            Assert.Equal(VocabularyMergeResult.None,
                await repository.MergeVocabularyWordsWithResultAsync("grape-1", ["grape-2"]));
            Assert.Empty((await repository.FindDuplicateVocabularyGroupsAsync()).Groups);
            Assert.Empty(await repository.GetAllVocabularyWordsWithResourcesAsync());
        }

        await using var db = Context(database);
        Assert.True(await db.VocabularyWords.AnyAsync(word => word.Id == "a-own"));
        Assert.True(await db.VocabularyWords.AnyAsync(word => word.Id == "grape-2"));
        Assert.Equal(before, await SnapshotOfBAsync(database));
    }
    private async Task<Action<IServiceCollection>> SeedAsync()
    {
        var database = await CreateDatabaseAsync();
        await using (var db = Context(database))
        {
            foreach (var user in new[] { UserA, UserB })
                db.UserProfiles.Add(new UserProfile {
                    Id = user, Name = user, NativeLanguage = "English", TargetLanguage = "Korean",
                    CreatedAt = At });
            db.LearningResources.Add(new LearningResource {
                Id = ResourceA, UserProfileId = UserA, Title = "A", MediaType = "Vocabulary List",
                Language = "Korean", CreatedAt = At, UpdatedAt = At });
            db.LearningResources.Add(new LearningResource {
                Id = ResourceB, UserProfileId = UserB, Title = "B", MediaType = "Vocabulary List",
                Language = "Korean", CreatedAt = At, UpdatedAt = At });
            Word(db, "a-own", "하나", UserA, ResourceA);
            Word(db, "a-other", "둘", UserA, ResourceA);
            Word(db, "a-used-by-b", "셋", UserA, ResourceA);
            db.VocabularyProgresses.Add(new VocabularyProgress {
                Id = "b-progress", VocabularyWordId = "a-used-by-b", UserId = UserB,
                MasteryScore = 0.5f, TotalAttempts = 3 });
            Word(db, "a-answered", "넷", UserA, ResourceA);
            db.MinimalPairs.Add(new MinimalPair {
                Id = "a-pair", UserId = UserA, VocabularyWordAId = "a-answered",
                VocabularyWordBId = "a-other", CreatedAt = At, UpdatedAt = At });
            db.MinimalPairSessions.Add(new MinimalPairSession {
                Id = "a-session", UserId = UserA, StartedAt = At, CreatedAt = At, UpdatedAt = At });
            db.MinimalPairAttempts.Add(new MinimalPairAttempt {
                Id = "a-answer", UserId = UserA, SessionId = "a-session", PairId = "a-pair",
                PromptWordId = "a-answered", SelectedWordId = "a-other", CreatedAt = At });
            Word(db, "b-linked-by-a", "다섯", UserB, ResourceB, ResourceA);
            Word(db, "b-private", "여섯", UserB, ResourceB);
            // B's private copy that A's resource still links (B linked it, then unlinked it).
            var bCopy = Word(db, "dup-b", "사과", UserB, ResourceA);
            bCopy.MnemonicText = "B's private memory story";
            bCopy.MnemonicImageUri = "https://images.example.invalid/b?token=private";
            bCopy.AudioPronunciationUri = "https://audio.example.invalid/b.mp3";
            Word(db, "dup-a", "사과", UserA, ResourceA);
            Word(db, "pear-a", "배", UserA, ResourceA);
            // Words saved before words had owners (recorded to nobody below). Both are no one's,
            // the one both learners link and the one only A links (one ownership rule, Captain
            // 2026-11-06), so neither is shown to anyone; their rows stay.
            Word(db, "pear-legacy", "배", UserA, ResourceA, ResourceB);
            Word(db, "persimmon-a", "감", UserA, ResourceA);
            var ownLegacy = Word(db, "persimmon-legacy", "감", UserA, ResourceA);
            ownLegacy.MnemonicText = "A's own story from before owners";
            Word(db, "grape-1", "포도", UserA, ResourceA);
            Word(db, "grape-2", "포도", UserA, ResourceA);
            await db.SaveChangesAsync();
            await db.VocabularyWords.Where(word => word.Id == "pear-legacy" || word.Id == "persimmon-legacy")
                .ExecuteUpdateAsync(set => set.SetProperty(word => word.UserProfileId, (string?)null));
        }
        return database;
    }

    private static VocabularyWord Word(ApplicationDbContext db, string id, string term,
        string owner, params string[] resources)
    {
        var word = new VocabularyWord {
            Id = id, TargetLanguageTerm = term, NativeLanguageTerm = $"{term} meaning",
            Language = "Korean", LexicalUnitType = LexicalUnitType.Word, UserProfileId = owner,
            CreatedAt = At, UpdatedAt = At };
        db.VocabularyWords.Add(word);
        foreach (var resource in resources)
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                Id = $"{resource}-{id}", ResourceId = resource, VocabularyWordId = id });
        return word;
    }

    /// <summary>Every row of B's, and every word recorded to B, as text.</summary>
    private async Task<string> SnapshotOfBAsync(Action<IServiceCollection> database)
    {
        await using var db = Context(database);
        var words = (await db.VocabularyWords.AsNoTracking()
                .Where(word => word.UserProfileId == UserB).OrderBy(word => word.Id)
                .ToListAsync())
            .Select(word => string.Join("|", word.Id, word.TargetLanguageTerm, word.NativeLanguageTerm,
                word.MnemonicText, word.MnemonicImageUri, word.AudioPronunciationUri, word.UpdatedAt));
        var mappings = await db.ResourceVocabularyMappings.AsNoTracking()
            .Where(m => m.ResourceId == ResourceB || m.VocabularyWordId.StartsWith("b-")
                || m.VocabularyWordId == "dup-b")
            .OrderBy(m => m.Id).Select(m => m.Id + ">" + m.VocabularyWordId).ToListAsync();
        var progress = await db.VocabularyProgresses.AsNoTracking()
            .Where(p => p.UserId == UserB).OrderBy(p => p.Id)
            .Select(p => p.Id + ">" + p.VocabularyWordId + ":" + p.TotalAttempts).ToListAsync();
        return string.Join("\n", words.Concat(mappings).Concat(progress));
    }

    private sealed class ActiveUser(string? id) : IPreferencesService
    {
        public T Get<T>(string key, T defaultValue) =>
            key == "active_profile_id" && id is not null ? (T)(object)id : defaultValue;
        public void Set<T>(string key, T value) => throw new NotSupportedException();
        public void Remove(string key) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
    }
}

/// <summary>The MAUI apps' path: the same repository on SQLite.</summary>
public sealed class VocabularyWordOwnerIsolationSqliteTests : VocabularyWordOwnerIsolationScenarios, IAsyncLifetime
{
    private readonly List<SqliteConnection> _connections = [];

    protected override async Task<Action<IServiceCollection>> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        _connections.Add(connection);
        void Database(IServiceCollection services) =>
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
        var services = new ServiceCollection();
        Database(services);
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
        return Database;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await DisposeProvidersAsync();
        foreach (var connection in _connections)
            await connection.DisposeAsync();
    }

    [Fact] public Task OwnDelete() => OwnDelete_RemovesOnlyTheLearnersWordAndRows();
    [Fact] public Task RefusedDeletes() => Delete_RefusesWhatTheServerWouldKeep_AndChangesNoRow();
    [Fact] public Task Duplicates() => Duplicates_ShowAndMergeOnlyTheLearnersOwnCopies();
    [Fact] public Task MergeSentenceOwners() => Merge_DecidesSentenceOwnersByTheirRecordedOwner();
    [Fact] public Task AbsentLearner() => AbsentLearner_ReadsAndWritesNothing();
    [Fact] public Task Editor() => Editor_OpensOnlyTheLearnersOwnWord_AndNeverSavesAnother();
    [Fact] public Task BulkDelete() => BulkDelete_RemovesOnlyTheLearnersFreeWords();
    [Fact] public Task CoachEdit() => CoachEdit_RefusesWordsNotRecordedToTheLearner();
    [Fact] public Task CoachRemoval() => CoachRemoval_ActsOnlyOnTheLearnersOwnWords();
    [Fact] public Task CoachRemovalKeptWord() => CoachRemoval_KeptWord_SaysItStaysInTheVocabularyAndWhy();
    [Fact] public Task Reads() => Reads_ShowOnlyTheLearnersOwnWords();
    [Fact] public Task SaveResourceKeepsHiddenLinks() => SaveResource_KeepsLinksToHiddenWords();
    [Fact] public Task SaveResourceWithoutWords() => SaveResource_LoadedWithoutItsWords_KeepsEveryLink();
    [Fact] public Task CoachResourceEditKeepsLinks() => CoachResourceEdit_AndItsUndo_KeepEveryLink();
    [Fact] public Task PhraseParts() => PhraseParts_ChangeOnlyTheLinksTheLearnerSees();
    [Fact] public Task SavesNeverGuessOwners() => Saves_NeverGuessOwners_AndRefuseNewOwnerlessWords();
    [Fact] public Task BulkEdits() => BulkEdits_ChangeOnlyTheLearnersOwnWords();
    [Fact] public Task TermSwap() => TermSwap_TellsRefusalsFromFailures();
    [Fact] public Task QuizHints() => QuizHints_UseOnlyTheLearnersOwnSentences();
}

/// <summary>The web app's path: the same repository on a throwaway PostgreSQL database.</summary>
public sealed class VocabularyWordOwnerIsolationPostgresTests : VocabularyWordOwnerIsolationScenarios, IAsyncLifetime
{
    private readonly List<ApplicationOperationPostgresHarness> _databases = [];

    protected override async Task<Action<IServiceCollection>> CreateDatabaseAsync()
    {
        var harness = await ApplicationOperationPostgresHarness.CreateAsync("vocab_owner", migrate: false);
        _databases.Add(harness);
        void Database(IServiceCollection services) =>
            services.AddDbContext<ApplicationDbContext>(options => options
                .UseNpgsql(harness.ConnectionString)
                .ConfigureWarnings(ApplicationOperationPostgresHarness.IgnoreKnownLegacyTimestampDrift));
        var services = new ServiceCollection();
        Database(services);
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
        return Database;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await DisposeProvidersAsync();
        foreach (var database in _databases)
            await database.DisposeAsync();
    }

    [ApplicationOperationPostgresFact] public Task OwnDelete() => OwnDelete_RemovesOnlyTheLearnersWordAndRows();
    [ApplicationOperationPostgresFact] public Task RefusedDeletes() => Delete_RefusesWhatTheServerWouldKeep_AndChangesNoRow();
    [ApplicationOperationPostgresFact] public Task Duplicates() => Duplicates_ShowAndMergeOnlyTheLearnersOwnCopies();
    [ApplicationOperationPostgresFact] public Task MergeSentenceOwners() => Merge_DecidesSentenceOwnersByTheirRecordedOwner();
    [ApplicationOperationPostgresFact] public Task AbsentLearner() => AbsentLearner_ReadsAndWritesNothing();
    [ApplicationOperationPostgresFact] public Task Editor() => Editor_OpensOnlyTheLearnersOwnWord_AndNeverSavesAnother();
    [ApplicationOperationPostgresFact] public Task BulkDelete() => BulkDelete_RemovesOnlyTheLearnersFreeWords();
    [ApplicationOperationPostgresFact] public Task CoachEdit() => CoachEdit_RefusesWordsNotRecordedToTheLearner();
    [ApplicationOperationPostgresFact] public Task CoachRemoval() => CoachRemoval_ActsOnlyOnTheLearnersOwnWords();
    [ApplicationOperationPostgresFact] public Task CoachRemovalKeptWord() => CoachRemoval_KeptWord_SaysItStaysInTheVocabularyAndWhy();
    [ApplicationOperationPostgresFact] public Task Reads() => Reads_ShowOnlyTheLearnersOwnWords();
    [ApplicationOperationPostgresFact] public Task SaveResourceKeepsHiddenLinks() => SaveResource_KeepsLinksToHiddenWords();
    [ApplicationOperationPostgresFact] public Task SaveResourceWithoutWords() => SaveResource_LoadedWithoutItsWords_KeepsEveryLink();
    [ApplicationOperationPostgresFact] public Task CoachResourceEditKeepsLinks() => CoachResourceEdit_AndItsUndo_KeepEveryLink();
    [ApplicationOperationPostgresFact] public Task PhraseParts() => PhraseParts_ChangeOnlyTheLinksTheLearnerSees();
    [ApplicationOperationPostgresFact] public Task SavesNeverGuessOwners() => Saves_NeverGuessOwners_AndRefuseNewOwnerlessWords();
    [ApplicationOperationPostgresFact] public Task BulkEdits() => BulkEdits_ChangeOnlyTheLearnersOwnWords();
    [ApplicationOperationPostgresFact] public Task TermSwap() => TermSwap_TellsRefusalsFromFailures();
    [ApplicationOperationPostgresFact] public Task QuizHints() => QuizHints_UseOnlyTheLearnersOwnSentences();

    // Review 4: the delete's checks and its write share one serializable transaction. Another
    // learner's progress row inserted (and committed) by a second connection after the delete
    // read "no other learner uses it" fails the delete instead of being removed with the word.
    [ApplicationOperationPostgresFact]
    public async Task Delete_RacingAnotherLearnersRow_NeverRemovesIt()
    {
        var harness = await ApplicationOperationPostgresHarness.CreateAsync("vocab_race", migrate: false);
        _databases.Add(harness);
        DbContextOptions<ApplicationDbContext> Options(params IInterceptor[] interceptors) =>
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(harness.ConnectionString)
                .ConfigureWarnings(ApplicationOperationPostgresHarness.IgnoreKnownLegacyTimestampDrift)
                .AddInterceptors(interceptors)
                .Options;
        await using (var db = new ApplicationDbContext(Options()))
        {
            await db.Database.EnsureCreatedAsync();
            foreach (var user in new[] { UserA, UserB })
                db.UserProfiles.Add(new UserProfile {
                    Id = user, Name = user, NativeLanguage = "English", TargetLanguage = "Korean" });
            db.LearningResources.Add(new LearningResource {
                Id = "race-resource", UserProfileId = UserA, Title = "A", MediaType = "Vocabulary List" });
            db.VocabularyWords.Add(new VocabularyWord {
                Id = "race-word", TargetLanguageTerm = "경주", NativeLanguageTerm = "race",
                UserProfileId = UserA });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping {
                Id = "race-mapping", ResourceId = "race-resource", VocabularyWordId = "race-word" });
            await db.SaveChangesAsync();
        }

        // Right before the delete is written, another learner's progress for the word commits
        // on a second connection: after the checks that saw no other learner's use.
        var race = new InsertBeforeWordDelete(async () =>
        {
            await using var other = new ApplicationDbContext(Options());
            other.VocabularyProgresses.Add(new VocabularyProgress {
                Id = "raced-in", VocabularyWordId = "race-word", UserId = UserB });
            await other.SaveChangesAsync();
        });
        var services = new ServiceCollection();
        services.AddScoped(_ => new ApplicationDbContext(Options(race)));
        await using var provider = services.BuildServiceProvider();
        var repository = new LearningResourceRepository(provider,
            NullLogger<LearningResourceRepository>.Instance, fileSystem: null!);

        var result = await repository.DeleteVocabularyWordAsync("race-word", UserA);

        Assert.True(race.Ran);
        Assert.NotEqual(VocabularyWordDeleteResult.Deleted, result);
        await using var after = new ApplicationDbContext(Options());
        Assert.True(await after.VocabularyWords.AnyAsync(word => word.Id == "race-word"));
        Assert.True(await after.VocabularyProgresses.AnyAsync(progress => progress.Id == "raced-in"));
    }

    private sealed class InsertBeforeWordDelete(Func<Task> insert) : DbCommandInterceptor
    {
        internal bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await RaceAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await RaceAsync(command);
            return result;
        }

        private async Task RaceAsync(DbCommand command)
        {
            if (Ran || !command.CommandText.Contains("DELETE FROM \"VocabularyWord\"", StringComparison.Ordinal))
                return;
            Ran = true;
            await insert();
        }
    }
}
