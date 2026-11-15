using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Features.Settings;
using SentenceStudio.Native.Features.Vocabulary;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// docs/known-words-plan.md on the native heads' shared layer: the store's owner-scoped marks,
/// the word editor's and select mode's actions, the quiz's Mark Known and the Learning mode
/// setting, each on the real SQLite store.
/// </summary>
public sealed class KnownWordsNativeTests
{
    private const string UserA = NativeSliceFixture.UserA;
    private const string UserB = NativeSliceFixture.UserB;

    [Fact]
    public async Task Store_MarksOnlyTheOwnersWords_AndTheDetailSaysWhoDecided()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await fixture.SeedAsync(UserB);
        fixture.Activate(UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var own = fixture.WordId(UserA, 0);
        var foreign = fixture.WordId(UserB, 0);

        var before = (await store.GetOwnedVocabularyAsync(UserA, own, default))!;
        Assert.True(before.CanMarkKnown);
        Assert.False(before.CanPracticeAgain);
        Assert.False(before.CanStartOver);

        var marked = await store.ApplyLearnerActionAsync(UserA, [own, foreign],
            VocabularyLearnerAction.MarkKnown, default);
        Assert.Equal(1, marked.Changed);
        Assert.Equal(NativeResourceSyncPhase.Pending, (await fixture.Resources.LoadListAsync()).Sync);
        var known = (await store.GetOwnedVocabularyAsync(UserA, own, default))!;
        Assert.True(known.Word.IsKnown);
        Assert.Equal(VocabularyKnownSource.Learner, known.KnownSource);
        Assert.True(known.CanPracticeAgain);
        Assert.False(known.CanMarkKnown);
        Assert.True(known.CanStartOver);
        Assert.Contains((await store.ListOwnedVocabularyAsync(UserA, default)).Words,
            word => word.Id == own && word.IsKnown);

        // The other learner's word and progress are untouched, and a foreign word alone is refused.
        var refused = await store.ApplyLearnerActionAsync(UserA, [foreign],
            VocabularyLearnerAction.MarkKnown, default);
        Assert.Equal(0, refused.Changed);
        Assert.Equal("Vocabulary_MarkRefusedNotYours", refused.RefusalKey);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await db.VocabularyProgresses.AnyAsync(row => row.VocabularyWordId == foreign
                && row.VerificationState != VerificationStatus.None));
            Assert.False(await db.VocabularyProgresses.AnyAsync(row =>
                row.UserId == UserA && row.VocabularyWordId == foreign));
        }

        var again = await store.ApplyLearnerActionAsync(UserA, [own],
            VocabularyLearnerAction.PracticeAgain, default);
        Assert.Equal(1, again.Changed);
        var learning = (await store.GetOwnedVocabularyAsync(UserA, own, default))!;
        Assert.False(learning.Word.IsKnown);
        Assert.Equal(LearningStatus.Learning, learning.Word.Status);
        Assert.Equal(VocabularyKnownMarks.PracticeAgainMastery, learning.Progress!.MasteryScore);
        Assert.Equal(VocabularyKnownSource.None, learning.KnownSource);
    }

    [Fact]
    public async Task PracticeLed_OffersNoMarkKnown_AndTheStoreRefusesIt()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await SetModeAsync(fixture, UserA, LearningMode.PracticeLed);
        fixture.Activate(UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        var own = fixture.WordId(UserA, 0);

        Assert.Equal(LearningMode.PracticeLed, (await store.ListOwnedVocabularyAsync(UserA, default)).Mode);
        Assert.False((await store.GetOwnedVocabularyAsync(UserA, own, default))!.CanMarkKnown);
        var refused = await store.ApplyLearnerActionAsync(UserA, [own],
            VocabularyLearnerAction.MarkKnown, default);
        Assert.Equal(0, refused.Changed);
        Assert.Equal("Vocabulary_MarkKnownPracticeLed", refused.RefusalKey);
        using var scope = fixture.Scope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyProgresses.AnyAsync(row => row.UserId == UserA));
    }

    [Fact]
    public async Task Browser_SelectMode_MarksTheChosenWordsInOneSave_AndTheEditorMarksItsWord()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        fixture.Activate(UserA);
        var store = fixture.Provider.GetRequiredService<INativeResourceStore>();
        using var browser = new VocabularyBrowser(store, fixture.UserScope, fixture.Preferences,
            new ScratchQuizMedia(), NullLogger<VocabularyBrowser>.Instance, key => key);
        Assert.True(await browser.OpenAsync(UserA));
        var first = fixture.WordId(UserA, 0);
        var second = fixture.WordId(UserA, 1);
        var third = fixture.WordId(UserA, 2);

        // Outside select mode a tap chooses nothing.
        browser.ToggleSelected(first);
        Assert.Empty(browser.ListState.Selected);
        var arrangement = browser.Arrangement;
        browser.SetSelecting(true);
        Assert.NotEqual(arrangement, browser.Arrangement);
        Assert.True(browser.ListState.OffersMarkKnown);
        Assert.False(browser.ListState.CanMarkSelected);
        browser.ToggleSelected(first);
        browser.ToggleSelected(second);
        browser.ToggleSelected(third);
        browser.ToggleSelected(third);
        browser.ToggleSelected("not-a-word");
        Assert.Equal(new[] { first, second }, browser.ListState.Selected.Order(StringComparer.Ordinal));
        Assert.True(browser.ListState.CanMarkSelected);

        await browser.MarkSelectedAsync(VocabularyLearnerAction.MarkKnown);
        var list = browser.ListState;
        Assert.False(list.Selecting);
        Assert.Empty(list.Selected);
        Assert.False(list.FeedbackWarning);
        Assert.NotNull(list.Feedback);
        Assert.Equal(new[] { first, second },
            list.Catalog.Words.Where(word => word.IsKnown).Select(word => word.Id)
                .Order(StringComparer.Ordinal));

        await browser.OpenWordAsync(first);
        Assert.True(browser.EditState.CanMark);
        Assert.True(browser.EditState.Detail!.CanPracticeAgain);
        await browser.MarkOpenWordAsync(VocabularyLearnerAction.StartOver);
        var edit = browser.EditState;
        Assert.False(edit.Marking);
        Assert.Null(edit.ErrorKey);
        Assert.False(edit.Detail!.Word.IsKnown);
        Assert.Equal(LearningStatus.Unknown, edit.Detail.Word.Status);
        Assert.True(edit.Detail.CanMarkKnown);
        Assert.DoesNotContain(browser.ListState.Catalog.Words, word => word.Id == first && word.IsKnown);

        // Select All takes the filtered words. Start Over asks first; Cancel changes nothing.
        browser.CloseWord();
        browser.SetSelecting(true);
        browser.SelectAllFiltered();
        Assert.Equal(browser.ListState.FilteredWords.Count, browser.ListState.Selected.Count);
        await browser.RequestMarkSelectedAsync(VocabularyLearnerAction.StartOver);
        Assert.NotNull(browser.StartOverSelectedMessage);
        browser.CancelStartOverSelected();
        Assert.Null(browser.StartOverSelectedMessage);
        Assert.Contains(browser.ListState.Catalog.Words, word => word.Id == second && word.IsKnown);

        // Practice Again changes only the Known word; the others keep what practice earned.
        await browser.RequestMarkSelectedAsync(VocabularyLearnerAction.PracticeAgain);
        Assert.True(browser.ListState.FeedbackWarning);
        using (var scope = fixture.Scope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .VocabularyProgresses.AsNoTracking().Where(row => row.UserId == UserA).ToListAsync();
            Assert.Equal(VerificationStatus.Reopened, rows.Single(row => row.VocabularyWordId == second).VerificationState);
            Assert.DoesNotContain(rows, row => row.VocabularyWordId == third);
            Assert.Equal(0f, rows.Single(row => row.VocabularyWordId == first).MasteryScore);
        }

        // With no Known word chosen, Practice Again is refused and says why.
        browser.SetSelecting(true);
        browser.ToggleSelected(third);
        await browser.RequestMarkSelectedAsync(VocabularyLearnerAction.PracticeAgain);
        Assert.Equal("Vocabulary_PracticeAgainNotKnown", browser.ListState.Feedback);

        // Confirmed, Start Over applies; Done clears the choice.
        await browser.RequestMarkSelectedAsync(VocabularyLearnerAction.StartOver);
        await browser.ConfirmStartOverSelectedAsync();
        Assert.False(browser.ListState.Selecting);
        browser.SetSelecting(true);
        browser.ToggleSelected(first);
        browser.SetSelecting(false);
        Assert.Empty(browser.ListState.Selected);
    }

    [Fact]
    public async Task Quiz_MarkKnown_MarksTheWordOnScreen_SavesAnAnswerAlreadyGiven_AndMovesOn()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        var planItemId = await fixture.GenerateQuizPlanAsync(UserA);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(planItemId))!;
        fixture.Activate(UserA);
        var quiz = fixture.Quiz;
        var ready = await quiz.OpenAsync(launch);
        Assert.Equal(NativePhase.Ready, ready.Phase);
        Assert.True(ready.CanMarkKnown);

        // Before answering: the word is marked, nothing is scored, the next question shows.
        var next = await quiz.MarkKnownAsync();
        Assert.Equal(NativePhase.Ready, next.Phase);
        Assert.NotEqual(ready.TurnId, next.TurnId);
        Assert.Equal(0, next.WordsMastered);
        Assert.Equal(0, await fixture.AttemptCountAsync(UserA));
        Assert.Equal(1, await MarkedAsync(fixture, UserA));

        // After answering: the answer is saved as any other, then the word leaves.
        var feedback = await quiz.AnswerAsync(next.Choices[0].Text);
        Assert.Equal(NativePhase.Feedback, feedback.Phase);
        Assert.True(feedback.CanMarkKnown);
        var after = await quiz.MarkKnownAsync();
        Assert.NotEqual(feedback.TurnId, after.TurnId);
        Assert.Equal(1, await fixture.AttemptCountAsync(UserA));
        Assert.Equal(2, await MarkedAsync(fixture, UserA));
        Assert.Equal(0, after.WordsMastered);
        Assert.True(after.HasPendingSync);
    }

    [Fact]
    public async Task Quiz_PracticeLed_DoesNotOfferMarkKnown_AndRefusesIt()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await SetModeAsync(fixture, UserA, LearningMode.PracticeLed);
        var planItemId = await fixture.GenerateQuizPlanAsync(UserA);
        var launch = (await fixture.Dashboard.SelectAssignedQuizAsync(planItemId))!;
        fixture.Activate(UserA);
        var quiz = fixture.Quiz;
        var ready = await quiz.OpenAsync(launch);
        Assert.Equal(NativePhase.Ready, ready.Phase);
        Assert.False(ready.CanMarkKnown);

        var refused = await quiz.MarkKnownAsync();
        Assert.Equal(ready.TurnId, refused.TurnId);
        Assert.Equal(0, await MarkedAsync(fixture, UserA));
    }

    [Fact]
    public async Task LearningMode_IsKeptOnTheDeviceOnlyAfterTheServerAcceptedIt()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(UserA);
        await fixture.SeedAsync(UserB);
        fixture.Activate(UserA);
        var remote = new ModeRemote();
        using var setting = new LearningModeSetting(fixture.UserScope,
            fixture.Provider.GetRequiredService<VocabularyProgressRepository>(),
            fixture.Provider.GetRequiredService<UserProfileRepository>(), remote,
            NullLogger<LearningModeSetting>.Instance);
        await setting.OpenAsync(UserA);
        Assert.Equal(LearningMode.LearnerLed, setting.State.Mode);

        remote.Outcome = NativeProfileRemoteOutcome.TransportFailure;
        await setting.SetAsync(LearningMode.PracticeLed);
        Assert.Equal(LearningMode.LearnerLed, setting.State.Mode);
        Assert.Equal(LearningModeSetting.SaveFailedKey, setting.State.ErrorKey);
        Assert.Equal(LearningMode.LearnerLed, await ModeAsync(fixture, UserA));

        remote.Outcome = NativeProfileRemoteOutcome.Saved;
        await setting.SetAsync(LearningMode.PracticeLed);
        Assert.Equal(LearningMode.PracticeLed, setting.State.Mode);
        Assert.Null(setting.State.ErrorKey);
        Assert.False(setting.State.Saving);
        Assert.Equal(LearningMode.PracticeLed, await ModeAsync(fixture, UserA));
        Assert.Equal(LearningMode.LearnerLed, await ModeAsync(fixture, UserB));
        Assert.Equal([(UserA, LearningMode.PracticeLed), (UserA, LearningMode.PracticeLed)], remote.Writes);

        // Another owner signed in: the setting shows nothing and writes nothing.
        fixture.Activate(UserB);
        Assert.Null(setting.State.Mode);
        await setting.SetAsync(LearningMode.LearnerLed);
        Assert.Equal(2, remote.Writes.Count);
    }

    private static async Task SetModeAsync(NativeSliceFixture fixture, string userId, LearningMode mode)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.UserProfiles.SingleAsync(profile => profile.Id == userId)).LearningMode = mode;
        await db.SaveChangesAsync();
    }

    private static async Task<LearningMode> ModeAsync(NativeSliceFixture fixture, string userId)
    {
        using var scope = fixture.Scope();
        return (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .UserProfiles.AsNoTracking().SingleAsync(profile => profile.Id == userId)).LearningMode;
    }

    private static async Task<int> MarkedAsync(NativeSliceFixture fixture, string userId)
    {
        using var scope = fixture.Scope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyProgresses.CountAsync(row => row.UserId == userId
                && row.VerificationState == VerificationStatus.Confirmed);
    }

    private sealed class ModeRemote : INativeProfileRemoteStore
    {
        public NativeProfileRemoteOutcome Outcome { get; set; } = NativeProfileRemoteOutcome.Saved;
        public List<(string Owner, LearningMode Mode)> Writes { get; } = [];

        public Task<NativeProfileRemoteOutcome> SaveOnboardingAsync(
            string owner, NativeOnboardingDraft draft, CancellationToken cancellationToken) =>
            Task.FromResult(NativeProfileRemoteOutcome.Unavailable);

        public Task<NativeProfileSaveResult> SaveProfileAsync(
            string owner, NativeProfileFields fields, CancellationToken cancellationToken) =>
            Task.FromResult(new NativeProfileSaveResult(NativeProfileRemoteOutcome.Unavailable));

        public Task<NativeProfileRemoteOutcome> SaveLearningModeAsync(
            string owner, LearningMode mode, CancellationToken cancellationToken)
        {
            Writes.Add((owner, mode));
            return Task.FromResult(Outcome);
        }
    }
}
