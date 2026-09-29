using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Services.Timer;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeProductionSyncInterleavingTests
{
    [Fact]
    public async Task SyncBeforeQuizCommit_CannotAcknowledgeTheLaterDurableAttempt()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        Assert.False((await dashboard.RefreshAsync()).HasPendingSync);
        var card = Assert.Single((await dashboard.GenerateAsync()).Cards);
        Assert.False((await dashboard.RefreshAsync()).HasPendingSync);

        var launch = (await dashboard.SelectAssignedQuizAsync(card.Id))!;
        using var scope = fixture.Scope();
        var delayed = new DelayedCommitter(scope.ServiceProvider
            .GetRequiredService<INativeQuizAttemptCommitter>());
        using var quiz = NewPresenter(fixture, delayed);
        var question = await quiz.OpenAsync(launch);
        await quiz.AnswerAsync(question.Choices[0].Text);
        var pendingNext = quiz.NextAsync();
        await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await dashboard.RefreshAsync()).HasPendingSync);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        delayed.Release.TrySetResult();
        var advanced = await pendingNext;
        Assert.Equal(NativePhase.Ready, advanced.Phase);
        Assert.True(advanced.HasPendingSync);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.True((await dashboard.LoadAsync()).HasPendingSync);
        fixture.Sync.Outcome = NativeSyncOutcome.Unconfirmed;
        Assert.True((await dashboard.RefreshAsync()).HasPendingSync);
        fixture.Sync.Outcome = NativeSyncOutcome.Confirmed;
        Assert.False((await dashboard.RefreshAsync()).HasPendingSync);
    }

    [Fact]
    public async Task SyncBeforePlanInsert_CannotClearItsLaterPendingMarker()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var control = fixture.Dashboard;
        Assert.False((await control.RefreshAsync()).HasPendingSync);

        using var scope = fixture.Scope();
        var services = scope.ServiceProvider;
        var delayed = new DelayedPlanCreation(
            services.GetRequiredService<INativePlanCreation>());
        var dashboard = new NativeDashboardPresenter(
            services.GetRequiredService<NativePlanLeaseFactory>(),
            services.GetRequiredService<DeviceUserScopeProvider>(),
            services.GetRequiredService<IPreferencesService>(),
            services.GetRequiredService<INativeSyncReadiness>(),
            delayed,
            services.GetRequiredService<IActivitySessionService>(),
            services.GetRequiredService<NativePendingWrites>(),
            services.GetRequiredService<ILogger<NativeDashboardPresenter>>());

        var generating = dashboard.GenerateAsync();
        await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await control.RefreshAsync()).HasPendingSync);
        delayed.Release.TrySetResult();
        Assert.Equal(NativePhase.Ready, (await generating).Phase);
        Assert.True((await control.LoadAsync()).HasPendingSync);
        Assert.Single(await services.GetRequiredService<ApplicationDbContext>()
            .DailyPlans.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Q25bAfterPriorConfirmation_RestoresPendingFromDurableReceipt()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var dashboard = fixture.Dashboard;
        var card = Assert.Single((await dashboard.GenerateAsync()).Cards);
        Assert.False((await dashboard.RefreshAsync()).HasPendingSync);
        var launch = (await dashboard.SelectAssignedQuizAsync(card.Id))!;
        using (var quiz = fixture.Quiz)
        {
            var question = await quiz.OpenAsync(launch);
            Assert.Equal(NativePhase.Feedback,
                (await quiz.AnswerAsync(question.Choices[0].Text)).Phase);
        }

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var session = await db.ActivitySessions.AsNoTracking().SingleAsync(
                row => row.UserId == NativeSliceFixture.UserA
                    && row.ActivityType == "VocabularyReview");
            var pending = NativeQuizStoredState.Deserialize(session.StateJson);
            await scope.ServiceProvider.GetRequiredService<INativeQuizAttemptCommitter>()
                .CommitAsync(NativeSliceFixture.UserA, pending.LaunchContextKey,
                    pending.TurnId, pending.PendingAttempt!, CancellationToken.None);
        }
        Assert.False((await dashboard.LoadAsync()).HasPendingSync);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice,
            (await resumed.OpenAsync(launch)).Phase);
        var next = await resumed.ResumeAsync();
        Assert.Equal(NativePhase.Ready, next.Phase);
        Assert.True(next.HasPendingSync);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        fixture.Sync.Outcome = NativeSyncOutcome.Unconfirmed;
        Assert.True((await resumed.RetrySyncAsync()).HasPendingSync);
    }

    private static NativeQuizPresenter NewPresenter(
        NativeSliceFixture fixture, INativeQuizAttemptCommitter committer)
    {
        using var scope = fixture.Scope();
        var services = scope.ServiceProvider;
        return new NativeQuizPresenter(
            services.GetRequiredService<NativePlanLeaseFactory>(),
            services.GetRequiredService<DeviceUserScopeProvider>(),
            services.GetRequiredService<IPreferencesService>(),
            services.GetRequiredService<UserProfileRepository>(),
            services.GetRequiredService<LearningResourceRepository>(),
            services.GetRequiredService<VocabularyProgressRepository>(),
            services.GetRequiredService<VocabQuizLaunchValidator>(),
            services.GetRequiredService<IProgressService>(),
            services.GetRequiredService<IActivitySessionService>(),
            services.GetRequiredService<VocabularyQuizPreferences>(),
            services.GetRequiredService<INativeQuizChoices>(),
            services.GetRequiredService<INativeQuizEligibility>(),
            services.GetRequiredService<INativeQuizGrader>(),
            services.GetRequiredService<INativeQuizMedia>(),
            committer,
            services.GetRequiredService<INativeSyncReadiness>(),
            services.GetRequiredService<NativePendingWrites>(),
            services.GetRequiredService<ILogger<NativeQuizPresenter>>(),
            services.GetRequiredService<ILogger<ActivityTimerService>>());
    }

    private sealed class DelayedCommitter(INativeQuizAttemptCommitter inner)
        : INativeQuizAttemptCommitter
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<NativeQuizCommitReceipt?> FindAsync(
            string userId, string launchContextKey, string turnId,
            CancellationToken cancellationToken) =>
            inner.FindAsync(userId, launchContextKey, turnId, cancellationToken);

        public async Task<NativeQuizCommitReceipt> CommitAsync(
            string userId, string launchContextKey, string turnId,
            VocabularyAttempt attempt, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.CommitAsync(
                userId, launchContextKey, turnId, attempt, cancellationToken);
        }
    }

    private sealed class DelayedPlanCreation(INativePlanCreation inner)
        : INativePlanCreation
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<NativePlanCreateResult> CreateIfAbsentAsync(
            string userId, DateOnly localDate, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.CreateIfAbsentAsync(
                userId, localDate, cancellationToken);
        }
    }
}
