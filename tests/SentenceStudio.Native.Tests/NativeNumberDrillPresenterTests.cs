using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Number Drill source contract (activities.md 1.3 and 12) over the shared
/// Numbers services against real SQLite.
/// </summary>
public sealed class NativeNumberDrillPresenterTests
{
    private const string Route = NativeActivityCatalog.NumberDrill;

    [Fact]
    public void Catalog_RegistersNumberDrillForItsPlanTypeWithoutTimerSkill()
    {
        var definition = NativeActivityCatalog.ForRoute(Route);
        Assert.NotNull(definition);
        Assert.Equal(PlanActivityType.NumberDrill, definition.TimerType);
        Assert.False(definition.TimerRecordsSkill);
        Assert.True(NativeActivityCatalog.IsImplemented(PlanActivityType.NumberDrill));
        Assert.Contains(NativeDashboardPresenter.ChooseOwnActivities, tile => tile.Route == Route);
    }

    [Theory]
    [InlineData(null, "TapTheCounter", false)]
    [InlineData(null, "ListenAndPlace", false)]
    [InlineData(null, "Disambiguate", true)]
    [InlineData("Counting", "TapTheCounter", true)]
    [InlineData("Money", "TapTheCounter", false)]
    [InlineData("Time", "ListenAndPlace", true)]
    [InlineData("Age", "ListenAndPlace", false)]
    [InlineData("Date", "ReadAndProduce", true)]
    public void ValidCombos_FollowTheSource(string? context, string mode, bool valid) =>
        Assert.Equal(valid, NativeNumberDrillPresenter.IsValidCombo(context, mode));

    [Theory]
    // The source helper re-adds underscores before counters, doubling them.
    [InlineData("5시 30분", "______시 ______분")]
    [InlineData("스물세 살", "___살")]
    [InlineData("", "___")]
    [InlineData("만 오천 원", "___")]
    public void SpacingHint_MatchesTheSourcePlaceholder(string answer, string hint) =>
        Assert.Equal(hint, NativeNumberDrillPresenter.SpacingHint(answer));

