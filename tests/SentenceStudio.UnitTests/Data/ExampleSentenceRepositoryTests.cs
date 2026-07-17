using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

public sealed class ExampleSentenceRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly ServiceProvider _provider;
    private readonly ExampleSentenceRepository _repo;

    public ExampleSentenceRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        _db.LearningResources.Add(new LearningResource { Id = "r1", Title = "Resource 1", UserProfileId = "user-1" });
        _db.LearningResources.Add(new LearningResource { Id = "r2", Title = "Resource 2", UserProfileId = "user-1" });
        _db.VocabularyWords.Add(new VocabularyWord { Id = "w1", TargetLanguageTerm = "단어1", Lemma = "단어1" });
        _db.VocabularyWords.Add(new VocabularyWord { Id = "w2", TargetLanguageTerm = "단어2", Lemma = "단어2" });
        _db.VocabularyWords.Add(new VocabularyWord { Id = "w3", TargetLanguageTerm = "먹어요", Lemma = "먹다" });
        _db.SaveChanges();

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        _provider = services.BuildServiceProvider();
        _repo = new ExampleSentenceRepository(
            _db,
            _provider,
            NullLogger<ExampleSentenceRepository>.Instance);
    }

    private ExampleSentence New(string word, string target, bool core = false,
        ExampleSentenceStatus status = ExampleSentenceStatus.Curated,
        bool flagged = false) => new()
    {
        VocabularyWordId = word,
        TargetSentence = target,
        IsCore = core,
        Status = status,
        IsFlagged = flagged,
    };

    [Fact]
    public async Task SetCoreAsync_DemotesOtherCoresForSameWord()
    {
        var a = await _repo.CreateAsync(New("w1", "A", core: true));
        var b = await _repo.CreateAsync(New("w1", "B"));

        await _repo.SetCoreAsync(b.Id, true);

        var all = await _repo.GetByVocabularyWordIdAsync("w1");
        all.Single(s => s.Id == b.Id).IsCore.Should().BeTrue();
        all.Single(s => s.Id == a.Id).IsCore.Should().BeFalse();
        all.Count(s => s.IsCore).Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_NewCore_DemotesExistingCore()
    {
        var a = await _repo.CreateAsync(New("w1", "A", core: true));
        var b = await _repo.CreateAsync(New("w1", "B", core: true));

        var all = await _repo.GetByVocabularyWordIdAsync("w1");
        all.Single(s => s.Id == b.Id).IsCore.Should().BeTrue();
        all.Single(s => s.Id == a.Id).IsCore.Should().BeFalse();
    }

    [Fact]
    public async Task SetCoreAsync_DoesNotAffectOtherWords()
    {
        var a = await _repo.CreateAsync(New("w1", "A", core: true));
        var b = await _repo.CreateAsync(New("w2", "B", core: true));

        await _repo.SetCoreAsync(a.Id, true);

        (await _repo.GetByVocabularyWordIdAsync("w2")).Single().IsCore.Should().BeTrue();
    }

    [Fact]
    public async Task GetQuizEligibleAsync_ExcludesSuggestedAndFlagged_CoreFirst()
    {
        await _repo.CreateAsync(New("w1", "suggested", status: ExampleSentenceStatus.Suggested));
        await _repo.CreateAsync(New("w1", "flagged", flagged: true));
        await _repo.CreateAsync(New("w1", "supplementary"));
        await _repo.CreateAsync(New("w1", "core", core: true));

        var eligible = await _repo.GetQuizEligibleAsync("w1");

        eligible.Select(s => s.TargetSentence).Should().BeEquivalentTo(
            new[] { "core", "supplementary" }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void IsQuizEligible_TrueOnlyForCuratedOrVerifiedAndNotFlagged()
    {
        New("w", "s", status: ExampleSentenceStatus.Suggested).IsQuizEligible.Should().BeFalse();
        New("w", "c").IsQuizEligible.Should().BeTrue();
        New("w", "v", status: ExampleSentenceStatus.Verified).IsQuizEligible.Should().BeTrue();
        New("w", "f", flagged: true).IsQuizEligible.Should().BeFalse();
    }

    [Fact]
    public async Task GetCoreOrFirstEligibleAsync_PrefersCore_ThenEarliestEligible()
    {
        await _repo.CreateAsync(New("w1", "suggested", status: ExampleSentenceStatus.Suggested));
        await _repo.CreateAsync(New("w1", "supplementary"));
        await _repo.CreateAsync(New("w1", "core", core: true));

        (await _repo.GetCoreOrFirstEligibleAsync("w1"))!.TargetSentence.Should().Be("core");
    }

    [Fact]
    public async Task GetCoreOrFirstEligibleAsync_ReturnsNull_WhenNoEligible()
    {
        await _repo.CreateAsync(New("w1", "suggested", status: ExampleSentenceStatus.Suggested));
        await _repo.CreateAsync(New("w1", "flagged", flagged: true));

        (await _repo.GetCoreOrFirstEligibleAsync("w1")).Should().BeNull();
    }

    [Fact]
    public async Task CreateFromReadingIfNewAsync_InsertsCuratedFromReadingNonCore_WhenTermAppears()
    {
        var created = await _repo.CreateFromReadingIfNewAsync(
            "w1",
            "r1",
            "  단어1을 오늘 배웠어요.  ",
            "I learned word one today.",
            status: ExampleSentenceStatus.Verified);

        created.Should().NotBeNull();
        created!.TargetSentence.Should().Be("단어1을 오늘 배웠어요.");
        created.NativeSentence.Should().Be("I learned word one today.");
        created.Source.Should().Be(ExampleSentenceSource.FromReading);
        created.Status.Should().Be(ExampleSentenceStatus.Verified);
        created.IsCore.Should().BeFalse();
        created.LearningResourceId.Should().Be("r1");
        _db.ExampleSentences.Should().ContainSingle(es => es.Id == created.Id);
    }

    [Fact]
    public async Task CreateFromReadingIfNewAsync_ReturnsNull_WhenSentenceIsBlank()
    {
        var created = await _repo.CreateFromReadingIfNewAsync("w1", "r1", "   ", "blank");

        created.Should().BeNull();
        _db.ExampleSentences.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateFromReadingIfNewAsync_ReturnsNull_WhenTargetSentenceExceedsMaxLength()
    {
        // Guard against the Postgres varchar(500) limit: a run-on segment must be skipped, not thrown.
        var longSentence = "단어1 " + new string('가', 600);

        var created = await _repo.CreateFromReadingIfNewAsync("w1", "r1", longSentence, "translation");

        created.Should().BeNull();
        _db.ExampleSentences.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateFromReadingIfNewAsync_DropsOverlongTranslation_ButKeepsSentence()
    {
        var longTranslation = new string('x', 600);

        var created = await _repo.CreateFromReadingIfNewAsync("w1", "r1", "단어1을 배웠어요.", longTranslation);

        created.Should().NotBeNull();
        created!.NativeSentence.Should().BeNull();
        created.TargetSentence.Should().Be("단어1을 배웠어요.");
    }

    [Fact]
    public async Task CreateFromReadingIfNewAsync_ReturnsNull_WhenSentenceDoesNotContainTermOrLemma()
    {
        var created = await _repo.CreateFromReadingIfNewAsync(
            "w1",
            "r1",
            "다른 표현만 들어 있어요.",
            "Only another expression appears.");

        created.Should().BeNull();
        _db.ExampleSentences.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateFromReadingIfNewAsync_DeduplicatesNormalizedSentenceForSameWordAcrossSources()
    {
        await _repo.CreateAsync(new ExampleSentence
        {
            VocabularyWordId = "w1",
            LearningResourceId = "r1",
            TargetSentence = "단어1을   읽었어요.",
            Source = ExampleSentenceSource.AiGenerated,
            Status = ExampleSentenceStatus.Curated
        });

        var created = await _repo.CreateFromReadingIfNewAsync(
            "w1",
            "r1",
            "  “단어1을 읽었어요.”  ",
            "I read word one.");

        created.Should().BeNull();
        _db.ExampleSentences.Should().ContainSingle();
    }

    [Fact]
    public async Task CreateFromReadingIfNewAsync_RejectsThirdFromReadingSentenceForSameWordAndResourceButAllowsDifferentResource()
    {
        (await _repo.CreateFromReadingIfNewAsync("w1", "r1", "단어1 첫 문장입니다.", "first")).Should().NotBeNull();
        (await _repo.CreateFromReadingIfNewAsync("w1", "r1", "단어1 두 번째 문장입니다.", "second")).Should().NotBeNull();

        var thirdForSameResource = await _repo.CreateFromReadingIfNewAsync("w1", "r1", "단어1 세 번째 문장입니다.", "third");
        var firstForOtherResource = await _repo.CreateFromReadingIfNewAsync("w1", "r2", "단어1 다른 자료 문장입니다.", "other");

        thirdForSameResource.Should().BeNull();
        firstForOtherResource.Should().NotBeNull();
        _db.ExampleSentences.Where(es => es.VocabularyWordId == "w1").Should().HaveCount(3);
        _db.ExampleSentences.Count(es => es.VocabularyWordId == "w1" && es.LearningResourceId == "r1").Should().Be(2);
    }

    [Fact]
    public async Task CreateFromReadingIfNewAsync_BatchOverload_UsesPreloadedExistingSentencesForDuplicateAndCapChecks()
    {
        var word = await _db.VocabularyWords.FindAsync("w1");
        var existingForWord = new List<ExampleSentence>
        {
            new()
            {
                VocabularyWordId = "w1",
                LearningResourceId = "r1",
                TargetSentence = "단어1 기존 문장입니다.",
                Source = ExampleSentenceSource.UserAuthored,
                Status = ExampleSentenceStatus.Curated
            },
            new()
            {
                VocabularyWordId = "w1",
                LearningResourceId = "r1",
                TargetSentence = "단어1 읽기 하나입니다.",
                Source = ExampleSentenceSource.FromReading,
                Status = ExampleSentenceStatus.Curated
            },
            new()
            {
                VocabularyWordId = "w1",
                LearningResourceId = "r1",
                TargetSentence = "단어1 읽기 둘입니다.",
                Source = ExampleSentenceSource.FromReading,
                Status = ExampleSentenceStatus.Curated
            }
        };

        var duplicate = await _repo.CreateFromReadingIfNewAsync(
            word!,
            "r2",
            "「단어1 기존 문장입니다.」",
            "duplicate",
            existingForWord);
        var overCap = await _repo.CreateFromReadingIfNewAsync(
            word!,
            "r1",
            "단어1 새 읽기 문장입니다.",
            "over cap",
            existingForWord);
        var allowed = await _repo.CreateFromReadingIfNewAsync(
            word!,
            "r2",
            "단어1 다른 자료 문장입니다.",
            "allowed",
            existingForWord);

        duplicate.Should().BeNull();
        overCap.Should().BeNull();
        allowed.Should().NotBeNull();
        _db.ExampleSentences.Should().ContainSingle(es => es.TargetSentence == "단어1 다른 자료 문장입니다.");
    }

    [Fact]
    public async Task CreateFromReadingIfNewAsync_AlwaysCreatesNonCoreEvenWhenOtherCoreExists()
    {
        await _repo.CreateAsync(New("w1", "단어1 핵심 문장입니다.", core: true));

        var created = await _repo.CreateFromReadingIfNewAsync("w1", "r1", "단어1 읽기 문장입니다.", "reading");

        created.Should().NotBeNull();
        created!.IsCore.Should().BeFalse();
        (await _repo.GetCoreExamplesAsync("w1")).Should().ContainSingle();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }
}
