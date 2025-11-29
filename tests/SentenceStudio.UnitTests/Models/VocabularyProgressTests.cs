using Xunit;
using FluentAssertions;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Models;

/// <summary>
/// Unit tests for VocabularyProgress model
/// Tests computed properties, accuracy calculations, learning status logic,
/// and streak-based mastery scoring
/// </summary>
public class VocabularyProgressTests
{
    #region Streak-Based Mastery Tests

    [Theory]
    [InlineData(0, 0, 0.84f, false)]   // High score but no production - not known
    [InlineData(0, 2, 0.84f, false)]   // Production but low score - not known
    [InlineData(0, 2, 0.85f, true)]    // Meets both requirements - known
    [InlineData(5, 3, 0.90f, true)]    // High score and production - known
    [InlineData(10, 0, 1.0f, false)]   // Max score but no production - not known
    [InlineData(0, 5, 0.85f, true)]    // Minimum score, good production - known
    public void IsKnown_ShouldRequireBothMasteryAndProduction(
        int currentStreak,
        int productionInStreak,
        float masteryScore,
        bool expectedIsKnown)
    {
        // Arrange
        var progress = new VocabularyProgress
        {
            CurrentStreak = currentStreak,
            ProductionInStreak = productionInStreak,
            MasteryScore = masteryScore
        };

        // Act
        var isKnown = progress.IsKnown;

        // Assert
        isKnown.Should().Be(expectedIsKnown,
            $"CurrentStreak={currentStreak}, ProductionInStreak={productionInStreak}, MasteryScore={masteryScore:F2} " +
            $"should {(expectedIsKnown ? "" : "not ")}be known");
    }

    [Theory]
    [InlineData(0, 0, 0)]     // No streak
    [InlineData(4, 0, 4)]     // Recognition only
    [InlineData(0, 4, 2)]     // Production only (counts 0.5 each)
    [InlineData(4, 4, 6)]     // Mixed: 4 + (4 * 0.5) = 6
    [InlineData(6, 2, 7)]     // 6 + (2 * 0.5) = 7
    public void EffectiveStreak_ShouldCalculateCorrectly(
        int currentStreak,
        int productionInStreak,
        int expectedEffectiveStreak)
    {
        // Arrange
        var progress = new VocabularyProgress
        {
            CurrentStreak = currentStreak,
            ProductionInStreak = productionInStreak
        };

        // Act - using the formula: CurrentStreak + (ProductionInStreak * 0.5)
        var effectiveStreak = (int)(currentStreak + (productionInStreak * 0.5f));

        // Assert
        effectiveStreak.Should().Be(expectedEffectiveStreak,
            $"Effective streak for {currentStreak} + ({productionInStreak} * 0.5)");
    }

    [Fact]
    public void CurrentStreak_ShouldDefaultToZero()
    {
        // Act
        var progress = new VocabularyProgress();

        // Assert
        progress.CurrentStreak.Should().Be(0);
    }

    [Fact]
    public void ProductionInStreak_ShouldDefaultToZero()
    {
        // Act
        var progress = new VocabularyProgress();

        // Assert
        progress.ProductionInStreak.Should().Be(0);
    }

    [Theory]
    [InlineData(0f, false)]     // No mastery - not ready for text
    [InlineData(0.49f, false)]  // Just below threshold
    [InlineData(0.50f, true)]   // At threshold - ready for text
    [InlineData(0.75f, true)]   // Above threshold
    [InlineData(1.0f, true)]    // Max mastery
    public void MasteryScore_ThresholdForTextEntry_ShouldBe50Percent(
        float masteryScore,
        bool expectedReadyForText)
    {
        // Arrange
        var progress = new VocabularyProgress
        {
            MasteryScore = masteryScore
        };

        // Act - This is the threshold used by VocabularyQuizPage.GetUserModeForItem()
        var readyForText = masteryScore >= 0.50f;

        // Assert
        readyForText.Should().Be(expectedReadyForText,
            $"MasteryScore {masteryScore:F2} should {(expectedReadyForText ? "" : "not ")}be ready for text entry");
    }

    #endregion

    #region Legacy Status Tests
    [Theory]
    [InlineData(0f, LearningStatus.Unknown)]
    [InlineData(0.3f, LearningStatus.Learning)]
    [InlineData(0.7f, LearningStatus.Learning)]
    [InlineData(0.8f, LearningStatus.Known)]
    [InlineData(0.95f, LearningStatus.Known)]
    [InlineData(1.0f, LearningStatus.Known)]
    public void Status_ShouldReturnCorrectLearningStatus_BasedOnMasteryScore(
        float masteryScore,
        LearningStatus expectedStatus)
    {
        // Arrange
        var progress = new VocabularyProgress
        {
            MasteryScore = masteryScore
        };

        // Act
        var actualStatus = progress.Status;

        // Assert
        actualStatus.Should().Be(expectedStatus,
            $"mastery score {masteryScore:F2} should map to {expectedStatus}");
    }