    [Fact]
    public async Task PlanCard_SetupStartsReadAndProduceRecordsOwnedAttemptsAndSummarizes()
    {
        using var fixture = new NativeSliceFixture();
        var (card, dashboard) = await SignInWithPlanAsync(fixture);
        var launch = await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromPlanCardAsync(card.Id);
        Assert.NotNull(launch);
        Assert.Equal(Route, launch.Route);
        Assert.Empty(launch.ResourceIds);
        using var drill = Presenter(fixture);

        var state = await drill.OpenAsync(launch);

        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(NativeNumberDrillStage.Setup, state.Stage);
        Assert.Equal(["Counting", "Time", "Age", "Money", "Date", "Ordinal"],
            state.Contexts.Select(c => c.Code).ToArray());
        Assert.Null(state.SelectedContext);
        Assert.Equal("ListenAndType", state.SelectedSubMode);
        Assert.Equal(10, state.ItemCount);
        // Source ignores the plan's DueOnly query: the checkbox starts off.
        Assert.False(state.DueOnly);
        Assert.Equal(["ListenAndType", "ReadAndProduce", "Disambiguate"],
            state.SubModes.Select(m => m.Code).ToArray());

        state = await drill.SelectContextAsync("Counting");
        Assert.Contains(state.SubModes, m => m.Code == "TapTheCounter");
        state = await drill.SelectSubModeAsync("TapTheCounter");
        state = await drill.SelectContextAsync("Time");
        // TapTheCounter is invalid for Time: reset to the first valid mode.
        Assert.Equal("ListenAndType", state.SelectedSubMode);
        Assert.Contains(state.SubModes, m => m.Code == "ListenAndPlace");
        state = await drill.ChangeItemCountAsync(-1);
        Assert.Equal(5, state.ItemCount);
        state = await drill.ChangeItemCountAsync(-1);
        Assert.Equal(5, state.ItemCount);
        for (var i = 0; i < 5; i++)
            state = await drill.ChangeItemCountAsync(+1);
        Assert.Equal(20, state.ItemCount);
        for (var i = 0; i < 3; i++)
            state = await drill.ChangeItemCountAsync(-1);
        state = await drill.SetDueOnlyAsync(true);
        state = await drill.SetDueOnlyAsync(false);
        await drill.SelectContextAsync("Counting");
        state = await drill.SelectSubModeAsync("ReadAndProduce");

        state = await drill.StartAsync();

        Assert.Equal(NativeNumberDrillStage.InSession, state.Stage);
        Assert.Equal(5, state.ItemTotal);
        Assert.Equal(0, state.ItemIndex);
        Assert.Equal("ReadAndProduce", state.Item!.SubModeCode);
        Assert.False(state.CanSubmitText);

        // Item 1: the bare digit value is accepted (source grader shortcut).
        drill.UpdateAnswer(Digits(state.Item.DisplayPrompt));
        state = await drill.SubmitAnswerAsync();
        Assert.Equal(NativePhase.Feedback, state.Phase);
        Assert.True(state.Feedback!.IsCorrect);
        Assert.Equal(1, state.CorrectCount);
        Assert.False(string.IsNullOrEmpty(state.Feedback.CanonicalAnswer));
        Assert.Equal(1, state.Feedback.ProgressTotal);

        // Item 2: wrong, then "I was correct" auto-advances.
        state = await drill.NextAsync();
        Assert.Equal(1, state.ItemIndex);
        state = await drill.SubmitAnswerAsync("틀림");
        Assert.False(state.Feedback!.IsCorrect);
        Assert.Equal(1, state.CorrectCount);
        state = await drill.OverrideAsync();
        Assert.Equal(2, state.ItemIndex);
        Assert.Equal(2, state.CorrectCount);

        // Items 3-5 wrong.
        for (var i = 2; i < 5; i++)
        {
            state = await drill.SubmitAnswerAsync("틀림");
            Assert.False(state.Feedback!.IsCorrect);
            Assert.Equal(i == 4, state.IsLastItem);
            state = await drill.NextAsync();
        }

        Assert.Equal(NativePhase.Summary, state.Phase);
        Assert.Equal(2, state.Summary!.CorrectCount);
        Assert.Equal(5, state.Summary.TotalItems);
        Assert.True(state.Summary.MedianLatencyMs >= 0);
        Assert.Equal(3, state.Summary.ErrorClassBreakdown.Sum(pair => pair.Value));

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var attempts = await db.NumberAttempts.AsNoTracking().ToListAsync();
            Assert.Equal(5, attempts.Count);
            Assert.All(attempts, attempt =>
            {
                Assert.Equal(NativeSliceFixture.UserA, attempt.UserProfileId);
                Assert.Equal("ko", attempt.LanguageCode);
                Assert.Equal("Counting", attempt.ContextCode);
            });
            // "I was correct" re-grades the stored attempt and its mastery (Captain, 2026-11-02).
            Assert.Equal(2, attempts.Count(attempt => attempt.IsCorrect));
            var overridden = Assert.Single(attempts, attempt => attempt.IsCorrect && attempt.UserAnswer == "틀림");
            Assert.False(string.IsNullOrEmpty(overridden.ErrorClass));
            var mastery = await db.NumberMasteryProgresses.AsNoTracking().ToListAsync();
            Assert.NotEmpty(mastery);
            Assert.All(mastery, row => Assert.Equal(NativeSliceFixture.UserA, row.UserProfileId));
            Assert.Equal(5, mastery.Sum(row => row.TotalCount));
            Assert.Equal(2, mastery.Sum(row => row.CorrectCount));
            Assert.False(await db.UserActivities.AnyAsync());
            Assert.False(await db.VocabularyLearningContexts.AnyAsync());
        }

        // Another round returns to Setup with the same choices.
        state = await drill.TryAgainAsync();
        Assert.Equal(NativeNumberDrillStage.Setup, state.Stage);
        Assert.Equal("Counting", state.SelectedContext);
        Assert.Equal("ReadAndProduce", state.SelectedSubMode);

