using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Minimal Pairs source contract (activities.md 15: list, create, session)
/// against real SQLite and the real native speech cache: owner-scoped pairs
/// (not the source's hard-coded "1"), create validation and duplicates,
/// delete, Focus 10 / Mixed 20 trials, audio-only prompt with no answer leak
/// before checking, attempts and session rows, offline audio, Back targets,
/// and Back while speech hangs.
/// </summary>
public sealed class NativeMinimalPairsPresenterTests
{
    private const string Route = NativeActivityCatalog.MinimalPairs;
    private const string UserA = NativeSliceFixture.UserA;
    private const string UserB = NativeSliceFixture.UserB;

    [Fact]
    public async Task Catalog_RegistersAChooseOwnTileWithoutPlanTypeOrTimer()
    {
        var definition = NativeActivityCatalog.ForRoute(Route);
        Assert.NotNull(definition);
        Assert.Empty(definition.PlanTypes);
        Assert.Null(definition.TimerType);
        Assert.Contains(NativeDashboardPresenter.ChooseOwnActivities, tile => tile.Route == Route);
        Assert.All(Enum.GetValues<PlanActivityType>(), type =>
            Assert.NotSame(definition, NativeActivityCatalog.ForPlanType(type)));

        using var fixture = await SignedInAsync();
        var launch = await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromChooseOwnAsync(new NativeQuizLaunch(UserA, new DateOnly(2026, 11, 1), string.Empty,
                [], null, []), Route);
        Assert.NotNull(launch);
        Assert.Equal(Route, launch.Route);
        Assert.Null(launch.PlanItemId);
    }

    [Fact]
    public async Task List_ShowsOnlyTheOwnersPairsOldestFirst_EmptyWhenNone()
    {
        using var fixture = await SignedInAsync(seedB: true);
        var test = Create(fixture);
        await using var _ = test;
        var empty = await test.Presenter.OpenAsync(Launch());
        Assert.Equal(NativePhase.Ready, empty.Phase);
        Assert.Equal(NativeMinimalPairsPage.List, empty.Page);
        Assert.True(empty.List!.IsEmpty);
        Assert.False(empty.List.ShowsStart);

        await InsertPairAsync(fixture, UserA, 0, 1, "first", DateTime.UtcNow.AddMinutes(-5));
        await InsertPairAsync(fixture, UserA, 2, 3, null, DateTime.UtcNow);
        await InsertPairAsync(fixture, UserB, 0, 1, "foreign", DateTime.UtcNow);
        // The source listed user "1"; a row stamped "1" is not this owner's.
        await InsertPairAsync(fixture, "1", 0, 2, "legacy", DateTime.UtcNow, wordOwner: UserA);

        var listed = Create(fixture);
        using var __ = listed.Presenter;
        var state = await listed.Presenter.OpenAsync(Launch());
        var pairs = state.List!.Pairs;
        Assert.Equal(2, pairs.Length);
        Assert.Equal(("target-native-owner-a-0", "target-native-owner-a-1", "first"),
            (pairs[0].WordA, pairs[0].WordB, pairs[0].ContrastLabel));
        Assert.Null(pairs[1].ContrastLabel);
        Assert.Equal(NativeMinimalPairsMode.Focus, state.List.Mode);
        Assert.False(state.List.CanStart);
        Assert.Equal(0, await PlanRowCountAsync(fixture));
    }

    [Fact]
    public async Task Modes_FocusNeedsASelection_MixedHighlightsAllAndClearsTheSelection()
    {
        using var fixture = await SignedInAsync();
        var first = await InsertPairAsync(fixture, UserA, 0, 1);
        var second = await InsertPairAsync(fixture, UserA, 2, 3);
        var test = Create(fixture);
        await using var _ = test;
        await test.Presenter.OpenAsync(Launch());

        var refused = await test.Presenter.StartSessionAsync();
        Assert.Equal(NativeMinimalPairsPage.List, refused.Page);
        Assert.Equal(new NativeMessage(NativeMessageKind.Error,
            NativeMinimalPairsText.SelectPairFirst), Assert.Single(refused.Notices));

        // An error is a banner that stays until the learner's next action.
        Assert.True(Assert.Single(refused.Notices).IsBanner);
        var selected = await test.Presenter.SelectPairAsync(second);
        Assert.Empty(selected.Notices);
        Assert.Equal(second, selected.List!.SelectedPairId);
        Assert.True(selected.List.CanStart);
        Assert.True(selected.List.IsHighlighted(second));
        Assert.False(selected.List.IsHighlighted(first));

        var mixed = await test.Presenter.SetModeAsync(NativeMinimalPairsMode.Mixed);
        Assert.Null(mixed.List!.SelectedPairId);
        Assert.True(mixed.List.CanStart);
        Assert.True(mixed.List.IsHighlighted(first) && mixed.List.IsHighlighted(second));
        // Selecting a card does nothing in Mixed (source SelectPair).
        Assert.Null((await test.Presenter.SelectPairAsync(first)).List!.SelectedPairId);
    }

