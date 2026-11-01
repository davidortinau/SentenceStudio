using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Diary source contract (activities.md 17: list, editor, viewer) against
/// real SQLite with a fake owner-bound AI port.
/// </summary>
public sealed class NativeDiaryPresenterTests
{
    private const string Diary = NativeActivityCatalog.Diary;

    [Fact]
    public async Task Catalog_RegistersDiaryAsAChooseOwnTileWithoutPlanOrTimer()
    {
        Assert.True(NativeActivityCatalog.IsImplemented(Diary));
        var definition = NativeActivityCatalog.ForRoute(Diary)!;
        Assert.True(definition.PlanTypes.IsEmpty);
        Assert.Null(definition.TimerType);
        Assert.Contains(NativeDashboardPresenter.ChooseOwnActivities, tile => tile.Route == Diary);

        using var fixture = await SignedInAsync();
        var launch = await fixture.Provider.GetRequiredService<NativeActivityLaunchService>()
            .FromChooseOwnAsync(new NativeQuizLaunch(NativeSliceFixture.UserA,
                new DateOnly(2026, 11, 1), string.Empty, [], null, []), Diary);
        Assert.NotNull(launch);
        Assert.Equal(Diary, launch.Route);
        Assert.Null(launch.PlanItemId);
    }

    [Fact]
    public async Task Journey_WritesTodaysEntryWithPromptAndReturnsToTheList()
    {
        using var fixture = await SignedInAsync();
        var ai = new FakeDiaryAi();
        using var diary = Presenter(fixture, ai);

        var state = await diary.OpenAsync(Launch(NativeSliceFixture.UserA));
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(NativeDiaryPage.List, state.Page);
        Assert.True(state.List!.IsEmpty);

        state = await diary.OpenTodayAsync();
        Assert.Equal(NativeDiaryPage.Editor, state.Page);
        var editor = state.Editor!;
        Assert.False(editor.IsLoading);
        Assert.False(editor.IsLoadingPrompt);
        Assert.Equal("오늘 뭐 먹었어요?", editor.PromptText);
        Assert.Equal("What did you eat today?", editor.PromptHint);
        Assert.Equal(NativeDiaryPresenter.DefaultFontSize, editor.FontSize);
        Assert.Equal([(NativeSliceFixture.UserA, "Korean", "English")], ai.Prompts);
        // A new entry is not written until the learner saves.
        Assert.Equal(0, await CountEntriesAsync(fixture, NativeSliceFixture.UserA));

        var typed = diary.SetContent("저는 오늘 김치찌개를 먹었어요")!;
        Assert.Equal(4, typed.WordCount);
        Assert.Equal(0, typed.Stars);
        Assert.Equal(NativeDiarySaveStatus.Idle, typed.SaveStatus);

        state = await diary.SaveAsync();
        Assert.Equal(NativeDiarySaveStatus.Saved, state.Editor!.SaveStatus);
        Assert.True(state.HasPendingSync);
        using (var scope = fixture.Scope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .DiaryEntries.AsNoTracking().SingleAsync();
            Assert.Equal(NativeSliceFixture.UserA, row.UserProfileId);
            Assert.Equal("Korean", row.Language);
            Assert.Equal(DiaryEntryRepository.NormalizeEntryDate(DateTime.UtcNow), row.EntryDate);
            Assert.Equal("저는 오늘 김치찌개를 먹었어요", row.Content);
            Assert.Equal(4, row.WordCount);
            Assert.Equal(200, row.WordGoal);
            Assert.Equal("오늘 뭐 먹었어요?", row.PromptText);
        }

        // Typing more then Back flushes a silent save; the list shows today's word count.
        diary.SetContent("저는 오늘 김치찌개를 먹었어요 그리고 커피도 마셨어요");
        state = await diary.BackToListAsync();
        Assert.Equal(NativeDiaryPage.List, state.Page);
        Assert.Equal(7, state.List!.Today!.WordCount);
        Assert.Empty(state.List.PastEntries);

        // Reopening today's entry loads it instead of requesting another prompt.
        state = await diary.OpenTodayAsync();
        Assert.Equal("저는 오늘 김치찌개를 먹었어요 그리고 커피도 마셨어요", state.Editor!.Content);
        Assert.Single(ai.Prompts);
        state = await diary.BackToListAsync();

        var exit = await diary.ExitAsync();
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        // Source: no timer, plan row or UserActivity for Diary.
        using var check = fixture.Scope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.UserActivities.AnyAsync());
        Assert.False(await db.DailyPlanCompletions.AnyAsync());
        Assert.Equal(1, await db.DiaryEntries.CountAsync());
    }

    [Fact]
    public async Task Editor_StarsFontSizeAndPromptPreferencesFollowTheSource()
    {
        using var fixture = await SignedInAsync();
        using var diary = Presenter(fixture, new FakeDiaryAi());
        await diary.OpenAsync(Launch(NativeSliceFixture.UserA));
        await diary.OpenTodayAsync();

        Assert.Equal(0, diary.SetContent(Words(49))!.Stars);
        Assert.Equal(1, diary.SetContent(Words(50))!.Stars);
        Assert.Equal(1, diary.SetContent(Words(100))!.Stars);
        Assert.Equal(2, diary.SetContent(Words(101))!.Stars);
        Assert.Equal(3, diary.SetContent(Words(151))!.Stars);

        NativeDiaryState state = diary.State;
        for (var i = 0; i < 20; i++)
            state = await diary.IncreaseFontSizeAsync();
        Assert.Equal(NativeDiaryPresenter.MaxFontSize, state.Editor!.FontSize);
        Assert.False(state.Editor.CanIncreaseFont);
        for (var i = 0; i < 20; i++)
            state = await diary.DecreaseFontSizeAsync();
        Assert.Equal(NativeDiaryPresenter.MinFontSize, state.Editor!.FontSize);
        Assert.False(state.Editor.CanDecreaseFont);
        Assert.Equal(12, fixture.Preferences.Get(NativeDiaryPresenter.FontSizePreferenceKey, 0));

        state = await diary.TogglePromptCollapsedAsync();
        Assert.True(state.Editor!.IsPromptCollapsed);
        Assert.True(fixture.Preferences.Get(NativeDiaryPresenter.PromptCollapsedPreferenceKey, false));

        // A stored out-of-range font size is clamped on open.
        fixture.Preferences.Set(NativeDiaryPresenter.FontSizePreferenceKey, 99);
        await diary.BackToListAsync();
        state = await diary.OpenTodayAsync();
        Assert.Equal(NativeDiaryPresenter.MaxFontSize, state.Editor!.FontSize);
        Assert.True(state.Editor.IsPromptCollapsed);
    }

    [Fact]
    public async Task Editor_AutosavesAfterTheDelay()
    {
        using var fixture = await SignedInAsync();
        using var diary = Presenter(fixture, new FakeDiaryAi());
        diary.AutosaveDelay = TimeSpan.FromMilliseconds(50);
        await diary.OpenAsync(Launch(NativeSliceFixture.UserA));
        await diary.OpenTodayAsync();

        diary.SetContent("하나");
        diary.SetContent("하나 둘");
        var watch = Stopwatch.StartNew();
        while (diary.State.Editor?.SaveStatus != NativeDiarySaveStatus.Saved
            && watch.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20);

        Assert.Equal(NativeDiarySaveStatus.Saved, diary.State.Editor!.SaveStatus);
        using var scope = fixture.Scope();
        var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DiaryEntries.AsNoTracking().SingleAsync();
        Assert.Equal("하나 둘", row.Content);
    }

    [Fact]
    public async Task Viewer_ShowsOwnedPastEntriesAndSavesFeedback()
    {
        using var fixture = await SignedInAsync();
        var today = DiaryEntryRepository.NormalizeEntryDate(DateTime.UtcNow);
        var yesterday = await SeedEntryAsync(fixture, NativeSliceFixture.UserA, today.AddDays(-1),
            "어제는 비가 왔어요");
        var older = await SeedEntryAsync(fixture, NativeSliceFixture.UserA, today.AddDays(-5),
            new string('가', 130));
        var foreign = await SeedEntryAsync(fixture, NativeSliceFixture.UserB, today.AddDays(-1), "남의 일기");
        var ai = new FakeDiaryAi();
        using var diary = Presenter(fixture, ai);

        var state = await diary.OpenAsync(Launch(NativeSliceFixture.UserA));
        var past = state.List!.PastEntries;
        Assert.Equal([yesterday, older], past.Select(entry => entry.Id));
        Assert.Equal(NativeDiaryText.Yesterday, past[0].DateKey);
        Assert.Null(past[1].DateKey);
        Assert.Equal(NativeDiaryPresenter.SnippetLength + 1, past[1].Snippet.Length);
        Assert.EndsWith("…", past[1].Snippet);
        Assert.Null(state.List.Today);

        state = await diary.OpenEntryAsync(yesterday);
        var viewer = state.Viewer!;
        Assert.False(viewer.NotFound);
        Assert.Equal("어제는 비가 왔어요", viewer.Content);
        Assert.False(viewer.HasFeedback);
        Assert.True(viewer.CanRequestFeedback);

        state = await diary.RequestFeedbackAsync();
        viewer = state.Viewer!;
        Assert.True(viewer.HasFeedback);
        Assert.Equal("Recommended rewrite", viewer.FeedbackRecommended);
        Assert.Equal("Notes", viewer.FeedbackNotes);
        Assert.Equal("Strengths", viewer.FeedbackStrengths);
        Assert.Equal([(NativeSliceFixture.UserA, "어제는 비가 왔어요", "Prompt?", "Korean", "English")],
            ai.Feedback);

        state = await diary.BackToListAsync();
        Assert.True(state.List!.PastEntries[0].HasFeedback);

        // Another owner's entry is "not found", never shown.
        state = await diary.OpenEntryAsync(foreign);
        Assert.True(state.Viewer!.NotFound);
        Assert.False(state.Viewer.CanRequestFeedback);
        using var scope = fixture.Scope();
        var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DiaryEntries.AsNoTracking().SingleAsync(entry => entry.Id == foreign);
        Assert.Null(row.FeedbackAt);
    }

    [Fact]
    public async Task AiFailures_ShowSourceNoticesAndKeepTheEditorUsable()
    {
        using var fixture = await SignedInAsync();
        var ai = new FakeDiaryAi { ReturnNull = true };
        using var diary = Presenter(fixture, ai);
        await diary.OpenAsync(Launch(NativeSliceFixture.UserA));

        var state = await diary.OpenTodayAsync();
        var notice = Assert.Single(state.Notices);
        Assert.Equal(new NativeDiaryNotice(NativeDiaryNoticeKind.Warning, NativeDiaryText.PromptFailed), notice);
        Assert.Null(state.Editor!.PromptText);

        ai.ReturnNull = false;
        ai.Fail = true;
        state = await diary.RefreshPromptAsync();
        Assert.Contains(new NativeDiaryNotice(NativeDiaryNoticeKind.Error, NativeDiaryText.PromptFailed),
            state.Notices);
        state = await diary.DismissNoticesAsync();
        Assert.Empty(state.Notices);

        diary.SetContent("그래도 써요");
        state = await diary.SaveAsync();
        Assert.Equal(NativeDiarySaveStatus.Saved, state.Editor!.SaveStatus);

        var past = await SeedEntryAsync(fixture, NativeSliceFixture.UserA,
            DiaryEntryRepository.NormalizeEntryDate(DateTime.UtcNow).AddDays(-2), "옛날 일기");
        await diary.BackToListAsync();
        await diary.OpenEntryAsync(past);
        state = await diary.RequestFeedbackAsync();
        Assert.Contains(new NativeDiaryNotice(NativeDiaryNoticeKind.Error, NativeDiaryText.FeedbackFailed),
            state.Notices);
        Assert.False(state.Viewer!.HasFeedback);
        Assert.False(state.Viewer.IsLoadingFeedback);
    }

    [Fact]
    public async Task HangingAi_ExitReturnsPromptlyAndFlushesTheDraft()
    {
        using var fixture = await SignedInAsync();
        var ai = new FakeDiaryAi { Hang = true };
        using var diary = Presenter(fixture, ai);
        await diary.OpenAsync(Launch(NativeSliceFixture.UserA));

        var opening = diary.OpenTodayAsync();
        var watch = Stopwatch.StartNew();
        while (diary.State.Editor is not { IsLoadingPrompt: true } && watch.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);
        Assert.True(diary.State.Editor!.IsLoadingPrompt);
        diary.SetContent("기다리는 중");

        var exit = diary.ExitAsync();
        Assert.Same(exit, await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.Equal(NativeDestination.TodaysPlan, (await exit).Destination);
        await opening;
        // The prompt request was cancelled; the late result never applies.
        Assert.True(ai.Cancelled);
        Assert.Equal(NativeDestination.TodaysPlan, diary.State.Destination);
        using var scope = fixture.Scope();
        var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DiaryEntries.AsNoTracking().SingleAsync();
        Assert.Equal("기다리는 중", row.Content);
        Assert.Null(row.PromptText);
    }

    [Fact]
    public async Task HangingFeedback_BackToListReturnsPromptly()
    {
        using var fixture = await SignedInAsync();
        var past = await SeedEntryAsync(fixture, NativeSliceFixture.UserA,
            DiaryEntryRepository.NormalizeEntryDate(DateTime.UtcNow).AddDays(-1), "어제");
        var ai = new FakeDiaryAi { Hang = true };
        using var diary = Presenter(fixture, ai);
        await diary.OpenAsync(Launch(NativeSliceFixture.UserA));
        await diary.OpenEntryAsync(past);

        var feedback = diary.RequestFeedbackAsync();
        var back = diary.BackToListAsync();
        Assert.Same(back, await Task.WhenAny(back, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.Equal(NativeDiaryPage.List, (await back).Page);
        await feedback;
        Assert.Equal(NativeDiaryPage.List, diary.State.Page);
        using var scope = fixture.Scope();
        Assert.Null((await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DiaryEntries.AsNoTracking().SingleAsync()).FeedbackAt);
    }

    [Fact]
    public async Task AbsentOrChangedOwner_FailsClosedWithoutRows()
    {
        using var fixture = await SignedInAsync();
        var ai = new FakeDiaryAi();
        using (var foreign = Presenter(fixture, ai))
            Assert.Equal(NativePhase.Blocked, (await foreign.OpenAsync(Launch(NativeSliceFixture.UserB))).Phase);

        using var diary = Presenter(fixture, ai);
        await diary.OpenAsync(Launch(NativeSliceFixture.UserA));
        await diary.OpenTodayAsync();
        diary.SetContent("비밀");
        fixture.LoseOwner();
        Assert.Equal(NativePhase.Blocked, diary.State.Phase);
        Assert.Null(diary.SetContent("더 비밀"));
        Assert.Equal(NativePhase.Blocked, (await diary.SaveAsync()).Phase);

        // An owner change while the prompt request is in flight discards it.
        fixture.Activate(NativeSliceFixture.UserA);
        using var second = Presenter(fixture, ai);
        await second.OpenAsync(Launch(NativeSliceFixture.UserA));
        ai.OnPrompt = fixture.LoseOwner;
        Assert.Equal(NativePhase.Blocked, (await second.OpenTodayAsync()).Phase);
        Assert.Equal(NativeDestination.TodaysPlan, (await second.ExitAsync()).Destination);

        Assert.Equal(0, await CountEntriesAsync(fixture, NativeSliceFixture.UserA));
        Assert.Equal(0, await CountEntriesAsync(fixture, NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task Store_RefusesAnEmptyOwnerAndAnotherOwnersRow()
    {
        using var fixture = await SignedInAsync();
        var store = fixture.Provider.GetRequiredService<NativeDiaryStore>();
        var foreign = await SeedEntryAsync(fixture, NativeSliceFixture.UserB,
            DiaryEntryRepository.NormalizeEntryDate(DateTime.UtcNow), "B");

        Assert.Empty(await store.ListAsync(string.Empty, default));
        Assert.Null(await store.GetByIdAsync(NativeSliceFixture.UserA, foreign, default));
        Assert.Null(await store.SaveFeedbackAsync(NativeSliceFixture.UserA, foreign, "x", "y", "z", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UpsertAsync(
            NativeSliceFixture.UserA, new DiaryEntry { Id = foreign, Content = "hijack" }, default));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.UpsertAsync(
            string.Empty, new DiaryEntry { Content = "nobody" }, default));
        using var scope = fixture.Scope();
        var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DiaryEntries.AsNoTracking().SingleAsync();
        Assert.Equal("B", row.Content);
        Assert.Null(row.FeedbackAt);
    }

    [Fact]
    public async Task DiaryAi_RendersTheSourceTemplates()
    {
        using var fixture = await SignedInAsync(resourceAi: true);
        var ai = fixture.Provider.GetRequiredService<INativeDiaryAi>();
        try
        {
            await ai.GetFeedbackAsync(NativeSliceFixture.UserA, "제 일기", "주제", "Korean", "English", default);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // The scratch gateway may not know the response type; only the rendered prompt matters.
            Assert.True(fixture.ResourceAi.LastPrompt is not null, ex.ToString());
        }
        var prompt = fixture.ResourceAi.LastPrompt!;
        Assert.Contains("supportive Korean writing tutor", prompt);
        Assert.Contains("제 일기", prompt);
        Assert.Contains("주제", prompt);
        Assert.Contains("written in English", prompt);
        Assert.Null(await ai.GetFeedbackAsync(NativeSliceFixture.UserA, " ", null, "Korean", "English", default));
    }

    private static string Words(int count) => string.Join(' ', Enumerable.Repeat("단어", count));

    private static async Task<NativeSliceFixture> SignedInAsync(bool resourceAi = false)
    {
        var fixture = new NativeSliceFixture(resourceAiEnabled: resourceAi);
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password")).Phase);
        return fixture;
    }

    private static NativeDiaryPresenter Presenter(NativeSliceFixture fixture, INativeDiaryAi ai) =>
        ActivatorUtilities.CreateInstance<NativeDiaryPresenter>(fixture.Provider, ai);

    private static NativeActivityLaunch Launch(string owner) =>
        new(Diary, owner, new DateOnly(2026, 11, 1), null, [], null, [], false, null);

    private static async Task<string> SeedEntryAsync(NativeSliceFixture fixture, string owner,
        DateTime date, string content)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entry = new DiaryEntry
        {
            Id = Guid.NewGuid().ToString(),
            UserProfileId = owner,
            EntryDate = date,
            Language = "Korean",
            PromptText = "Prompt?",
            Content = content,
            WordCount = DiaryEntryRepository.CountWords(content),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.DiaryEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private static async Task<int> CountEntriesAsync(NativeSliceFixture fixture, string owner)
    {
        using var scope = fixture.Scope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DiaryEntries.CountAsync(entry => entry.UserProfileId == owner);
    }

    private sealed class FakeDiaryAi : INativeDiaryAi
    {
        public List<(string Owner, string Target, string Native)> Prompts { get; } = [];
        public List<(string Owner, string Content, string? Prompt, string Target, string Native)> Feedback { get; } = [];
        public bool Fail { get; set; }
        public bool ReturnNull { get; set; }
        public bool Hang { get; set; }
        public bool Cancelled { get; private set; }
        public Action? OnPrompt { get; set; }

        public async Task<DiaryPromptResponse?> GeneratePromptAsync(string owner, string targetLanguage,
            string nativeLanguage, CancellationToken cancellationToken)
        {
            await HangAsync(cancellationToken);
            if (Fail)
                throw new HttpRequestException("Synthetic AI offline.");
            Prompts.Add((owner, targetLanguage, nativeLanguage));
            OnPrompt?.Invoke();
            return ReturnNull ? null
                : new DiaryPromptResponse { Prompt = "오늘 뭐 먹었어요?", Hint = "What did you eat today?" };
        }

        public async Task<DiaryFeedbackResponse?> GetFeedbackAsync(string owner, string content,
            string? promptText, string targetLanguage, string nativeLanguage,
            CancellationToken cancellationToken)
        {
            await HangAsync(cancellationToken);
            if (Fail)
                throw new HttpRequestException("Synthetic AI offline.");
            Feedback.Add((owner, content, promptText, targetLanguage, nativeLanguage));
            return ReturnNull ? null : new DiaryFeedbackResponse
            {
                Recommended = "Recommended rewrite", Notes = "Notes", Strengths = "Strengths"
            };
        }

        private async Task HangAsync(CancellationToken cancellationToken)
        {
            if (!Hang)
                return;
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }
    }
}
