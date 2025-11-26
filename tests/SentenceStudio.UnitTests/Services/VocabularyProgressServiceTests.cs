using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

/// <summary>
/// Unit tests for VocabularyProgressService
/// Tests the enhanced progress tracking system with mastery scores and SRS scheduling
/// </summary>
public class VocabularyProgressServiceTests
{
    private readonly Mock<VocabularyProgressRepository> _mockProgressRepo;
    private readonly Mock<VocabularyLearningContextRepository> _mockContextRepo;
    private readonly Mock<ILogger<VocabularyProgressService>> _mockLogger;
    private readonly VocabularyProgressService _sut; // System Under Test

    public VocabularyProgressServiceTests()
    {
        _mockProgressRepo = new Mock<VocabularyProgressRepository>();
        _mockContextRepo = new Mock<VocabularyLearningContextRepository>();
        _mockLogger = new Mock<ILogger<VocabularyProgressService>>();
        _sut = new VocabularyProgressService(
            _mockProgressRepo.Object,
            _mockContextRepo.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task RecordAttemptAsync_WithCorrectAnswer_ShouldIncreaseMasteryScore()
    {
        // Arrange
        var wordId = 1;
        var existingProgress = new VocabularyProgress
        {
            VocabularyWordId = wordId,
            MasteryScore = 0.5f,
            TotalAttempts = 5,
            CorrectAttempts = 2,
            CurrentPhase = LearningPhase.Recognition
        };

        _mockProgressRepo
            .Setup(r => r.GetProgressAsync(wordId, It.IsAny<int>()))
            .ReturnsAsync(existingProgress);

        _mockProgressRepo
            .Setup(r => r.SaveAsync(It.IsAny<VocabularyProgress>()))
            .ReturnsAsync((VocabularyProgress p) => p);

        _mockContextRepo
            .Setup(r => r.SaveAsync(It.IsAny<VocabularyLearningContext>()))
            .ReturnsAsync((VocabularyLearningContext c) => c);

        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = 1,
            WasCorrect = true,
            DifficultyWeight = 1.0f,
            InputMode = InputMode.MultipleChoice.ToString(),
            Activity = "VocabularyQuiz",
            ContextType = "Isolated"
        };

        // Act
        var result = await _sut.RecordAttemptAsync(attempt);

        // Assert
        result.Should().NotBeNull("service should return updated progress");
        result.MasteryScore.Should().BeGreaterThan(0.5f, "correct attempts should increase mastery");
        result.TotalAttempts.Should().Be(6, "total attempts should increment");
        result.CorrectAttempts.Should().Be(3, "correct attempts should increment");

        _mockProgressRepo.Verify(r => r.SaveAsync(It.IsAny<VocabularyProgress>()), Times.Once,
            "progress should be saved to repository");
    }

    [Fact]
    public async Task RecordAttemptAsync_WithIncorrectAnswer_ShouldDecreaseMasteryScore()
    {
        // Arrange
        var wordId = 2;
        var existingProgress = new VocabularyProgress
        {
            VocabularyWordId = wordId,
            MasteryScore = 0.7f,
            TotalAttempts = 10,
            CorrectAttempts = 7,
            CurrentPhase = LearningPhase.Production
        };

        _mockProgressRepo
            .Setup(r => r.GetProgressAsync(wordId, It.IsAny<int>()))
            .ReturnsAsync(existingProgress);

        _mockProgressRepo
            .Setup(r => r.SaveAsync(It.IsAny<VocabularyProgress>()))
            .ReturnsAsync((VocabularyProgress p) => p);

        _mockContextRepo
            .Setup(r => r.SaveAsync(It.IsAny<VocabularyLearningContext>()))
            .ReturnsAsync((VocabularyLearningContext c) => c);

        var attempt = new VocabularyAttempt
        {
            VocabularyWordId = wordId,
            UserId = 1,
            WasCorrect = false,
            DifficultyWeight = 1.0f,
            InputMode = InputMode.Text.ToString()
        };

        // Act
        var result = await _sut.RecordAttemptAsync(attempt);

        // Assert
        result.MasteryScore.Should().BeLessThan(0.7f, "incorrect attempts should decrease mastery");
        result.TotalAttempts.Should().Be(11, "total attempts should increment even on failure");
        result.CorrectAttempts.Should().Be(7, "incorrect answers don't increase correct count");
    }

    [Fact]
    public async Task GetProgressAsync_WithExistingProgress_ShouldReturnProgress()
    {
        // Arrange
        var wordId = 3;
        var expectedProgress = new VocabularyProgress
        {
            VocabularyWordId = wordId,
            MasteryScore = 0.75f,
            CurrentPhase = LearningPhase.Production
        };

        _mockProgressRepo
            .Setup(r => r.GetProgressAsync(wordId, It.IsAny<int>()))
            .ReturnsAsync(expectedProgress);

        // Act
        var result = await _sut.GetProgressAsync(wordId);

        // Assert
        result.Should().NotBeNull();
        result.VocabularyWordId.Should().Be(wordId);
        result.MasteryScore.Should().Be(0.75f);
        result.CurrentPhase.Should().Be(LearningPhase.Production);
    }

    [Fact]
    public async Task GetAllProgressAsync_ShouldReturnAllUserProgress()
    {
        // Arrange
        var userId = 1;
        var allProgress = new List<VocabularyProgress>
        {
            new() { VocabularyWordId = 1, UserId = userId, MasteryScore = 0.5f },
            new() { VocabularyWordId = 2, UserId = userId, MasteryScore = 0.7f },
            new() { VocabularyWordId = 3, UserId = 2, MasteryScore = 0.9f } // Different user
        };

        _mockProgressRepo
            .Setup(r => r.ListAsync())
            .ReturnsAsync(allProgress);

        // Act
        var result = await _sut.GetAllProgressAsync(userId);

        // Assert
        result.Should().HaveCount(2, "should only return progress for requested user");
        result.Should().AllSatisfy(p => p.UserId.Should().Be(userId));
    }

    [Fact]
    public async Task GetReviewCandidatesAsync_ShouldReturnDueWords()
    {
        // Arrange
        var userId = 1;
        var allProgress = new List<VocabularyProgress>
        {
            new() { UserId = userId, NextReviewDate = DateTime.Now.AddDays(-1), MasteryScore = 0.5f }, // Due
            new() { UserId = userId, NextReviewDate = DateTime.Now.AddDays(1), MasteryScore = 0.6f },  // Not due
            new() { UserId = userId, NextReviewDate = DateTime.Now.AddDays(-2), MasteryScore = 0.9f }, // Known (excluded)
        };

        _mockProgressRepo
            .Setup(r => r.ListAsync())
            .ReturnsAsync(allProgress);

        // Act
        var result = await _sut.GetReviewCandidatesAsync(userId);

        // Assert
        result.Should().HaveCount(1, "should only return words that are due and not yet known");
        result[0].NextReviewDate.Should().BeBefore(DateTime.Now);
    }
}
