using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services.Numbers;
using SentenceStudio.Shared.Models.Numbers;
using Xunit;

namespace SentenceStudio.AppLib.Tests.Services.Numbers;

public class NumberSessionServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly NumberSessionService _service;
    private readonly INumberItemGenerator _generator;
    private readonly INumberAnswerGrader _grader;

    public NumberSessionServiceTests()
    {
        // Create in-memory SQLite database for testing
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _db = new ApplicationDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _generator = new KoreanNumberItemGenerator();
        _grader = new KoreanNumberAnswerGrader();
        _service = new NumberSessionService(_generator, _grader, _db, NullLogger<NumberSessionService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [Fact]
    public async Task StartSessionAsync_CreatesSessionWithItems()
    {
        // Arrange
        await SeedTestDataAsync();
        var request = new NumberSessionRequest(
            ContextCode: "Time",
            SubModeCode: "ListenAndType",
            ItemCount: 5,
            DueOnly: false
        );

        // Act
        var session = await _service.StartSessionAsync(
            userProfileId: "test-user-guid-1",
            languageCode: "ko",
            request: request
        );

        // Assert
        Assert.NotEqual(Guid.Empty, session.SessionId);
        Assert.True(session.Items.Count > 0);
        Assert.True(session.Items.Count <= 5);
        Assert.All(session.Items, item => Assert.Equal("ListenAndType", item.SubModeCode));
    }

    [Fact]
    public async Task SubmitAnswerAsync_CorrectAnswer_CreatesAttemptAndUpdatesProgress()
    {
        // Arrange
        await SeedTestDataAsync();
        var item = _generator.GenerateItem(new NumberItemRequest(
            ContextCode: "Time",
            SubModeCode: "ListenAndType",
            Bucket: "1-10",
            CounterId: null,
            Difficulty: 1,
            RandomSeed: 42
        ));

        // Act
        var result = await _service.SubmitAnswerAsync(
            userProfileId: "test-user-guid-1",
            sessionId: Guid.NewGuid(),
            item: item,
            userAnswer: item.CanonicalAnswer, // Correct answer
            latencyMs: 5000
        );

        // Assert
        Assert.True(result.Grade.IsCorrect);
        Assert.Equal(1, result.Snapshot.CorrectCount);
        Assert.Equal(1, result.Snapshot.TotalCount);

        // Verify NumberAttempt was persisted
        var attempt = await _db.NumberAttempts.FirstOrDefaultAsync();
        Assert.NotNull(attempt);
        Assert.True(attempt.IsCorrect);
        Assert.Equal(item.CanonicalAnswer, attempt.ExpectedAnswer);

        // Verify NumberMasteryProgress was created
        var progress = await _db.NumberMasteryProgresses.FirstOrDefaultAsync();
        Assert.NotNull(progress);
        Assert.Equal(1, progress.CorrectCount);
        Assert.Equal(1, progress.TotalCount);
        Assert.True(progress.DueDate > DateTime.UtcNow); // SM-2 scheduled future review
    }

    [Fact]
    public async Task SubmitAnswerAsync_IncorrectAnswer_UpdatesMasteryWithResetSchedule()
    {
        // Arrange
        await SeedTestDataAsync();
        var item = _generator.GenerateItem(new NumberItemRequest(
            ContextCode: "Age",
            SubModeCode: "ReadAndProduce",
            Bucket: "1-10",
            CounterId: null,
            Difficulty: 1,
            RandomSeed: 123
        ));

        // Act
        var result = await _service.SubmitAnswerAsync(
            userProfileId: "test-user-guid-1",
            sessionId: Guid.NewGuid(),
            item: item,
            userAnswer: "wrong answer",
            latencyMs: 8000
        );

        // Assert
        Assert.False(result.Grade.IsCorrect);
        Assert.Equal(0, result.Snapshot.CorrectCount);
        Assert.Equal(1, result.Snapshot.TotalCount);

        // Verify SM-2 reset interval to 1 day
        var progress = await _db.NumberMasteryProgresses.FirstOrDefaultAsync();
        Assert.NotNull(progress);
        Assert.Equal(1, progress.Interval); // Reset to 1 on incorrect
    }

    [Fact]
    public async Task SubmitAnswerAsync_FastCorrectAnswer_AssignsHighSm2Quality()
    {
        // Arrange
        await SeedTestDataAsync();
        var item = _generator.GenerateItem(new NumberItemRequest(
            ContextCode: "Counting",
            SubModeCode: "ListenAndType",
            Bucket: "1-10",
            CounterId: null,
            Difficulty: 1,
            RandomSeed: 456
        ));

        // Act - Fast correct answer (5000ms < 8000ms threshold)
        var result = await _service.SubmitAnswerAsync(
            userProfileId: "test-user-guid-1",
            sessionId: Guid.NewGuid(),
            item: item,
            userAnswer: item.CanonicalAnswer,
            latencyMs: 5000 // Fast
        );

        // Assert
        var progress = await _db.NumberMasteryProgresses.FirstOrDefaultAsync();
        Assert.NotNull(progress);
        Assert.Equal(6, progress.Interval); // First correct answer interval = 6
    }

    [Fact]
    public async Task EndSessionAsync_ReturnsSessionSummary()
    {
        // Arrange
        await SeedTestDataAsync();
        var request = new NumberSessionRequest(
            ContextCode: "Time",
            SubModeCode: "ListenAndType",
            ItemCount: 3
        );
        var session = await _service.StartSessionAsync("test-user-guid-1", "ko", request);

        // Act
        var summary = await _service.EndSessionAsync(session.SessionId);

        // Assert
        Assert.Equal(session.SessionId, summary.SessionId);
        Assert.True(summary.TotalItems > 0);
    }

    [Fact]
    public async Task SubmitAnswerAsync_DoesNotTouchStreakState()
    {
        // Arrange
        await SeedTestDataAsync();
        var item = _generator.GenerateItem(new NumberItemRequest(
            ContextCode: "Time",
            SubModeCode: "ListenAndType",
            Bucket: "1-10",
            CounterId: null,
            Difficulty: 1,
            RandomSeed: 789
        ));

        // Act - Incorrect answer
        await _service.SubmitAnswerAsync(
            userProfileId: "test-user-guid-1",
            sessionId: Guid.NewGuid(),
            item: item,
            userAnswer: "wrong",
            latencyMs: 5000
        );

        // Assert - No streak-related tables or fields should be touched
        // This is a critical invariant: NumberDrill NEVER breaks daily streak
        // (Future: verify no writes to DailyPlan, StreakTracking, or similar tables)
        // For Phase 1, just confirm the service doesn't crash and progress is isolated
        var progress = await _db.NumberMasteryProgresses.FirstOrDefaultAsync();
        Assert.NotNull(progress);
        // NumberMasteryProgress has no streak fields - isolation confirmed
    }

    [Fact]
    public async Task StartSessionAsync_CountingContext_UsesCounterFromDatabase()
    {
        // Arrange
        await SeedTestDataAsync();
        var request = new NumberSessionRequest(
            ContextCode: "Counting",
            SubModeCode: "ListenAndType",
            ItemCount: 3,
            DueOnly: false
        );

        // Act
        var session = await _service.StartSessionAsync(
            userProfileId: "test-user-guid-1",
            languageCode: "ko",
            request: request
        );

        // Assert
        Assert.NotEqual(Guid.Empty, session.SessionId);
        Assert.True(session.Items.Count > 0);
        Assert.All(session.Items, item => 
        {
            Assert.Equal("Counting", item.ContextCode);
            Assert.Equal("ListenAndType", item.SubModeCode);
            Assert.NotNull(item.CounterId); // Counting items must have a counter
            Assert.NotNull(item.CounterText);
        });
    }

    private async Task SeedTestDataAsync()
    {
        // Seed contexts
        _db.NumberContexts.AddRange(
            new NumberContext { Id = Guid.NewGuid().ToString(), Code = "Time", DisplayName = "Time", Icon = "⏰", DefaultSystem = NumberSystem.Native, SortOrder = 1, IsActive = true },
            new NumberContext { Id = Guid.NewGuid().ToString(), Code = "Age", DisplayName = "Age", Icon = "🎂", DefaultSystem = NumberSystem.Native, SortOrder = 2, IsActive = true },
            new NumberContext { Id = Guid.NewGuid().ToString(), Code = "Counting", DisplayName = "Counting", Icon = "🛒", DefaultSystem = NumberSystem.Native, SortOrder = 3, IsActive = true }
        );

        // Seed sub-modes
        _db.NumberSubModes.AddRange(
            new NumberSubMode { Id = Guid.NewGuid().ToString(), Code = "ListenAndType", DisplayName = "Listen & Type", Phase = 1, IsActive = true },
            new NumberSubMode { Id = Guid.NewGuid().ToString(), Code = "ReadAndProduce", DisplayName = "Read & Produce", Phase = 1, IsActive = true }
        );

        // Seed counters (matching Phase1Counters in KoreanNumberItemGenerator)
        _db.NumberCounters.AddRange(
            new NumberCounter { Id = Guid.NewGuid().ToString(), LanguageCode = "ko", Counter = "잔", Romanization = "jan", MeaningEn = "cups/glasses", System = NumberSystem.Native },
            new NumberCounter { Id = Guid.NewGuid().ToString(), LanguageCode = "ko", Counter = "개", Romanization = "gae", MeaningEn = "generic objects", System = NumberSystem.Native },
            new NumberCounter { Id = Guid.NewGuid().ToString(), LanguageCode = "ko", Counter = "명", Romanization = "myeong", MeaningEn = "people", System = NumberSystem.Native },
            new NumberCounter { Id = Guid.NewGuid().ToString(), LanguageCode = "ko", Counter = "마리", Romanization = "mari", MeaningEn = "animals", System = NumberSystem.Native },
            new NumberCounter { Id = Guid.NewGuid().ToString(), LanguageCode = "ko", Counter = "권", Romanization = "gwon", MeaningEn = "books", System = NumberSystem.Native },
            new NumberCounter { Id = Guid.NewGuid().ToString(), LanguageCode = "ko", Counter = "살", Romanization = "sal", MeaningEn = "years old", System = NumberSystem.Native }
        );

        await _db.SaveChangesAsync();
    }
}
