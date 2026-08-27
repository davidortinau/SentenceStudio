using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Budget behaviour for the Stage 1 in-memory <see cref="ICoachBudgetService"/>: one concurrent run
/// per learner, daily and weekly run caps, token and cost accounting, period rollover, and release
/// on cancellation.
/// </summary>
/// <remarks>
/// These tests describe the contract, not the storage. When the PostgreSQL <c>CoachUsage</c>-backed
/// implementation lands it should satisfy the same assertions — with the added guarantee, which the
/// in-memory version deliberately does not claim, that the limits hold across instances.
/// </remarks>
public class CoachBudgetServiceTests
{
    private const string UserA = "profile-a";
    private const string UserB = "profile-b";

    private static readonly DateOnly Monday = new(2026, 8, 10);
    private static readonly DateOnly Tuesday = new(2026, 8, 11);
    private static readonly DateOnly NextMonday = new(2026, 8, 17);
    private static readonly DateTimeOffset Start = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    private static (InMemoryCoachBudgetService Service, TestTimeProvider Clock) Create(
        int maxRunsPerDay = 10,
        int maxRunsPerWeek = 40)
    {
        var monitor = new TestOptionsMonitor<CoachOptions>(new CoachOptions
        {
            Enabled = true,
            MaxRunsPerDay = maxRunsPerDay,
            MaxRunsPerWeek = maxRunsPerWeek
        });

        var clock = new TestTimeProvider(Start);
        return (new InMemoryCoachBudgetService(monitor, clock), clock);
    }

    [Fact]
    public async Task TryStartRun_GrantsASlotAndChargesOneRun()
    {
        var (service, _) = Create();

        var result = await service.TryStartRunAsync(UserA, Monday);

        result.Acquired.Should().BeTrue();
        result.Lease.Should().NotBeNull();
        result.DeniedReason.Should().BeNull();
        result.Snapshot.Day.Runs.Should().Be(1);
        result.Snapshot.Week.Runs.Should().Be(1);
        result.Snapshot.HasActiveRun.Should().BeTrue();
        result.Snapshot.WeekKey.Should().Be("2026-W33");
    }

