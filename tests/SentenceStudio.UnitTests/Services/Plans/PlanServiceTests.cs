using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Data;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;
using Xunit;

namespace SentenceStudio.UnitTests.Services.Plans;

/// <summary>
/// Round-trip integration tests for <see cref="PlanService"/> against an
/// in-memory SQLite <see cref="ApplicationDbContext"/>. Covers the
/// happy-path flow (generate → get → progress → complete → reset), the
/// per-user isolation gate, and the device-local "today" rollover that
/// §14a of plan.md treats as an acceptance criterion.
/// </summary>
public sealed class PlanServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly FakeScope _scope;
    private readonly FakeDateContext _date;
    private readonly FakeDeterministicGenerator _generator;

    private const string UserA = "user-a";
    private const string UserB = "user-b";

    public PlanServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseSqlite(_connection)
               .ConfigureWarnings(w =>
                   w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        services.AddLogging();

        _scope = new FakeScope(UserA);
        _date = new FakeDateContext(new DateOnly(2026, 5, 16), TimeZoneInfo.Utc);
        _generator = new FakeDeterministicGenerator();

        services.AddSingleton<IUserScopeProvider>(_scope);
        services.AddSingleton<IPlanDateContext>(_date);
        services.AddSingleton<IDeterministicPlanGenerator>(_generator);
        services.AddSingleton<IPlanCopyProvider, EnglishPlanCopyProvider>();
        services.AddScoped<IPlanService, PlanService>();

        _provider = services.BuildServiceProvider();

        using var bootstrap = _provider.CreateScope();
        var db = bootstrap.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();
    }

    private IPlanService NewService()
    {
        var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPlanService>();
    }

    [Fact]
    public async Task GetToday_WithNoPlan_ReturnsNull()
    {
        var service = NewService();
        var result = await service.GetTodayAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateToday_PersistsPlanAndReturnsDto()
    {
        _generator.SetActivities(
            ("Reading", "resource-1", null, 15, 1),
            ("VocabularyReview", null, null, 10, 2));

        var service = NewService();
        var plan = await service.GenerateTodayAsync(new GenerateTodaysPlanRequest());

        Assert.NotNull(plan);
        Assert.Equal(new DateOnly(2026, 5, 16), plan.GeneratedForDate);
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(25, plan.EstimatedTotalMinutes);
        Assert.Equal(0, plan.CompletedCount);
        Assert.Equal(0, plan.CompletionPercentage);

        // Stable PlanItemId scheme: regeneration must produce identical ids.
        var planAgain = await service.GenerateTodayAsync(new GenerateTodaysPlanRequest());
        Assert.Equal(
            plan.Items.Select(i => i.Id).OrderBy(s => s, StringComparer.Ordinal),
            planAgain.Items.Select(i => i.Id).OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public async Task UpdateProgress_ClampsToMaxMinutes_AndPersists()
    {
        _generator.SetActivities(("Reading", "resource-1", null, 15, 1));
        var service = NewService();
        var plan = await service.GenerateTodayAsync(new GenerateTodaysPlanRequest());
        var itemId = plan.Items[0].Id;

        var ok = await service.UpdateProgressAsync(plan.GeneratedForDate, itemId, 9999);
        Assert.True(ok);

        var reloaded = await service.GetTodayAsync();
        Assert.NotNull(reloaded);
        Assert.Equal(PlanService.MaxMinutesSpent, reloaded!.Items.Single().MinutesSpent);
    }

    [Fact]
    public async Task UpdateProgress_OnlyMovesValueForward()
    {
        _generator.SetActivities(("Reading", "resource-1", null, 15, 1));
        var service = NewService();
        var plan = await service.GenerateTodayAsync(new GenerateTodaysPlanRequest());
        var itemId = plan.Items[0].Id;

        await service.UpdateProgressAsync(plan.GeneratedForDate, itemId, 12);
        await service.UpdateProgressAsync(plan.GeneratedForDate, itemId, 5);

        var reloaded = await service.GetTodayAsync();
        Assert.Equal(12, reloaded!.Items.Single().MinutesSpent);
    }

    [Fact]
    public async Task MarkComplete_IsIdempotent_AndPreservesEarliestCompletedAt()
    {
        _generator.SetActivities(("Reading", "resource-1", null, 15, 1));
        var service = NewService();
        var plan = await service.GenerateTodayAsync(new GenerateTodaysPlanRequest());
        var itemId = plan.Items[0].Id;

        _date.SetNow(new DateTime(2026, 5, 16, 10, 0, 0, DateTimeKind.Utc));
        var firstResult = await service.MarkCompleteAsync(plan.GeneratedForDate, itemId, 12);
        Assert.NotNull(firstResult);
        Assert.True(firstResult!.IsCompleted);
        var firstCompletedAt = firstResult.CompletedAtUtc;

        _date.SetNow(new DateTime(2026, 5, 16, 14, 0, 0, DateTimeKind.Utc));
        var secondResult = await service.MarkCompleteAsync(plan.GeneratedForDate, itemId, 11);
        Assert.True(secondResult!.IsCompleted);
        Assert.Equal(firstCompletedAt, secondResult.CompletedAtUtc);
        Assert.Equal(12, secondResult.MinutesSpent);
    }

    [Fact]
    public async Task Regenerate_PreservesProgressForMatchingItems()
    {
        _generator.SetActivities(
            ("Reading", "resource-1", null, 15, 1),
            ("VocabularyReview", null, null, 10, 2));

        var service = NewService();
        var first = await service.GenerateTodayAsync(new GenerateTodaysPlanRequest());
        var readingId = first.Items.First(i => i.ActivityType == "Reading").Id;
        await service.UpdateProgressAsync(first.GeneratedForDate, readingId, 8);
        await service.MarkCompleteAsync(first.GeneratedForDate, readingId, 8);

        // Regenerate with the same activity composition — progress survives.
        var second = await service.GenerateTodayAsync(new GenerateTodaysPlanRequest());
        var readingAfter = second.Items.Single(i => i.Id == readingId);
        Assert.True(readingAfter.IsCompleted);
        Assert.Equal(8, readingAfter.MinutesSpent);
    }

    [Fact]
    public async Task Reset_RemovesPlanAndChildren()
    {
        _generator.SetActivities(("Reading", "resource-1", null, 15, 1));
        var service = NewService();
        await service.GenerateTodayAsync(new GenerateTodaysPlanRequest());

        await service.ResetTodayAsync();
        var after = await service.GetTodayAsync();
        Assert.Null(after);
    }

    [Fact]
    public async Task MultiUserIsolation_UserACannotSeeUserBPlan()
    {
        _generator.SetActivities(("Reading", "resource-1", null, 15, 1));

        _scope.SetUser(UserA);
        var serviceA = NewService();
        await serviceA.GenerateTodayAsync(new GenerateTodaysPlanRequest());

        _scope.SetUser(UserB);
        var serviceB = NewService();
        var planForB = await serviceB.GetTodayAsync();
        Assert.Null(planForB);

        _scope.SetUser(UserA);
        var serviceA2 = NewService();
        var planForA = await serviceA2.GetTodayAsync();
        Assert.NotNull(planForA);
    }

    [Fact]
    public async Task DeviceLocalTurnover_NewDayReturns204()
    {
        _generator.SetActivities(("Reading", "resource-1", null, 15, 1));
        var service = NewService();

        // Generate plan for 2026-05-16
        await service.GenerateTodayAsync(new GenerateTodaysPlanRequest());

        // Advance the user-local date by one calendar day.
        _date.SetLocalDate(new DateOnly(2026, 5, 17));

        var service2 = NewService();
        var tomorrow = await service2.GetTodayAsync();
        Assert.Null(tomorrow);
    }

    [Fact]
    public async Task ProgressOnPreviousDay_AppliesToOriginalPlan_NotPhantomNewOne()
    {
        // §14a: generated at 23:55 local, completed at 00:05 local next day.
        _generator.SetActivities(("Reading", "resource-1", null, 15, 1));
        var service = NewService();
        var plan = await service.GenerateTodayAsync(new GenerateTodaysPlanRequest());
        var itemId = plan.Items[0].Id;

        // Time rolls over to the next user-local day; client still sends
        // the original plan's GeneratedForDate to /progress.
        _date.SetLocalDate(new DateOnly(2026, 5, 17));
        _date.SetNow(new DateTime(2026, 5, 17, 0, 5, 0, DateTimeKind.Utc));

        var service2 = NewService();
        var ok = await service2.UpdateProgressAsync(plan.GeneratedForDate, itemId, 12);
        Assert.True(ok);

        // The new "today" still has no plan…
        Assert.Null(await service2.GetTodayAsync());

        // …but the original plan row was updated.
        _date.SetLocalDate(new DateOnly(2026, 5, 16));
        var service3 = NewService();
        var reloaded = await service3.GetTodayAsync();
        Assert.NotNull(reloaded);
        Assert.Equal(12, reloaded!.Items.Single().MinutesSpent);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    // ---------- fakes ----------

    private sealed class FakeScope : IUserScopeProvider
    {
        private string _userId;
        public FakeScope(string userId) => _userId = userId;
        public void SetUser(string userId) => _userId = userId;
        public string UserProfileId => _userId;
        public bool TryGetUserProfileId(out string userProfileId)
        {
            userProfileId = _userId;
            return !string.IsNullOrWhiteSpace(_userId);
        }
    }

    private sealed class FakeDateContext : IPlanDateContext
    {
        public FakeDateContext(DateOnly localDate, TimeZoneInfo tz)
        {
            UserLocalDate = localDate;
            TimeZone = tz;
            UtcNow = localDate.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
        }
        public DateOnly UserLocalDate { get; private set; }
        public DateTime UtcNow { get; private set; }
        public TimeZoneInfo TimeZone { get; }
        public void SetLocalDate(DateOnly d) => UserLocalDate = d;
        public void SetNow(DateTime nowUtc) => UtcNow = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        public DateOnly ToUserLocal(DateTime utc) => DateOnly.FromDateTime(utc);
        public DateTime ToUtcMidnight(DateOnly userLocal) => userLocal.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    }

    private sealed class FakeDeterministicGenerator : IDeterministicPlanGenerator
    {
        private List<PlannedActivity> _activities = new();

        public void SetActivities(params (string Type, string? ResourceId, string? SkillId, int Minutes, int Priority)[] activities)
        {
            _activities = activities.Select(a => new PlannedActivity
            {
                ActivityType = a.Type,
                ResourceId = a.ResourceId,
                SkillId = a.SkillId,
                EstimatedMinutes = a.Minutes,
                Priority = a.Priority,
                Rationale = "test",
            }).ToList();
        }

        public Task<PlanSkeleton?> GenerateAsync(string? userProfileId = null, CancellationToken ct = default)
        {
            return Task.FromResult<PlanSkeleton?>(new PlanSkeleton
            {
                Activities = _activities.ToList(),
                TotalMinutes = _activities.Sum(a => a.EstimatedMinutes),
                ResourceSelectionReason = "test reason",
            });
        }
    }
}
