using SentenceStudio.Services.Spaced;
using Xunit;

namespace SentenceStudio.AppLib.Tests.Services.Spaced;

/// <summary>
/// Tests for SM-2 spaced repetition scheduler.
/// Pins the existing behavior from VocabularyProgressService to prevent regressions.
/// </summary>
public class Sm2SchedulerTests
{
    [Fact]
    public void Update_IncorrectAnswer_ResetsIntervalToOne()
    {
        // Arrange: Established card with interval 10 days
        double easeFactor = 2.3;
        int interval = 10;
        int repetitions = 3;
        int quality = 1; // Incorrect

        // Act
        var result = Sm2Scheduler.Update(easeFactor, interval, repetitions, quality);

        // Assert
        Assert.Equal(1, result.Interval);
        Assert.True(result.EaseFactor >= 1.3); // Floor at 1.3
        Assert.True(result.EaseFactor < easeFactor); // Decreased
    }

    [Fact]
    public void Update_IncorrectAnswer_DecreasesEaseFactorWithFloor()
    {
        // Arrange: Card at minimum ease factor
        double easeFactor = 1.4;
        int interval = 5;
        int repetitions = 2;
        int quality = 0; // Incorrect

        // Act
        var result = Sm2Scheduler.Update(easeFactor, interval, repetitions, quality);

        // Assert
        Assert.Equal(1.3, result.EaseFactor); // Floor enforced
        Assert.Equal(1, result.Interval);
    }

    [Fact]
    public void Update_FirstCorrectAnswer_SetsIntervalToSix()
    {
        // Arrange: New card (interval = 1)
        double easeFactor = 2.5;
        int interval = 1;
        int repetitions = 0;
        int quality = 5; // Correct, perfect

        // Act
        var result = Sm2Scheduler.Update(easeFactor, interval, repetitions, quality);

        // Assert
        Assert.Equal(6, result.Interval);
        Assert.True(result.EaseFactor >= 2.5); // Increased or unchanged
    }

    [Fact]
    public void Update_SubsequentCorrectAnswer_MultipliesIntervalByEaseFactor()
    {
        // Arrange: Established card (interval > 1)
        double easeFactor = 2.5;
        int interval = 6;
        int repetitions = 1;
        int quality = 4; // Correct, good

        // Act
        var result = Sm2Scheduler.Update(easeFactor, interval, repetitions, quality);

        // Assert
        Assert.Equal(15, result.Interval); // 6 * 2.5 = 15
        Assert.True(result.EaseFactor >= easeFactor); // Should stay at 2.5 cap
        Assert.Equal(2.5, result.EaseFactor); // Capped at 2.5
    }

    [Fact]
    public void Update_CorrectAnswer_IncreasesEaseFactorWithCap()
    {
        // Arrange: Card already at max ease factor
        double easeFactor = 2.5;
        int interval = 10;
        int repetitions = 5;
        int quality = 5; // Correct, perfect

        // Act
        var result = Sm2Scheduler.Update(easeFactor, interval, repetitions, quality);

        // Assert
        Assert.Equal(2.5, result.EaseFactor); // Capped at 2.5
    }

    [Fact]
    public void Update_IntervalCappedAtMaximum()
    {
        // Arrange: Very large interval approaching cap
        double easeFactor = 2.5;
        int interval = 360; // Close to 365-day cap
        int repetitions = 10;
        int quality = 5; // Correct

        // Act
        var result = Sm2Scheduler.Update(easeFactor, interval, repetitions, quality);

        // Assert
        Assert.True(result.Interval <= 365); // Capped at 1 year
    }

    [Fact]
    public void Update_DueDateIsInFuture()
    {
        // Arrange
        double easeFactor = 2.5;
        int interval = 6;
        int repetitions = 1;
        int quality = 4;
        var beforeUpdate = DateTime.UtcNow;

        // Act
        var result = Sm2Scheduler.Update(easeFactor, interval, repetitions, quality);

        // Assert
        Assert.True(result.DueDate > beforeUpdate);
        Assert.True(result.DueDate >= beforeUpdate.AddDays(result.Interval - 1)); // Allow for execution time
    }

    [Fact]
    public void Update_QualityThreeOrAbove_TreatedAsCorrect()
    {
        // Arrange: Quality 3 is the minimum "correct" threshold
        double easeFactor = 2.5;
        int interval = 1;
        int repetitions = 0;
        int quality = 3; // Minimal passing

        // Act
        var result = Sm2Scheduler.Update(easeFactor, interval, repetitions, quality);

        // Assert
        Assert.Equal(6, result.Interval); // Same as correct answer
    }

    [Fact]
    public void Update_QualityTwoOrBelow_TreatedAsIncorrect()
    {
        // Arrange: Quality 2 is below the "correct" threshold
        double easeFactor = 2.5;
        int interval = 10;
        int repetitions = 3;
        int quality = 2; // Partial recall, still incorrect

        // Act
        var result = Sm2Scheduler.Update(easeFactor, interval, repetitions, quality);

        // Assert
        Assert.Equal(1, result.Interval); // Same as incorrect answer
    }

    [Theory]
    [InlineData(2.5, 1, 0, 5, 6)]       // First correct: 1 → 6
    [InlineData(2.5, 6, 1, 5, 15)]      // Second correct: 6 * 2.5 = 15
    [InlineData(2.5, 15, 2, 5, 37)]     // Third correct: 15 * 2.5 = 37 (rounded)
    [InlineData(2.5, 10, 3, 1, 1)]      // Incorrect: reset to 1
    [InlineData(1.3, 1, 0, 0, 1)]       // Incorrect at floor: stays 1
    public void Update_KnownSequences_ProduceExpectedIntervals(
        double easeFactor, int interval, int repetitions, int quality, int expectedInterval)
    {
        // Act
        var result = Sm2Scheduler.Update(easeFactor, interval, repetitions, quality);

        // Assert
        Assert.Equal(expectedInterval, result.Interval);
    }
}
