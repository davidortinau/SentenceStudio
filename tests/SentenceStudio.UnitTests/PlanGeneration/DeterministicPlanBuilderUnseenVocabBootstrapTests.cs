using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Services.Plans;
using SentenceStudio.UnitTests;

namespace SentenceStudio.UnitTests.PlanGeneration;

public sealed class DeterministicPlanBuilderUnseenVocabBootstrapTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private readonly PlanGenerationTestFixture _fixture;

    public DeterministicPlanBuilderUnseenVocabBootstrapTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
    }

    public void Dispose()
    {
    }

    [Fact]
    public async Task PlanWithZeroDueAndManyUnseenMapped_SchedulesVocabActivityFromUnseenCohort()
    {
        _fixture.SeedUserProfile(20);
        var resource = _fixture.SeedResource(
            id: "bootstrap-many-unseen-resource",
            title: "David Scenario Podcast",
            mediaType: "Podcast",
            transcript: "Transcript with many new words",
            vocabWordCount: 3253);
        _fixture.SeedSkill(id: "bootstrap-skill");

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull(
            "mapped words with no progress rows are new vocabulary and must bootstrap practice");
        plan.VocabularyReview!.IsBootstrap.Should().BeTrue();
        plan.VocabularyReview.UnseenWordCount.Should().Be(plan.VocabularyReview.WordCount);
        plan.VocabularyReview.WordCount.Should().Be(15, "bootstrap should cap the first cohort at a manageable size");
        plan.Activities.Should().Contain(activity => activity.ActivityType == "VocabularyReview");
        plan.Activities.Count(activity => activity.ActivityType is "Listening" or "Shadowing").Should().BeLessThan(plan.Activities.Count,
            "a learner with thousands of new words should not get a comprehension-only plan");
        plan.FocusVocabularyIds.Should().HaveCount(15);
        var resourceWordIds = _fixture.GetResourceVocabularyWordIds(resource.Id).ToHashSet(StringComparer.Ordinal);
        plan.FocusVocabularyIds.Should().OnlyContain(id => resourceWordIds.Contains(id));
    }

    [Fact]
    public async Task LiveDavidShape_ZeroProgressManyMappedAndResourceUsedEightDaysAgo_SchedulesBootstrapVocabActivity()
    {
        _fixture.SeedUserProfile(20);
        var dateContext = _fixture.ServiceProvider.GetRequiredService<IPlanDateContext>();
        var today = dateContext.UserLocalDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var resource = _fixture.SeedResource(
            id: "live-david-primary-resource",
            title: "Learn Korean 100 Must Know sentences, Basic Korean, TOPIK Practice",
            mediaType: "Text",
            transcript: "Transcript with many new words",
            vocabWordCount: 3253);
        var skill = _fixture.SeedSkill(id: "live-david-skill");
        _fixture.SeedCompletion(today.AddDays(-8), "Reading", resourceId: resource.Id, skillId: skill.Id);

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan.Should().NotBeNull();
        plan!.PrimaryResource.Should().NotBeNull();
        plan.PrimaryResource!.Id.Should().Be(resource.Id);
        plan.PrimaryResource.DaysSinceLastUse.Should().Be(8);
        plan.VocabularyReview.Should().NotBeNull(
            "the live Mac Catalyst shape has 3253 mapped words and zero progress rows, so bootstrap must engage");
        plan.VocabularyReview!.IsBootstrap.Should().BeTrue();
        plan.Activities.Should().Contain(activity => activity.ActivityType == "VocabularyReview");
        plan.FocusVocabularyIds.Should().HaveCount(15);
        FocusVocabularyContractTestHelpers.GetPreviewWordIds(plan.Narrative).Should().Equal(plan.FocusVocabularyIds);
    }

    [Fact]
    public async Task PlanWithZeroDueAndZeroUnseen_ProducesNoVocabBlock()
    {
        _fixture.SeedUserProfile(20);
        _fixture.SeedResource(
            id: "bootstrap-empty-resource",
            title: "Empty Resource",
            mediaType: "Podcast",
            transcript: "Transcript without mapped vocabulary",
            vocabWordCount: 0);
        _fixture.SeedSkill(id: "bootstrap-empty-skill");

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().BeNull("there are no due rows and no mapped new words to practice");
        plan.Narrative?.VocabInsight.Should().BeNull("vocab insight only renders when a vocab cohort exists");
        plan.FocusVocabularyIds.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanWithManyDueAndManyUnseen_PrefersDueOverUnseen()
    {
        _fixture.SeedUserProfile(30);
        var dueResource = _fixture.SeedResource(
            id: "bootstrap-due-resource",
            title: "Due Vocabulary Resource",
            vocabWordCount: 25);
        var unseenResource = _fixture.SeedResource(
            id: "bootstrap-unseen-resource",
            title: "Unseen Vocabulary Resource",
            vocabWordCount: 25);
        _fixture.SeedSkill(id: "bootstrap-due-skill");

        var seededDue = new List<SentenceStudio.Shared.Models.VocabularyProgress>();
        foreach (var wordId in _fixture.GetResourceVocabularyWordIds(dueResource.Id))
        {
            seededDue.Add(_fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.25f,
                nextReviewDate: DateTime.UtcNow.Date.AddDays(-1),
                resourceId: dueResource.Id));
        }

        var expectedDueFocus = seededDue
            .OrderBy(progress => progress.NextReviewDate)
            .ThenBy(progress => progress.VocabularyWordId, StringComparer.Ordinal)
            .Take(20)
            .Select(progress => progress.VocabularyWordId)
            .ToList();
        var unseenIds = _fixture.GetResourceVocabularyWordIds(unseenResource.Id).ToHashSet(StringComparer.Ordinal);

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull();
        plan.VocabularyReview!.IsBootstrap.Should().BeFalse("SRS due words at or above the minimum gate should win without padding unseen words");
        plan.FocusVocabularyIds.Should().Equal(expectedDueFocus);
        plan.FocusVocabularyIds.Should().NotContain(id => unseenIds.Contains(id));
    }

    [Fact]
    public async Task PlanBootstrap_PreviewWordsEqualFocusVocabularyIds()
    {
        _fixture.SeedUserProfile(20);
        _fixture.SeedResource(
            id: "bootstrap-preview-resource",
            title: "Preview Bootstrap Resource",
            mediaType: "Podcast",
            transcript: "Transcript with new words",
            vocabWordCount: 12);
        _fixture.SeedSkill(id: "bootstrap-preview-skill");

        var plan = await _fixture.CreateBuilder().BuildPlanAsync();

        plan.Should().NotBeNull();
        var focusIds = FocusVocabularyContractTestHelpers.GetRequiredFocusVocabularyIds(
            plan!,
            "bootstrap focus set should be the canonical source of truth");
        var previewIds = FocusVocabularyContractTestHelpers.GetPreviewWordIds(plan!.Narrative);

        previewIds.Should().Equal(focusIds,
            "PreviewWords must remain a projection of FocusVocabularyIds for unseen bootstrap cohorts");
        plan.Narrative!.VocabInsight.Should().NotBeNull();
        plan.Narrative.VocabInsight!.NewCount.Should().Be(focusIds.Count);
        plan.Narrative.VocabInsight.ReviewCount.Should().Be(0);
    }
}
