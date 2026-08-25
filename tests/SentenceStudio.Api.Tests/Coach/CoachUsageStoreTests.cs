using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Persistence;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>Daily and weekly coach usage counters, scoped to the calling learner.</summary>
public class CoachUsageStoreTests
{
    private static readonly DateOnly Monday = new(2026, 8, 10);
    private static readonly DateOnly Wednesday = new(2026, 8, 12);
    private static readonly DateOnly NextMonday = new(2026, 8, 17);

    [Fact]
    public async Task RecordRunAsync_AccumulatesRunsTokensAndCostPerDay()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewUsageStore(db);

        await store.RecordRunAsync(CoachPersistenceSamples.OwnerUserId, Monday, 100, 40, 0.002m);
        await store.RecordRunAsync(CoachPersistenceSamples.OwnerUserId, Monday, 250, 60, 0.005m);

        var daily = await store.GetDailyTotalsAsync(CoachPersistenceSamples.OwnerUserId, Monday);

        daily.RunCount.Should().Be(2);
        daily.InputTokens.Should().Be(350);
        daily.OutputTokens.Should().Be(100);
        daily.TotalTokens.Should().Be(450);
        daily.EstimatedCostUsd.Should().Be(0.007m);

        (await db.CoachUsages.CountAsync()).Should().Be(1, "one row per learner per learner-local date");
    }

    [Fact]
    public async Task GetWeeklyTotalsAsync_SumsTheIsoWeekOnly()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewUsageStore(db);

        await store.RecordRunAsync(CoachPersistenceSamples.OwnerUserId, Monday, 10, 5, 0.001m);
        await store.RecordRunAsync(CoachPersistenceSamples.OwnerUserId, Wednesday, 20, 5, 0.002m);
        await store.RecordRunAsync(CoachPersistenceSamples.OwnerUserId, NextMonday, 999, 999, 1.000m);

        var weekly = await store.GetWeeklyTotalsAsync(CoachPersistenceSamples.OwnerUserId, Wednesday);

        weekly.RunCount.Should().Be(2);
        weekly.InputTokens.Should().Be(30);
        weekly.EstimatedCostUsd.Should().Be(0.003m);

        var nextWeek = await store.GetWeeklyTotalsAsync(CoachPersistenceSamples.OwnerUserId, NextMonday);
        nextWeek.RunCount.Should().Be(1);
    }

    [Fact]
    public async Task Counters_AreScopedToTheCallingLearner()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewUsageStore(db);

        await store.RecordRunAsync(CoachPersistenceSamples.OwnerUserId, Monday, 100, 100, 0.01m);
        await store.RecordRunAsync(CoachPersistenceSamples.OtherUserId, Monday, 500, 500, 0.05m);

        var mine = await store.GetDailyTotalsAsync(CoachPersistenceSamples.OwnerUserId, Monday);
        mine.RunCount.Should().Be(1);
        mine.InputTokens.Should().Be(100);

        var myWeek = await store.GetWeeklyTotalsAsync(CoachPersistenceSamples.OwnerUserId, Monday);
        myWeek.InputTokens.Should().Be(100, "another learner's usage must never count against mine");
    }

    [Fact]
    public async Task EmptyUserId_RecordsNothingAndReadsZero()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewUsageStore(db);

        (await store.RecordRunAsync("  ", Monday, 10, 10, 0.01m)).Should().BeNull();
        (await db.CoachUsages.CountAsync()).Should().Be(0);

        (await store.GetDailyTotalsAsync("", Monday)).Should().Be(CoachUsageTotals.Empty);
        (await store.GetWeeklyTotalsAsync("", Monday)).Should().Be(CoachUsageTotals.Empty);
    }

    [Fact]
    public async Task UnusedDay_ReturnsEmptyTotals()
    {
        using var harness = new CoachPersistenceHarness();
        using var db = harness.NewContext();
        var store = harness.NewUsageStore(db);

        (await store.GetDailyTotalsAsync(CoachPersistenceSamples.OwnerUserId, Monday))
            .Should().Be(CoachUsageTotals.Empty);
    }

    [Theory]
    [InlineData(2026, 8, 10, "2026-W33")]
    [InlineData(2026, 8, 16, "2026-W33")]
    [InlineData(2026, 8, 17, "2026-W34")]
    public void WeekKey_UsesIsoWeeks(int year, int month, int day, string expected)
    {
        CoachNormalizedJson.WeekKey(new DateOnly(year, month, day)).Should().Be(expected);
    }
}
