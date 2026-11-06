using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using Xunit;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// The video import keeps a transcript sentence as a word's example only under the Import
/// Content rule (B72 review): a target-language sentence from the transcript that does not carry
/// the word's meaning, with conjugated Korean found from the dictionary form.
/// </summary>
public sealed class VideoImportExampleSentenceTests : IDisposable
{
    private const string Owner = "video-example-owner";
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public VideoImportExampleSentenceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        services.AddLogging();
        services.AddScoped<ExampleSentenceRepository>();
        _provider = services.BuildServiceProvider();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
        db.UserProfiles.Add(new UserProfile { Id = Owner, Name = "Owner", Email = "video-example@test.invalid" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task OnlyRealTranscriptSentencesBecomeExamples()
    {
        const string transcript =
            "오늘은 김치를 배워 봐요. I ate 김치 today. 어제 학교에 갔어요. 사과 - apple";
        var items = new[]
        {
            // A mixed line around the word: it contains the word, but shows its meaning.
            Item("김치", "kimchi", "I ate 김치 today.", "I ate kimchi today."),
            // A word-list line.
            Item("사과", "apple", "사과 - apple", "apple - apple"),
            // A real sentence with a contracted verb (가다 in 갔어요).
            Item("가다", "to go", "어제 학교에 갔어요.", "I went to school yesterday."),
            // A real sentence the transcript does not contain.
            Item("학교", "school", "학교가 커요.", "The school is big.")
        };
        var pipeline = new VideoImportPipelineService(
            _provider, NullLogger<VideoImportPipelineService>.Instance,
            youtubeImport: null!, formatting: null!, aiService: null!,
            fileSystem: Mock.Of<IFileSystemService>());
        var import = new VideoImport
        {
            UserProfileId = Owner,
            VideoUrl = "https://youtube.com/watch?v=EXAMPLES",
            VideoTitle = "Example rule",
            Language = "Korean",
            CleanedTranscript = transcript
        };

        await pipeline.CreateLearningResourceAsync(import, items
            .Select(item => new VideoImportPipelineService.ExtractedVocabularyForImport(item.ToVocabularyWord("Korean"), item))
            .ToList());

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var kept = await (
            from example in db.ExampleSentences.AsNoTracking()
            join word in db.VocabularyWords.AsNoTracking() on example.VocabularyWordId equals word.Id
            select new { word.TargetLanguageTerm, example.TargetSentence }).ToListAsync();
        kept.Select(example => (example.TargetLanguageTerm, example.TargetSentence))
            .Should().BeEquivalentTo(new[] { ("가다", "어제 학교에 갔어요.") });
    }

    private static ExtractedVocabularyItem Item(string term, string native, string sentence, string translation) =>
        new()
        {
            TargetLanguageTerm = term,
            NativeLanguageTerm = native,
            ExampleSentence = sentence,
            ExampleSentenceTranslation = translation
        };
}
