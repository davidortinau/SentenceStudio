using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SentenceStudio.Services.Progress;
using SentenceStudio.Services.Timer;

namespace SentenceStudio.UnitTests.Services;

public sealed class ActivityTimerValidatedAdHocTests
{
    [Fact]
    public async Task ValidatedStart_PropagatesExplicitUserAndVocabularyIds()
    {
        var progress = new Mock<IProgressService>(MockBehavior.Strict);
        var expectedWordIds = new[] { "word-a", "word-b" };
        progress.Setup(service => service.StartAdHocSessionAsync(
                "user-a",
                PlanActivityType.VocabularyReview,
                "resource-a",
                "skill-a",
                It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(expectedWordIds)),
                10,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("adhoc-owned");
        var timer = CreateTimer(progress.Object);

        var lease = await timer.StartValidatedSessionAsync(new ActivityTimerStartRequest(
            "user-a",
            PlanActivityType.VocabularyReview,
            PlanItemId: null,
            "resource-a",
            "skill-a",
            expectedWordIds));

        lease.Should().NotBeNull();
        lease!.UserId.Should().Be("user-a");
        lease.ActivityId.Should().Be("adhoc-owned");
        timer.CurrentActivityId.Should().Be("adhoc-owned");
        progress.VerifyAll();
        timer.CancelSession(lease);
    }

    [Fact]
    public async Task RefusedValidatedStart_DoesNotStartTimer()
    {
        var progress = new Mock<IProgressService>(MockBehavior.Strict);
        progress.Setup(service => service.StartAdHocSessionAsync(
                "user-a",
                PlanActivityType.VocabularyReview,
                "resource-a",
                "skill-a",
                It.IsAny<IReadOnlyCollection<string>>(),
                10,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        var timer = CreateTimer(progress.Object);

        var lease = await timer.StartValidatedSessionAsync(new ActivityTimerStartRequest(
            "user-a",
            PlanActivityType.VocabularyReview,
            PlanItemId: null,
            "resource-a",
            "skill-a",
            ["owned-word", "foreign-word"]));

        lease.Should().BeNull();
        timer.IsActive.Should().BeFalse();
        timer.IsRunning.Should().BeFalse();
        timer.CurrentActivityId.Should().BeNull();
        progress.VerifyAll();
    }

    [Fact]
    public async Task ValidatedPlanStart_LoadsOnlyExplicitValidatedPlanItem()
    {
        var progress = new Mock<IProgressService>(MockBehavior.Strict);
        progress.Setup(service => service.ValidatePlanItemAsync(
                "user-a",
                "plan-vocab",
                PlanActivityType.VocabularyReview,
                "resource-a",
                "skill-a",
                It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "word-a" })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidatedPlanItemProgress("plan-vocab", 3, 10));
        var timer = CreateTimer(progress.Object);

        var lease = await timer.StartValidatedSessionAsync(new ActivityTimerStartRequest(
            "user-a",
            PlanActivityType.VocabularyReview,
            "plan-vocab",
            "resource-a",
            "skill-a",
            ["word-a"]));

        lease.Should().NotBeNull();
        timer.CurrentActivityId.Should().Be("plan-vocab");
        timer.ElapsedTime.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(3));
        timer.IsRunning.Should().BeTrue();
        progress.VerifyAll();
        timer.CancelSession(lease!);
    }

    [Fact]
    public async Task OlderLease_CannotPauseOrStopNewerSessionInSameScope()
    {
        var progress = new Mock<IProgressService>(MockBehavior.Strict);
        progress.SetupSequence(service => service.StartAdHocSessionAsync(
                "user-a",
                PlanActivityType.VocabularyReview,
                null,
                null,
                It.IsAny<IReadOnlyCollection<string>>(),
                10,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("adhoc-first")
            .ReturnsAsync("adhoc-second");
        var timer = CreateTimer(progress.Object);

        var first = await timer.StartValidatedSessionAsync(Request("user-a", "word-a"));
        var second = await timer.StartValidatedSessionAsync(Request("user-a", "word-b"));

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        timer.Pause(first!).Should().BeFalse();
        timer.StopSession(first!).Should().Be(TimeSpan.Zero);
        timer.CurrentActivityId.Should().Be("adhoc-second");
        timer.IsRunning.Should().BeTrue();

        timer.CancelSession(second!);
    }

    [Fact]
    public async Task OutOfOrderStarts_KeepNewestAndDiscardStaleCompletion()
    {
        var firstCompletion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var progress = new Mock<IProgressService>(MockBehavior.Strict);
        progress.Setup(service => service.StartAdHocSessionAsync(
                "user-a",
                PlanActivityType.VocabularyReview,
                null,
                null,
                It.IsAny<IReadOnlyCollection<string>>(),
                10,
                It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref callCount) == 1
                ? firstCompletion.Task
                : secondCompletion.Task);
        progress.Setup(service => service.DiscardAdHocSessionAsync(
                "user-a",
                "adhoc-stale",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var timer = CreateTimer(progress.Object);

        var firstStart = timer.StartValidatedSessionAsync(Request("user-a", "word-a"));
        var secondStart = timer.StartValidatedSessionAsync(Request("user-a", "word-b"));

        secondCompletion.SetResult("adhoc-newest");
        var newestLease = await secondStart;
        firstCompletion.SetResult("adhoc-stale");
        var staleLease = await firstStart;

        newestLease.Should().NotBeNull();
        staleLease.Should().BeNull();
        timer.CurrentActivityId.Should().Be("adhoc-newest");
        timer.IsRunning.Should().BeTrue();
        progress.Verify(service => service.DiscardAdHocSessionAsync(
            "user-a",
            "adhoc-stale",
            It.IsAny<CancellationToken>()), Times.Once);

        timer.CancelSession(newestLease!);
    }

    [Fact]
    public async Task ValidatedStart_CanceledBeforePersistenceCompletes_DoesNotStartTimerOrSaveProgress()
    {
        var persistenceCompletion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var persistenceStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new Mock<IProgressService>(MockBehavior.Strict);
        progress.Setup(service => service.StartAdHocSessionAsync(
                "user-a",
                PlanActivityType.VocabularyReview,
                null,
                null,
                It.IsAny<IReadOnlyCollection<string>>(),
                10,
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                persistenceStarted.SetResult();
                return persistenceCompletion.Task;
            });
        progress.Setup(service => service.DiscardAdHocSessionAsync(
                "user-a",
                "adhoc-canceled",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        using var timer = CreateTimer(progress.Object);
        using var startCts = new CancellationTokenSource();

        var startTask = timer.StartValidatedSessionAsync(
            Request("user-a", "word-a"),
            startCts.Token);
        await persistenceStarted.Task;

        timer.IsActive.Should().BeTrue();
        timer.IsRunning.Should().BeFalse();

        startCts.Cancel();
        persistenceCompletion.SetResult("adhoc-canceled");
        var lease = await startTask;

        lease.Should().BeNull();
        timer.IsActive.Should().BeFalse();
        timer.IsRunning.Should().BeFalse();
        progress.Verify(service => service.DiscardAdHocSessionAsync(
            "user-a",
            "adhoc-canceled",
            It.IsAny<CancellationToken>()), Times.Once);
        progress.Verify(service => service.UpdatePlanItemProgressAsync(
            "user-a",
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
        progress.VerifyAll();
    }

    [Fact]
    public async Task ValidatedStart_AcceptThenCallerCancelsLeaseCleanup_StopsTimerWithoutProgressUpdate()
    {
        var progress = new Mock<IProgressService>(MockBehavior.Strict);
        progress.Setup(service => service.StartAdHocSessionAsync(
                "user-a",
                PlanActivityType.VocabularyReview,
                null,
                null,
                It.IsAny<IReadOnlyCollection<string>>(),
                10,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("adhoc-accepted-then-canceled");
        using var timer = CreateTimer(progress.Object);

        var lease = await timer.StartValidatedSessionAsync(Request("user-a", "word-a"));

        // Guards the VocabQuiz startCanceled-drops-lease race: a caller that observes
        // cancellation after accept must still cancel the accepted lease.
        lease.Should().NotBeNull();
        timer.IsActive.Should().BeTrue();
        timer.IsRunning.Should().BeTrue();

        timer.CancelSession(lease!);

        timer.IsActive.Should().BeFalse();
        timer.IsRunning.Should().BeFalse();
        timer.CurrentActivityId.Should().BeNull();
        progress.Verify(service => service.UpdatePlanItemProgressAsync(
            "user-a",
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
        progress.VerifyAll();
    }

    [Fact]
    public async Task Dispose_StopsRunningTimerAndClearsActiveSession()
    {
        var progress = new Mock<IProgressService>(MockBehavior.Strict);
        progress.Setup(service => service.StartAdHocSessionAsync(
                "user-a",
                PlanActivityType.VocabularyReview,
                null,
                null,
                It.IsAny<IReadOnlyCollection<string>>(),
                10,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("adhoc-running");
        var timer = CreateTimer(progress.Object);

        var lease = await timer.StartValidatedSessionAsync(Request("user-a", "word-a"));

        lease.Should().NotBeNull();
        timer.IsActive.Should().BeTrue();
        timer.IsRunning.Should().BeTrue();

        timer.Dispose();

        timer.IsActive.Should().BeFalse();
        timer.IsRunning.Should().BeFalse();
        timer.CurrentActivityId.Should().BeNull();
        progress.Verify(service => service.UpdatePlanItemProgressAsync(
            "user-a",
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
        progress.VerifyAll();
    }

    [Fact]
    public void ScopedRegistration_ProducesIndependentCircuitTimers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IActivityTimerService, ActivityTimerService>();
        using var provider = services.BuildServiceProvider();
        using var circuitA = provider.CreateScope();
        using var circuitB = provider.CreateScope();
        var timerA = circuitA.ServiceProvider.GetRequiredService<IActivityTimerService>();
        var timerB = circuitB.ServiceProvider.GetRequiredService<IActivityTimerService>();

        var leaseA = timerA.StartSession("UnpersistedTest");

        timerA.IsActive.Should().BeTrue();
        timerB.IsActive.Should().BeFalse();
        timerB.StopSession().Should().Be(TimeSpan.Zero);
        timerA.IsActive.Should().BeTrue();

        timerA.CancelSession(leaseA);
    }

    private static ActivityTimerStartRequest Request(string userId, string wordId) => new(
        userId,
        PlanActivityType.VocabularyReview,
        PlanItemId: null,
        ResourceId: null,
        SkillId: null,
        VocabularyWordIds: [wordId]);

    private static ActivityTimerService CreateTimer(IProgressService progress) =>
        new(progress, NullLogger<ActivityTimerService>.Instance);
}
