// CRITICAL REGRESSION TESTS — userId resolution
// These tests exist because the userId="" default parameter bug has recurred 3+ times.
// VocabularyProgressService methods default userId to "" which silently returns empty results.
// The ResolveUserId() method falls back to IPreferencesService.Get("active_profile_id").
// If these tests fail, known words will appear as new in quizzes — the #1 user complaint.
// DO NOT DELETE OR WEAKEN THESE TESTS.

using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Regression tests for userId resolution in VocabularyProgressService.
/// Uses in-memory SQLite so the full repo → EF Core path is exercised.
/// </summary>
public class VocabularyProgressServiceUserIdTests : IDisposable
{
    private const string UserA = "user-a-id";
    private const string UserB = "user-b-id";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<IPreferencesService> _mockPreferences;
    private readonly VocabularyProgressService _sut;

    public VocabularyProgressServiceUserIdTests()
    {
        // Keep connection open so the in-memory DB persists across scopes
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _mockPreferences = new Mock<IPreferencesService>();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opts =>
            opts.UseSqlite(_connection));
        services.AddSingleton<IPreferencesService>(_mockPreferences.Object);

        _serviceProvider = services.BuildServiceProvider();

        // Create schema
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.EnsureCreated();
        }

        // Build real repos backed by in-memory SQLite
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
            _mockPreferences.Object);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    // ── Seed helpers ──────────────────────────────────────────────

    private void SeedProgressForUser(string userId, params string[] wordIds)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var wordId in wordIds)
        {
            // Ensure the VocabularyWord FK target exists
            if (!db.VocabularyWords.Any(w => w.Id == wordId))
            {
                db.VocabularyWords.Add(new VocabularyWord
                {
                    Id = wordId,
                    TargetLanguageTerm = $"word-{wordId}",
                    NativeLanguageTerm = $"meaning-{wordId}"
                });
            }

            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = Guid.NewGuid().ToString(),
                VocabularyWordId = wordId,
                UserId = userId,
                TotalAttempts = 5,
                CorrectAttempts = 3,
                MasteryScore = 0.5f,
                NextReviewDate = DateTime.Now.AddDays(-1),
                FirstSeenAt = DateTime.Now.AddDays(-10),
                LastPracticedAt = DateTime.Now.AddDays(-1),
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            });
        }

        db.SaveChanges();
    }

    private void SetActiveProfile(string userId)
    {
        _mockPreferences
            .Setup(p => p.Get("active_profile_id", string.Empty))
            .Returns(userId);
    }

    // ── GetProgressForWordsAsync ──────────────────────────────────

    [Fact]
    public async Task GetProgressForWordsAsync_WithoutExplicitUserId_ResolvesFromPreferences()
    {
        // Arrange — seed progress for UserA, set preferences to UserA
        SeedProgressForUser(UserA, "w1", "w2");
        SetActiveProfile(UserA);

        // Act — call WITHOUT passing userId (the default "" path)
        var result = await _sut.GetProgressForWordsAsync(
            new List<string> { "w1", "w2" });

        // Assert — must return UserA's progress, NOT empty
        result.Should().HaveCount(2,
            "ResolveUserId must fall back to IPreferencesService when userId is empty");
        result.Keys.Should().Contain("w1").And.Contain("w2");
    }

    [Fact]
    public async Task GetProgressForWordsAsync_WithEmptyUserId_ResolvesFromPreferences()
    {
        // Arrange
        SeedProgressForUser(UserA, "w1");
        SetActiveProfile(UserA);

        // Act — explicitly pass ""
        var result = await _sut.GetProgressForWordsAsync(
            new List<string> { "w1" }, userId: "");

        // Assert
        result.Should().HaveCount(1,
            "passing userId=\"\" must be treated the same as omitting it");
    }

    [Fact]
    public async Task GetProgressForWordsAsync_DoesNotReturnOtherUsersProgress()
    {
        // Arrange — seed progress for BOTH users
        SeedProgressForUser(UserA, "w1", "w2");
        SeedProgressForUser(UserB, "w1", "w3");
        SetActiveProfile(UserA);

        // Act
        var result = await _sut.GetProgressForWordsAsync(
            new List<string> { "w1", "w2", "w3" });

        // Assert — only UserA's words
        result.Should().HaveCount(2,
            "only the active user's progress should be returned");
        result.Keys.Should().Contain("w1").And.Contain("w2");
        result.Keys.Should().NotContain("w3",
            "w3 belongs to UserB and must not leak into UserA's results");
    }

    [Fact]
    public async Task GetProgressForWordsAsync_WithExplicitUserId_IgnoresPreferences()
    {
        // Arrange
        SeedProgressForUser(UserA, "w1");
        SeedProgressForUser(UserB, "w2");
        SetActiveProfile(UserA);

        // Act — explicitly pass UserB
        var result = await _sut.GetProgressForWordsAsync(
            new List<string> { "w1", "w2" }, userId: UserB);

        // Assert — should return UserB's word, not UserA's
        result.Should().HaveCount(1);
        result.Keys.Should().Contain("w2");
    }

    // ── GetAllProgressDictionaryAsync ─────────────────────────────

    [Fact]
    public async Task GetAllProgressDictionaryAsync_WithoutExplicitUserId_ResolvesFromPreferences()
    {
        // Arrange
        SeedProgressForUser(UserA, "w1", "w2", "w3");
        SeedProgressForUser(UserB, "w4");
        SetActiveProfile(UserA);

        // Act
        var result = await _sut.GetAllProgressDictionaryAsync();

        // Assert
        result.Should().HaveCount(3,
            "must resolve userId from preferences and return only UserA's records");
        result.Keys.Should().NotContain("w4",
            "UserB's progress must not appear");
    }

    [Fact]
    public async Task GetAllProgressDictionaryAsync_WithEmptyUserId_ResolvesFromPreferences()
    {
        // Arrange
        SeedProgressForUser(UserA, "w1");
        SetActiveProfile(UserA);

        // Act
        var result = await _sut.GetAllProgressDictionaryAsync(userId: "");

        // Assert
        result.Should().HaveCount(1);
    }

    // ── GetReviewCandidatesAsync ──────────────────────────────────

    [Fact]
    public async Task GetReviewCandidatesAsync_WithoutExplicitUserId_ResolvesFromPreferences()
    {
        // Arrange — seed UserA with a word that is due for review
        SeedProgressForUser(UserA, "w1");
        SetActiveProfile(UserA);

        // Act
        var result = await _sut.GetReviewCandidatesAsync();

        // Assert — should return something (not empty due to userId="")
        // The word is seeded with NextReviewDate in the past and MasteryScore=0.5
        // so it should be a review candidate.
        result.Should().NotBeEmpty(
            "ResolveUserId must resolve from preferences; " +
            "empty userId would return zero candidates — the exact bug we're guarding against");
        result.All(r => r.UserId == UserA).Should().BeTrue(
            "all returned candidates must belong to the active user");
    }

    [Fact]
    public async Task GetReviewCandidatesAsync_DoesNotReturnOtherUsersReviewCandidates()
    {
        // Arrange
        SeedProgressForUser(UserA, "w1");
        SeedProgressForUser(UserB, "w2");
        SetActiveProfile(UserA);

        // Act
        var result = await _sut.GetReviewCandidatesAsync();

        // Assert
        result.Should().OnlyContain(r => r.UserId == UserA,
            "review candidates must be scoped to the active user");
    }

    // ── Guard: empty preferences should NOT crash ────────────────

    [Fact]
    public async Task GetProgressForWordsAsync_WhenNoActiveProfile_ReturnsEmptyGracefully()
    {
        // Arrange — no active profile set (preferences returns "")
        SeedProgressForUser(UserA, "w1");
        _mockPreferences
            .Setup(p => p.Get("active_profile_id", string.Empty))
            .Returns(string.Empty);

        // Act
        var result = await _sut.GetProgressForWordsAsync(
            new List<string> { "w1" });

        // Assert — should return empty, not throw
        result.Should().BeEmpty(
            "with no active profile and no explicit userId, results should be empty (not crash)");
    }
}