    [Fact]
    public async Task TryStartRun_WhileARunIsInFlight_DeniesWithConcurrencyLimit()
    {
        var (service, _) = Create();
        var first = await service.TryStartRunAsync(UserA, Monday);

        var second = await service.TryStartRunAsync(UserA, Monday);

        second.Acquired.Should().BeFalse();
        second.DeniedReason.Should().Be(CoachStopReason.ConcurrencyLimit);
        second.Lease.Should().BeNull();
        second.Snapshot.Day.Runs.Should().Be(1, "a refused run must not consume the daily budget");

        await first.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task DisposingTheLease_ReleasesTheConcurrencySlot()
    {
        var (service, _) = Create();
        var first = await service.TryStartRunAsync(UserA, Monday);

        await first.Lease!.DisposeAsync();
        first.Lease.IsReleased.Should().BeTrue();

        var second = await service.TryStartRunAsync(UserA, Monday);
        second.Acquired.Should().BeTrue();
        second.Snapshot.Day.Runs.Should().Be(2);
    }

    [Fact]
    public async Task ConcurrencyIsPerLearner()
    {
        var (service, _) = Create();
        var a = await service.TryStartRunAsync(UserA, Monday);

        var b = await service.TryStartRunAsync(UserB, Monday);

        b.Acquired.Should().BeTrue();
        b.Snapshot.Day.Runs.Should().Be(1, "each learner has an independent budget");

        await a.Lease!.DisposeAsync();
        await b.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task DailyCap_DeniesWithRateLimitAndResetsOnTheNextUserLocalDay()
    {
        var (service, _) = Create(maxRunsPerDay: 2, maxRunsPerWeek: 40);

        await using (var run1 = (await service.TryStartRunAsync(UserA, Monday)).Lease!) { }
        await using (var run2 = (await service.TryStartRunAsync(UserA, Monday)).Lease!) { }

        var denied = await service.TryStartRunAsync(UserA, Monday);
        denied.Acquired.Should().BeFalse();
        denied.DeniedReason.Should().Be(CoachStopReason.RateLimit);
        denied.Snapshot.RunsRemainingToday.Should().Be(0);

        var nextDay = await service.TryStartRunAsync(UserA, Tuesday);
        nextDay.Acquired.Should().BeTrue("the daily counter resets on the learner's next local day");
        nextDay.Snapshot.Day.Runs.Should().Be(1);
        nextDay.Snapshot.Week.Runs.Should().Be(3, "the weekly counter keeps accumulating across days");

        await nextDay.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task WeeklyCap_DeniesWithRateLimitAndResetsOnTheNextIsoWeek()
    {
        var (service, _) = Create(maxRunsPerDay: 10, maxRunsPerWeek: 2);

        await using (var run1 = (await service.TryStartRunAsync(UserA, Monday)).Lease!) { }
        await using (var run2 = (await service.TryStartRunAsync(UserA, Tuesday)).Lease!) { }

        var denied = await service.TryStartRunAsync(UserA, Tuesday);
        denied.Acquired.Should().BeFalse();
        denied.DeniedReason.Should().Be(CoachStopReason.RateLimit);
        denied.Snapshot.RunsRemainingThisWeek.Should().Be(0);

        var nextWeek = await service.TryStartRunAsync(UserA, NextMonday);
        nextWeek.Acquired.Should().BeTrue();
        nextWeek.Snapshot.Week.Runs.Should().Be(1);
        nextWeek.Snapshot.WeekKey.Should().Be("2026-W34");

        await nextWeek.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task RecordUsage_AccumulatesTokensAndCostAcrossBothPeriods()
    {
        var (service, _) = Create();

        await using (var lease = (await service.TryStartRunAsync(UserA, Monday)).Lease!)
        {
            await lease.RecordUsageAsync(new CoachRunUsage(1_000, 400, 0.012m));
            await lease.RecordUsageAsync(new CoachRunUsage(200, 50, 0.003m));
        }

        var snapshot = await service.GetSnapshotAsync(UserA, Monday);

        snapshot.Day.InputTokens.Should().Be(1_200);
        snapshot.Day.OutputTokens.Should().Be(450);
        snapshot.Day.EstimatedCostUsd.Should().Be(0.015m);
        snapshot.Week.InputTokens.Should().Be(1_200);
        snapshot.Week.EstimatedCostUsd.Should().Be(0.015m);
    }

    [Fact]
    public async Task RecordUsage_AfterRelease_IsIgnored()
    {
        var (service, _) = Create();
        var run = await service.TryStartRunAsync(UserA, Monday);
        var lease = run.Lease!;

        await lease.DisposeAsync();
        await lease.RecordUsageAsync(new CoachRunUsage(500, 500, 1m));

        var snapshot = await service.GetSnapshotAsync(UserA, Monday);
        snapshot.Day.InputTokens.Should().Be(0, "a late completion callback must not charge a period the run no longer owns");
        snapshot.Day.EstimatedCostUsd.Should().Be(0m);
    }

    [Fact]
    public async Task CancelledRun_ReleasesTheSlotButStillConsumesADailyRun()
    {
        var (service, _) = Create(maxRunsPerDay: 2, maxRunsPerWeek: 40);
        using var cts = new CancellationTokenSource();

        var run = await service.TryStartRunAsync(UserA, Monday, cts.Token);
        await cts.CancelAsync();

        // The caller's finally / await-using path runs even on cancellation.
        await run.Lease!.DisposeAsync();

        var afterCancel = await service.GetSnapshotAsync(UserA, Monday);
        afterCancel.HasActiveRun.Should().BeFalse("the slot must be released however the run ended");
        afterCancel.Day.Runs.Should().Be(1, "charging at acquisition stops cancel-and-retry from walking past the cap");

        var next = await service.TryStartRunAsync(UserA, Monday);
        next.Acquired.Should().BeTrue();
        await next.Lease!.DisposeAsync();

        var exhausted = await service.TryStartRunAsync(UserA, Monday);
        exhausted.DeniedReason.Should().Be(CoachStopReason.RateLimit);
    }

    [Fact]
    public async Task TryStartRun_WithAlreadyCancelledToken_Throws()
    {
        var (service, _) = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await service.TryStartRunAsync(UserA, Monday, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DisposingTheLeaseTwice_IsSafeAndDoesNotFreeANewerRunsSlot()
    {
        var (service, _) = Create();
        var first = await service.TryStartRunAsync(UserA, Monday);
        await first.Lease!.DisposeAsync();

        var second = await service.TryStartRunAsync(UserA, Monday);
        await first.Lease.DisposeAsync();

        var snapshot = await service.GetSnapshotAsync(UserA, Monday);
        snapshot.HasActiveRun.Should().BeTrue("a stale lease must not release the slot held by a newer run");

        await second.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task AbandonedRun_IsReclaimedAfterTheTimeoutPlusGrace()
    {
        var (service, clock) = Create();
        var options = new CoachOptions();
        await service.TryStartRunAsync(UserA, Monday);

        var stillHeld = await service.GetSnapshotAsync(UserA, Monday);
        stillHeld.HasActiveRun.Should().BeTrue();

        clock.Advance(options.RequestTimeout + InMemoryCoachBudgetService.AbandonedRunGrace);

        var reclaimed = await service.TryStartRunAsync(UserA, Monday);
        reclaimed.Acquired.Should().BeTrue("a crashed run must not lock the learner out until the process restarts");

        await reclaimed.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task Snapshot_DoesNotReserveAnything()
    {
        var (service, _) = Create();

        var snapshot = await service.GetSnapshotAsync(UserA, Monday);

        snapshot.Day.Runs.Should().Be(0);
        snapshot.HasActiveRun.Should().BeFalse();
        snapshot.RunsRemainingToday.Should().Be(10);
        snapshot.RunsRemainingThisWeek.Should().Be(40);
        snapshot.MaxRunsPerDay.Should().Be(10);
        snapshot.MaxRunsPerWeek.Should().Be(40);
    }

    [Fact]
    public async Task Snapshot_ReflectsALiveCapChange()
    {
        var monitor = new TestOptionsMonitor<CoachOptions>(new CoachOptions { MaxRunsPerDay = 1, MaxRunsPerWeek = 5 });
        var service = new InMemoryCoachBudgetService(monitor, new TestTimeProvider(Start));

        await using (var lease = (await service.TryStartRunAsync(UserA, Monday)).Lease!) { }
        (await service.TryStartRunAsync(UserA, Monday)).DeniedReason.Should().Be(CoachStopReason.RateLimit);

        monitor.Set(new CoachOptions { MaxRunsPerDay = 5, MaxRunsPerWeek = 5 });

        var afterRaise = await service.TryStartRunAsync(UserA, Monday);
        afterRaise.Acquired.Should().BeTrue();
        await afterRaise.Lease!.DisposeAsync();
    }

    [Theory]
    [InlineData(2026, 8, 10, "2026-W33")]
    [InlineData(2026, 8, 16, "2026-W33")]
    [InlineData(2026, 8, 17, "2026-W34")]
    [InlineData(2027, 1, 3, "2026-W53")]
    public void GetWeekKey_UsesIsoWeeks(int year, int month, int day, string expected)
        => InMemoryCoachBudgetService.GetWeekKey(new DateOnly(year, month, day)).Should().Be(expected);

    [Fact]
    public async Task TryStartRun_WithoutAUserProfileId_Throws()
    {
        var (service, _) = Create();

        var act = async () => await service.TryStartRunAsync("   ", Monday);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void RunUsage_AddsComponentwise()
    {
        var total = new CoachRunUsage(10, 5, 0.1m).Add(new CoachRunUsage(1, 2, 0.02m));

        total.Should().Be(new CoachRunUsage(11, 7, 0.12m));
        CoachRunUsage.None.Should().Be(new CoachRunUsage(0, 0, 0m));
    }

    [Fact]
    public async Task ConcurrentStartAttempts_GrantExactlyOneSlot()
    {
        var (service, _) = Create(maxRunsPerDay: 50, maxRunsPerWeek: 50);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
            await service.TryStartRunAsync(UserA, Monday)));

        attempts.Count(a => a.Acquired).Should().Be(1);
        attempts.Where(a => !a.Acquired).Should().OnlyContain(a => a.DeniedReason == CoachStopReason.ConcurrencyLimit);

        foreach (var granted in attempts.Where(a => a.Acquired))
        {
            await granted.Lease!.DisposeAsync();
        }
    }
}