        var exit = await drill.ExitAsync();
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Null(exit.ErrorKey);
        // Commands after Back are ignored.
        Assert.Equal(NativeDestination.TodaysPlan, (await drill.StartAsync()).Destination);
        var (row, _) = await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id);
        Assert.Equal(nameof(PlanActivityType.NumberDrill), row.ActivityType);
        Assert.Null(row.SkillId);
        Assert.Equal(NativePhase.Ready, (await dashboard.LoadAsync()).Phase);
    }

    [Fact]
    public async Task TapTheCounter_AutoAdvancesAndNextDuringThePauseDoesNotSkipAnItem()
    {
        using var fixture = new NativeSliceFixture();
        using var drill = await StartAsync(fixture, "Counting", "TapTheCounter");
        var state = drill.State;
        Assert.NotEmpty(state.Item!.Choices);

        state = await drill.TapChoiceAsync(state.Item.Choices[0]);
        Assert.Equal(1, state.ItemIndex);
        Assert.Equal(1, drillAttempts(fixture));

        drill.AutoAdvanceDelay = TimeSpan.FromSeconds(30);
        var pending = drill.TapChoiceAsync(state.Item!.Choices[0]);
        await WaitForAsync(() => drill.State.Phase == NativePhase.Feedback);
        state = await drill.NextAsync();
        Assert.Equal(2, state.ItemIndex);
        await drill.ExitAsync();
        await pending;
        Assert.Equal(2, drillAttempts(fixture));

        static int drillAttempts(NativeSliceFixture fixture)
        {
            using var scope = fixture.Scope();
            return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .NumberAttempts.Count();
        }
    }

    [Fact]
    public async Task Override_RegradesTheStoredAttemptAndMasteryThenAdvances()
    {
        using var fixture = new NativeSliceFixture();
        using var drill = await StartAsync(fixture, "Counting", "ReadAndProduce");
        drill.AutoAdvanceDelay = TimeSpan.FromSeconds(30);

        var state = await drill.SubmitAnswerAsync("틀림");
        Assert.False(state.Feedback!.IsCorrect);
        Assert.Equal(0, state.Feedback.ProgressCorrect);
        var pending = drill.OverrideAsync();
        await WaitForAsync(() => drill.State.Feedback?.Overridden == true);

        state = drill.State;
        Assert.True(state.Feedback!.IsCorrect);
        Assert.Equal(NativeNumberDrillText.OverriddenVerdict, state.Feedback.VerdictKey);
        Assert.Equal(1, state.Feedback.ProgressCorrect);
        Assert.Equal(1, state.Feedback.ProgressTotal);
        Assert.Equal(1, state.CorrectCount);
        Assert.Null(state.ErrorKey);
        Assert.True(state.IsOverriding);
        // A second tap during the pause changes nothing.
        Assert.Equal(1, (await drill.OverrideAsync()).CorrectCount);
        state = await drill.NextAsync();
        Assert.Equal(1, state.ItemIndex);
        await drill.ExitAsync();
        await pending;

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var attempt = await db.NumberAttempts.AsNoTracking().SingleAsync();
        Assert.True(attempt.IsCorrect);
        Assert.Equal(NativeSliceFixture.UserA, attempt.UserProfileId);
        var row = await db.NumberMasteryProgresses.AsNoTracking().SingleAsync();
        Assert.Equal(NativeSliceFixture.UserA, row.UserProfileId);
        Assert.Equal(1, row.CorrectCount);
        Assert.Equal(1, row.TotalCount);
        // Same schedule as a first correct grade: interval 1 -> 6.
        Assert.Equal(6, row.Interval);
    }

    [Fact]
    public async Task Override_WhenTheCorrectionCannotBeStored_SaysSoAndTheSessionContinues()
    {
        using var fixture = new NativeSliceFixture();
        using var drill = await StartAsync(fixture, "Counting", "ReadAndProduce");

        var state = await drill.SubmitAnswerAsync("틀림");
        Assert.False(state.Feedback!.IsCorrect);
        var failedError = state.Feedback.ErrorClass;
        // Another writer changes the mastery row after the attempt.
        using (var scope = fixture.Scope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database
                .ExecuteSqlRawAsync("UPDATE NumberMasteryProgress SET Interval = 9");

        state = await drill.OverrideAsync();

        Assert.Equal(NativePhase.Feedback, state.Phase);
        Assert.Equal(0, state.ItemIndex);
        Assert.Equal(NativeNumberDrillText.OverrideFailed, state.ErrorKey);
        Assert.False(state.Feedback!.IsCorrect);
        Assert.False(state.Feedback.Overridden);
        Assert.False(state.IsOverriding);
        Assert.Equal(0, state.CorrectCount);

        state = await drill.NextAsync();
        Assert.Equal(1, state.ItemIndex);
        Assert.Null(state.ErrorKey);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False((await db.NumberAttempts.AsNoTracking().SingleAsync()).IsCorrect);
            var row = await db.NumberMasteryProgresses.AsNoTracking().SingleAsync();
            Assert.Equal(0, row.CorrectCount);
            Assert.Equal(9, row.Interval);
        }

        // The failed item keeps its error class in the summary patterns.
        for (var i = 1; i < drill.State.ItemTotal; i++)
        {
            await drill.SubmitAnswerAsync(Digits(drill.State.Item!.DisplayPrompt));
            state = await drill.NextAsync();
        }
        Assert.Equal(NativePhase.Summary, state.Phase);
        if (!string.IsNullOrEmpty(failedError))
            Assert.Contains(state.Summary!.ErrorClassBreakdown, pair => pair.Key == failedError);
    }

    [Fact]
    public async Task Disambiguate_GradesBothOrNothingWithOneAttempt()
    {
        using var fixture = new NativeSliceFixture();
        using var drill = await StartAsync(fixture, null, "Disambiguate");
        var item = drill.State.Item!;
        Assert.NotEmpty(item.ChoicesA);
        Assert.NotEmpty(item.ChoicesB);
        Assert.False(drill.State.CanSubmitBoth);

        await drill.SelectPairedAnswerAsync(true, item.ChoicesA[0]);
        Assert.False(drill.State.CanSubmitBoth);
        await drill.SelectPairedAnswerAsync(false, item.ChoicesB[0]);
        var state = await drill.SubmitBothAsync();

        Assert.Equal(NativePhase.Feedback, state.Phase);
        var feedback = state.Feedback!;
        Assert.NotNull(feedback.CorrectA);
        Assert.NotNull(feedback.CorrectB);
        Assert.Equal(feedback.CorrectA!.Value && feedback.CorrectB!.Value, feedback.IsCorrect);
        Assert.StartsWith("A: ", feedback.CanonicalAnswer);
        Assert.Equal($"{item.ChoicesA[0]} | {item.ChoicesB[0]}", feedback.UserAnswer);
        using var scope = fixture.Scope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .NumberAttempts.CountAsync());
    }

    [Fact]
    public async Task Audio_PlaysTheCueInKoreanAndSaysWhenItIsUnavailableOffline()
    {
        using var fixture = new NativeSliceFixture();
        using var drill = await StartAsync(fixture, "Counting", "ListenAndType");
        Assert.True(drill.State.Item!.HasAudio);

        fixture.Media.AudioAvailable = false;
        var state = await drill.PlayAudioAsync();
        Assert.Equal(NativeNumberDrillText.AudioOffline, state.NoticeKey);
        Assert.False(state.IsPlayingAudio);
        Assert.Empty(fixture.Media.Played);

        fixture.Media.AudioAvailable = true;
        state = await drill.PlayAudioAsync();
        Assert.Null(state.NoticeKey);
        Assert.Equal("ko", Assert.Single(fixture.Media.Played).Language);
    }

    [Fact]
    public async Task NonKoreanLearner_GetsTheKoreanOnlyMessageWithoutATimerRow()
    {
        using var fixture = new NativeSliceFixture();
        var (card, _) = await SignInWithPlanAsync(fixture);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var profile = await db.UserProfiles.SingleAsync(p => p.Id == NativeSliceFixture.UserA);
            profile.TargetLanguage = "Spanish";
            await db.SaveChangesAsync();
        }
        var launch = await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromPlanCardAsync(card.Id);
        using var drill = Presenter(fixture);

        var state = await drill.OpenAsync(launch!);

        Assert.Equal(NativePhase.Empty, state.Phase);
        Assert.Equal(NativeNumberDrillText.KoreanOnly, state.ErrorKey);
        Assert.Equal(NativePhase.Empty, (await drill.StartAsync()).Phase);
        Assert.Equal(NativeDestination.TodaysPlan, (await drill.ExitAsync()).Destination);
        var (row, _) = await fixture.RowsAsync(NativeSliceFixture.UserA, card.Id);
        Assert.Equal(0, row.MinutesSpent);
    }

    [Fact]
    public async Task AbsentOrChangedOwner_FailsClosedWithoutAttempts()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 2);
        await SignInAsync(fixture);

        using (var foreign = Presenter(fixture))
            Assert.Equal(NativePhase.Blocked,
                (await foreign.OpenAsync(ChooseOwnLaunch(NativeSliceFixture.UserB))).Phase);

        using var drill = Presenter(fixture);
        Assert.Equal(NativePhase.Ready,
            (await drill.OpenAsync(ChooseOwnLaunch(NativeSliceFixture.UserA))).Phase);
        await drill.SelectContextAsync("Counting");
        await drill.SelectSubModeAsync("ReadAndProduce");
        var state = await drill.StartAsync();
        fixture.LoseOwner();
        state = await drill.SubmitAnswerAsync("1");

        Assert.Equal(NativePhase.Blocked, state.Phase);
        using var scope = fixture.Scope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .NumberAttempts.CountAsync());
    }

    private static NativeNumberDrillPresenter Presenter(NativeSliceFixture fixture)
    {
        var presenter = fixture.Provider.GetRequiredService<NativeNumberDrillPresenter>();
        presenter.AutoAdvanceDelay = TimeSpan.Zero;
        return presenter;
    }

    private static async Task<NativeNumberDrillPresenter> StartAsync(
        NativeSliceFixture fixture, string? context, string mode)
    {
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 2);
        await SignInAsync(fixture);
        var drill = Presenter(fixture);
        Assert.Equal(NativePhase.Ready,
            (await drill.OpenAsync(ChooseOwnLaunch(NativeSliceFixture.UserA))).Phase);
        await drill.SelectContextAsync(context);
        await drill.SelectSubModeAsync(mode);
        var state = await drill.StartAsync();
        Assert.Equal(NativeNumberDrillStage.InSession, state.Stage);
        Assert.Equal(mode, state.Item!.SubModeCode);
        return drill;
    }

    private static NativeActivityLaunch ChooseOwnLaunch(string owner) =>
        new(Route, owner, new DateOnly(2026, 9, 28), null, [], null, [], false, null);

    private static string Digits(string prompt) => Regex.Match(prompt, @"\d+").Value;

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private static async Task SignInAsync(NativeSliceFixture fixture) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password")).Phase);

    private static async Task<(NativePlanCard Card, NativeDashboardPresenter Dashboard)>
        SignInWithPlanAsync(NativeSliceFixture fixture)
    {
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 4);
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            (nameof(PlanActivityType.VocabularyReview), null, null, 6, 1),
            (nameof(PlanActivityType.NumberDrill), null, null, 5, 2));
        await SignInAsync(fixture);
        var dashboard = fixture.Dashboard;
        var plan = await dashboard.GenerateAsync();
        Assert.Equal(NativePhase.Ready, plan.Phase);
        return (plan.Cards.Single(card => card.Activity == PlanActivityType.NumberDrill), dashboard);
    }
}
