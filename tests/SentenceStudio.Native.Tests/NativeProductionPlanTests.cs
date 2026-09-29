using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeProductionPlanTests
{
    private static readonly DateOnly LocalDate = new(2026, 9, 28);
    private static readonly DateTime DateKey =
        LocalDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    [Fact]
    public async Task CreateIfAbsent_PersistsSeparatePlansForTwoOwnersWithTheSameItemId()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var creation = NewAdapter(fixture);

        fixture.Activate(NativeSliceFixture.UserA);
        var first = await creation.CreateIfAbsentAsync(
            NativeSliceFixture.UserA, LocalDate, CancellationToken.None);
        Assert.True(first.Created);
        Assert.Equal(LocalDate, first.Plan.GeneratedForDate);
        Assert.Contains(fixture.WordId(NativeSliceFixture.UserA, 0),
            first.Plan.FocusVocabularyIds);
        var itemA = Assert.Single(first.Plan.Items);

        fixture.Activate(NativeSliceFixture.UserB);
        var second = await creation.CreateIfAbsentAsync(
            NativeSliceFixture.UserB, LocalDate, CancellationToken.None);
        Assert.True(second.Created);
        Assert.Equal(LocalDate, second.Plan.GeneratedForDate);
        Assert.Contains(fixture.WordId(NativeSliceFixture.UserB, 0),
            second.Plan.FocusVocabularyIds);
        var itemB = Assert.Single(second.Plan.Items);

        // Stable plan-item IDs can coincide between owners; the persisted
        // primary keys and every query must still remain owner-scoped.
        Assert.Equal(itemA.Id, itemB.Id);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var plans = await db.DailyPlans.AsNoTracking()
            .Where(row => row.Date == DateKey).ToListAsync();
        var items = await db.DailyPlanCompletions.AsNoTracking()
            .Where(row => row.Date == DateKey).ToListAsync();
        Assert.Equal(2, plans.Count);
        Assert.Equal(2, items.Count);
        Assert.Equal(2, plans.Select(row => row.UserProfileId).Distinct().Count());
        Assert.Equal(2, items.Select(row => row.Id).Distinct().Count());
        Assert.All(items, row => Assert.Equal(itemA.Id, row.PlanItemId));
    }

    [Fact]
    public async Task MissingForeignOrStaleOwnerAndWrongLocalDate_RejectBeforeAnyPlanWrite()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        var creation = NewAdapter(fixture);
        fixture.Activate(NativeSliceFixture.UserA);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            creation.CreateIfAbsentAsync("", LocalDate, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            creation.CreateIfAbsentAsync(" ", LocalDate, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            creation.CreateIfAbsentAsync(
                NativeSliceFixture.UserB, LocalDate, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            creation.CreateIfAbsentAsync(
                NativeSliceFixture.UserA, default, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            creation.CreateIfAbsentAsync(
                NativeSliceFixture.UserA, LocalDate.AddDays(1), CancellationToken.None));

        fixture.Preferences.Set(
            UserProfileRepository.ActiveProfileIdKey, NativeSliceFixture.UserB);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            creation.CreateIfAbsentAsync(
                NativeSliceFixture.UserA, LocalDate, CancellationToken.None));

        fixture.Activate("not-a-local-profile");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            creation.CreateIfAbsentAsync(
                "not-a-local-profile", LocalDate, CancellationToken.None));
        fixture.LoseOwner();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            creation.CreateIfAbsentAsync(
                NativeSliceFixture.UserA, LocalDate, CancellationToken.None));

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.DailyPlans.AsNoTracking().ToListAsync());
        Assert.Empty(await db.DailyPlanCompletions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task OrphanedCompletions_BlockOnlyTheirOwnerAndDateWithoutDeletingHistory()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.DailyPlanCompletions.AddRange(
                new DailyPlanCompletion
                {
                    Id = "orphan-owner-a",
                    UserProfileId = NativeSliceFixture.UserA,
                    Date = DateKey,
                    PlanItemId = "displaced-work",
                    ActivityType = "VocabularyReview",
                    MinutesSpent = 6,
                    EstimatedMinutes = 10,
                    Priority = 1,
                    CreatedAt = DateKey.AddHours(8),
                    UpdatedAt = DateKey.AddHours(9)
                },
                new DailyPlanCompletion
                {
                    Id = "orphan-owner-b-yesterday",
                    UserProfileId = NativeSliceFixture.UserB,
                    Date = DateKey.AddDays(-1),
                    PlanItemId = "previous-day-work",
                    ActivityType = "VocabularyReview",
                    IsCompleted = true,
                    CompletedAt = DateKey.AddHours(-2),
                    MinutesSpent = 8,
                    EstimatedMinutes = 10,
                    Priority = 1,
                    CreatedAt = DateKey.AddHours(-4),
                    UpdatedAt = DateKey.AddHours(-2)
                });
            await db.SaveChangesAsync();
        }
        var creation = NewAdapter(fixture);

        fixture.Activate(NativeSliceFixture.UserA);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            creation.CreateIfAbsentAsync(
                NativeSliceFixture.UserA, LocalDate, CancellationToken.None));
        var history = Assert.Single(await ReadHistoryAsync(fixture));
        Assert.Equal("displaced-work", history.PlanItemId);
        Assert.Equal(6, history.MinutesSpent);

        fixture.Activate(NativeSliceFixture.UserB);
        var createdB = await creation.CreateIfAbsentAsync(
            NativeSliceFixture.UserB, LocalDate, CancellationToken.None);
        Assert.True(createdB.Created);
        using var verify = fixture.Scope();
        var persisted = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await persisted.DailyPlans.AsNoTracking().AnyAsync(
            row => row.UserProfileId == NativeSliceFixture.UserA));
        Assert.Equal(6, (await persisted.DailyPlanCompletions.AsNoTracking()
            .SingleAsync(row => row.Id == "orphan-owner-a")).MinutesSpent);
        Assert.True((await persisted.DailyPlanCompletions.AsNoTracking()
            .SingleAsync(row => row.Id == "orphan-owner-b-yesterday")).IsCompleted);
    }

    [Fact]
    public async Task CreateAfterArchivingPreviousPlan_PreservesHistoryAndStartsFresh()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var creation = NewAdapter(fixture);
        var first = await creation.CreateIfAbsentAsync(
            NativeSliceFixture.UserA, LocalDate, CancellationToken.None);
        var oldItem = Assert.Single(first.Plan.Items);
        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
        {
            Assert.NotNull(await lease.Service.MarkCompleteAsync(
                LocalDate, oldItem.Id, 8));
            await lease.Service.ResetTodayAsync();
            Assert.Null(await lease.Service.GetTodayAsync());
        }

        var replacement = await creation.CreateIfAbsentAsync(
            NativeSliceFixture.UserA, LocalDate, CancellationToken.None);
        Assert.True(replacement.Created);
        Assert.Equal(oldItem.Id, Assert.Single(replacement.Plan.Items).Id);
        Assert.Equal(0, Assert.Single(replacement.Plan.Items).MinutesSpent);
        Assert.False(Assert.Single(replacement.Plan.Items).IsCompleted);
        var rows = (await ReadRowsAsync(fixture, NativeSliceFixture.UserA)).Items;
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, row => row.IsArchived && row.IsCompleted
            && row.MinutesSpent == 8 && row.PlanItemId == oldItem.Id);
        Assert.Single(rows, row => !row.IsArchived && !row.IsCompleted
            && row.MinutesSpent == 0 && row.PlanItemId == oldItem.Id);

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        Assert.False((await NewAdapter(fixture).CreateIfAbsentAsync(
            NativeSliceFixture.UserA, LocalDate, CancellationToken.None)).Created);
        Assert.Equal(2, (await ReadRowsAsync(fixture, NativeSliceFixture.UserA)).Items.Count);
    }

    [Fact]
    public async Task DuplicateAfterCompletionAndRestart_DoesNotRegenerateOrRewriteMetadata()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            ("VocabularyReview", null, null, 10, 1),
            ("Reading", fixture.ResourceId(NativeSliceFixture.UserA), null, 15, 2));
        var creation = NewAdapter(fixture);
        var created = await creation.CreateIfAbsentAsync(
            NativeSliceFixture.UserA, LocalDate, CancellationToken.None);
        Assert.True(created.Created);
        var completedId = created.Plan.Items.Single(
            item => item.ActivityType == "VocabularyReview").Id;

        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
        {
            Assert.True(await lease.Service.UpdateProgressAsync(
                LocalDate, completedId, 7));
            var completion = await lease.Service.MarkCompleteAsync(
                LocalDate, completedId, 3);
            Assert.True(completion!.IsCompleted);
            Assert.Equal(7, completion.MinutesSpent);
        }
        var before = await ReadRowsAsync(fixture, NativeSliceFixture.UserA);
        var completedBefore = before.Items.Single(item => item.PlanItemId == completedId);

        // If this port accidentally calls GenerateTodayAsync on an existing
        // plan, the new Writing activity would displace the completed card.
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            ("Writing", fixture.ResourceId(NativeSliceFixture.UserA), null, 5, 1));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await creation.CreateIfAbsentAsync(
                NativeSliceFixture.UserA, LocalDate, CancellationToken.None);
            Assert.False(existing.Created);
            Assert.Equal(2, existing.Plan.Items.Count);
            Assert.Equal(7, existing.Plan.Items.Single(
                item => item.Id == completedId).MinutesSpent);
            Assert.DoesNotContain(existing.Plan.Items, item =>
                item.ActivityType == "Writing");
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        creation = NewAdapter(fixture);
        var restored = await creation.CreateIfAbsentAsync(
            NativeSliceFixture.UserA, LocalDate, CancellationToken.None);
        var after = await ReadRowsAsync(fixture, NativeSliceFixture.UserA);
        Assert.False(restored.Created);
        Assert.True(restored.Plan.Items.Single(
            item => item.Id == completedId).IsCompleted);
        Assert.Equal(before.Plan.Id, after.Plan.Id);
        Assert.Equal(before.Plan.CreatedAt, after.Plan.CreatedAt);
        Assert.Equal(before.Plan.GeneratedAtUtc, after.Plan.GeneratedAtUtc);
        Assert.Equal(before.Plan.FocusVocabularyFacts, after.Plan.FocusVocabularyFacts);
        Assert.Equal(before.Plan.NarrativeFacts, after.Plan.NarrativeFacts);
        Assert.Equal(before.Plan.RationaleFacts, after.Plan.RationaleFacts);
        Assert.Equal(before.Items.Select(item => item.Id),
            after.Items.Select(item => item.Id));
        var completedAfter = after.Items.Single(item => item.PlanItemId == completedId);
        Assert.Equal(completedBefore.CompletedAt, completedAfter.CompletedAt);
        Assert.Equal(completedBefore.MinutesSpent, completedAfter.MinutesSpent);
    }

    [Fact]
    public async Task DashboardUsingProductionAdapter_CompletesAndReloadsAfterRestart()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var dashboard = NewDashboard(fixture);

        Assert.Equal(NativePhase.Empty, (await dashboard.LoadAsync()).Phase);
        var generated = await dashboard.GenerateAsync();
        Assert.Equal(NativePhase.Ready, generated.Phase);
        var card = Assert.Single(generated.Cards);

        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
        {
            Assert.NotNull(await lease.Service.MarkCompleteAsync(
                LocalDate, card.Id, 4));
        }

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var restored = await NewDashboard(fixture).LoadAsync();
        Assert.Equal(NativePhase.Ready, restored.Phase);
        Assert.True(Assert.Single(restored.Cards).IsCompleted);
        Assert.Equal(1, restored.CompletedCount);
    }

    [Fact]
    public async Task ConcurrentCreates_OnlyOneCommitsAndAllOthersReturnPersistedPlan()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserA);
        var creation = NewAdapter(fixture);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await creation.CreateIfAbsentAsync(
                NativeSliceFixture.UserA, LocalDate, CancellationToken.None);
        })).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.Single(results, result => result.Created);
        Assert.Equal(5, results.Count(result => !result.Created));
        Assert.All(results, result =>
        {
            Assert.Equal(LocalDate, result.Plan.GeneratedForDate);
            Assert.Equal(results[0].Plan.GeneratedAtUtc, result.Plan.GeneratedAtUtc);
            Assert.Equal(results[0].Plan.Items.Single().Id,
                result.Plan.Items.Single().Id);
        });
        var persisted = await ReadRowsAsync(fixture, NativeSliceFixture.UserA);
        Assert.Single(persisted.Items);
        Assert.Equal(results[0].Plan.GeneratedAtUtc, persisted.Plan.GeneratedAtUtc);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.DailyPlans.AsNoTracking()
            .CountAsync(row => row.UserProfileId == NativeSliceFixture.UserA
                && row.Date == DateKey));
    }

    [Fact]
    public async Task RepeatedRegeneration_ArchivesDisplacedWorkAndKeepsReplacementStable()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA);
        await fixture.SeedAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            ("VocabularyReview", null, null, 10, 1),
            ("Reading", fixture.ResourceId(NativeSliceFixture.UserA), null, 15, 2));
        var created = await NewAdapter(fixture).CreateIfAbsentAsync(
            NativeSliceFixture.UserA, LocalDate, CancellationToken.None);
        var completedId = created.Plan.Items.Single(
            item => item.ActivityType == "VocabularyReview").Id;
        var startedId = created.Plan.Items.Single(
            item => item.ActivityType == "Reading").Id;
        using (var lease = fixture.PlanLease(NativeSliceFixture.UserA))
        {
            Assert.True(await lease.Service.UpdateProgressAsync(
                LocalDate, startedId, 5));
            Assert.NotNull(await lease.Service.MarkCompleteAsync(
                LocalDate, completedId, 3));
        }

        fixture.SetPlanActivities(NativeSliceFixture.UserA,
            ("Writing", fixture.ResourceId(NativeSliceFixture.UserA), null, 5, 1));
        var first = await fixture.Dashboard.RegenerateAsync();
        Assert.Equal(NativePhase.Ready, first.Phase);
        var replacement = Assert.Single(first.Cards);
        Assert.Equal(PlanActivityType.Writing, replacement.Activity);
        Assert.False(replacement.IsCompleted);
        Assert.Equal(0, replacement.MinutesSpent);
        var second = await fixture.Dashboard.RegenerateAsync();
        Assert.Equal(NativePhase.Ready, second.Phase);
        Assert.Equal(replacement.Id, Assert.Single(second.Cards).Id);

        var before = await ReadRowsAsync(fixture, NativeSliceFixture.UserA);
        Assert.Equal(3, before.Items.Count);
        Assert.Equal(5, Assert.Single(before.Items, row =>
            row.PlanItemId == startedId).MinutesSpent);
        Assert.True(Assert.Single(before.Items, row =>
            row.PlanItemId == startedId).IsArchived);
        Assert.True(Assert.Single(before.Items, row =>
            row.PlanItemId == completedId).IsCompleted);
        Assert.True(Assert.Single(before.Items, row =>
            row.PlanItemId == completedId).IsArchived);
        Assert.Single(before.Items, row => !row.IsArchived
            && row.PlanItemId == replacement.Id && row.MinutesSpent == 0);
        var history = await ReadHistoryAsync(fixture);
        Assert.Equal(3, history.Count);
        Assert.Contains(history, entry =>
            entry.PlanItemId == completedId && entry.IsCompleted);
        Assert.Contains(history, entry =>
            entry.PlanItemId == startedId && entry.MinutesSpent == 5);
        Assert.Contains(history, entry =>
            entry.ActivityType == PlanActivityType.Writing && entry.MinutesSpent == 0);

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var after = await ReadRowsAsync(fixture, NativeSliceFixture.UserA);
        Assert.Equal(before.Plan.Id, after.Plan.Id);
        Assert.Equal(before.Items.Select(item => item.Id),
            after.Items.Select(item => item.Id));
        Assert.Equal(3, (await ReadHistoryAsync(fixture)).Count);

        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Empty(await ReadHistoryAsync(fixture));
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.DailyPlans.AsNoTracking().AnyAsync(
            row => row.UserProfileId == NativeSliceFixture.UserB));
    }

    private static NativePlanCreationAdapter NewAdapter(NativeSliceFixture fixture)
    {
        using var scope = fixture.Scope();
        var services = scope.ServiceProvider;
        return new NativePlanCreationAdapter(
            services.GetRequiredService<NativePlanLeaseFactory>(),
            services.GetRequiredService<IUserScopeProvider>(),
            services.GetRequiredService<IPreferencesService>());
    }

    private static NativeDashboardPresenter NewDashboard(NativeSliceFixture fixture)
    {
        using var scope = fixture.Scope();
        var services = scope.ServiceProvider;
        return new NativeDashboardPresenter(
            services.GetRequiredService<NativePlanLeaseFactory>(),
            services.GetRequiredService<DeviceUserScopeProvider>(),
            services.GetRequiredService<IPreferencesService>(),
            services.GetRequiredService<INativeSyncReadiness>(),
            NewAdapter(fixture),
            services.GetRequiredService<IActivitySessionService>(),
            services.GetRequiredService<NativePendingWrites>(),
            services.GetRequiredService<ILogger<NativeDashboardPresenter>>());
    }

    private static async Task<(DailyPlan Plan, List<DailyPlanCompletion> Items)> ReadRowsAsync(
        NativeSliceFixture fixture, string userId)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var plan = await db.DailyPlans.AsNoTracking()
            .SingleAsync(row => row.UserProfileId == userId && row.Date == DateKey);
        var items = await db.DailyPlanCompletions.AsNoTracking()
            .Where(row => row.UserProfileId == userId && row.Date == DateKey)
            .OrderBy(row => row.Priority).ToListAsync();
        return (plan, items);
    }

    private static async Task<List<ActivityLogEntry>> ReadHistoryAsync(
        NativeSliceFixture fixture)
    {
        using var scope = fixture.Scope();
        var weeks = await scope.ServiceProvider.GetRequiredService<IProgressService>()
            .GetActivityLogAsync(DateKey, DateKey);
        return weeks.SelectMany(week => week.Days)
            .Where(day => day.Date == DateKey)
            .SelectMany(day => day.Plans)
            .SelectMany(plan => plan.Items)
            .ToList();
    }
}
