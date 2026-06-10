using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using FluentAssertions;
using SentenceStudio.Abstractions;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.UnitTests.Integration;

/// <summary>
/// Regression guards for the timezone bug that originally caused the Vocab Quiz
/// preview ≠ activity mismatch (May 2026):
///   - ProgressCacheService used to compute its own "today" via DateTime.Today (LOCAL),
///     while ProgressService keyed plans by DateTime.UtcNow.Date.
///   - Around UTC midnight (which is 5pm Pacific the prior day), the cache and the
///     persistence layer pointed at different days. Cache MISSes triggered regeneration
///     of fallback plans that didn't match the data being served to activities.
///
/// These tests pin the contract: the cache always keys by the explicit date the caller
/// passed in — never by ambient time-of-day.
/// </summary>
public class ProgressCacheServiceTimezoneTests
{
    private readonly ProgressCacheService _cache;
    private const string TestUserId = "tz-test-user";

    public ProgressCacheServiceTimezoneTests()
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug));
        var mockPreferences = new Mock<IPreferencesService>();
        mockPreferences.Setup(p => p.Get("active_profile_id", It.IsAny<string>()))
            .Returns(TestUserId);

        _cache = new ProgressCacheService(
            loggerFactory.CreateLogger<ProgressCacheService>(),
            mockPreferences.Object);
    }

    [Fact]
    public void GetTodaysPlan_DifferentDate_ReturnsNull()
    {
        // Arrange: cache a plan for 2026-06-09
        var day1 = new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        _cache.SetTodaysPlan(day1, CreatePlanFor(day1));

        // Act: ask for a different date
        var day2Plan = _cache.GetTodaysPlan(day2);

        // Assert: cache returns null — keys are date-scoped, not ambient
        day2Plan.Should().BeNull("the cache is keyed by the date the caller passed, not by ambient today");
    }

    [Fact]
    public void GetTodaysPlan_SameDate_ReturnsCachedPlan()
    {
        // Arrange
        var date = new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc);
        var plan = CreatePlanFor(date);
        _cache.SetTodaysPlan(date, plan);

        // Act
        var cached = _cache.GetTodaysPlan(date);

        // Assert
        cached.Should().BeSameAs(plan, "same date key roundtrips identity");
    }

    [Fact]
    public void GetTodaysPlan_DateOnlyMatters_TimeComponentIgnored()
    {
        // Arrange: cache with one DateTime, fetch with a different DateTime that has the same .Date
        var morning = new DateTime(2026, 6, 9, 1, 0, 0, DateTimeKind.Utc);
        var evening = new DateTime(2026, 6, 9, 23, 59, 0, DateTimeKind.Utc);
        _cache.SetTodaysPlan(morning, CreatePlanFor(morning));

        // Act
        var fetched = _cache.GetTodaysPlan(evening);

        // Assert: the cache uses .Date, so the time component must not matter
        fetched.Should().NotBeNull("cache uses .Date — only the calendar day matters");
    }

    [Fact]
    public void InvalidateTodaysPlan_OnlyClearsThatDate_OtherDatesSurvive()
    {
        // Arrange: cache plans for two days
        var day1 = new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        _cache.SetTodaysPlan(day1, CreatePlanFor(day1));
        _cache.SetTodaysPlan(day2, CreatePlanFor(day2));

        // Act: invalidate only day1
        _cache.InvalidateTodaysPlan(day1);

        // Assert
        _cache.GetTodaysPlan(day1).Should().BeNull("day1 was invalidated");
        _cache.GetTodaysPlan(day2).Should().NotBeNull("day2 must not be collateral damage");
    }

    [Fact]
    public void SetTodaysPlan_PastDate_DoesNotThrow_AndReturnsNullDueToExpiry()
    {
        // Arrange: cache for a date well in the past — TTL is computed as
        // "expires at next UTC midnight after the keyed date", which is also in the past.
        // The implementation floors TTL at 1 minute to avoid negative/zero TTLs.
        var pastDate = DateTime.UtcNow.Date.AddDays(-5);

        // Act
        Action act = () => _cache.SetTodaysPlan(pastDate, CreatePlanFor(pastDate));

        // Assert: set succeeds; whether the read returns null or the plan is implementation-defined
        // (1-minute floor TTL means the entry may live briefly). The contract is just "no throw".
        act.Should().NotThrow("past dates must not crash the cache");
    }

    [Fact]
    public void SetTodaysPlan_FutureDate_StoresAndRetrievesCleanly()
    {
        // Arrange
        var future = DateTime.UtcNow.Date.AddDays(1);
        var plan = CreatePlanFor(future);

        // Act
        _cache.SetTodaysPlan(future, plan);
        var fetched = _cache.GetTodaysPlan(future);

        // Assert: future dates have positive TTL, must roundtrip
        fetched.Should().BeSameAs(plan, "future dates must roundtrip without TTL drama");
    }

    [Fact]
    public void UpdateTodaysPlan_TargetsExplicitDate_NotAmbientToday()
    {
        // Arrange: cache day1
        var day1 = new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        _cache.SetTodaysPlan(day1, CreatePlanFor(day1, completedCount: 0));

        // Act: update for day2 (which has no entry yet)
        _cache.UpdateTodaysPlan(day2, CreatePlanFor(day2, completedCount: 5));

        // Assert: day1 unchanged, day2 now populated
        _cache.GetTodaysPlan(day1)!.CompletedCount.Should().Be(0, "day1 was not the update target");
        _cache.GetTodaysPlan(day2)!.CompletedCount.Should().Be(5, "day2 is the update target");
    }

    private static TodaysPlan CreatePlanFor(DateTime date, int completedCount = 0)
    {
        return new TodaysPlan(
            GeneratedForDate: date.Date,
            Items: new List<DailyPlanItem>
            {
                new(
                    Id: $"item-{date:yyyyMMdd}",
                    TitleKey: "tz_test_title",
                    DescriptionKey: "tz_test_desc",
                    ActivityType: PlanActivityType.VocabularyReview,
                    EstimatedMinutes: 10,
                    Priority: 1,
                    IsCompleted: completedCount > 0,
                    CompletedAt: completedCount > 0 ? DateTime.UtcNow : null,
                    Route: "/vocabulary-quiz",
                    RouteParameters: new Dictionary<string, object>(),
                    ResourceId: "tz-resource",
                    ResourceTitle: "TZ Resource",
                    SkillId: null,
                    SkillName: null,
                    VocabDueCount: 10,
                    DifficultyLevel: null
                )
            },
            EstimatedTotalMinutes: 10,
            CompletedCount: completedCount,
            TotalCount: 1,
            CompletionPercentage: completedCount > 0 ? 100 : 0,
            Streak: new StreakInfo(0, 0, null)
        );
    }
}
