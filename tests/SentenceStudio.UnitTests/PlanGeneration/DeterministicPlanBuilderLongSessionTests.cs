using FluentAssertions;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// "I have 45 minutes today" (Captain, 2026-11-17): the planner fills sessions longer than its
/// four standard steps, keeps a pinned resource, never picks yesterday's resource, and reviews
/// focus words even when nothing is due. Sessions up to 34 minutes come out as before.
/// </summary>
public sealed class DeterministicPlanBuilderLongSessionTests : IClassFixture<PlanGenerationTestFixture>
{
    private readonly PlanGenerationTestFixture _fixture;

    public DeterministicPlanBuilderLongSessionTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
    }

    [Fact]
    public async Task A_45_minute_session_is_filled_with_distinct_activities()
    {
        Seed(minutes: 45, dueWords: 20);

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan.Should().NotBeNull();
        plan!.Activities.Sum(a => a.EstimatedMinutes).Should().Be(45);
        plan.Activities.Select(a => (a.ActivityType, a.ResourceId, a.SkillId)).Should().OnlyHaveUniqueItems(
            "plan item ids are (date, activity, resource, skill), so a repeated activity would vanish");
        plan.Activities.Where(a => a.ActivityType != "VocabularyReview")
            .Should().OnlyContain(a => a.EstimatedMinutes <= 15);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(34)]
    public async Task Sessions_up_to_34_minutes_never_grow_a_block_past_the_standard_length(int minutes)
    {
        Seed(minutes, dueWords: 20);

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan!.Activities.Should().OnlyContain(a => a.EstimatedMinutes <= 10,
            "the long-session fill only runs above 34 minutes");
        plan.Activities.Sum(a => a.EstimatedMinutes).Should().BeLessThanOrEqualTo(minutes);
    }

    [Fact]
    public async Task A_pinned_resource_is_kept_over_the_rotation_choice()
    {
        _fixture.SeedUserProfile(30);
        _fixture.SeedSkill(id: "skill-1");
        _fixture.SeedResource(id: "fresh", title: "Fresh one", vocabWordCount: 10);
        _fixture.SeedResource(id: "today", title: "Today's resource", vocabWordCount: 2);
        _fixture.SeedCompletion(DateTime.UtcNow.Date.AddDays(-2), "Reading", resourceId: "today");

        var plan = await _fixture.CreateBuilder().BuildPlanAsync(new PlanBuildRequest
        {
            UserProfileId = PlanGenerationTestFixture.TestUserId,
            PinnedResourceId = "today",
            AllowWrites = false
        });

        plan!.PrimaryResource!.Id.Should().Be("today");
    }

    [Fact]
    public async Task Yesterdays_resource_is_never_chosen_when_another_exists()
    {
        _fixture.SeedUserProfile(30);
        _fixture.SeedSkill(id: "skill-1");
        _fixture.SeedResource(id: "yesterday", title: "Used yesterday", vocabWordCount: 30);
        _fixture.SeedResource(id: "other", title: "Other", vocabWordCount: 1);
        _fixture.SeedCompletion(DateTime.UtcNow.Date.AddDays(-1), "Reading", resourceId: "yesterday");

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan!.PrimaryResource!.Id.Should().Be("other");
    }

    [Fact]
    public async Task A_learners_only_resource_is_still_used_the_day_after()
    {
        _fixture.SeedUserProfile(30);
        _fixture.SeedSkill(id: "skill-1");
        _fixture.SeedResource(id: "only", title: "Only resource", vocabWordCount: 5);
        _fixture.SeedCompletion(DateTime.UtcNow.Date.AddDays(-1), "Reading", resourceId: "only");

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan!.PrimaryResource!.Id.Should().Be("only", "without another resource, yesterday's beats none");
    }

    [Fact]
    public async Task Focus_words_with_nothing_due_still_get_a_review()
    {
        _fixture.SeedUserProfile(30);
        _fixture.SeedSkill(id: "skill-1");
        var resource = _fixture.SeedResource(id: "res", title: "Resource", vocabWordCount: 6);
        var words = _fixture.GetResourceVocabularyWordIds(resource.Id).Take(4).ToList();

        var plan = await _fixture.CreateBuilder().BuildPlanAsync(new PlanBuildRequest
        {
            UserProfileId = PlanGenerationTestFixture.TestUserId,
            FocusVocabularyWordIds = words,
            AllowWrites = false
        });

        var review = plan!.Activities.Should().ContainSingle(a => a.ActivityType == "VocabularyReview").Which;
        review.FocusVocabularyIds.Should().BeEquivalentTo(words);
    }

    private void Seed(int minutes, int dueWords)
    {
        _fixture.SeedUserProfile(minutes);
        _fixture.SeedSkill(id: "skill-1");
        var resource = _fixture.SeedResource(id: "res-long", title: "Long resource", mediaType: "Video",
            mediaUrl: "https://youtube.example/v", vocabWordCount: dueWords);
        foreach (var wordId in _fixture.GetResourceVocabularyWordIds(resource.Id))
            _fixture.SeedVocabularyProgress(vocabularyWordId: wordId, masteryScore: 0.3f,
                nextReviewDate: DateTime.UtcNow.Date.AddDays(-1), resourceId: resource.Id);
    }
}
