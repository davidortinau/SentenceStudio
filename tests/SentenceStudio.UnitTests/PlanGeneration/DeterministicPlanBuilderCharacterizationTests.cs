using Xunit;
using FluentAssertions;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Characterization tests: pin today's deterministic planner output so the
/// constraint work can be proven additive. Every assertion here describes the
/// pre-constraint behavior. If one of these fails, the constraint layer has
/// leaked into the unconstrained path.
/// </summary>
public class DeterministicPlanBuilderCharacterizationTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private readonly PlanGenerationTestFixture _fixture;

    public DeterministicPlanBuilderCharacterizationTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
    }

    public void Dispose() { }

    private void SeedStandardScenario(int sessionMinutes = 30)
    {
        _fixture.SeedUserProfile(sessionMinutes);
        var resource = _fixture.SeedResource(
            title: "Characterization Resource",
            mediaType: "Podcast",
            transcript: "Transcript text",
            vocabWordCount: 10);
        _fixture.SeedSkill();

        foreach (var wordId in _fixture.GetResourceVocabularyWordIds(resource.Id))
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.3f,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }
    }

    private static void AssertSamePlan(PlanSkeleton? expected, PlanSkeleton? actual)
    {
        actual.Should().NotBeNull();
        expected.Should().NotBeNull();

        actual!.TotalMinutes.Should().Be(expected!.TotalMinutes);
        actual.PrimaryResource?.Id.Should().Be(expected.PrimaryResource?.Id);
        actual.PrimarySkill?.Id.Should().Be(expected.PrimarySkill?.Id);
        actual.FocusVocabularyIds.Should().BeEquivalentTo(expected.FocusVocabularyIds);
        actual.VocabularyReview?.WordCount.Should().Be(expected.VocabularyReview?.WordCount);

        actual.Activities
            .Select(a => (a.ActivityType, a.Priority, a.EstimatedMinutes, a.ResourceId, a.SkillId))
            .Should()
            .Equal(expected.Activities
                .Select(a => (a.ActivityType, a.Priority, a.EstimatedMinutes, a.ResourceId, a.SkillId)));
    }

    [Fact]
    public async Task LegacyOverload_AndRequestWithNullConstraints_ProduceIdenticalPlans()
    {
        SeedStandardScenario();

        var legacy = await _fixture.CreateBuilder().BuildPlanAsync();
        var viaRequest = await _fixture.CreateBuilder()
            .BuildPlanAsync(new PlanBuildRequest { UserProfileId = null, Constraints = null });

        AssertSamePlan(legacy, viaRequest);
    }

    [Fact]
    public async Task ExplicitUserId_WithNullConstraints_MatchesLegacyExplicitUserIdOverload()
    {
        SeedStandardScenario();

        var legacy = await _fixture.CreateBuilder().BuildPlanAsync(PlanGenerationTestFixture.TestUserId);
        var viaRequest = await _fixture.CreateBuilder().BuildPlanAsync(
            new PlanBuildRequest { UserProfileId = PlanGenerationTestFixture.TestUserId });

        AssertSamePlan(legacy, viaRequest);
    }

    [Fact]
    public async Task UnconstrainedPlan_UsesProfileSessionMinutesAsBudget()
    {
        SeedStandardScenario(sessionMinutes: 30);

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan.Should().NotBeNull();
        plan!.TotalMinutes.Should().BeLessThanOrEqualTo(30,
            "the profile's PreferredSessionMinutes is the budget when no constraints are supplied");
    }

    [Fact]
    public async Task UnconstrainedPlan_UsesTenMinuteBlocksForInputAndOutput()
    {
        SeedStandardScenario(sessionMinutes: 60);

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan.Should().NotBeNull();
        var graded = plan!.Activities
            .Where(a => a.ActivityType != "VocabularyReview"
                && a.ActivityType != "VocabularyGame"
                && a.ActivityType != "NumberDrill")
            .ToList();

        graded.Should().NotBeEmpty();
        graded.Should().OnlyContain(a => a.EstimatedMinutes == 10,
            "input and output blocks are 10 minutes at normal energy");
    }

    [Fact]
    public async Task UnconstrainedPlan_IncludesCloserWhenBudgetRemains()
    {
        _fixture.SeedUserProfile(60);
        _fixture.SeedResource(
            title: "Closer Resource", mediaType: "Podcast",
            transcript: "Transcript", vocabWordCount: 3);
        _fixture.SeedSkill();

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan.Should().NotBeNull();
        plan!.Activities.Should().Contain(
            a => a.ActivityType == "VocabularyGame" || a.ActivityType == "NumberDrill",
            "a light closer is appended when 5 or more minutes remain at normal energy");
    }

    [Fact]
    public async Task UnconstrainedPlan_WithZeroBudget_ReturnsEmptySkeletonNotNull()
    {
        // Pinning the historical shape: an unconstrained build with no room for
        // any block still returns a (non-null) skeleton with no activities.
        // Only constrained builds get the explicit no-feasible-plan null.
        _fixture.SeedUserProfile(sessionMinutes: 3);
        _fixture.SeedResource(
            title: "Tiny Budget Resource", mediaType: "Podcast",
            transcript: "Transcript", vocabWordCount: 0);
        _fixture.SeedSkill();

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan.Should().NotBeNull("unconstrained behavior is unchanged by the constraint work");
        plan!.Activities.Should().BeEmpty();
    }
}
