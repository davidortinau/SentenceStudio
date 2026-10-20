using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeProductionJourneyTests
{
    [Fact]
    public void ExplicitRegistration_ReplacesFivePlanAndQuizPortsWithoutEnablingSyncOrMedia()
    {
        var services = new ServiceCollection();
        services.AddSentenceStudioNativeSlice();
        services.AddSentenceStudioNativeProductionAdapters();

        AssertPort<INativePlanCreation, NativePlanCreationAdapter>();
        AssertPort<INativeQuizAttemptCommitter, NativeQuizAttemptCommitterAdapter>();
        AssertPort<INativeQuizChoices, NativeQuizChoicesAdapter>();
        AssertPort<INativeQuizEligibility, NativeQuizEligibilityAdapter>();
        AssertPort<INativeQuizGrader, NativeQuizGraderAdapter>();
        var readiness = Assert.Single(services,
            descriptor => descriptor.ServiceType == typeof(INativeSyncReadiness));
        Assert.NotNull(readiness.ImplementationFactory);
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(OwnerBoundNativeSyncBearerHandler));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(INativeQuizMedia)
            && descriptor.ImplementationType == typeof(UnavailableNativeQuizMedia));

        services.AddOwnerBoundNativeSyncReadiness();
        AssertPort<INativeSyncReadiness, OwnerBoundNativeSyncReadiness>();

        void AssertPort<TPort, TAdapter>()
        {
            var registration = Assert.Single(services,
                descriptor => descriptor.ServiceType == typeof(TPort));
            Assert.Equal(typeof(TAdapter), registration.ImplementationType);
        }
    }

    [Fact]
    public async Task ProductionAdapters_CreateGradeBothModesCompleteAndRestartOnRealSqlite()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);

        var signedIn = await fixture.Auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");
        Assert.Equal(NativeDestination.Dashboard, signedIn.Destination);
        var generated = await fixture.Dashboard.LoadAsync();
        Assert.Equal(NativePhase.Ready, generated.Phase);
        Assert.Equal(generated.Cards.Select(card => card.Id),
            (await fixture.Dashboard.GenerateAsync()).Cards.Select(card => card.Id));
        Assert.False(fixture.LastPlanGenerationAllowedWrites);
        var card = Assert.Single(generated.Cards);
        var launch = await fixture.Dashboard.SelectAssignedQuizAsync(card.Id);
        Assert.NotNull(launch);

        using var quiz = fixture.Quiz;
        var state = await quiz.OpenAsync(launch!);
        var multipleChoice = 0;
        var text = 0;
        var committed = 0;
        for (var turn = 0; turn < 100
            && state.Destination != NativeDestination.TodaysPlan; turn++)
        {
            if (state.Phase == NativePhase.Summary)
            {
                state = multipleChoice > 0 && text > 0
                    ? await quiz.DoneAsync()
                    : await quiz.ContinueRoundAsync();
                continue;
            }

            Assert.Equal(NativePhase.Ready, state.Phase);
            Assert.True(state.CanSubmit);
            var word = await WordAsync(fixture, state.Prompt!.WordId);
            var answer = state.Prompt.IsTargetLanguagePrompt
                ? word.NativeLanguageTerm! : word.TargetLanguageTerm!;
            if (state.Mode == "MultipleChoice")
            {
                multipleChoice++;
                Assert.Contains(state.Choices, option => option.Text == answer);
            }
            else
            {
                Assert.Equal("Text", state.Mode);
                text++;
            }

            var feedback = await quiz.AnswerAsync(answer);
            Assert.Equal(NativePhase.Feedback, feedback.Phase);
            Assert.Equal(committed, await fixture.AttemptCountAsync(
                NativeSliceFixture.UserA));
            state = await quiz.NextAsync();
            committed++;
            Assert.Equal(committed, await fixture.AttemptCountAsync(
                NativeSliceFixture.UserA));
        }

        Assert.Equal(NativeDestination.TodaysPlan, state.Destination);
        Assert.True(multipleChoice > 0);
        Assert.True(text > 0);
        Assert.True(committed > 0);
        var rows = await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id);
        Assert.True(rows.Item.IsCompleted);
        Assert.Equal(ActivitySessionStatus.Completed, rows.Session!.Status);
        var completedAt = rows.Item.CompletedAt;

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(committed, await db.ActivitySessions.AsNoTracking()
                .CountAsync(session => session.UserId == NativeSliceFixture.UserA
                    && session.ActivityType
                        == NativeQuizAttemptCommitterAdapter.ReceiptActivityType));
            Assert.Equal(committed, await db.VocabularyLearningContexts.AsNoTracking()
                .CountAsync(context => db.VocabularyProgresses.Any(progress =>
                    progress.Id == context.VocabularyProgressId
                    && progress.UserId == NativeSliceFixture.UserA)));
            Assert.Equal(0, await db.VocabularyProgresses.AsNoTracking()
                .CountAsync(progress => progress.UserId == NativeSliceFixture.UserB));
        }

        fixture.Restart();
        Assert.Equal(NativeDestination.Dashboard,
            (await fixture.Auth.RestoreAsync()).Destination);
        var restored = await fixture.Dashboard.LoadAsync();
        Assert.True(Assert.Single(restored.Cards).IsCompleted);
        var replay = await fixture.Dashboard.SelectAssignedQuizAsync(card.Id);
        Assert.NotNull(replay);
        using (var reopened = fixture.Quiz)
            Assert.NotEqual(NativePhase.Blocked, (await reopened.OpenAsync(
                replay with { IsRepractice = true })).Phase);
        Assert.Equal(completedAt,
            (await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id)).Item.CompletedAt);
        Assert.Equal(committed,
            await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Equal(NativePhase.Ready, (await fixture.Dashboard.LoadAsync()).Phase);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task RealDeterministicPlanner_CreatesAndResumesQuizUnderSqliteWriterTransaction()
    {
        using var fixture = new NativeSliceFixture(
            productionAdapters: true, realPlanGenerator: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 8);
        fixture.Activate(NativeSliceFixture.UserA);

        var dashboard = await fixture.Dashboard.GenerateAsync();
        Assert.Equal(NativePhase.Ready, dashboard.Phase);
        var card = Assert.Single(dashboard.Cards,
            item => item.Activity == PlanActivityType.VocabularyReview);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(card.Id))!;
        using (var quiz = fixture.Quiz)
        {
            var question = await quiz.OpenAsync(launch);
            Assert.Equal(NativePhase.Ready, question.Phase);
            var word = await WordAsync(fixture, question.Prompt!.WordId);
            Assert.Equal(NativePhase.Feedback,
                (await quiz.AnswerAsync(word.NativeLanguageTerm!)).Phase);
            Assert.Equal(NativePhase.Ready, (await quiz.NextAsync()).Phase);
            Assert.Equal(1, await fixture.AttemptCountAsync(
                NativeSliceFixture.UserA));
            Assert.Equal(NativeDestination.TodaysPlan,
                (await quiz.BackAsync()).Destination);
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        Assert.False(Assert.Single(
            (await fixture.Dashboard.LoadAsync()).Cards,
            item => item.Id == card.Id).IsCompleted);
        using var resumed = fixture.Quiz;
        Assert.Equal(NativePhase.ResumeChoice,
            (await resumed.OpenAsync(launch)).Phase);
        Assert.Equal(2, (await resumed.ResumeAsync()).CurrentTurn);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task CompletionFailureRollsBackPlanAndSessionTogether_ThenRetriesOnce()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        fixture.Activate(NativeSliceFixture.UserA);
        var card = Assert.Single((await fixture.Dashboard.GenerateAsync()).Cards);
        using var quiz = fixture.Quiz;
        var state = await quiz.OpenAsync(
            (await fixture.Dashboard.SelectAssignedQuizAsync(card.Id))!);
        for (var turn = 0; turn < 2; turn++)
        {
            Assert.Equal(NativePhase.Ready, state.Phase);
            var word = await WordAsync(fixture, state.Prompt!.WordId);
            await quiz.AnswerAsync(word.NativeLanguageTerm!);
            state = await quiz.NextAsync();
        }
        Assert.Equal(NativePhase.Summary, state.Phase);
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER FailNativeQuizCompletion
                BEFORE UPDATE OF Status ON ActivitySession
                WHEN NEW.Status = 'Completed'
                BEGIN SELECT RAISE(ABORT, 'injected completion failure'); END;
                """);
        }
        var failedCompletion = await quiz.DoneAsync();
        Assert.Equal(NativePhase.Error, failedCompletion.Phase);
        Assert.Equal(NativeDestination.QuizSummary, failedCompletion.Destination);
        Assert.Equal(2, failedCompletion.Results.Length);
        Assert.All(failedCompletion.Results, result =>
            Assert.StartsWith($"word-{NativeSliceFixture.UserA}-", result.WordId));
        var rolledBack = await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id);
        Assert.False(rolledBack.Item.IsCompleted);
        Assert.Equal(ActivitySessionStatus.InProgress, rolledBack.Session!.Status);
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailNativeQuizCompletion;");
        }
        Assert.Equal(NativeDestination.TodaysPlan,
            (await quiz.DoneAsync()).Destination);
        var completed = await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id);
        Assert.True(completed.Item.IsCompleted);
        Assert.Equal(ActivitySessionStatus.Completed, completed.Session!.Status);
        await quiz.DoneAsync();
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        Assert.Equal(completed.Item.CompletedAt,
            (await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id)).Item.CompletedAt);
    }

    [Fact]
    public async Task OfflineAndQ25bPersistLocallyUntilAnOwnerBoundSyncIsConfirmed()
    {
        using var fixture = new NativeSliceFixture(productionAdapters: true);
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Sync.Outcome = NativeSyncOutcome.Offline;
        var signedIn = await fixture.Auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");
        Assert.Equal(NativeDestination.Dashboard, signedIn.Destination);
        Assert.True(signedIn.HasPendingSync);
        var card = Assert.Single((await fixture.Dashboard.GenerateAsync()).Cards);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(card.Id))!;
        using (var quiz = fixture.Quiz)
        {
            var question = await quiz.OpenAsync(launch);
            var word = await WordAsync(fixture, question.Prompt!.WordId);
            Assert.Equal(NativePhase.Feedback,
                (await quiz.AnswerAsync(word.NativeLanguageTerm!)).Phase);
            Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
            fixture.FaultSessions.FailNextSnapshot = true;
            Assert.Equal(NativePhase.Error, (await quiz.NextAsync()).Phase);
            Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using (var resumed = fixture.Quiz)
        {
            Assert.Equal(NativePhase.ResumeChoice,
                (await resumed.OpenAsync(launch)).Phase);
            var state = await resumed.ResumeAsync();
            Assert.Equal(NativePhase.Ready, state.Phase);
            Assert.Equal(2, state.CurrentTurn);
            Assert.True(state.HasPendingSync);
            Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
            fixture.Sync.Outcome = NativeSyncOutcome.Unconfirmed;
            Assert.True((await resumed.RetrySyncAsync()).HasPendingSync);
        }
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    private static async Task<VocabularyWord> WordAsync(
        NativeSliceFixture fixture, string wordId)
    {
        using var scope = fixture.Scope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyWords.AsNoTracking().SingleAsync(word => word.Id == wordId);
    }
}
