using FluentAssertions;
using SentenceStudio.Services.Plans;
using Xunit;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Covers the real <c>DeterministicPlanBuilder</c> path for a trusted vocabulary
/// focus: the resolved word ids replace the SRS-derived focus set, while the
/// review block itself (and therefore the due-review minimum) is untouched.
/// </summary>
public class DeterministicPlanBuilderVocabularyFocusTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private readonly PlanGenerationTestFixture _fixture;

    public DeterministicPlanBuilderVocabularyFocusTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
    }

    public void Dispose() { }

    /// <summary>Seeds a learner whose SRS pass produces a real review block.</summary>
    private List<string> SeedLearnerWithDueVocabulary()
    {
        _fixture.SeedUserProfile(40);
        var resource = _fixture.SeedResource(
            title: "Focus Resource", mediaType: "Podcast",
            transcript: "Transcript text", vocabWordCount: 10);
        _fixture.SeedSkill();

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);
        foreach (var wordId in wordIds)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.3f,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        return wordIds;
    }

    [Fact]
    public async Task TrustedFocus_ReplacesTheDerivedFocusSetExactly()
    {
        var wordIds = SeedLearnerWithDueVocabulary();
        var selected = wordIds.Take(5).ToList();

        var derived = await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PlanGenerationTestFixture.TestUserId));
        derived!.FocusVocabularyIds.Should().NotBeEmpty("the SRS pass derives its own focus set");

        var focused = await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PlanGenerationTestFixture.TestUserId, null, selected));

        focused!.FocusVocabularyIds.Should().Equal(selected,
            "vocabulary activities must carry exactly the resolved set");
    }

    [Fact]
    public async Task TrustedFocus_FlowsOntoVocabularyDrivenActivities()
    {
        var wordIds = SeedLearnerWithDueVocabulary();
        var selected = wordIds.Take(6).ToList();

        var plan = await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PlanGenerationTestFixture.TestUserId, null, selected));

        var review = plan!.Activities.Single(a => a.ActivityType == "VocabularyReview");
        review.FocusVocabularyIds.Should().Equal(selected);
    }

    [Fact]
    public async Task TrustedFocus_PreservesTheDueReviewBlock()
    {
        var wordIds = SeedLearnerWithDueVocabulary();
        var selected = wordIds.Take(5).ToList();

        var baseline = await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PlanGenerationTestFixture.TestUserId));
        var focused = await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PlanGenerationTestFixture.TestUserId, null, selected));

        focused!.VocabularyReview.Should().NotBeNull();
        focused.VocabularyReview!.WordCount.Should().Be(baseline!.VocabularyReview!.WordCount,
            "focusing changes which words are highlighted, never the due-review minimum");
        focused.Activities.Select(a => a.ActivityType)
            .Should().Equal(baseline.Activities.Select(a => a.ActivityType));
    }

    [Fact]
    public async Task TrustedFocus_DedupesRepeatedIds()
    {
        var wordIds = SeedLearnerWithDueVocabulary();
        var duplicated = new List<string> { wordIds[0], wordIds[0], wordIds[1] };

        var plan = await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PlanGenerationTestFixture.TestUserId, null, duplicated));

        plan!.FocusVocabularyIds.Should().Equal(wordIds[0], wordIds[1]);
    }

    [Fact]
    public async Task NoTrustedFocus_LeavesTheDerivedSetUntouched()
    {
        SeedLearnerWithDueVocabulary();

        var first = await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PlanGenerationTestFixture.TestUserId));
        var second = await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PlanGenerationTestFixture.TestUserId, null, null));

        second!.FocusVocabularyIds.Should().Equal(first!.FocusVocabularyIds);
    }

    [Fact]
    public async Task TrustedFocus_WithNoReviewBlock_DoesNotFabricateOne()
    {
        // No vocabulary at all: there is no review block to focus.
        _fixture.SeedUserProfile(40);
        _fixture.SeedResource(title: "Bare Resource", mediaType: "Podcast", transcript: "Transcript text");
        _fixture.SeedSkill();

        var plan = await _fixture.CreateBuilder().BuildPlanAsync(
            PlanBuildRequest.Preview(PlanGenerationTestFixture.TestUserId, null, ["orphan-word-1"]));

        plan!.VocabularyReview.Should().BeNull();
        plan.FocusVocabularyIds.Should().BeEmpty("a focus set never invents vocabulary work");
    }
}