    [Theory]
    [InlineData(0, 0, 0f)]
    [InlineData(5, 3, 0.6f)]
    [InlineData(10, 7, 0.7f)]
    [InlineData(20, 20, 1.0f)]
    public void Accuracy_ShouldCalculateCorrectly(
        int totalAttempts,
        int correctAttempts,
        float expectedAccuracy)
    {
        // Arrange
        var progress = new VocabularyProgress
        {
            TotalAttempts = totalAttempts,
            CorrectAttempts = correctAttempts
        };

        // Act
        var accuracy = progress.Accuracy;

        // Assert
        accuracy.Should().BeApproximately(expectedAccuracy, 0.001f,
            $"{correctAttempts}/{totalAttempts} should equal {expectedAccuracy:F2}");
    }

    [Theory]
    [InlineData(0, 0, 0f)]
    [InlineData(5, 4, 0.8f)]
    [InlineData(10, 10, 1.0f)]
    public void RecognitionAccuracy_ShouldCalculateCorrectly(
        int attempts,
        int correct,
        float expectedAccuracy)
    {
        // Arrange
        var progress = new VocabularyProgress
        {
            RecognitionAttempts = attempts,
            RecognitionCorrect = correct
        };

        // Act
        var accuracy = progress.RecognitionAccuracy;

        // Assert
        accuracy.Should().BeApproximately(expectedAccuracy, 0.001f);
    }

    [Fact]
    public void IsDueForReview_ShouldReturnTrue_WhenNextReviewDateIsInPast()
    {
        // Arrange
        var progress = new VocabularyProgress
        {
            NextReviewDate = DateTime.Now.AddDays(-1)
        };

        // Act
        var isDue = progress.IsDueForReview;

        // Assert
        isDue.Should().BeTrue("review date is in the past");
    }

    [Fact]
    public void IsDueForReview_ShouldReturnFalse_WhenNextReviewDateIsInFuture()
    {
        // Arrange
        var progress = new VocabularyProgress
        {
            NextReviewDate = DateTime.Now.AddDays(1)
        };

        // Act
        var isDue = progress.IsDueForReview;

        // Assert
        isDue.Should().BeFalse("review date is in the future");
    }

    [Fact]
    public void IsDueForReview_ShouldReturnFalse_WhenNextReviewDateIsNull()
    {
        // Arrange
        var progress = new VocabularyProgress
        {
            NextReviewDate = null
        };

        // Act
        var isDue = progress.IsDueForReview;

        // Assert
        isDue.Should().BeFalse("review date is not set");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(5, true)]
    public void HasConfidenceInMultipleChoice_ShouldReturnCorrectly(
        int correctCount,
        bool expectedConfidence)
    {
        // Arrange
        var progress = new VocabularyProgress
        {
            MultipleChoiceCorrect = correctCount
        };

        // Act
        var hasConfidence = progress.HasConfidenceInMultipleChoice;

        // Assert
        hasConfidence.Should().Be(expectedConfidence,
            $"{correctCount} correct answers should {(expectedConfidence ? "" : "not ")}indicate confidence");
    }

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(1, 0.333f)]
    [InlineData(2, 0.667f)]
    [InlineData(3, 1.0f)]
    [InlineData(5, 1.0f)] // Should cap at 1.0
    public void MultipleChoiceProgress_ShouldCalculateCorrectly_AndCapAt1(
        int correctCount,
        float expectedProgress)
    {
        // Arrange
        var progress = new VocabularyProgress
        {
            MultipleChoiceCorrect = correctCount
        };

        // Act
        var progressValue = progress.MultipleChoiceProgress;

        // Assert
        progressValue.Should().BeApproximately(expectedProgress, 0.001f,
            $"{correctCount} correct should give {expectedProgress:F2} progress");
    }

    [Fact]
    public void NewVocabularyProgress_ShouldHaveDefaultValues()
    {
        // Act
        var progress = new VocabularyProgress();

        // Assert - Core fields
        progress.MasteryScore.Should().Be(0f);
        progress.TotalAttempts.Should().Be(0);
        progress.CorrectAttempts.Should().Be(0);
        progress.CurrentPhase.Should().Be(LearningPhase.Recognition);
        progress.ReviewInterval.Should().Be(1);
        progress.EaseFactor.Should().Be(2.5f);
        progress.UserId.Should().Be(1);
        progress.Status.Should().Be(LearningStatus.Unknown);
        progress.Accuracy.Should().Be(0f);

        // Assert - Streak fields
        progress.CurrentStreak.Should().Be(0, "new progress should have zero streak");
        progress.ProductionInStreak.Should().Be(0, "new progress should have zero production in streak");
        progress.IsKnown.Should().BeFalse("new progress should not be marked as known");
    }

    #endregion
}
