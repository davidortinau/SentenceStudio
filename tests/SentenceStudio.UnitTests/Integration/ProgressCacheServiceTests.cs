using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using FluentAssertions;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.UnitTests.Integration;

/// <summary>
/// Tests for ProgressCacheService: TodaysPlan caching, expiration,
/// user-keyed isolation, and plan update behavior.
/// </summary>
public class ProgressCacheServiceTests
{
    private readonly ProgressCacheService _cache;
    private readonly Mock<IActiveUserProvider> _mockActiveUser;

    private const string TestUserId = "test-user-cache";
    private const string OtherUserId = "other-user-cache";

    public ProgressCacheServiceTests()
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug));
        _mockActiveUser = new Mock<IActiveUserProvider>();
        _mockActiveUser.Setup(p => p.GetActiveProfileId()).Returns(TestUserId);
        _mockActiveUser.Setup(p => p.ShouldFallbackToFirstProfile).Returns(true);

        var services = new ServiceCollection();
        services.AddSingleton(_mockActiveUser.Object);
        var sp = services.BuildServiceProvider();

        _cache = new ProgressCacheService(
            loggerFactory.CreateLogger<ProgressCacheService>(),
            sp);
    }

    #region TodaysPlan Basic Operations

    [Fact]
    public void GetTodaysPlan_ReturnsNull_WhenNotSet()
    {
        // Act
        var plan = _cache.GetTodaysPlan();

        // Assert
        plan.Should().BeNull("no plan has been cached yet");
    }

    [Fact]
    public void SetTodaysPlan_ThenGet_ReturnsCachedPlan()
    {
        // Arrange
        var plan = CreateTestPlan();

        // Act
        _cache.SetTodaysPlan(plan);
        var cached = _cache.GetTodaysPlan();

        // Assert
        cached.Should().NotBeNull();
        cached.Should().BeSameAs(plan);
    }

    [Fact]
    public void UpdateTodaysPlan_ReplacesCachedPlan()
    {
        // Arrange
        var plan1 = CreateTestPlan(completedCount: 0);
        var plan2 = CreateTestPlan(completedCount: 2);
        _cache.SetTodaysPlan(plan1);

        // Act
        _cache.UpdateTodaysPlan(plan2);
        var cached = _cache.GetTodaysPlan();

        // Assert
        cached.Should().NotBeNull();
        cached!.CompletedCount.Should().Be(2, "updated plan should replace the original");
    }

    [Fact]
    public void InvalidateTodaysPlan_RemovesCachedPlan()
    {
        // Arrange
        _cache.SetTodaysPlan(CreateTestPlan());
        _cache.GetTodaysPlan().Should().NotBeNull("precondition: plan is cached");

        // Act
        _cache.InvalidateTodaysPlan();

        // Assert
        _cache.GetTodaysPlan().Should().BeNull("plan should be removed after invalidation");
    }

    #endregion

    #region User Isolation

    [Fact]
    public void CacheEntries_AreKeyedByUser_NoCrossBleed()
    {
        // Arrange: set plan for TestUserId
        _cache.SetTodaysPlan(CreateTestPlan(completedCount: 1));

        // Act: switch to different user
        _mockActiveUser.Setup(p => p.GetActiveProfileId()).Returns(OtherUserId);

        var otherPlan = _cache.GetTodaysPlan();

        // Assert: other user should not see TestUserId's plan
        otherPlan.Should().BeNull("cache entries are keyed by userId, no cross-bleed");
    }

    [Fact]
    public void DifferentUsers_HaveIndependentCaches()
    {
        // Arrange: set plan for TestUserId
        _cache.SetTodaysPlan(CreateTestPlan(completedCount: 1));

        // Switch to other user, set different plan
        _mockActiveUser.Setup(p => p.GetActiveProfileId()).Returns(OtherUserId);
        _cache.SetTodaysPlan(CreateTestPlan(completedCount: 5));

        // Switch back to first user
        _mockActiveUser.Setup(p => p.GetActiveProfileId()).Returns(TestUserId);
        var firstUserPlan = _cache.GetTodaysPlan();

        // Assert: first user's plan is unaffected
        firstUserPlan.Should().NotBeNull();
        firstUserPlan!.CompletedCount.Should().Be(1);
    }

    #endregion

    #region VocabSummary Cache

    [Fact]
    public void VocabSummary_CacheSetAndGet()
    {
        // Arrange
        var summary = new VocabProgressSummary(10, 5, 2, 3, 8, 0.75);

        // Act
        _cache.SetVocabSummary(summary);
        var cached = _cache.GetVocabSummary();

        // Assert
        cached.Should().NotBeNull();
        cached!.Known.Should().Be(8);
        cached.New.Should().Be(10);
        cached.SuccessRate7d.Should().Be(0.75);
    }

    [Fact]
    public void InvalidateVocabSummary_ClearsCacheForCurrentUser()
    {
        // Arrange
        _cache.SetVocabSummary(new VocabProgressSummary(10, 5, 2, 3, 8, 0.75));

        // Act
        _cache.InvalidateVocabSummary();

        // Assert
        _cache.GetVocabSummary().Should().BeNull();
    }

    #endregion

    #region SkillProgress Cache (composite key)

    [Fact]
    public void SkillProgress_UsesCompositeKey_UserAndSkill()
    {
        // Arrange
        var skill1 = new SkillProgress("skill-1", "Listening", 0.5, 0.1, DateTime.UtcNow);
        var skill2 = new SkillProgress("skill-2", "Speaking", 0.7, 0.05, DateTime.UtcNow);

        // Act
        _cache.SetSkillProgress("skill-1", skill1);
        _cache.SetSkillProgress("skill-2", skill2);

        // Assert
        _cache.GetSkillProgress("skill-1")!.Proficiency.Should().Be(0.5);
        _cache.GetSkillProgress("skill-2")!.Proficiency.Should().Be(0.7);
        _cache.GetSkillProgress("skill-3").Should().BeNull("never cached");
    }

    [Fact]
    public void InvalidateSkillProgress_OnlyInvalidatesSpecificSkill()
    {
        // Arrange
        _cache.SetSkillProgress("skill-1", new SkillProgress("skill-1", "Listening", 0.5, 0, DateTime.UtcNow));
        _cache.SetSkillProgress("skill-2", new SkillProgress("skill-2", "Speaking", 0.7, 0, DateTime.UtcNow));

        // Act
        _cache.InvalidateSkillProgress("skill-1");

        // Assert
        _cache.GetSkillProgress("skill-1").Should().BeNull("invalidated");
        _cache.GetSkillProgress("skill-2").Should().NotBeNull("not invalidated");
    }

    #endregion

    #region InvalidateAll

    [Fact]
    public void InvalidateAll_ClearsEverything()
    {
        // Arrange: populate all caches
        _cache.SetTodaysPlan(CreateTestPlan());
        _cache.SetVocabSummary(new VocabProgressSummary(1, 2, 3, 4, 5, 0.8));
        _cache.SetSkillProgress("s1", new SkillProgress("s1", "Test", 0.5, 0, DateTime.UtcNow));
        _cache.SetResourceProgress(new List<ResourceProgress>
        {
            new("r1", "Res", 0.5, DateTime.UtcNow, 10, 0.8, 5)
        });
        _cache.SetPracticeHeat(new List<PracticeHeatPoint>
        {
            new(DateTime.UtcNow.Date, 5)
        });

        // Act
        _cache.InvalidateAll();

        // Assert
        _cache.GetTodaysPlan().Should().BeNull();
        _cache.GetVocabSummary().Should().BeNull();
        _cache.GetSkillProgress("s1").Should().BeNull();
        _cache.GetResourceProgress().Should().BeNull();
        _cache.GetPracticeHeat().Should().BeNull();
    }

    #endregion

    #region ResourceProgress Cache

    [Fact]
    public void ResourceProgress_CacheSetAndGet()
    {
        // Arrange
        var data = new List<ResourceProgress>
        {
            new("r1", "Resource 1", 0.5, DateTime.UtcNow, 10, 0.8, 5),
            new("r2", "Resource 2", 0.3, DateTime.UtcNow.AddDays(-1), 5, 0.6, 3)
        };

        // Act
        _cache.SetResourceProgress(data);
        var cached = _cache.GetResourceProgress();

        // Assert
        cached.Should().NotBeNull();
        cached.Should().HaveCount(2);
    }

    [Fact]
    public void InvalidateResourceProgress_ClearsCache()
    {
        // Arrange
        _cache.SetResourceProgress(new List<ResourceProgress>
        {
            new("r1", "Res", 0.5, DateTime.UtcNow, 10, 0.8, 5)
        });

        // Act
        _cache.InvalidateResourceProgress();

        // Assert
        _cache.GetResourceProgress().Should().BeNull();
    }

    #endregion

    #region PracticeHeat Cache

    [Fact]
    public void PracticeHeat_CacheSetAndGet()
    {
        // Arrange
        var data = new List<PracticeHeatPoint>
        {
            new(DateTime.UtcNow.Date, 5),
            new(DateTime.UtcNow.Date.AddDays(-1), 3)
        };

        // Act
        _cache.SetPracticeHeat(data);
        var cached = _cache.GetPracticeHeat();

        // Assert
        cached.Should().NotBeNull();
        cached.Should().HaveCount(2);
    }

    [Fact]
    public void InvalidatePracticeHeat_ClearsCache()
    {
        // Arrange
        _cache.SetPracticeHeat(new List<PracticeHeatPoint> { new(DateTime.UtcNow.Date, 1) });

        // Act
        _cache.InvalidatePracticeHeat();

        // Assert
        _cache.GetPracticeHeat().Should().BeNull();
    }

    #endregion

    private TodaysPlan CreateTestPlan(int completedCount = 0)
    {
        return new TodaysPlan(
            GeneratedForDate: DateTime.UtcNow.Date,
            Items: new List<DailyPlanItem>
            {
                new(
                    Id: "item-1",
                    TitleKey: "test_title",
                    DescriptionKey: "test_desc",
                    ActivityType: PlanActivityType.VocabularyReview,
                    EstimatedMinutes: 10,
                    Priority: 1,
                    IsCompleted: completedCount > 0,
                    CompletedAt: completedCount > 0 ? DateTime.UtcNow : null,
                    Route: "/vocabulary-quiz",
                    RouteParameters: new Dictionary<string, object>(),
                    ResourceId: "test-resource",
                    ResourceTitle: "Test Resource",
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
            Streak: new StreakInfo(3, 5, DateTime.UtcNow.AddDays(-1))
        );
    }
}
