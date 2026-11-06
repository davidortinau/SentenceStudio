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
/// B66 and B69 (2026-11-05): the shared word delete and duplicate merge, which the web app
/// runs on PostgreSQL and the MAUI apps run on SQLite, with two learners. Learner A cannot
/// delete or merge-copy learner B's data, B's rows stay exactly as they were, and with no
/// active learner nothing is read or written.
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

        // A's word that B practises; B's word that A linked; A's word with A's own answer.
        Assert.Equal(VocabularyWordDeleteResult.RefusedShared,
            await repository.DeleteVocabularyWordAsync("a-used-by-b"));
        Assert.Equal(VocabularyWordDeleteResult.RefusedShared,
            await repository.DeleteVocabularyWordAsync("b-linked-by-a"));
        Assert.Equal(VocabularyWordDeleteResult.RefusedAnswers,
            await repository.DeleteVocabularyWordAsync("a-answered"));
        // B's word A has never linked: not A's to see.
        Assert.Equal(VocabularyWordDeleteResult.NotFound,
            await repository.DeleteVocabularyWordAsync("b-private"));
        Assert.Equal(VocabularyWordDeleteResult.NotFound,
            await repository.DeleteVocabularyWordAsync("no-such-word"));

        await using var db = Context(database);
        foreach (var id in new[] { "a-used-by-b", "b-linked-by-a", "a-answered", "b-private" })
            Assert.True(await db.VocabularyWords.AnyAsync(word => word.Id == id), id);
        Assert.Equal(2, await db.ResourceVocabularyMappings.CountAsync(m => m.VocabularyWordId == "b-linked-by-a"));
        Assert.Equal(before, await SnapshotOfBAsync(database));
    }

    protected async Task Merge_OfACopyNotRecordedToTheLearner_IsBlockedAndCopiesNothing()
    {
        var database = await SeedAsync();
        var before = await SnapshotOfBAsync(database);
        var repository = Repository(database, UserA);

        var scan = await repository.FindDuplicateVocabularyGroupsAsync();
        var theirs = Assert.Single(scan.Groups, group => group.NormalizedTerm == "사과");
        Assert.True(theirs.HasCopyNotOwnedByLearner);
        Assert.False(theirs.CanMergeAutomatically);
        // B's memory aids and audio are not shown to A.
        var bCopy = Assert.Single(theirs.Words, info => info.Word.Id == "dup-b");
        Assert.Null(bCopy.Word.MnemonicText);
        Assert.Null(bCopy.Word.MnemonicImageUri);
        Assert.Null(bCopy.Word.AudioPronunciationUri);
        Assert.False(bCopy.IsLearnersOwn);
        var legacy = Assert.Single(scan.Groups, group => group.NormalizedTerm == "배");
        Assert.True(legacy.HasCopyNotOwnedByLearner);
        Assert.False(legacy.CanMergeAutomatically);
        var own = Assert.Single(scan.Groups, group => group.NormalizedTerm == "포도");
        Assert.False(own.HasCopyNotOwnedByLearner);
        Assert.True(own.CanMergeAutomatically);
        // Review 4 (one ownership rule): A's own legacy word keeps its memory aid and merges.
        var ownLegacy = Assert.Single(scan.Groups, group => group.NormalizedTerm == "감");
        Assert.False(ownLegacy.HasCopyNotOwnedByLearner);
        Assert.True(ownLegacy.CanMergeAutomatically);
        Assert.Equal("A's own story from before owners", Assert.Single(ownLegacy.Words,
            info => info.Word.Id == "persimmon-legacy").Word.MnemonicText);

        // Asked directly, either keeper is refused.
        Assert.Equal(VocabularyMergeResult.None,
            await repository.MergeVocabularyWordsWithResultAsync("dup-a", ["dup-b"]));
        Assert.Equal(VocabularyMergeResult.None,
            await repository.MergeVocabularyWordsWithResultAsync("dup-b", ["dup-a"]));
        Assert.Equal(VocabularyMergeResult.None,
            await repository.MergeVocabularyWordsWithResultAsync("pear-a", ["pear-legacy"]));

        await using (var db = Context(database))
        {
            var keeper = await db.VocabularyWords.AsNoTracking().SingleAsync(word => word.Id == "dup-a");
            Assert.Null(keeper.MnemonicText);
            Assert.Null(keeper.MnemonicImageUri);
            Assert.Null(keeper.AudioPronunciationUri);
            Assert.True(await db.VocabularyWords.AnyAsync(word => word.Id == "pear-legacy"));
            Assert.Null((await db.VocabularyWords.AsNoTracking()
                .SingleAsync(word => word.Id == "pear-legacy")).UserProfileId);
        }
        Assert.Equal(before, await SnapshotOfBAsync(database));

        // The learner's own copies still merge, an own legacy copy too (named learner, explicitly).
        Assert.Equal(1, (await repository.MergeVocabularyWordsWithResultAsync("grape-1", ["grape-2"])).Deleted);
        Assert.Equal(1, (await Repository(database, null).MergeVocabularyWordsWithResultAsync(
            "persimmon-a", ["persimmon-legacy"], default, UserA)).Deleted);
        await using (var db = Context(database))
            Assert.Equal("A's own story from before owners", (await db.VocabularyWords.AsNoTracking()
                .SingleAsync(word => word.Id == "persimmon-a")).MnemonicText);
    }

    protected async Task Editor_OpensAnotherLearnersWordReadOnly_AndNeverSavesIt()
    {
        var database = await SeedAsync();
        var before = await SnapshotOfBAsync(database);
        var repository = Repository(database, null);

        var theirs = await repository.GetVocabularyWordForLearnerAsync("dup-b", UserA);
        Assert.NotNull(theirs);
        Assert.False(theirs.IsLearnersOwn);
        Assert.Null(theirs.Word.MnemonicText);
        Assert.Null(theirs.Word.MnemonicImageUri);
        Assert.Null(theirs.Word.AudioPronunciationUri);
        Assert.Null(await repository.GetVocabularyWordForLearnerAsync("b-private", UserA));
        Assert.True((await repository.GetVocabularyWordForLearnerAsync("a-own", UserA))!.IsLearnersOwn);
        Assert.True((await repository.GetVocabularyWordForLearnerAsync("persimmon-legacy", UserA))!.IsLearnersOwn);
        Assert.Null(await repository.GetVocabularyWordForLearnerAsync("a-own", null));

        theirs.Word.NativeLanguageTerm = "A overwrites";
        theirs.Word.MnemonicText = "A's text on B's word";
        Assert.Equal(VocabularyWordSaveResult.NotLearnersOwn,
            await repository.SaveLearnersVocabularyWordAsync(theirs.Word, UserA));
        var own = (await repository.GetVocabularyWordForLearnerAsync("a-own", UserA))!.Word;
        own.NativeLanguageTerm = "edited by A";
        Assert.Equal(VocabularyWordSaveResult.Saved,
            await repository.SaveLearnersVocabularyWordAsync(own, UserA));

        await using (var db = Context(database))
            Assert.Equal("edited by A", (await db.VocabularyWords.AsNoTracking()
                .SingleAsync(word => word.Id == "a-own")).NativeLanguageTerm);
        Assert.Equal(before, await SnapshotOfBAsync(database));
    }

    protected async Task BulkDelete_RemovesOnlyTheLearnersFreeWords()
    {
        var database = await SeedAsync();
        var before = await SnapshotOfBAsync(database);
        var result = await Repository(database, null).BulkDeleteVocabularyWordsAsync(
            ["a-own", "a-used-by-b", "b-linked-by-a", "a-answered"], UserA);
        Assert.Equal(new VocabularyBulkDeleteResult(1, 3, 0), result);
        await using (var db = Context(database))
        {
            Assert.False(await db.VocabularyWords.AnyAsync(word => word.Id == "a-own"));
            foreach (var id in new[] { "a-used-by-b", "b-linked-by-a", "a-answered" })
                Assert.True(await db.VocabularyWords.AnyAsync(word => word.Id == id), id);
        }
        Assert.Equal(before, await SnapshotOfBAsync(database));
    }

    // Review 4: the Coach's remove-word decides on the word before unlinking it. A legacy word
    // only A links is A's through that very link, so it is deleted, not left with no link and
    // no owner; a word B also links is only unlinked from A's resources.
    protected async Task CoachRemoval_DeletesAnOwnLegacyWord_AndOnlyUnlinksAShared()
    {
        var database = await SeedAsync();
        var before = await SnapshotOfBAsync(database);
        await using (var db = Context(database))
        {
            var orphanCandidate = Word(db, "legacy-solo", "일곱", UserA, ResourceA);
            orphanCandidate.UserProfileId = UserA;
            await db.SaveChangesAsync();
            await db.VocabularyWords.Where(word => word.Id == "legacy-solo")
                .ExecuteUpdateAsync(set => set.SetProperty(word => word.UserProfileId, (string?)null));
        }

        foreach (var wordId in new[] { "legacy-solo", "b-linked-by-a" })
        {
            await using var appDb = Context(database);
            var handler = new SentenceStudio.Api.Coach.Operations.Handlers.CoachVocabularyRemovalHandler(
                new SentenceStudio.Api.Coach.Operations.CoachWriteOwnership(appDb),
                Repository(database, null));
            await handler.ExecuteAsync(UserA, System.Text.Json.JsonSerializer.Serialize(
                    new SentenceStudio.Api.Coach.Operations.Handlers.CoachVocabularyRemovalArgs(wordId),
                    SentenceStudio.Api.Coach.Persistence.CoachNormalizedJson.Options),
                default);
        }

        await using (var db = Context(database))
        {
            Assert.False(await db.VocabularyWords.AnyAsync(word => word.Id == "legacy-solo"));
            Assert.True(await db.VocabularyWords.AnyAsync(word => word.Id == "b-linked-by-a"));
            Assert.Equal([$"{ResourceB}-b-linked-by-a"], await db.ResourceVocabularyMappings
                .Where(m => m.VocabularyWordId == "b-linked-by-a").Select(m => m.Id).ToListAsync());
        }
        // A's link to B's word went; nothing else of B's changed.
        Assert.Equal(before.Replace($"\n{ResourceA}-b-linked-by-a>b-linked-by-a", string.Empty),
            await SnapshotOfBAsync(database));
    }

    protected async Task Delete_OfAnOwnLegacyWord_UsesTheSameOwnershipRuleAsMerge()
    {
        var database = await SeedAsync();
        var repository = Repository(database, null);
        // A's own legacy word (only A links it) goes; the one both learners link stays.
        Assert.Equal(VocabularyWordDeleteResult.Deleted,
            await repository.DeleteVocabularyWordAsync("persimmon-legacy", UserA));
        Assert.Equal(VocabularyWordDeleteResult.RefusedShared,
            await repository.DeleteVocabularyWordAsync("pear-legacy", UserA));
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
            // Words saved before words had owners (recorded to nobody below). One both learners
            // link is no one's; one only A links is A's (the one ownership rule).
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
    [Fact] public Task BlockedMerge() => Merge_OfACopyNotRecordedToTheLearner_IsBlockedAndCopiesNothing();
    [Fact] public Task AbsentLearner() => AbsentLearner_ReadsAndWritesNothing();
    [Fact] public Task Editor() => Editor_OpensAnotherLearnersWordReadOnly_AndNeverSavesIt();
    [Fact] public Task BulkDelete() => BulkDelete_RemovesOnlyTheLearnersFreeWords();
    [Fact] public Task OwnLegacyDelete() => Delete_OfAnOwnLegacyWord_UsesTheSameOwnershipRuleAsMerge();
    [Fact] public Task CoachRemoval() => CoachRemoval_DeletesAnOwnLegacyWord_AndOnlyUnlinksAShared();
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
    [ApplicationOperationPostgresFact] public Task BlockedMerge() => Merge_OfACopyNotRecordedToTheLearner_IsBlockedAndCopiesNothing();
    [ApplicationOperationPostgresFact] public Task AbsentLearner() => AbsentLearner_ReadsAndWritesNothing();
    [ApplicationOperationPostgresFact] public Task Editor() => Editor_OpensAnotherLearnersWordReadOnly_AndNeverSavesIt();
    [ApplicationOperationPostgresFact] public Task BulkDelete() => BulkDelete_RemovesOnlyTheLearnersFreeWords();
    [ApplicationOperationPostgresFact] public Task OwnLegacyDelete() => Delete_OfAnOwnLegacyWord_UsesTheSameOwnershipRuleAsMerge();
    [ApplicationOperationPostgresFact] public Task CoachRemoval() => CoachRemoval_DeletesAnOwnLegacyWord_AndOnlyUnlinksAShared();

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
