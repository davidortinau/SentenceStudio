using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Word Association source contract (activities.md 1.1-1.4 and 14) on the
/// shared activity foundation, against real SQLite and shared services, with
/// a fake owner-bound AI port.
/// </summary>
public sealed class NativeWordAssociationPresenterTests
{
    private const string Route = NativeActivityCatalog.WordAssociation;
    private const string SecondResource = "resource-native-owner-a-second";

    [Fact]
    public void Catalog_RegistersAChooseOwnOnlyRouteWithoutATimer()
    {
        Assert.True(NativeActivityCatalog.IsImplemented(Route));
        var definition = NativeActivityCatalog.ForRoute(Route)!;
        Assert.Empty(definition.PlanTypes);
        Assert.Null(definition.TimerType);
        Assert.True(NativeActivityCatalog.TakesResourceList(Route));
        Assert.Contains(NativeDashboardPresenter.ChooseOwnActivities, tile => tile.Route == Route);
    }

    [Fact]
    public async Task ChooseOwn_UsesEveryResourceAndStartsNoTimer()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        await AddSecondResourceAsync(fixture, NativeSliceFixture.UserA, 4);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var dashboard = fixture.Dashboard;
        dashboard.SetMode(NativeDashboardMode.ChooseOwn);
        await dashboard.LoadChooseOwnAsync();
        dashboard.SelectChooseOwn([fixture.ResourceId(NativeSliceFixture.UserA), SecondResource], null);
        var selection = await dashboard.SelectChooseOwnQuizAsync();
        var launch = await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromChooseOwnAsync(selection!, Route);

        Assert.NotNull(launch);
        Assert.Equal([fixture.ResourceId(NativeSliceFixture.UserA), SecondResource],
            launch.ResourceIds.ToArray());
        Assert.Null(launch.ActivityType);

