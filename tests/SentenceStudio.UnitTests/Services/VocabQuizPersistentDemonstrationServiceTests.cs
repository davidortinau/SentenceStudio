using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

public sealed class VocabQuizPersistentDemonstrationServiceTests : IDisposable
{
    private const string UserId = "persistent-demo-user";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly VocabularyProgressService _sut;

    public VocabQuizPersistentDemonstrationServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection));

        _serviceProvider = services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.EnsureCreated();
        }

        var progressRepo = new VocabularyProgressRepository(
            _serviceProvider,
            NullLogger<VocabularyProgressRepository>.Instance);

        var contextRepo = new VocabularyLearningContextRepository(
            _serviceProvider,
            NullLogger<VocabularyLearningContextRepository>.Instance);

        _sut = new VocabularyProgressService(
            progressRepo,
            contextRepo,
            NullLogger<VocabularyProgressService>.Instance,
            _serviceProvider);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task RecordAttemptAsync_IncrementsOnlyCorrectVocabularyQuizDemonstrationCountersByMode()
    {
        SeedWords("recognition", "production", "wrong", "non-quiz");

        var recognition = await _sut.RecordAttemptAsync(MakeAttempt("recognition", wasCorrect: true, "MultipleChoice", "VocabularyQuiz"));
        var production = await _sut.RecordAttemptAsync(MakeAttempt("production", wasCorrect: true, "Text", "VocabularyQuiz"));
        var wrong = await _sut.RecordAttemptAsync(MakeAttempt("wrong", wasCorrect: false, "MultipleChoice", "VocabularyQuiz"));
        var nonQuiz = await _sut.RecordAttemptAsync(MakeAttempt("non-quiz", wasCorrect: true, "MultipleChoice", "Reading"));

        recognition.QuizRecognitionDemonstrations.Should().Be(1,
            "a correct VocabularyQuiz MultipleChoice attempt is one recognition demonstration");
        recognition.QuizProductionDemonstrations.Should().Be(0);

        production.QuizRecognitionDemonstrations.Should().Be(0);
        production.QuizProductionDemonstrations.Should().Be(1,
            "a correct VocabularyQuiz Text attempt is one production demonstration");

        wrong.QuizRecognitionDemonstrations.Should().Be(0,
            "wrong VocabularyQuiz attempts must not add recognition demonstrations");
        wrong.QuizProductionDemonstrations.Should().Be(0,
            "wrong VocabularyQuiz attempts must not add production demonstrations");

        nonQuiz.QuizRecognitionDemonstrations.Should().Be(0,
            "non-VocabularyQuiz activities must not add quiz demonstration counters");
        nonQuiz.QuizProductionDemonstrations.Should().Be(0);
    }

    private static VocabularyAttempt MakeAttempt(
        string wordId,
        bool wasCorrect,
        string inputMode,
        string activity)
    {
        return new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = UserId,
            Activity = activity,
            InputMode = inputMode,
            WasCorrect = wasCorrect,
            DifficultyWeight = 1.0f,
            ContextType = "Isolated"
        };
    }

    private void SeedWords(params string[] wordIds)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var wordId in wordIds)
        {
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = wordId,
                TargetLanguageTerm = wordId,
                NativeLanguageTerm = wordId,
                LexicalUnitType = LexicalUnitType.Word
            });
        }

        db.SaveChanges();
    }
}
