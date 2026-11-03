using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services.Numbers;
using SentenceStudio.WebUI.Pages;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Number Drill fixes ported from the native presenter (docs/native-port-backports.md
/// B18-B23, B34, B37), exercised on the real page with real NumberSessionService and
/// SQLite storage.
/// </summary>
public sealed class NumberDrillPageTests
{
    [Fact]
    public async Task NonKoreanLearner_GetsTheKoreanOnlyMessage_AndNoTimerStarts()
    {
        await using var harness = await CreateAsync(targetLanguage: "Spanish");
        await harness.RenderAsync<NumberDrill>();

        var html = harness.Html();
        html.Should().Contain("id=\"numberdrill-gate\"");
        html.Should().Contain(Text(harness, "NumberDrill_KoreanOnly"));
        html.Should().NotContain(Text(harness, "NumberDrill_ChooseDrill"));
        harness.Timer.IsActive.Should().BeFalse("a learner who cannot practice records no minutes");
    }

    [Fact]
    public async Task NoActiveProfile_IsRefused_AndNothingIsWritten()
    {
        await using var harness = await CreateAsync();
        harness.Preferences.Remove(SentenceStudio.Data.UserProfileRepository.ActiveProfileIdKey);
        await harness.RenderAsync<NumberDrill>();

        harness.Html().Should().Contain(Text(harness, "NativeActivity_LaunchUnavailable"));
        (await harness.WithDbAsync(db => db.NumberAttempts.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task ModePicker_ListsModesInSeedOrder_WhateverTheStoredRowOrder()
    {
        await using var harness = await CreateAsync();
        // Store the sub-modes in reverse seed order (a query without ORDER BY returns them so).
        await harness.WithDbAsync(async db =>
        {
            var modes = await db.NumberSubModes.AsNoTracking().ToListAsync();
            db.NumberSubModes.RemoveRange(await db.NumberSubModes.ToListAsync());
            await db.SaveChangesAsync();
            foreach (var mode in modes.OrderByDescending(m => NumberSubModeOrder.SeedPosition(m.Code)))
            {
                db.NumberSubModes.Add(mode);
                await db.SaveChangesAsync();
            }
            return 0;
        });

        await harness.RenderAsync<NumberDrill>();
        ModeButtons(harness.Html()).Should().Equal("ListenAndType", "ReadAndProduce", "Disambiguate");

        await harness.DispatchAsync("OnContextSelected", "Time");
        ModeButtons(harness.Html()).Should().Equal(
            "ListenAndType", "ReadAndProduce", "Disambiguate", "ListenAndPlace");
    }

    [Fact]
    public async Task ListenAndPlace_TappedTimeCard_IsCountedWithFeedback_AndTheSummaryAddsUp()
    {
        await using var harness = await CreateAsync();
        await StartAsync(harness, "Time", "ListenAndPlace", items: 2);

        var first = harness.Get<NumberItem>("currentItem");
        await harness.DispatchAsync("TapTimeCard", first.CanonicalAnswer);

        harness.Get<int>("currentIndex").Should().Be(1, "the tap auto-advances after the pause");
        var second = harness.Get<NumberItem>("currentItem");
        var wrong = second.CounterChoices!.First(choice => choice != second.CanonicalAnswer);
        var tap = harness.DispatchAsync("TapTimeCard", wrong);
        await harness.WaitUntilAsync(() => harness.Get<bool>("showFeedback"));

        var html = harness.Html();
        html.Should().Contain("id=\"numberdrill-feedback\"");
        html.Should().Contain("alert-danger");
        html.Should().Contain(second.CanonicalAnswer);
        html.Should().Contain(harness.Get<BlazorLocalizationService>("Localize").Get("NumberDrill_CorrectCount", 1));
        await tap;

        harness.Html().Should().Contain("id=\"numberdrill-summary\"");
        harness.Get<NumberSessionSummary>("sessionSummary").Should().Match<NumberSessionSummary>(
            summary => summary.CorrectCount == 1 && summary.TotalItems == 2);
        var attempts = await harness.WithDbAsync(db => db.NumberAttempts.AsNoTracking()
            .Where(a => a.UserProfileId == ActivityPageHarness.OwnerA).ToListAsync());
        attempts.Select(a => a.IsCorrect).Should().BeEquivalentTo([true, false]);
        attempts.Should().OnlyContain(a => a.SubModeCode == "ListenAndPlace");
    }

    [Fact]
    public async Task Next_DuringTheAutoAdvancePause_MovesExactlyOneItem()
    {
        await using var harness = await CreateAsync();
        await StartAsync(harness, "Counting", "TapTheCounter", items: 3);
        harness.Set("_autoAdvanceDelay", TimeSpan.FromMilliseconds(300));

        var item = harness.Get<NumberItem>("currentItem");
        var tap = harness.DispatchAsync("TapCounter", item.CounterChoices!.First());
        await harness.WaitUntilAsync(() => harness.Get<bool>("showFeedback"));
        harness.Html().Should().Contain("id=\"numberdrill-next\"", "feedback and Next show during the pause");

        await harness.DispatchAsync("NextItemAsync");
        harness.Get<int>("currentIndex").Should().Be(1);
        await tap;

        harness.Get<int>("currentIndex").Should().Be(1, "the superseded auto-advance must not skip an item");
        harness.Get<bool>("showFeedback").Should().BeFalse();
        harness.Html().Should().Contain(">2 / 3<");
    }

    [Fact]
    public async Task Disambiguate_ShowsEachPromptsHintWithTheResult()
    {
        await using var harness = await CreateAsync();
        await StartAsync(harness, null, "Disambiguate", items: 1);
        var item = harness.Get<NumberItem>("currentItem");
        harness.Html().Should().NotContain(item.HintA!, "hints appear only after Submit");

        await harness.DispatchAsync("SelectAnswerA", item.CorrectAnswerA);
        await harness.DispatchAsync("SelectAnswerB", item.ChoicesB!.First(choice => choice != item.CorrectAnswerB));
        await harness.DispatchAsync("SubmitBothAsync");

        var html = harness.Html();
        html.Should().Contain("id=\"numberdrill-hint-a\"");
        html.Should().Contain("id=\"numberdrill-hint-b\"");
        html.Should().Contain(item.HintA!);
        html.Should().Contain(item.HintB!);
        html.Should().Contain(Text(harness, "NumberDrillPartialCorrect"));
        html.Should().Contain(Text(harness, "NumberDrill_DisambiguateTip"));
    }

    [Fact]
    public async Task IWasCorrect_RegradesTheStoredAttemptAndMastery_ThenAdvances()
    {
        await using var harness = await CreateAsync();
        await StartAsync(harness, "Age", "ReadAndProduce", items: 2);
        await SubmitTypedAsync(harness, "틀림");
        harness.Get<SentenceStudio.Services.Numbers.GradeResult>("lastGrade").IsCorrect.Should().BeFalse();
        harness.Html().Should().Contain("id=\"numberdrill-override\"");

        await harness.DispatchAsync("OverrideAsCorrect");

        var attempt = await harness.WithDbAsync(db => db.NumberAttempts.AsNoTracking().SingleAsync());
        attempt.IsCorrect.Should().BeTrue();
        attempt.UserProfileId.Should().Be(ActivityPageHarness.OwnerA);
        var mastery = await harness.WithDbAsync(db => db.NumberMasteryProgresses.AsNoTracking().SingleAsync());
        mastery.CorrectCount.Should().Be(1);
        mastery.TotalCount.Should().Be(1);
        harness.Get<int>("correctCount").Should().Be(1);
        harness.Get<int>("currentIndex").Should().Be(1, "a stored correction auto-advances");
    }

    [Fact]
    public async Task IWasCorrect_WhenTheCorrectionIsRefused_SaysSo_AndKeepsTheStoredGrade()
    {
        await using var harness = await CreateAsync();
        await StartAsync(harness, "Age", "ReadAndProduce", items: 2);
        await SubmitTypedAsync(harness, "틀림");
        // The row changed since the attempt (another device synced, for example).
        await harness.WithDbAsync(async db =>
        {
            var row = await db.NumberMasteryProgresses.SingleAsync();
            row.TotalCount += 1;
            return await db.SaveChangesAsync();
        });

        await harness.DispatchAsync("OverrideAsCorrect");

        harness.Html().Should().Contain(Text(harness, "NumberDrill_OverrideFailed"));
        harness.Get<SentenceStudio.Services.Numbers.GradeResult>("lastGrade").IsCorrect.Should().BeFalse();
        harness.Get<int>("correctCount").Should().Be(0);
        harness.Get<int>("currentIndex").Should().Be(0, "the learner continues with Next");
        (await harness.WithDbAsync(db => db.NumberAttempts.AsNoTracking().SingleAsync()))
            .IsCorrect.Should().BeFalse();
    }

    [Fact]
    public async Task IWasCorrect_DuringATappedChoicesPause_ReplacesItsAutoAdvance()
    {
        await using var harness = await CreateAsync();
        await StartAsync(harness, "Counting", "TapTheCounter", items: 2);
        harness.Set("_autoAdvanceDelay", TimeSpan.FromMilliseconds(300));
        var item = harness.Get<NumberItem>("currentItem");
        var tap = harness.DispatchAsync("TapCounter", item.CounterChoices!.First(c => c != item.CounterText));
        await harness.WaitUntilAsync(() => harness.Get<bool>("showFeedback"));
        await harness.WithDbAsync(async db =>
        {
            var row = await db.NumberMasteryProgresses.SingleAsync();
            row.TotalCount += 1;
            return await db.SaveChangesAsync();
        });

        await harness.DispatchAsync("OverrideAsCorrect");
        await tap;

        harness.Get<int>("currentIndex").Should().Be(0, "the refused correction stays on screen until Next");
        harness.Html().Should().Contain(Text(harness, "NumberDrill_OverrideFailed"));
    }

    [Fact]
    public async Task Summary_ShowsTheSessionsMedianTimeAndPatternsToReview()
    {
        await using var harness = await CreateAsync();
        await StartAsync(harness, "Age", "ReadAndProduce", items: 2);
        await SubmitTypedAsync(harness, "틀림", answeredAfter: TimeSpan.FromSeconds(4));
        await harness.DispatchAsync("NextItemAsync");
        await SubmitTypedAsync(harness, "아니", answeredAfter: TimeSpan.FromSeconds(6));
        await harness.DispatchAsync("NextItemAsync");

        var summary = harness.Get<NumberSessionSummary>("sessionSummary");
        summary.MedianLatencyMs.Should().BeInRange(4900, 5500);
        summary.ErrorClassBreakdown.Values.Sum().Should().Be(2);
        var html = harness.Html();
        html.Should().Contain("id=\"numberdrill-summary-patterns\"");
        html.Should().Contain(Text(harness, "NumberDrill_PatternsToReview"));
        html.Should().NotContain(">0 ms<");
    }

    [Fact]
    public async Task AnswerAfterTheActiveProfileChanged_IsRefused_AndNotStored()
    {
        await using var harness = await CreateAsync();
        await StartAsync(harness, "Age", "ReadAndProduce", items: 1);
        harness.Preferences.Set(SentenceStudio.Data.UserProfileRepository.ActiveProfileIdKey,
            ActivityPageHarness.OwnerB);

        await SubmitTypedAsync(harness, "스무 살");

        harness.Get<bool>("showFeedback").Should().BeFalse();
        harness.Html().Should().Contain(Text(harness, "NativeActivity_LaunchUnavailable"));
        (await harness.WithDbAsync(db => db.NumberAttempts.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task SetupCopy_FollowsTheLearnersDisplayLanguage()
    {
        await using var harness = await CreateAsync();
        await harness.RenderAsync<NumberDrill>();
        var localize = harness.Get<BlazorLocalizationService>("Localize");

        await harness.DispatchAsync("OnCultureChanged");
        harness.Html().Should().Contain(localize["NumberDrill_ChooseDrill"]);

        localize.SetCulture("ko");
        await harness.WaitUntilAsync(() => harness.Html().Contains("연습 선택"));
        harness.Html().Should().Contain("연습 시작").And.NotContain("Start Session");
    }

    private static async Task<ActivityPageHarness> CreateAsync(string targetLanguage = "Korean")
    {
        var harness = await ActivityPageHarness.CreateAsync(services =>
        {
            services.AddSingleton<INumberItemGenerator>(
                new KoreanNumberItemGenerator(NullLogger<KoreanNumberItemGenerator>.Instance));
            services.AddSingleton<INumberAnswerGrader, KoreanNumberAnswerGrader>();
            services.AddScoped<NumberSessionService>();
        }, targetLanguage);
        await harness.WithDbAsync(async db =>
        {
            await new NumberContentSeeder(db, NullLogger<NumberContentSeeder>.Instance).SeedAsync("ko");
            return 0;
        });
        return harness;
    }

    private static async Task StartAsync(ActivityPageHarness harness, string? context, string mode, int items)
    {
        await harness.RenderAsync<NumberDrill>();
        await harness.DispatchAsync("OnContextSelected", context);
        harness.Set("selectedSubMode", mode);
        harness.Set("itemCount", items);
        await harness.DispatchAsync("StartSessionAsync");
        harness.Get<NumberSession?>("session").Should().NotBeNull(harness.Html());
        harness.Set("_autoAdvanceDelay", TimeSpan.FromMilliseconds(20));
    }

    private static async Task SubmitTypedAsync(ActivityPageHarness harness, string answer, TimeSpan? answeredAfter = null)
    {
        if (answeredAfter is { } elapsed)
            harness.Set("itemStartTime", DateTime.UtcNow - elapsed);
        harness.Set("userAnswer", answer);
        await harness.DispatchAsync("SubmitAnswerAsync");
    }

    private static string Text(ActivityPageHarness harness, string key) =>
        harness.Get<BlazorLocalizationService>("Localize")[key];

    private static List<string> ModeButtons(string html) =>
        System.Text.RegularExpressions.Regex.Matches(html, "data-submode=\"([A-Za-z]+)\"")
            .Select(match => match.Groups[1].Value).ToList();
}