        // Seven words across both resources; the round takes five distinct ones.
        var seen = new HashSet<string>();
        for (var i = 0; i < 6; i++)
        {
            using var presenter = Presenter(fixture, new FakeAi());
            var state = await presenter.OpenAsync(launch);
            Assert.Equal(NativePhase.Ready, state.Phase);
            Assert.Equal(NativeWordAssociationPresenter.RoundSize, state.WordCount);
            Assert.Equal(0, state.Index);
            Assert.True(state.ShowsInstructions);
            seen.Add(state.TargetTerm);
            await presenter.ExitAsync();
        }
        Assert.Contains(seen, term => term.StartsWith("second-", StringComparison.Ordinal));

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.DailyPlanCompletions.AnyAsync());
    }

    [Fact]
    public async Task Round_GradesScoresRecordsAndKeepsHighScores()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 5);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeAi();
        using var round = Presenter(fixture, ai);
        var state = await round.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));

        // Clues: blank is ignored, text is trimmed, ungraded clues can be removed.
        round.SetDraft("   ");
        Assert.Empty((await round.AddClueAsync()).Clues);
        foreach (var clue in new[] { " 학교 ", "random", "나는 학교에 가요" , "remove me" })
        {
            round.SetDraft(clue);
            state = await round.AddClueAsync();
        }
        Assert.Equal(string.Empty, state.Draft);
        Assert.Equal("학교", state.Clues[0].Text);
        state = await round.RemoveClueAsync(3);
        Assert.Equal(3, state.Clues.Length);
        Assert.True(state.CanGrade);
        Assert.False(state.CanAdvance);
        Assert.Equal(0, (await round.NextAsync()).Index);

        // related, unrelated, related sentence (2 points)
        ai.Verdicts.Enqueue([(true, false, "same place", null), (false, false, "no link", null),
            (true, true, "uses it", "나는 학교에 가요.")]);
        var firstWord = state.TargetTerm;
        state = await round.GradeAsync();
        Assert.All(state.Clues, clue => Assert.True(clue.Graded));
        Assert.Equal(3, state.WordScore);
        Assert.Equal(4, state.WordMax);
        Assert.True(state.Clues[2].ShowsDouble);
        Assert.True(state.Clues[2].ShowsCorrection);
        Assert.False(state.Clues[0].ShowsCorrection);
        Assert.True(state.CanAdvance);
        var call = Assert.Single(ai.Calls);
        Assert.Equal((NativeSliceFixture.UserA, firstWord, "Korean", "English"),
            (call.Owner, call.Target, call.TargetLanguage, call.NativeLanguage));
        Assert.Equal(["학교", "random", "나는 학교에 가요"], call.Clues);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var attempt = await db.VocabularyLearningContexts.AsNoTracking()
                .Join(db.VocabularyProgresses, context => context.VocabularyProgressId,
                    progress => progress.Id, (context, progress) => new { context, progress })
                .SingleAsync();
            Assert.Equal(NativeSliceFixture.UserA, attempt.progress.UserId);
            Assert.Equal("WordAssociation", attempt.context.Activity);
            Assert.Equal("Text", attempt.context.InputMode);
            Assert.Equal("Association", attempt.context.ContextType);
            Assert.True(attempt.context.WasCorrect); // 2 of 3 related >= 50 percent
            var activity = await db.UserActivities.AsNoTracking().SingleAsync();
            Assert.Equal("WordAssociation", activity.Activity);
            Assert.Equal(NativeSliceFixture.UserA, activity.UserProfileId);
            Assert.Equal("학교; random; 나는 학교에 가요", activity.Input);
            Assert.Equal(200.0 / 3, activity.Accuracy, 3);
            Assert.True((DateTime.UtcNow - DateTime.SpecifyKind(activity.CreatedAt, DateTimeKind.Utc))
                .Duration() < TimeSpan.FromMinutes(5));
        }

        // Previous is always available after the first word; Next for the rest.
        state = await round.NextAsync();
        Assert.Equal(1, state.Index);
        Assert.True(state.ShowsInstructions);
        state = await round.PreviousAsync();
        Assert.Equal(0, state.Index);
        Assert.Equal(3, state.Clues.Length);
        for (var i = 0; i < 4; i++)
            state = await round.NextAsync();
        Assert.True(state.IsLastWord);
        round.SetDraft("unrelated");
        await round.AddClueAsync();
        ai.Verdicts.Enqueue([(false, false, "no", null)]);
        state = await round.GradeAsync();
        Assert.Equal(2, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        state = await round.FinishAsync();
        Assert.Equal(NativePhase.Summary, state.Phase);
        Assert.Equal(2, state.TotalRelated);
        Assert.Equal(4, state.TotalClues);
        Assert.Equal(3, state.TotalScore);
        Assert.Equal(NativeWordAssociationHighScore.AllTime, state.HighScore);

        // Play again with a lower score: no banner. Then a week-old high leaves a weekly best.
        state = await round.PlayAgainAsync();
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(0, state.Index);
        Assert.Empty(state.Clues);
        state = await PlayToEndAsync(round, ai, related: 1);
        Assert.Equal(1, state.TotalScore);
        Assert.Equal(NativeWordAssociationHighScore.None, state.HighScore);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            foreach (var row in db.WordAssociationScores)
                row.CreatedAt = DateTime.UtcNow.AddDays(-14);
            await db.SaveChangesAsync();
        }
        await round.PlayAgainAsync();
        state = await PlayToEndAsync(round, ai, related: 2);
        Assert.Equal(NativeWordAssociationHighScore.Weekly, state.HighScore);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.WordAssociationScores.AsNoTracking().ToListAsync();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, row => Assert.Equal(NativeSliceFixture.UserA, row.UserProfileId));
            Assert.Contains(rows, row => row.RoundScore == 3 && row.TotalClues == 4 && row.WordCount == 5
                && row.WordIds!.Split(',').Length == 5);
        }

        var exit = await round.ExitAsync();
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.True(exit.HasPendingSync);
    }

    [Fact]
    public async Task GracePeriodWordsAndForeignResourcesAreExcluded()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 6);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = "progress-familiar",
                UserId = NativeSliceFixture.UserA,
                VocabularyWordId = fixture.WordId(NativeSliceFixture.UserA, 0),
                IsUserDeclared = true,
                UserDeclaredAt = DateTime.UtcNow.AddDays(-1),
                VerificationState = VerificationStatus.Pending
            });
            await db.SaveChangesAsync();
        }
        var launch = Launch(fixture, NativeSliceFixture.UserA) with
        {
            ResourceIds = [fixture.ResourceId(NativeSliceFixture.UserA), fixture.ResourceId(NativeSliceFixture.UserB)]
        };
        var terms = new HashSet<string>();
        for (var i = 0; i < 5; i++)
        {
            using var presenter = Presenter(fixture, new FakeAi());
            var state = await presenter.OpenAsync(launch);
            Assert.Equal(5, state.WordCount);
            for (var w = 0; w < state.WordCount; w++)
            {
                terms.Add(state.TargetTerm);
                presenter.SetDraft("x");
                await presenter.AddClueAsync();
                state = await presenter.RemoveClueAsync(0);
                state = await presenter.NextAsync();
            }
        }
        Assert.DoesNotContain($"target-{NativeSliceFixture.UserA}-0", terms);
        Assert.DoesNotContain(terms, term => term.Contains(NativeSliceFixture.UserB, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptySelection_ShowsTheSourceWarningAndGoBack()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 0);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var presenter = Presenter(fixture, new FakeAi());
        var state = await presenter.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        Assert.Equal(NativePhase.Empty, state.Phase);
        Assert.Equal(NativeWordAssociationText.NoVocabulary, state.ErrorKey);
        Assert.Equal(NativeWordAssociationText.NoResourceVocabulary, Assert.Single(state.Notices).Key);
        Assert.Equal(NativeDestination.TodaysPlan, (await presenter.ExitAsync()).Destination);
    }

    [Fact]
    public async Task AiFailures_LeaveCluesUngradedAndWriteNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 5);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeAi { Fail = true };
        using var presenter = Presenter(fixture, ai);
        await presenter.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        presenter.SetDraft("clue");
        await presenter.AddClueAsync();

        var state = await presenter.GradeAsync();
        Assert.False(state.IsGrading);
        Assert.False(Assert.Single(state.Clues).Graded);
        Assert.Equal(NativeWordAssociationText.GradeFailed, Assert.Single(state.Notices).Key);
        Assert.False(state.CanAdvance);

        // An empty verdict list (the source service's swallowed failure) is also a failure.
        ai.Fail = false;
        state = await presenter.GradeAsync();
        Assert.Equal(NativeWordAssociationText.GradeFailed, Assert.Single(state.Notices).Key);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        using var scope = fixture.Scope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .UserActivities.AnyAsync());
    }

    [Fact]
    public async Task HangingAi_ExitReturnsPromptlyAndDiscardsTheLateResult()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 5);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeAi { Hang = true };
        using var presenter = Presenter(fixture, ai);
        await presenter.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        presenter.SetDraft("clue");
        await presenter.AddClueAsync();

        var grading = presenter.GradeAsync();
        await ai.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(presenter.State.IsGrading);
        Assert.False(presenter.State.CanAdvance);
        Assert.False(presenter.State.CanPrevious);
        var watch = Stopwatch.StartNew();
        var exit = await presenter.ExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        var late = await grading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NativeDestination.TodaysPlan, late.Destination);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task Back_ReturnsItsDestinationToTheCancelledGrade_EvenWhenItResumesFirst()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 5);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeAi { Hang = true };
        using var presenter = Presenter(fixture, ai);
        await presenter.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        presenter.SetDraft("clue");
        await presenter.AddClueAsync();
        var published = new List<NativeWordAssociationState>();
        presenter.StateChanged += published.Add;

        var grading = presenter.GradeAsync();
        await ai.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ExitRace.HoldExitUntil(presenter.ExitFence, () => grading);
        var exit = await presenter.ExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var late = await grading.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Equal(NativeDestination.TodaysPlan, late.Destination);
        Assert.Equal(NativeDestination.TodaysPlan, published[^1].Destination);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task AnAbandonedBackOrDisposal_NeverLeavesTheGradeWaiting()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 5);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);

        // Back whose own gate wait is cancelled: the grade still completes.
        var ai = new FakeAi { Hang = true };
        using (var presenter = Presenter(fixture, ai))
        {
            await presenter.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
            presenter.SetDraft("clue");
            await presenter.AddClueAsync();
            var grading = presenter.GradeAsync();
            await ai.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => presenter.ExitAsync(new CancellationToken(canceled: true)));
            var late = await grading.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(late.IsGrading);
            Assert.Equal(NativeDestination.TodaysPlan,
                (await presenter.ExitAsync().WaitAsync(TimeSpan.FromSeconds(10))).Destination);
        }

        // Disposal while grading: the grade completes too.
        var hung = new FakeAi { Hang = true };
        var disposed = Presenter(fixture, hung);
        await disposed.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        disposed.SetDraft("clue");
        await disposed.AddClueAsync();
        var pending = disposed.GradeAsync();
        await hung.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        disposed.Dispose();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task AbsentOrChangedOwner_FailsClosedWithoutRows()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 5);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 5);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeAi();

        using (var foreign = Presenter(fixture, ai))
            Assert.Equal(NativePhase.Blocked,
                (await foreign.OpenAsync(Launch(fixture, NativeSliceFixture.UserB))).Phase);

        using var presenter = Presenter(fixture, ai);
        await presenter.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        presenter.SetDraft("clue");
        await presenter.AddClueAsync();
        ai.OnGrade = fixture.LoseOwner;
        ai.Verdicts.Enqueue([(true, false, "ok", null)]);
        Assert.Equal(NativePhase.Blocked, (await presenter.GradeAsync()).Phase);
        Assert.Equal(NativePhase.Blocked, (await presenter.FinishAsync()).Phase);

        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.UserActivities.AnyAsync());
        Assert.False(await db.WordAssociationScores.AnyAsync());
        var scores = fixture.Provider.GetRequiredService<NativeWordAssociationScores>();
        Assert.False(await scores.SaveAsync(string.Empty, 3, 3, ["w"], CancellationToken.None));
        Assert.Equal(NativeWordAssociationHighScore.None,
            await scores.CheckHighScoreAsync(string.Empty, 99, DateTime.Now, CancellationToken.None));
    }

    [Fact]
    public async Task WordAssociationAi_RendersTheSourcePromptAtTheReasoningTier()
    {
        var gateway = new FakeReasoningGateway();
        var ai = new NativeWordAssociationAi(gateway);
        var result = await ai.GradeAsync("owner", "학교", "school", ["선생님", "I go to school"],
            "Korean", "English", CancellationToken.None);
        Assert.NotNull(result);
        var prompt = Assert.Single(gateway.Prompts);
        Assert.Contains("- Korean term: \"학교\"", prompt);
        Assert.Contains("- English meaning: \"school\"", prompt);
        Assert.Contains("1. \"선생님\"", prompt);
        Assert.Contains("2. \"I go to school\"", prompt);
        Assert.Contains("Return exactly 2 entries", prompt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NativeWordAssociationAi(null)
            .GradeAsync("owner", "학교", null, ["x"], "Korean", "English", CancellationToken.None));
    }

    [Fact]
    public void WeekStart_IsLocalMonday()
    {
        var sunday = new DateTime(2026, 11, 1, 15, 0, 0, DateTimeKind.Local);
        var start = NativeWordAssociationScores.WeekStartUtc(sunday).ToLocalTime();
        Assert.Equal(new DateTime(2026, 10, 26), start.Date);
        Assert.Equal(DayOfWeek.Monday, start.DayOfWeek);
        Assert.Equal(TimeSpan.Zero, start.TimeOfDay);
    }

    private static async Task<NativeWordAssociationState> PlayToEndAsync(
        NativeWordAssociationPresenter round, FakeAi ai, int related)
    {
        var state = round.State;
        for (var w = 0; w < state.WordCount; w++)
        {
            if (w < related)
            {
                round.SetDraft("clue");
                await round.AddClueAsync();
                ai.Verdicts.Enqueue([(true, false, "ok", null)]);
                state = await round.GradeAsync();
            }
            state = state.IsLastWord ? await round.FinishAsync() : await round.NextAsync();
        }
        return state;
    }

    private static NativeWordAssociationPresenter Presenter(NativeSliceFixture fixture, INativeWordAssociationAi ai) =>
        ActivatorUtilities.CreateInstance<NativeWordAssociationPresenter>(fixture.Provider, ai);

    private static NativeActivityLaunch Launch(NativeSliceFixture fixture, string owner) =>
        new(Route, owner, new DateOnly(2026, 9, 28), null,
            [fixture.ResourceId(owner)], null, [], false, fixture.ResourceId(owner));

    private static async Task AddSecondResourceAsync(NativeSliceFixture fixture, string owner, int words)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.LearningResources.Add(new LearningResource
        {
            Id = SecondResource, UserProfileId = owner, Language = "Korean",
            MediaType = "Vocabulary List", Title = "Second", CreatedAt = DateTime.UtcNow
        });
        for (var i = 0; i < words; i++)
        {
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = $"second-word-{i}", UserProfileId = owner, TargetLanguageTerm = $"second-{i}",
                NativeLanguageTerm = $"second-native-{i}", Language = "Korean"
            });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = $"second-mapping-{i}", ResourceId = SecondResource, VocabularyWordId = $"second-word-{i}"
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task SignInAsync(NativeSliceFixture fixture, string email) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(email, "fixture-password")).Phase);

    private sealed class FakeAi : INativeWordAssociationAi
    {
        public List<(string Owner, string Target, string? Native, string[] Clues, string TargetLanguage,
            string NativeLanguage)> Calls { get; } = [];
        public Queue<(bool Related, bool IsCloze, string Explanation, string? Corrected)[]> Verdicts { get; } = new();
        public bool Fail { get; set; }
        public bool Hang { get; set; }
        public Action? OnGrade { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WordAssociationGradeResponse?> GradeAsync(string owner, string targetTerm,
            string? nativeTerm, IReadOnlyList<string> clues, string targetLanguage, string nativeLanguage,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            if (Hang)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Fail)
                throw new HttpRequestException("Synthetic grader offline.");
            Calls.Add((owner, targetTerm, nativeTerm, [.. clues], targetLanguage, nativeLanguage));
            OnGrade?.Invoke();
            var verdicts = Verdicts.Count > 0 ? Verdicts.Dequeue() : [];
            return new WordAssociationGradeResponse
            {
                Entries = verdicts.Select((verdict, index) => new ClueGrade
                {
                    Text = clues[index],
                    Related = verdict.Related,
                    IsCloze = verdict.IsCloze,
                    Explanation = verdict.Explanation,
                    CorrectedText = verdict.Corrected
                }).ToList()
            };
        }
    }

    private sealed class FakeReasoningGateway : IOwnerBoundNativeReasoningAiGateway
    {
        public List<string> Prompts { get; } = [];

        public Task<T?> SendReasoningPromptAsync<T>(string owner, string prompt, CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            return Task.FromResult((T?)(object)new WordAssociationGradeResponse());
        }
    }
}
