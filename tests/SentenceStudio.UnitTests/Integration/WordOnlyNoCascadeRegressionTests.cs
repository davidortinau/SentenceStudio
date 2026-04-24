using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FluentAssertions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.UnitTests.PlanGeneration;
using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.UnitTests.Integration;

/// <summary>
/// Regression tests proving word/phrase feature is additive, not disruptive.
/// Word-only activities (Cloze, Writing, VocabReview) must behave identically to pre-feature:
/// - NO cascade logic fires (no PhraseConstituent queries)
/// - NO cascade logs emitted
/// - Word mastery updates as expected
/// </summary>
public class WordOnlyNoCascadeRegressionTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private readonly PlanGenerationTestFixture _fixture;
    private readonly VocabularyProgressService _progressService;
    private readonly VocabularyProgressRepository _progressRepo;
    private readonly ApplicationDbContext _db;
    private readonly InMemoryLoggerProvider _loggerProvider;
    private readonly IServiceScope _scope;

    public WordOnlyNoCascadeRegressionTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
        _fixture.SeedUserProfile();

        _scope = fixture.ServiceProvider.CreateScope();
        _progressRepo = _scope.ServiceProvider.GetRequiredService<VocabularyProgressRepository>();
        _db = _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // In-memory logger to capture cascade logs
        _loggerProvider = new InMemoryLoggerProvider();
        var loggerFactory = _scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        loggerFactory.AddProvider(_loggerProvider);

        var contextRepo = new VocabularyLearningContextRepository(
            fixture.ServiceProvider,
            loggerFactory.CreateLogger<VocabularyLearningContextRepository>());

        _progressService = new VocabularyProgressService(
            _progressRepo,
            contextRepo,
            loggerFactory.CreateLogger<VocabularyProgressService>(),
            fixture.ServiceProvider);
    }

    public void Dispose()
    {
        _scope?.Dispose();
    }

    #region Word-Only Activity Regression Tests

    [Fact]
    public async Task RecordAttempt_WordTypeCloze_NoCascadeLogic()
    {
        // Arrange: Seed a Word-type vocabulary entry (simulating Cloze activity context)
        var wordId = await SeedWordVocabulary("책", "book", LexicalUnitType.Word);
        var initialPhraseConstituentCount = await _db.PhraseConstituents.CountAsync();
        initialPhraseConstituentCount.Should().Be(0, "test starts with no phrase constituents");

        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = PlanGenerationTestFixture.TestUserId,
            WasCorrect = true,
            DifficultyWeight = 1.0f,
            InputMode = InputMode.MultipleChoice.ToString(),
            Activity = "Cloze",
            ContextType = "Isolated"
        };

        _loggerProvider.Clear();

        // Act
        var result = await _progressService.RecordAttemptAsync(attempt);

        // Assert: Word progress updated as expected
        result.Should().NotBeNull();
        result.VocabularyWordId.Should().Be(wordId);
        result.TotalAttempts.Should().Be(1);
        result.CorrectAttempts.Should().Be(1);
        result.CurrentStreak.Should().Be(1);

        // Assert: NO cascade behavior
        var finalPhraseConstituentCount = await _db.PhraseConstituents.CountAsync();
        finalPhraseConstituentCount.Should().Be(0, "word-only activity must not touch PhraseConstituent table");

        var cascadeLogs = _loggerProvider.GetLogs().Where(log => log.Contains("PhraseCascade")).ToList();
        cascadeLogs.Should().BeEmpty("word-only activity must not emit cascade logs");
    }

    [Fact]
    public async Task RecordAttempt_WordTypeWriting_NoCascadeLogic()
    {
        // Arrange: Seed a Word-type vocabulary entry (simulating Writing activity context)
        var wordId = await SeedWordVocabulary("읽다", "to read", LexicalUnitType.Word);
        var initialPhraseConstituentCount = await _db.PhraseConstituents.CountAsync();

        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = PlanGenerationTestFixture.TestUserId,
            WasCorrect = true,
            DifficultyWeight = 1.5f,
            InputMode = InputMode.Text.ToString(),
            Activity = "Writing",
            ContextType = "Sentence"
        };

        _loggerProvider.Clear();

        // Act
        var result = await _progressService.RecordAttemptAsync(attempt);

        // Assert: Word progress updated as expected (production mode)
        result.Should().NotBeNull();
        result.ProductionInStreak.Should().Be(1, "Text input should count as production");
        result.CurrentStreak.Should().Be(1.5f, "CurrentStreak increments by DifficultyWeight (1.5)");

        // Assert: NO cascade behavior
        var finalPhraseConstituentCount = await _db.PhraseConstituents.CountAsync();
        finalPhraseConstituentCount.Should().Be(initialPhraseConstituentCount, "word-only activity must not touch PhraseConstituent table");

        var cascadeLogs = _loggerProvider.GetLogs().Where(log => log.Contains("PhraseCascade")).ToList();
        cascadeLogs.Should().BeEmpty("word-only activity must not emit cascade logs");
    }

    [Fact]
    public async Task RecordAttempt_WordTypeVocabReview_NoCascadeLogic()
    {
        // Arrange: Seed a Word-type vocabulary entry (simulating VocabularyReview activity)
        var wordId = await SeedWordVocabulary("사람", "person", LexicalUnitType.Word);
        var initialPhraseConstituentCount = await _db.PhraseConstituents.CountAsync();

        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = PlanGenerationTestFixture.TestUserId,
            WasCorrect = true,
            DifficultyWeight = 1.0f,
            InputMode = InputMode.MultipleChoice.ToString(),
            Activity = "VocabularyReview",
            ContextType = "Isolated"
        };

        _loggerProvider.Clear();

        // Act
        var result = await _progressService.RecordAttemptAsync(attempt);

        // Assert: Word progress updated
        result.Should().NotBeNull();
        result.TotalAttempts.Should().Be(1);
        result.CorrectAttempts.Should().Be(1);

        // Assert: NO cascade behavior
        var finalPhraseConstituentCount = await _db.PhraseConstituents.CountAsync();
        finalPhraseConstituentCount.Should().Be(initialPhraseConstituentCount, "word-only activity must not touch PhraseConstituent table");

        var cascadeLogs = _loggerProvider.GetLogs().Where(log => log.Contains("PhraseCascade")).ToList();
        cascadeLogs.Should().BeEmpty("word-only activity must not emit cascade logs");
    }

    [Fact]
    public async Task RecordAttempt_UnknownTypeFallsBackToWord_NoCascadeLogic()
    {
        // Arrange: Seed vocabulary with Unknown type (pre-feature default)
        var wordId = await SeedWordVocabulary("나", "I/me", LexicalUnitType.Unknown);
        var initialPhraseConstituentCount = await _db.PhraseConstituents.CountAsync();

        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = PlanGenerationTestFixture.TestUserId,
            WasCorrect = true,
            DifficultyWeight = 1.0f,
            InputMode = InputMode.MultipleChoice.ToString(),
            Activity = "VocabularyQuiz",
            ContextType = "Isolated"
        };

        _loggerProvider.Clear();

        // Act
        var result = await _progressService.RecordAttemptAsync(attempt);

        // Assert: Word progress updated
        result.Should().NotBeNull();

        // Assert: NO cascade behavior (Unknown != Phrase/Sentence)
        var finalPhraseConstituentCount = await _db.PhraseConstituents.CountAsync();
        finalPhraseConstituentCount.Should().Be(initialPhraseConstituentCount, "Unknown type must not trigger cascade");

        var cascadeLogs = _loggerProvider.GetLogs().Where(log => log.Contains("PhraseCascade")).ToList();
        cascadeLogs.Should().BeEmpty("Unknown type must not emit cascade logs");
    }

    #endregion

    #region Composite Scenario: Mixed Word and Phrase Vocabulary

    [Fact]
    public async Task RecordAttempt_WordActivityInMixedVocabulary_OnlyWordProgressChanges()
    {
        // Arrange: User has BOTH word and phrase vocabulary
        var wordId = await SeedWordVocabulary("먹다", "to eat", LexicalUnitType.Word);
        var phraseId = await SeedPhraseVocabulary("잘 먹겠습니다", "I will eat well (polite)", LexicalUnitType.Phrase, new[] { wordId });

        // Initialize progress for both
        await _progressService.GetProgressAsync(wordId, PlanGenerationTestFixture.TestUserId);
        var phraseProgressBefore = await _progressService.GetProgressAsync(phraseId, PlanGenerationTestFixture.TestUserId);
        var phraseExposureCountBefore = phraseProgressBefore.ExposureCount;

        var initialPhraseConstituentCount = await _db.PhraseConstituents.CountAsync();
        initialPhraseConstituentCount.Should().BeGreaterThan(0, "phrase should have constituents seeded");

        // Act: Record attempt for WORD only (not phrase)
        var wordAttempt = new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = PlanGenerationTestFixture.TestUserId,
            WasCorrect = true,
            DifficultyWeight = 1.0f,
            InputMode = InputMode.MultipleChoice.ToString(),
            Activity = "VocabularyQuiz",
            ContextType = "Isolated"
        };

        _loggerProvider.Clear();
        var wordResult = await _progressService.RecordAttemptAsync(wordAttempt);

        // Assert: Word progress changed
        wordResult.TotalAttempts.Should().Be(1);
        wordResult.CorrectAttempts.Should().Be(1);

        // Assert: Phrase progress UNCHANGED (no cascade leak)
        var phraseProgressAfter = await _progressService.GetProgressAsync(phraseId, PlanGenerationTestFixture.TestUserId);
        phraseProgressAfter.ExposureCount.Should().Be(phraseExposureCountBefore, "phrase exposure count must not change when only word is practiced");
        phraseProgressAfter.TotalAttempts.Should().Be(0, "phrase has not been directly practiced");

        // Assert: PhraseConstituent table unchanged
        var finalPhraseConstituentCount = await _db.PhraseConstituents.CountAsync();
        finalPhraseConstituentCount.Should().Be(initialPhraseConstituentCount, "word-only attempt must not modify PhraseConstituent table");

        // Assert: NO cascade logs
        var cascadeLogs = _loggerProvider.GetLogs().Where(log => log.Contains("PhraseCascade")).ToList();
        cascadeLogs.Should().BeEmpty("word-only attempt must not emit cascade logs even when phrase vocabulary exists");
    }

    #endregion

    #region Helper Methods

    private async Task<string> SeedWordVocabulary(string targetTerm, string nativeTerm, LexicalUnitType type)
    {
        var word = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            TargetLanguageTerm = targetTerm,
            NativeLanguageTerm = nativeTerm,
            Language = "Korean",
            LexicalUnitType = type,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.VocabularyWords.Add(word);
        await _db.SaveChangesAsync();
        return word.Id;
    }

    private async Task<string> SeedPhraseVocabulary(string targetTerm, string nativeTerm, LexicalUnitType type, string[] constituentWordIds)
    {
        var phrase = new VocabularyWord
        {
            Id = Guid.NewGuid().ToString(),
            TargetLanguageTerm = targetTerm,
            NativeLanguageTerm = nativeTerm,
            Language = "Korean",
            LexicalUnitType = type,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.VocabularyWords.Add(phrase);

        // Link constituents
        foreach (var constituentId in constituentWordIds)
        {
            _db.PhraseConstituents.Add(new PhraseConstituent
            {
                Id = Guid.NewGuid().ToString(),
                PhraseWordId = phrase.Id,
                ConstituentWordId = constituentId
            });
        }

        await _db.SaveChangesAsync();
        return phrase.Id;
    }

    #endregion
}

/// <summary>
/// In-memory logger provider for capturing logs during tests.
/// </summary>
public class InMemoryLoggerProvider : ILoggerProvider
{
    private readonly List<string> _logs = new();
    private readonly object _lock = new();

    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(this, categoryName);

    public void Dispose() { }

    public List<string> GetLogs()
    {
        lock (_lock)
        {
            return _logs.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _logs.Clear();
        }
    }

    private class InMemoryLogger : ILogger
    {
        private readonly InMemoryLoggerProvider _provider;
        private readonly string _categoryName;

        public InMemoryLogger(InMemoryLoggerProvider provider, string categoryName)
        {
            _provider = provider;
            _categoryName = categoryName;
        }

        public IDisposable BeginScope<TState>(TState state) => null!;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_provider._lock)
            {
                _provider._logs.Add($"[{logLevel}] {_categoryName}: {message}");
            }
        }
    }
}
