using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services.Vocabulary;

/// <summary>
/// The phrase-part backfill (run at API startup and from every device's sync) under the
/// owner-only rule (Captain 2026-11-06): it links a learner's phrase only to parts recorded to
/// the same learner. Review of bf701c42, finding 5: it took a learner's words from their
/// progress, so legacy progress on another learner's word made that word a part of their
/// phrase, and their phrase-shaped progress on another learner's phrase gave that phrase parts.
/// </summary>
public sealed class PhraseConstituentBackfillOwnerTests : IDisposable
{
    private const string LearnerA = "backfill-learner-a";
    private const string LearnerB = "backfill-learner-b";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _services;

    public PhraseConstituentBackfillOwnerTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection));
        _services = services.BuildServiceProvider();
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Backfill_LinksPhrasesOnlyToPartsRecordedToTheSameLearner()
    {
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.UserProfiles.AddRange(
                new UserProfile { Id = LearnerA, Name = "A" },
                new UserProfile { Id = LearnerB, Name = "B" });
            Word(db, "phrase-a", "사과 바나나", LexicalUnitType.Phrase, LearnerA);
            Word(db, "apple-a", "사과", LexicalUnitType.Word, LearnerA);
            Word(db, "banana-b", "바나나", LexicalUnitType.Word, LearnerB);
            Word(db, "phrase-b", "바나나 사과", LexicalUnitType.Phrase, LearnerB);
            // A practises every word here, B's too (legacy rows from before owners); B practises
            // their own.
            foreach (var word in new[] { "phrase-a", "apple-a", "banana-b", "phrase-b" })
                Progress(db, LearnerA, word);
            foreach (var word in new[] { "banana-b", "phrase-b" })
                Progress(db, LearnerB, word);
            await db.SaveChangesAsync();
        }

        await new VocabularyClassificationBackfillService(
                _services, NullLogger<VocabularyClassificationBackfillService>.Instance)
            .BackfillPhraseConstituentsAsync();

        using var check = _services.CreateScope();
        var links = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .PhraseConstituents.AsNoTracking()
            .Select(link => new { link.PhraseWordId, link.ConstituentWordId })
            .ToListAsync();
        links.Where(link => link.PhraseWordId == "phrase-a").Select(link => link.ConstituentWordId)
            .Should().Equal(["apple-a"], "B's word is not A's to use as a part");
        links.Where(link => link.PhraseWordId == "phrase-b").Select(link => link.ConstituentWordId)
            .Should().Equal(["banana-b"], "A's progress gives B's phrase no part, and A's word is not B's");
    }

    private static void Word(ApplicationDbContext db, string id, string term, LexicalUnitType type, string owner) =>
        db.VocabularyWords.Add(new VocabularyWord
        {
            Id = id, TargetLanguageTerm = term, NativeLanguageTerm = $"{term} meaning", Language = "Korean",
            LexicalUnitType = type, UserProfileId = owner,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

    private static void Progress(ApplicationDbContext db, string learner, string word) =>
        db.VocabularyProgresses.Add(new VocabularyProgress
        {
            Id = $"{learner}-{word}", UserId = learner, VocabularyWordId = word,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
}
