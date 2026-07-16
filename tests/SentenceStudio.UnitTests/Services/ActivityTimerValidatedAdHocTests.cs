using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Services.Progress;
using SentenceStudio.Services.Timer;

namespace SentenceStudio.UnitTests.Services;

public sealed class ActivityTimerValidatedAdHocTests
{
    [Fact]
    public async Task ValidatedStart_PropagatesActualVocabularyIds()
    {
        var progress = new Mock<IProgressService>(MockBehavior.Strict);
        var expectedWordIds = new[] { "word-a", "word-b" };
        progress.Setup(service => service.StartAdHocSessionAsync(
                PlanActivityType.VocabularyReview,
                "resource-a",
                "skill-a",
                It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(expectedWordIds)),
                10,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("adhoc-owned");
        var timer = new ActivityTimerService(
            progress.Object,
            NullLogger<ActivityTimerService>.Instance);

        var started = await timer.StartValidatedSessionAsync(
            "VocabularyReview",
            activityId: null,
            "resource-a",
            "skill-a",
            expectedWordIds);

        started.Should().BeTrue();
        timer.IsActive.Should().BeTrue();
        timer.CurrentActivityId.Should().Be("adhoc-owned");
        progress.VerifyAll();
        timer.CancelSession();
    }

    [Fact]
    public async Task RefusedValidatedStart_DoesNotStartTimer()
    {
        var progress = new Mock<IProgressService>(MockBehavior.Strict);
        progress.Setup(service => service.StartAdHocSessionAsync(
                PlanActivityType.VocabularyReview,
                "resource-a",
                "skill-a",
                It.IsAny<IReadOnlyCollection<string>>(),
                10,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        var timer = new ActivityTimerService(
            progress.Object,
            NullLogger<ActivityTimerService>.Instance);

        var started = await timer.StartValidatedSessionAsync(
            "VocabularyReview",
            activityId: null,
            "resource-a",
            "skill-a",
            ["owned-word", "foreign-word"]);

        started.Should().BeFalse();
        timer.IsActive.Should().BeFalse();
        timer.IsRunning.Should().BeFalse();
        timer.CurrentActivityId.Should().BeNull();
        progress.VerifyAll();
    }
}
