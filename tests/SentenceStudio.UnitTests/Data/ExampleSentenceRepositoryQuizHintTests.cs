using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

public sealed class ExampleSentenceRepositoryQuizHintTests : IDisposable
{
    private const string UserA = "quiz-hint-user-a";
    private const string UserB = "quiz-hint-user-b";
    private const string ResourceA = "quiz-hint-resource-a";
    private const string ResourceB = "quiz-hint-resource-b";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly ServiceProvider _provider;
    private readonly SelectCommandCounter _queryCounter;
    private readonly CollectingLoggerProvider _logs;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ExampleSentenceRepository _repo;

    public ExampleSentenceRepositoryQuizHintTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _queryCounter = new SelectCommandCounter();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_queryCounter)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        _logs = new CollectingLoggerProvider();
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(_logs);
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        _provider = services.BuildServiceProvider();
        _repo = new ExampleSentenceRepository(
            _db,
            _provider,
            _loggerFactory.CreateLogger<ExampleSentenceRepository>());

        _db.UserProfiles.AddRange(
            new UserProfile { Id = UserA },
            new UserProfile { Id = UserB });
        _db.LearningResources.AddRange(
            new LearningResource { Id = ResourceA, Title = "A", UserProfileId = UserA },
            new LearningResource { Id = ResourceB, Title = "B", UserProfileId = UserB });
        _db.SaveChanges();
        _queryCounter.Reset();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetQuizHintsForWordsAsync_EmptyUser_FailsClosedWithWarning(string userId)
    {
        var results = await _repo.GetQuizHintsForWordsAsync(userId, new[] { "word-a" }, "B1");

        results.Should().BeEmpty();
        _logs.HasWarningContaining("no userId", "cross-tenant").Should().BeTrue();
        _queryCounter.SelectCount.Should().Be(0);
    }

    [Fact]
    public async Task GetQuizHintsForWordsAsync_UnknownUser_FailsClosedWithWarning()
    {
        SeedOwnedWord("word-a", ResourceA);
        SeedExample("word-a", ResourceA, "소유한 문장");
        SaveAndResetQueryCounter();

        var results = await _repo.GetQuizHintsForWordsAsync(
            "missing-user",
            new[] { "word-a" },
            "B1");

        results.Should().BeEmpty();
        _logs.HasWarningContaining("unknown userId", "cross-tenant").Should().BeTrue();
        _queryCounter.SelectCount.Should().Be(1);
    }

    [Fact]
    public async Task GetQuizHintsForWordsAsync_MixedBatch_ReturnsOnlyExplicitUsersOwnedExamples()
    {
        SeedOwnedWord("owned-word", ResourceA);
        SeedOwnedWord("foreign-word", ResourceB);
        SeedExample("owned-word", ResourceA, "내 문장");
        SeedExample("foreign-word", ResourceB, "다른 사용자의 문장");
        SaveAndResetQueryCounter();

        var results = await _repo.GetQuizHintsForWordsAsync(
            UserA,
            new[] { "owned-word", "foreign-word", "missing-word" },
            "B1");

        results.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new VocabQuizSentenceHint(1, "owned-word", "내 문장"));
    }

    [Fact]
    public async Task GetQuizHintsForWordsAsync_AmbiguousOrNullOwnership_CannotBridgeTenants()
    {
        SeedOwnedWord("shared-word", ResourceA, ResourceB);
        SeedOwnedWord("foreign-only-word", ResourceB);
        SeedExample("shared-word", null, "소유자가 불명확한 문장");
        SeedExample("shared-word", ResourceB, "다른 사용자의 연결 문장");
        SeedExample("foreign-only-word", ResourceA, "잘못 연결된 문장");
        SaveAndResetQueryCounter();

        var results = await _repo.GetQuizHintsForWordsAsync(
            UserA,
            new[] { "shared-word", "foreign-only-word" },
            "B1");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetQuizHintsForWordsAsync_FiltersIneligibleSentences()
    {
        SeedOwnedWord("word-a", ResourceA);
        SeedExample("word-a", ResourceA, "curated", status: ExampleSentenceStatus.Curated);
        SeedExample("word-a", ResourceA, "verified", status: ExampleSentenceStatus.Verified);
        SeedExample("word-a", ResourceA, "suggested", status: ExampleSentenceStatus.Suggested);
        SeedExample("word-a", ResourceA, "flagged", flagged: true);
        SeedExample("word-a", ResourceA, "   ");
        SaveAndResetQueryCounter();

        var results = await _repo.GetQuizHintsForWordsAsync(UserA, new[] { "word-a" }, null);

        results.Select(hint => hint.TargetSentence).Should().Equal("verified", "curated");
    }

    [Theory]
    [InlineData("A1", 1)]
    [InlineData("a2", 2)]
    [InlineData(" B1 ", 3)]
    [InlineData("B2", 4)]
    [InlineData("C1", 5)]
    [InlineData("C2", 5)]
    public async Task GetQuizHintsForWordsAsync_MapsRecognizedCefrLevels(
        string targetCefrLevel,
        int expectedDifficulty)
    {
        SeedOwnedWord("word-a", ResourceA);
        for (var difficulty = 1; difficulty <= 5; difficulty++)
        {
            SeedExample(
                "word-a",
                ResourceA,
                $"difficulty-{difficulty}",
                difficulty: difficulty);
        }
        SaveAndResetQueryCounter();

        var results = await _repo.GetQuizHintsForWordsAsync(
            UserA,
            new[] { "word-a" },
            targetCefrLevel);

        results.First().TargetSentence.Should().Be($"difficulty-{expectedDifficulty}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("B3")]
    [InlineData("advanced")]
    public async Task GetQuizHintsForWordsAsync_UnrecognizedCefr_UsesLevelNeutralFallback(
        string? targetCefrLevel)
    {
        SeedOwnedWord("word-a", ResourceA);
        SeedExample("word-a", ResourceA, "difficulty-match", difficulty: 5);
        SeedExample("word-a", ResourceA, "core-null", difficulty: null, core: true);
        SaveAndResetQueryCounter();

        var results = await _repo.GetQuizHintsForWordsAsync(
            UserA,
            new[] { "word-a" },
            targetCefrLevel);

        results.First().TargetSentence.Should().Be("core-null");
    }

    [Fact]
    public async Task GetQuizHintsForWordsAsync_RanksByDistanceBeforeCore()
    {
        SeedOwnedWord("word-a", ResourceA);
        SeedExample("word-a", ResourceA, "far-core", difficulty: 5, core: true);
        SeedExample("word-a", ResourceA, "near-noncore", difficulty: 3);
        SaveAndResetQueryCounter();

        var results = await _repo.GetQuizHintsForWordsAsync(UserA, new[] { "word-a" }, "B1");

        results.Select(hint => hint.TargetSentence).Should().StartWith("near-noncore");
    }

    [Fact]
    public async Task GetQuizHintsForWordsAsync_RanksCoreThenVerifiedForEqualDistance()
    {
        SeedOwnedWord("word-a", ResourceA);
        SeedExample(
            "word-a",
            ResourceA,
            "curated-noncore",
            difficulty: 3,
            status: ExampleSentenceStatus.Curated);
        SeedExample(
            "word-a",
            ResourceA,
            "verified-noncore",
            difficulty: 3,
            status: ExampleSentenceStatus.Verified);
        SeedExample(
            "word-a",
            ResourceA,
            "curated-core",
            difficulty: 3,
            core: true,
            status: ExampleSentenceStatus.Curated);
        SaveAndResetQueryCounter();

        var results = await _repo.GetQuizHintsForWordsAsync(UserA, new[] { "word-a" }, "B1");

        results.Select(hint => hint.TargetSentence)
            .Should().Equal("curated-core", "verified-noncore", "curated-noncore");
    }

    [Fact]
    public async Task GetQuizHintsForWordsAsync_RanksCreatedAtThenIdDeterministically()
    {
        SeedOwnedWord("word-a", ResourceA);
        var later = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
        var earlier = later.AddMinutes(-1);
        SeedExample("word-a", ResourceA, "later", id: 30, difficulty: 3, createdAt: later);
        SeedExample("word-a", ResourceA, "earlier-high-id", id: 20, difficulty: 3, createdAt: earlier);
        SeedExample("word-a", ResourceA, "earlier-low-id", id: 10, difficulty: 3, createdAt: earlier);
        SaveAndResetQueryCounter();

        var results = await _repo.GetQuizHintsForWordsAsync(UserA, new[] { "word-a" }, "B1");

        results.Select(hint => hint.TargetSentence)
            .Should().Equal("earlier-low-id", "earlier-high-id", "later");
    }

    [Fact]
    public async Task GetQuizHintsForWordsAsync_NullDifficultyFollowsKnownWhenTargetBandExists()
    {
        SeedOwnedWord("word-a", ResourceA);
        SeedExample("word-a", ResourceA, "null-core", difficulty: null, core: true);
        SeedExample("word-a", ResourceA, "known-noncore", difficulty: 5);
        SaveAndResetQueryCounter();

        var results = await _repo.GetQuizHintsForWordsAsync(UserA, new[] { "word-a" }, "A1");

        results.Select(hint => hint.TargetSentence).Should().StartWith("known-noncore");
    }

    [Fact]
    public async Task GetQuizHintsForWordsAsync_ReturnsAtMostThreePerWord()
    {
        SeedOwnedWord("word-a", ResourceA);
        for (var index = 1; index <= 5; index++)
        {
            SeedExample("word-a", ResourceA, $"sentence-{index}", difficulty: 3);
        }
        SaveAndResetQueryCounter();

        var results = await _repo.GetQuizHintsForWordsAsync(UserA, new[] { "word-a" }, "B1");

        results.Should().HaveCount(3);
        results.Select(hint => hint.TargetSentence)
            .Should().Equal("sentence-1", "sentence-2", "sentence-3");
    }

    [Fact]
    public async Task GetQuizHintsForWordsAsync_HonorsRequestedMaximumBelowThree()
    {
        SeedOwnedWord("word-a", ResourceA);
        SeedExample("word-a", ResourceA, "sentence-1", difficulty: 3);
        SeedExample("word-a", ResourceA, "sentence-2", difficulty: 3);
        SeedExample("word-a", ResourceA, "sentence-3", difficulty: 3);
        SaveAndResetQueryCounter();

        var results = await _repo.GetQuizHintsForWordsAsync(
            UserA,
            new[] { "word-a" },
            "B1",
            maxHintsPerWord: 2);

        results.Select(hint => hint.TargetSentence)
            .Should().Equal("sentence-1", "sentence-2");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task GetQuizHintsForWordsAsync_RejectsMaximumOutsideSupportedRange(
        int maxHintsPerWord)
    {
        var results = await _repo.GetQuizHintsForWordsAsync(
            UserA,
            new[] { "word-a" },
            "B1",
            maxHintsPerWord);

        results.Should().BeEmpty();
        _logs.HasWarningContaining("supported range", "1-3").Should().BeTrue();
        _queryCounter.SelectCount.Should().Be(0);
    }

    [Fact]
    public async Task GetQuizHintsForWordsAsync_DeduplicatesInputAndAllowsTwentyDistinctIds()
    {
        SeedOwnedWord("word-a", ResourceA);
        SeedExample("word-a", ResourceA, "single sentence");
        var ids = Enumerable.Range(1, 19)
            .Select(index => $"missing-{index}")
            .Prepend("word-a")
            .Append("word-a")
            .ToList();
        SaveAndResetQueryCounter();

        var results = await _repo.GetQuizHintsForWordsAsync(UserA, ids, "B1");

        results.Should().ContainSingle();
        _queryCounter.SelectCount.Should().Be(1);
    }

    [Fact]
    public async Task GetQuizHintsForWordsAsync_RejectsMoreThanTwentyDistinctIds()
    {
        var ids = Enumerable.Range(1, 21).Select(index => $"word-{index}").ToList();

        var results = await _repo.GetQuizHintsForWordsAsync(UserA, ids, "B1");

        results.Should().BeEmpty();
        _logs.HasWarningContaining("maximum", "20").Should().BeTrue();
        _queryCounter.SelectCount.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(EmptyWordIdInputs))]
    public async Task GetQuizHintsForWordsAsync_EmptyWordIds_ReturnEmptyWithWarning(
        IEnumerable<string>? wordIds)
    {
        var results = await _repo.GetQuizHintsForWordsAsync(UserA, wordIds, "B1");

        results.Should().BeEmpty();
        _logs.HasWarningContaining("no vocabulary word IDs").Should().BeTrue();
        _queryCounter.SelectCount.Should().Be(0);
    }

    [Fact]
    public async Task GetQuizHintsForWordsAsync_HonorsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var action = () => _repo.GetQuizHintsForWordsAsync(
            UserA,
            new[] { "word-a" },
            "B1",
            ct: cts.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        _queryCounter.SelectCount.Should().Be(0);
    }

    [Fact]
    public void VocabQuizSentenceHint_ContainsNoNativeAnswerProperty()
    {
        typeof(VocabQuizSentenceHint).GetProperties()
            .Select(property => property.Name)
            .Should().Equal(
                nameof(VocabQuizSentenceHint.ExampleSentenceId),
                nameof(VocabQuizSentenceHint.VocabularyWordId),
                nameof(VocabQuizSentenceHint.TargetSentence));
    }

    [Fact]
    public async Task GetQuizHintsForWordsAsync_UsesOneSelectForMultipleWords()
    {
        SeedOwnedWord("word-a", ResourceA);
        SeedOwnedWord("word-b", ResourceA);
        SeedExample("word-a", ResourceA, "문장 A");
        SeedExample("word-b", ResourceA, "문장 B");
        SaveAndResetQueryCounter();

        var results = await _repo.GetQuizHintsForWordsAsync(
            UserA,
            new[] { "word-a", "word-b" },
            "B1");

        results.Should().HaveCount(2);
        _queryCounter.SelectCount.Should().Be(1);
        _queryCounter.Commands.Should().ContainSingle()
            .Which.Should().NotContain("NativeSentence");
    }

    public static TheoryData<IEnumerable<string>?> EmptyWordIdInputs => new()
    {
        null,
        Array.Empty<string>(),
        new[] { string.Empty, "   " }
    };

    private void SeedOwnedWord(string wordId, params string[] resourceIds)
    {
        _db.VocabularyWords.Add(new VocabularyWord
        {
            Id = wordId,
            TargetLanguageTerm = wordId
        });
        foreach (var resourceId in resourceIds)
        {
            _db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = $"{resourceId}-{wordId}",
                ResourceId = resourceId,
                VocabularyWordId = wordId
            });
        }
    }

    private void SeedExample(
        string wordId,
        string? resourceId,
        string targetSentence,
        int? id = null,
        int? difficulty = 3,
        bool core = false,
        ExampleSentenceStatus status = ExampleSentenceStatus.Curated,
        bool flagged = false,
        DateTime? createdAt = null)
    {
        _db.ExampleSentences.Add(new ExampleSentence
        {
            Id = id ?? 0,
            VocabularyWordId = wordId,
            LearningResourceId = resourceId,
            TargetSentence = targetSentence,
            DifficultyLevel = difficulty,
            IsCore = core,
            Status = status,
            IsFlagged = flagged,
            NativeSentence = "must never be projected",
            CreatedAt = createdAt ?? new DateTime(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc)
                .AddSeconds(_db.ExampleSentences.Local.Count),
            UpdatedAt = createdAt ?? DateTime.UtcNow
        });
    }

    private void SaveAndResetQueryCounter()
    {
        _db.SaveChanges();
        _queryCounter.Reset();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _loggerFactory.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class SelectCommandCounter : DbCommandInterceptor
    {
        private readonly List<string> _commands = new();

        public int SelectCount { get; private set; }
        public IReadOnlyList<string> Commands => _commands;

        public void Reset()
        {
            SelectCount = 0;
            _commands.Clear();
        }

        private void Inspect(DbCommand command)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                SelectCount++;
                _commands.Add(command.CommandText);
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Inspect(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Inspect(command);
            return ValueTask.FromResult(result);
        }
    }
}