    [Fact]
    public async Task SelectWord_ClearSearch_EmptiesOnlyThatWordsSearch_ARefusedChoiceKeepsIt()
    {
        using var fixture = await SignedInAsync(seedB: true);
        var test = Create(fixture);
        await using var _ = test;
        await test.Presenter.OpenAsync(Launch());
        await test.Presenter.OpenCreateAsync();
        test.Presenter.SetSearch('A', "owner-a-2");
        test.Presenter.SetSearch('B', "owner-a-3");

        // Another learner's word is not offered: the choice is refused and the search stays.
        var refused = await test.Presenter.SelectWordAsync('A', fixture.WordId(UserB, 2), clearSearch: true);
        Assert.Null(refused.Create!.WordAId);
        Assert.Equal("owner-a-2", refused.Create.SearchA);

        var chosen = await test.Presenter.SelectWordAsync('A', fixture.WordId(UserA, 2), clearSearch: true);
        Assert.Equal(fixture.WordId(UserA, 2), chosen.Create!.WordAId);
        Assert.Equal(string.Empty, chosen.Create.SearchA);
        Assert.Equal("owner-a-3", chosen.Create.SearchB);

        // Without clearSearch (Android keeps its own field) the search is left as typed.
        var kept = await test.Presenter.SelectWordAsync('B', fixture.WordId(UserA, 3));
        Assert.Equal("owner-a-3", kept.Create!.SearchB);
    }

    [Fact]
    public async Task Create_ValidatesNormalizesRefusesDuplicatesAndReturnsToTheReloadedList()
    {
        using var fixture = await SignedInAsync(seedB: true);
        var test = Create(fixture);
        await using var _ = test;
        await test.Presenter.OpenAsync(Launch());

        var create = await test.Presenter.OpenCreateAsync();
        Assert.Equal(NativeMinimalPairsPage.Create, create.Page);
        var words = create.Create!.Words;
        Assert.Equal(4, words.Length);
        Assert.All(words, word => Assert.StartsWith("target-native-owner-a-", word.Target));

        // Source filter: target prefix or native contains, case-insensitive.
        var filtered = test.Presenter.SetSearch('A', "TARGET-NATIVE-OWNER-A-2")!;
        Assert.Equal(["target-native-owner-a-2"], filtered.FilteredA.Select(word => word.Target));
        Assert.Equal(4, filtered.FilteredB.Length);
        Assert.Equal(["target-native-owner-a-3"],
            test.Presenter.SetSearch('B', "owner-a-3")!.FilteredB.Select(word => word.Target));

        var missing = await test.Presenter.CreatePairAsync();
        Assert.Equal(NativeMinimalPairsText.SelectBothWords, missing.Create!.ErrorKey);
        Assert.False(missing.Create.CanCreate);

        var wordA = fixture.WordId(UserA, 3);
        var wordB = fixture.WordId(UserA, 1);
        await test.Presenter.SelectWordAsync('A', wordA);
        await test.Presenter.SelectWordAsync('B', wordA);
        var same = await test.Presenter.CreatePairAsync();
        Assert.Equal(NativeMinimalPairsText.SelectDifferent, same.Create!.ErrorKey);

        // Another owner's word is not offered and cannot be chosen.
        var foreign = await test.Presenter.SelectWordAsync('B', fixture.WordId(UserB, 0));
        Assert.Equal(wordA, foreign.Create!.WordBId);

        await test.Presenter.SelectWordAsync('B', wordB);
        test.Presenter.SetContrastLabel("  ㅂ vs ㅃ  ");
        var created = await test.Presenter.CreatePairAsync();
        Assert.Equal(NativeMinimalPairsPage.List, created.Page);
        Assert.Equal(new NativeMessage(NativeMessageKind.Success,
            NativeMinimalPairsText.Created), Assert.Single(created.Notices));
        Assert.True(Assert.Single(created.Notices).IsToast);
        var pair = Assert.Single(created.List!.Pairs);
        Assert.Equal("ㅂ vs ㅃ", pair.ContrastLabel);
        var row = await SinglePairRowAsync(fixture, pair.Id);
        Assert.Equal(UserA, row.UserId);
        Assert.Equal((wordB, wordA), (row.VocabularyWordAId, row.VocabularyWordBId));
        // The same ID on every device and the web, so sync merges instead of duplicating.
        Assert.Equal(SentenceStudio.Services.SyncIds.MinimalPair(UserA, wordB, wordA), row.Id);

        // The same two words in either order are a duplicate (source returns null).
        await test.Presenter.OpenCreateAsync();
        await test.Presenter.SelectWordAsync('A', wordB);
        await test.Presenter.SelectWordAsync('B', wordA);
        var duplicate = await test.Presenter.CreatePairAsync();
        Assert.Equal(NativeMinimalPairsPage.Create, duplicate.Page);
        Assert.Equal(NativeMinimalPairsText.FailedToCreate, duplicate.Create!.ErrorKey);
        Assert.False(duplicate.Create.IsSaving);

        // Create Back returns to the list (source NavigateTo("/minimal-pairs")).
        var back = await test.Presenter.BackToListAsync();
        Assert.Equal(NativeMinimalPairsPage.List, back.Page);
        Assert.Equal(NativeDestination.Activity, back.Destination);
        Assert.Single(back.List!.Pairs);
    }

