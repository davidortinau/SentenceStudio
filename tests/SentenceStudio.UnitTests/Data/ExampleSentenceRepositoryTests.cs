using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Data;

public sealed class ExampleSentenceRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
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

        _db.VocabularyWords.Add(new VocabularyWord { Id = "w1", TargetLanguageTerm = "단어1" });
        _db.VocabularyWords.Add(new VocabularyWord { Id = "w2", TargetLanguageTerm = "단어2" });
        _db.SaveChanges();

        _repo = new ExampleSentenceRepository(_db, NullLogger<ExampleSentenceRepository>.Instance);
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

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
