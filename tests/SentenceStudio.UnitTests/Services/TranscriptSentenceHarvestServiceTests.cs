using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using SentenceStudio.Services;

namespace SentenceStudio.UnitTests.Services;

public sealed class TranscriptSentenceHarvestServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly TranscriptSentenceHarvestService _service;

    public TranscriptSentenceHarvestServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _db = new ApplicationDbContext(options);
        _db.Database.EnsureCreated();

        var exampleRepository = new ExampleSentenceRepository(
            _db,
            NullLogger<ExampleSentenceRepository>.Instance);
        _service = new TranscriptSentenceHarvestService(
            _db,
            exampleRepository,
            new EchoBulkTranslationAiService(),
            NullLogger<TranscriptSentenceHarvestService>.Instance);
    }

    [Fact]
    public async Task HarvestForUserAsync_AddsCuratedFromReadingExamples_IsIdempotent_AndSkipsNoMatches()
    {
        SeedTranscriptScenario();

        var first = await _service.HarvestForUserAsync("user-1");
        var countAfterFirst = await _db.ExampleSentences.CountAsync();

        var second = await _service.HarvestForUserAsync("user-1");
        var countAfterSecond = await _db.ExampleSentences.CountAsync();

        first.ResourcesScanned.Should().Be(1);
        first.WordsExamined.Should().Be(3);
        first.SentencesAdded.Should().Be(3);
        first.SkippedNoMatch.Should().Be(1);
        first.TranslationFailures.Should().Be(0);

        second.SentencesAdded.Should().Be(0);
        countAfterSecond.Should().Be(countAfterFirst);

        var createdRows = await _db.ExampleSentences
            .OrderBy(es => es.VocabularyWordId)
            .ThenBy(es => es.TargetSentence)
            .ToListAsync();

        createdRows.Should().HaveCount(3);
        createdRows.Should().OnlyContain(es =>
            es.Source == ExampleSentenceSource.FromReading &&
            es.Status == ExampleSentenceStatus.Curated &&
            es.LearningResourceId == "resource-user-1" &&
            !es.IsCore &&
            !string.IsNullOrWhiteSpace(es.NativeSentence));
        createdRows.Count(es => es.VocabularyWordId == "word-go").Should().Be(2);
        createdRows.Should().Contain(es => es.VocabularyWordId == "word-book");
        createdRows.Should().Contain(es => es.TargetSentence == "저는 학교에 가요.");
        createdRows.Should().Contain(es => es.TargetSentence == "내일도 학교에 가고 싶어요!");
        createdRows.Should().Contain(es => es.TargetSentence == "친구와 같이 가서 책을 읽어요.");
        createdRows.Should().NotContain(es => es.VocabularyWordId == "word-none");
        createdRows.Should().NotContain(es => es.LearningResourceId == "resource-user-2");
    }

    [Fact]
    public async Task HarvestForUserAsync_EmptyUserIdReturnsEmptySummaryAndCreatesNoRows()
    {
        SeedTranscriptScenario();

        var summary = await _service.HarvestForUserAsync("");

        summary.ResourcesScanned.Should().Be(0);
        summary.WordsExamined.Should().Be(0);
        summary.SentencesAdded.Should().Be(0);
        summary.SkippedDuplicate.Should().Be(0);
        summary.SkippedNoMatch.Should().Be(0);
        summary.TranslationFailures.Should().Be(0);
        _db.ExampleSentences.Should().BeEmpty();
    }

    private void SeedTranscriptScenario()
    {
        _db.UserProfiles.AddRange(
            new UserProfile
            {
                Id = "user-1",
                Name = "Tester One",
                NativeLanguage = "English",
                TargetLanguage = "Korean",
                CreatedAt = DateTime.UtcNow
            },
            new UserProfile
            {
                Id = "user-2",
                Name = "Tester Two",
                NativeLanguage = "English",
                TargetLanguage = "Korean",
                CreatedAt = DateTime.UtcNow
            });

        _db.LearningResources.AddRange(
            new LearningResource
            {
                Id = "resource-user-1",
                Title = "User one transcript",
                Language = "Korean",
                MediaType = "Transcript",
                Transcript = """
                    저는 학교에 가요
                    내일도 학교에 가고 싶어요! 친구와 같이 가서 책을 읽어요
                    없는 매칭은 없습니다
                    """,
                UserProfileId = "user-1",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new LearningResource
            {
                Id = "resource-user-2",
                Title = "Other user transcript",
                Language = "Korean",
                MediaType = "Transcript",
                Transcript = "다른 사용자의 단어가 있어요.",
                UserProfileId = "user-2",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

        _db.VocabularyWords.AddRange(
            new VocabularyWord
            {
                Id = "word-go",
                TargetLanguageTerm = "가다",
                Lemma = "가다",
                NativeLanguageTerm = "go",
                Language = "Korean"
            },
            new VocabularyWord
            {
                Id = "word-book",
                TargetLanguageTerm = "책",
                Lemma = "책",
                NativeLanguageTerm = "book",
                Language = "Korean"
            },
            new VocabularyWord
            {
                Id = "word-none",
                TargetLanguageTerm = "우산",
                Lemma = "우산",
                NativeLanguageTerm = "umbrella",
                Language = "Korean"
            },
            new VocabularyWord
            {
                Id = "word-other-user",
                TargetLanguageTerm = "다른",
                Lemma = "다른",
                NativeLanguageTerm = "different",
                Language = "Korean"
            });

        _db.ResourceVocabularyMappings.AddRange(
            new ResourceVocabularyMapping
            {
                Id = "mapping-go",
                ResourceId = "resource-user-1",
                VocabularyWordId = "word-go"
            },
            new ResourceVocabularyMapping
            {
                Id = "mapping-book",
                ResourceId = "resource-user-1",
                VocabularyWordId = "word-book"
            },
            new ResourceVocabularyMapping
            {
                Id = "mapping-none",
                ResourceId = "resource-user-1",
                VocabularyWordId = "word-none"
            },
            new ResourceVocabularyMapping
            {
                Id = "mapping-other-user",
                ResourceId = "resource-user-2",
                VocabularyWordId = "word-other-user"
            });

        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class EchoBulkTranslationAiService : IAiService
    {
        public Task<T> SendPrompt<T>(string prompt, AiTier tier = AiTier.Fast, string? reasoningEffort = null)
        {
            if (typeof(T) != typeof(BulkTranslationResponse))
                return Task.FromResult(Activator.CreateInstance<T>());

            var response = new BulkTranslationResponse
            {
                Translations = ExtractPromptSentences(prompt)
                    .Select(sentence => new TranslationPair
                    {
                        TargetLanguageTerm = sentence,
                        NativeLanguageTerm = $"translation: {sentence}"
                    })
                    .ToList()
            };

            return Task.FromResult((T)(object)response);
        }

        public Task<string> SendImage(string imagePath, string prompt) => Task.FromResult(string.Empty);

        public Task<Stream> TextToSpeechAsync(string text, string voice, float speed = 1.0f) =>
            Task.FromResult<Stream>(Stream.Null);

        private static IEnumerable<string> ExtractPromptSentences(string prompt)
        {
            return prompt
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
                .Select(line => line[2..].Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line));
        }
    }
}