    [Fact]
    public async Task Store_RefusesForeignWordsAndForeignRows()
    {
        using var fixture = await SignedInAsync(seedB: true);
        var store = fixture.Provider.GetRequiredService<NativeMinimalPairStore>();
        Assert.Null(await store.CreatePairAsync(UserA, fixture.WordId(UserA, 0), fixture.WordId(UserB, 0),
            null, CancellationToken.None));
        var foreignPair = await InsertPairAsync(fixture, UserB, 0, 1);
        Assert.False(await store.DeletePairAsync(UserA, foreignPair, CancellationToken.None));
        Assert.Empty(await store.GetPairsAsync(UserA, [foreignPair], CancellationToken.None));
        Assert.Empty(await store.ListPairsAsync(string.Empty, CancellationToken.None));
        var session = await store.StartSessionAsync(UserB, "Focus", 10, CancellationToken.None);
        Assert.Null(await store.RecordAttemptAsync(UserA, session.Id, foreignPair,
            fixture.WordId(UserB, 0), fixture.WordId(UserB, 0), true, CancellationToken.None));
        Assert.False(await store.EndSessionAsync(UserA, session.Id, CancellationToken.None));
        Assert.NotNull(await SinglePairRowAsync(fixture, foreignPair));
    }

    [Fact]
    public async Task Delete_ConfirmsRemovesThePairAndItsAttempts_CancelKeepsIt()
    {
        using var fixture = await SignedInAsync();
        var kept = await InsertPairAsync(fixture, UserA, 0, 1);
        var removed = await InsertPairAsync(fixture, UserA, 2, 3);
        var store = fixture.Provider.GetRequiredService<NativeMinimalPairStore>();
        var session = await store.StartSessionAsync(UserA, "Focus", 10, CancellationToken.None);
        Assert.NotNull(await store.RecordAttemptAsync(UserA, session.Id, removed,
            fixture.WordId(UserA, 2), fixture.WordId(UserA, 3), false, CancellationToken.None));
        var test = Create(fixture);
        await using var _ = test;
        await test.Presenter.OpenAsync(Launch());

        var asked = await test.Presenter.RequestDeleteAsync(removed);
        Assert.Equal(removed, asked.List!.PendingDelete!.Id);
        var cancelled = await test.Presenter.CancelDeleteAsync();
        Assert.Null(cancelled.List!.PendingDelete);
        Assert.Equal(2, cancelled.List.Pairs.Length);

        await test.Presenter.SelectPairAsync(removed);
        await test.Presenter.RequestDeleteAsync(removed);
        var deleted = await test.Presenter.ConfirmDeleteAsync();
        Assert.Equal([kept], deleted.List!.Pairs.Select(pair => pair.Id));
        Assert.Null(deleted.List.SelectedPairId);
        Assert.False(deleted.List.IsDeleting);
        Assert.Equal(new NativeMessage(NativeMessageKind.Success,
            NativeMinimalPairsText.Deleted), Assert.Single(deleted.Notices));
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.MinimalPairs.AnyAsync(pair => pair.Id == removed));
        Assert.False(await db.MinimalPairAttempts.AnyAsync(attempt => attempt.PairId == removed));
    }

    [Fact]
    public async Task FocusSession_TenTrialsAudioOnlyPrompt_RecordsAttemptsEndsSessionAndShowsTheSummary()
    {
        using var fixture = await SignedInAsync();
        using var audio = new ScratchAudioCache(fixture);
        var pairId = await InsertPairAsync(fixture, UserA, 0, 1);
        var test = Create(fixture);
        await using var _ = test;
        await test.Presenter.OpenAsync(Launch());
        await test.Presenter.SelectPairAsync(pairId);

        var started = await test.Presenter.StartSessionAsync();
        Assert.Equal(NativeMinimalPairsPage.Session, started.Page);
        var session = started.Session!;
        Assert.Equal(NativeMinimalPairsPresenter.FocusTrials, session.TotalTrials);
        Assert.Equal(NativeMinimalPairsMode.Focus, session.Mode);
        Assert.Equal(1, session.TrialNumber);
        AssertNoLeak(session);
        Assert.False(session.CanCheck);

        var spoken = new List<string>();
        for (var trial = 0; trial < NativeMinimalPairsPresenter.FocusTrials; trial++)
        {
            // Each trial speaks its word once, automatically (500 ms after start / 1500 ms after a check).
            var ready = await WaitAsync(test.Presenter, state => state.Session is { HasTrial: true,
                HasChecked: false, IsPlayingAudio: false } && test.Player.Rates.Count > trial);
            var current = ready.Session!;
            Assert.Equal(trial + 1, current.TrialNumber);
            AssertNoLeak(current);
            Assert.Contains(current.Left!.WordId, new[] { fixture.WordId(UserA, 0), fixture.WordId(UserA, 1) });
            Assert.NotEqual(current.Left.WordId, current.Right!.WordId);
            // Alternate right and wrong answers by choosing the left tile.
            var selected = await test.Presenter.SelectAnswerAsync(current.Left.WordId);
            Assert.True(selected.Session!.CanCheck);
            Assert.Equal(NativeMinimalPairTileMark.Selected, selected.Session.MarkFor(current.Left));
            var checkedState = (await test.Presenter.CheckAnswerAsync()).Session!;
            Assert.True(checkedState.HasChecked);
            Assert.NotNull(checkedState.AnswerWordId);
            spoken.Add(checkedState.AnswerWordId!);
            Assert.False(checkedState.CanReplay);
            Assert.False(checkedState.CanSelect);
            Assert.True(checkedState.ShowsCheck(checkedState.AnswerWordId == current.Left.WordId
                ? current.Left : current.Right));
            Assert.Equal(checkedState.WasCorrect == true ? NativeMinimalPairTileMark.SelectedCorrect
                : NativeMinimalPairTileMark.SelectedWrong, checkedState.MarkFor(current.Left));
            if (checkedState.WasCorrect == false)
                Assert.Equal(NativeMinimalPairTileMark.Answer, checkedState.MarkFor(current.Right));
        }

        var summary = (await WaitAsync(test.Presenter, state => state.Session?.ShowSummary == true)).Session!;
        Assert.Equal(NativeMinimalPairsPresenter.FocusTrials, summary.CorrectCount + summary.IncorrectCount);
        Assert.InRange(summary.Accuracy, 0, 100);
        Assert.True(summary.DurationMinutes >= 0);
        // The prompt plays the spoken word's target term through the owner's TTS cache.
        Assert.All(test.Source.Synthesized, item => Assert.Equal(UserA, item.Owner));
        Assert.All(test.Source.Synthesized, item => Assert.StartsWith("target-native-owner-a-", item.Text));
        Assert.True(test.Source.Synthesized.Count <= 2);
        Assert.All(test.Player.Rates, rate => Assert.Equal(1.0f, rate));

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.MinimalPairSessions.AsNoTracking().SingleAsync();
            Assert.Equal((UserA, "Focus", (int?)10), (row.UserId, row.Mode, row.PlannedTrialCount));
            Assert.NotNull(row.EndedAt);
            var attempts = await db.MinimalPairAttempts.AsNoTracking().OrderBy(a => a.SequenceNumber).ToListAsync();
            Assert.Equal(Enumerable.Range(1, 10), attempts.Select(a => a.SequenceNumber));
            Assert.All(attempts, a => Assert.Equal((UserA, row.Id, pairId), (a.UserId, a.SessionId, a.PairId)));
            Assert.Equal(spoken, attempts.Select(a => a.PromptWordId));
            Assert.Equal(summary.CorrectCount, attempts.Count(a => a.IsCorrect));
            Assert.All(attempts, a => Assert.Equal(a.IsCorrect, a.PromptWordId == a.SelectedWordId));
            // No timer, plan, progress or streak rows (source).
            Assert.False(await db.UserActivities.AnyAsync());
            Assert.False(await db.VocabularyProgresses.AnyAsync());
        }
        Assert.Equal(0, await PlanRowCountAsync(fixture));

        var done = await test.Presenter.BackToListAsync();
        Assert.Equal(NativeMinimalPairsPage.List, done.Page);
        Assert.Equal(NativeDestination.Activity, done.Destination);
        var exit = await test.Presenter.ExitAsync();
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
    }

    [Fact]
    public async Task MixedSession_UsesEveryPairForTwentyTrials()
    {
        using var fixture = await SignedInAsync();
        using var audio = new ScratchAudioCache(fixture);
        await InsertPairAsync(fixture, UserA, 0, 1);
        await InsertPairAsync(fixture, UserA, 2, 3);
        var test = Create(fixture);
        await using var _ = test;
        await test.Presenter.OpenAsync(Launch());
        await test.Presenter.SetModeAsync(NativeMinimalPairsMode.Mixed);
        var state = await test.Presenter.StartSessionAsync();
        Assert.Equal(NativeMinimalPairsPresenter.MixedTrials, state.Session!.TotalTrials);
        var tiles = new HashSet<string>();
        for (var trial = 0; trial < NativeMinimalPairsPresenter.MixedTrials; trial++)
        {
            var current = (await WaitAsync(test.Presenter, s => s.Session is { HasTrial: true, HasChecked: false })).Session!;
            tiles.Add(current.Left!.WordId);
            tiles.Add(current.Right!.WordId);
            await test.Presenter.SelectAnswerAsync(current.Right.WordId);
            await test.Presenter.CheckAnswerAsync();
        }
        await WaitAsync(test.Presenter, s => s.Session?.ShowSummary == true);
        Assert.True(tiles.Count >= 2);
        using var scope = fixture.Scope();
        var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .MinimalPairSessions.AsNoTracking().SingleAsync();
        Assert.Equal(("Mixed", (int?)20), (row.Mode, row.PlannedTrialCount));
    }

    [Fact]
    public async Task ChoosingAPairOrModeDuringASession_ChangesOnlyTheList_TheSessionGoesOn()
    {
        using var fixture = await SignedInAsync();
        using var audio = new ScratchAudioCache(fixture);
        var first = await InsertPairAsync(fixture, UserA, 0, 1);
        var second = await InsertPairAsync(fixture, UserA, 2, 3);
        var test = Create(fixture);
        await using var _ = test;
        await test.Presenter.OpenAsync(Launch());
        await test.Presenter.SelectPairAsync(first);
        await test.Presenter.StartSessionAsync();
        var trial = (await WaitAsync(test.Presenter, s => s.Session is { HasTrial: true })).Session!;

        // The iPad split view shows the list beside the session.
        var chosen = await test.Presenter.SelectPairAsync(second);
        Assert.Equal(NativeMinimalPairsPage.Session, chosen.Page);
        Assert.Equal(second, chosen.List!.SelectedPairId);
        Assert.Equal(trial.TrialNumber, chosen.Session!.TrialNumber);
        var mixed = await test.Presenter.SetModeAsync(NativeMinimalPairsMode.Mixed);
        Assert.Equal(NativeMinimalPairsPage.Session, mixed.Page);
        Assert.Equal(NativeMinimalPairsMode.Mixed, mixed.List!.Mode);
        Assert.Equal(NativeMinimalPairsMode.Focus, mixed.Session!.Mode);
        // Opening another page still needs the list (the iOS list goes back to it first).
        Assert.Equal(NativeMinimalPairsPage.Session, (await test.Presenter.OpenCreateAsync()).Page);
    }

    [Fact]
    public async Task StudyPair_FromMixed_StartsAFocusSessionOnThatPairOnly_AnUnknownPairStaysOnTheList()
    {
        using var fixture = await SignedInAsync();
        using var audio = new ScratchAudioCache(fixture);
        await InsertPairAsync(fixture, UserA, 0, 1);
        var studied = await InsertPairAsync(fixture, UserA, 2, 3);
        var test = Create(fixture);
        await using var _ = test;
        await test.Presenter.OpenAsync(Launch());
        await test.Presenter.SetModeAsync(NativeMinimalPairsMode.Mixed);

        var unknown = await test.Presenter.StudyPairAsync("mp:not-a-pair");
        Assert.Equal(NativeMinimalPairsPage.List, unknown.Page);
        Assert.Equal(NativeMinimalPairsMode.Mixed, unknown.List!.Mode);

        var state = await test.Presenter.StudyPairAsync(studied);
        Assert.Equal(NativeMinimalPairsPage.Session, state.Page);
        Assert.Equal(NativeMinimalPairsMode.Focus, state.Session!.Mode);
        Assert.Equal(NativeMinimalPairsPresenter.FocusTrials, state.Session.TotalTrials);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var pair = await db.MinimalPairs.AsNoTracking().SingleAsync(row => row.Id == studied);
        var current = (await WaitAsync(test.Presenter, s => s.Session is { HasTrial: true })).Session!;
        Assert.Equal(new HashSet<string> { pair.VocabularyWordAId, pair.VocabularyWordBId },
            new HashSet<string> { current.Left!.WordId, current.Right!.WordId });
        var row = await db.MinimalPairSessions.AsNoTracking().SingleAsync();
        Assert.Equal(("Focus", (int?)NativeMinimalPairsPresenter.FocusTrials), (row.Mode, row.PlannedTrialCount));

        // Back on the list, Focus keeps the studied pair chosen.
        var list = await test.Presenter.BackToListAsync();
        Assert.Equal(NativeMinimalPairsMode.Focus, list.List!.Mode);
        Assert.Equal(studied, list.List.SelectedPairId);
    }

    [Fact]
    public async Task Audio_ReplaysFromTheCache_OfflineUncachedShowsTheNotice()
    {
        using var fixture = await SignedInAsync();
        using var audio = new ScratchAudioCache(fixture);
        var pairId = await InsertPairAsync(fixture, UserA, 0, 1);
        var test = Create(fixture);
        await using var _ = test;
        await test.Presenter.OpenAsync(Launch());
        await test.Presenter.SelectPairAsync(pairId);
        await test.Presenter.StartSessionAsync();
        await WaitAsync(test.Presenter, s => test.Player.Rates.Count == 1 && s.Session?.IsPlayingAudio == false);
        var synthesized = test.Source.Synthesized.Count;

        var replay = await test.Presenter.ReplayAsync();
        Assert.Null(replay.Session!.AudioNoticeKey);
        Assert.Equal(2, test.Player.Rates.Count);
        Assert.Equal(synthesized, test.Source.Synthesized.Count);

        // Offline: a cached word still plays; an uncached one shows the notice.
        fixture.SetConnectivity(false);
        Assert.Null((await test.Presenter.ReplayAsync()).Session!.AudioNoticeKey);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.StreamHistories.RemoveRange(db.StreamHistories);
            await db.SaveChangesAsync();
        }
        var offline = await test.Presenter.ReplayAsync();
        Assert.Equal(NativeMinimalPairsText.AudioOffline, offline.Session!.AudioNoticeKey);
        Assert.True(offline.Session.CanReplay);

        fixture.SetConnectivity(true);
        test.Source.Fails = true;
        var failed = await test.Presenter.ReplayAsync();
        Assert.Equal(NativeMinimalPairsText.PlaybackError, failed.Session!.AudioNoticeKey);
    }

    [Fact]
    public async Task SessionWithMissingPair_ShowsTheEmptyStateAndGoesBackToTheList()
    {
        using var fixture = await SignedInAsync();
        var pairId = await InsertPairAsync(fixture, UserA, 0, 1);
        var test = Create(fixture);
        await using var _ = test;
        await test.Presenter.OpenAsync(Launch());
        await test.Presenter.SelectPairAsync(pairId);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.MinimalPairs.RemoveRange(db.MinimalPairs);
            await db.SaveChangesAsync();
        }
        var state = await test.Presenter.StartSessionAsync();
        Assert.True(state.Session!.IsEmpty);
        Assert.False(state.Session.HasTrial);
        var back = await test.Presenter.BackToListAsync();
        Assert.Equal(NativeMinimalPairsPage.List, back.Page);
        Assert.True(back.List!.IsEmpty);
    }

    [Fact]
    public async Task ForeignOrLostOwner_IsBlockedAndNothingIsWritten()
    {
        using var fixture = await SignedInAsync(seedB: true);
        var foreign = Create(fixture);
        using (foreign.Presenter)
            Assert.Equal(NativePhase.Blocked, (await foreign.Presenter.OpenAsync(Launch(UserB))).Phase);

        var pairId = await InsertPairAsync(fixture, UserA, 0, 1);
        var test = Create(fixture);
        await using var _ = test;
        await test.Presenter.OpenAsync(Launch());
        await test.Presenter.SelectPairAsync(pairId);
        fixture.LoseOwner();
        Assert.Equal(NativePhase.Blocked, test.Presenter.State.Phase);
        Assert.Equal(NativePhase.Blocked, (await test.Presenter.StartSessionAsync()).Phase);
        using var scope = fixture.Scope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .MinimalPairSessions.AnyAsync());
    }

    [Fact]
    public async Task Back_ReturnsPromptlyWhileSpeechHangs_AndTheLateClipNeverPlays()
    {
        using var fixture = await SignedInAsync();
        using var audio = new ScratchAudioCache(fixture);
        var pairId = await InsertPairAsync(fixture, UserA, 0, 1);
        var source = new ScratchHowDoYouSaySource { HangSynthesis = true };
        var test = Create(fixture, source);
        await using var _ = test;
        await test.Presenter.OpenAsync(Launch());
        await test.Presenter.SelectPairAsync(pairId);
        await test.Presenter.StartSessionAsync();
        await WaitAsync(test.Presenter, s => s.Session?.IsPlayingAudio == true);

        var back = await test.Presenter.BackToListAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(NativeMinimalPairsPage.List, back.Page);
        await Task.Delay(100);
        Assert.Empty(test.Player.Rates);
        Assert.Equal(NativeMinimalPairsPage.List, test.Presenter.State.Page);

        await test.Presenter.StartSessionAsync();
        await WaitAsync(test.Presenter, s => s.Session?.IsPlayingAudio == true);
        var exit = await test.Presenter.ExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Empty(test.Player.Rates);
        // Leaving mid-session keeps the source's open session rows (no EndedAt).
        using var scope = fixture.Scope();
        var sessions = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .MinimalPairSessions.AsNoTracking().ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, row => Assert.Null(row.EndedAt));
    }

    [Fact]
    public async Task OnceBackBegins_NoCommandStartsANewPageOperationOrSession()
    {
        using var fixture = await SignedInAsync();
        using var audio = new ScratchAudioCache(fixture);
        var pairId = await InsertPairAsync(fixture, UserA, 0, 1);
        var source = new ScratchHowDoYouSaySource { HangSynthesis = true };
        var test = Create(fixture, source);
        await using var _ = test;
        await test.Presenter.OpenAsync(Launch());
        await test.Presenter.SelectPairAsync(pairId);
        var published = new List<NativeMinimalPairsState>();
        test.Presenter.StateChanged += published.Add;

        // While Back is between its cancel and its gate, a session start (which
        // would swap in a new page operation) is refused and publishes nothing.
        NativeMinimalPairsState? started = null;
        test.Presenter.ExitFence.AfterCancel = async () =>
        {
            test.Presenter.ExitFence.AfterCancel = null;
            started = await test.Presenter.StartSessionAsync();
        };
        var exit = await test.Presenter.ExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.NotNull(started);
        Assert.NotEqual(NativeMinimalPairsPage.Session, started!.Page);
        Assert.Equal(NativeDestination.TodaysPlan, Assert.Single(published).Destination);
        await Task.Delay(100);
        Assert.Empty(test.Player.Rates);
        using var scope = fixture.Scope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .MinimalPairSessions.AnyAsync());
    }

    /// <summary>Learning Value Gate: before Check Answer nothing in the state tells which tile is spoken.</summary>
    private static void AssertNoLeak(NativeMinimalPairsSessionState session)
    {
        Assert.False(session.HasChecked);
        Assert.Null(session.AnswerWordId);
        Assert.Null(session.WasCorrect);
        if (session.Left is { } left && session.Right is { } right)
        {
            Assert.False(session.ShowsCheck(left) || session.ShowsCheck(right));
            Assert.NotEqual(NativeMinimalPairTileMark.Answer, session.MarkFor(left));
            Assert.NotEqual(NativeMinimalPairTileMark.Answer, session.MarkFor(right));
        }
    }

    /// <summary>
    /// Disposing cancels the presenter's background playback and advance, then waits for them to
    /// end, so none of them reads or writes the fixture's storage after the test.
    /// </summary>
    private sealed record TestPresenter(
        NativeMinimalPairsPresenter Presenter, ScratchHowDoYouSaySource Source, ScratchShadowingPlayer Player)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Presenter.Dispose();
            await Presenter.BackgroundWork.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static TestPresenter Create(NativeSliceFixture fixture, ScratchHowDoYouSaySource? source = null)
    {
        var provider = fixture.Provider;
        source ??= new ScratchHowDoYouSaySource();
        var owner = provider.GetRequiredService<NativeActivityOwner>();
        var history = new StreamHistoryRepository(provider, NullLogger<StreamHistoryRepository>.Instance);
        var speech = new NativeShadowingSpeech(owner,
            provider.GetRequiredService<IConnectivityService>(),
            provider.GetRequiredService<IFileSystemService>(),
            provider.GetRequiredService<SpeechVoicePreferences>(), history,
            NullLogger<NativeShadowingSpeech>.Instance, source, new ScratchSignedIn());
        var player = new ScratchShadowingPlayer();
        var presenter = new NativeMinimalPairsPresenter(owner,
            provider.GetRequiredService<NativeMinimalPairStore>(),
            provider.GetRequiredService<UserProfileRepository>(), speech, player,
            NullLogger<NativeMinimalPairsPresenter>.Instance)
        {
            FirstPromptDelay = TimeSpan.FromMilliseconds(5),
            AdvanceDelay = TimeSpan.FromMilliseconds(5),
            Random = new Random(7),
        };
        return new(presenter, source, player);
    }

    private static NativeActivityLaunch Launch(string owner = UserA) =>
        new(Route, owner, new DateOnly(2026, 11, 1), null, [], null, [], false, null, null);

    private static async Task<NativeSliceFixture> SignedInAsync(bool seedB = false)
    {
        var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        if (seedB)
            await fixture.SeedAsync(UserB);
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password")).Phase);
        return fixture;
    }

    private static async Task<string> InsertPairAsync(NativeSliceFixture fixture, string owner, int a, int b,
        string? label = null, DateTime? createdAt = null, string? wordOwner = null)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var words = wordOwner ?? owner;
        var row = new MinimalPair
        {
            Id = SentenceStudio.Services.SyncIds.MinimalPair(
                owner, fixture.WordId(words, a), fixture.WordId(words, b)),
            UserId = owner,
            VocabularyWordAId = fixture.WordId(words, a),
            VocabularyWordBId = fixture.WordId(words, b),
            ContrastLabel = label,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            UpdatedAt = createdAt ?? DateTime.UtcNow,
        };
        db.MinimalPairs.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static async Task<MinimalPair> SinglePairRowAsync(NativeSliceFixture fixture, string id)
    {
        using var scope = fixture.Scope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .MinimalPairs.AsNoTracking().SingleAsync(pair => pair.Id == id);
    }

    private static async Task<int> PlanRowCountAsync(NativeSliceFixture fixture)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.DailyPlanCompletions.CountAsync() + await db.ActivitySessions.CountAsync();
    }

    private static async Task<NativeMinimalPairsState> WaitAsync(
        NativeMinimalPairsPresenter presenter, Func<NativeMinimalPairsState, bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(presenter.State); i++)
            await Task.Delay(10);
        Assert.True(condition(presenter.State));
        return presenter.State;
    }
}
