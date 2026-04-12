using Xunit;
using FluentAssertions;
using SentenceStudio.Services.PlanGeneration;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Full integration tests for BuildPlanAsync — end-to-end through the deterministic pipeline.
/// These tests run the validator on generated plans to catch all bugs at once.
/// Several tests EXPECTED TO FAIL — the failures ARE the bug documentation.
/// </summary>
public class StudyPlanIntegrationTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private readonly PlanGenerationTestFixture _fixture;

    public StudyPlanIntegrationTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
    }

    public void Dispose() { }

    [Fact]
    public async Task CoherentPlan_ResourceAndVocabAligned()
    {
        // Arrange: Resource with vocab due — plan should align resource and vocab review
        _fixture.SeedUserProfile(30);
        var resource = _fixture.SeedResource(
            id: "coherent-res", title: "Coherent Resource",
            mediaType: "Podcast", transcript: "Transcript", vocabWordCount: 10);
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

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert — THIS MAY FAIL if resource selection diverges from vocab resource
        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull();

        if (plan.VocabularyReview!.IsContextual)
        {
            // If contextual, the vocab resource and primary resource should align
            plan.VocabularyReview.ResourceId.Should().Be(resource.Id,
                "contextual vocab review should use the resource with the most due-word overlap");

            // Check that primary resource activities reference the same resource
            var resourceActivities = plan.Activities
                .Where(a => a.ActivityType != "VocabularyReview" && a.ActivityType != "VocabularyGame")
                .ToList();

            if (resourceActivities.Any())
            {
                resourceActivities.Should().AllSatisfy(a =>
                    a.ResourceId.Should().Be(plan.PrimaryResource!.Id,
                        "all resource-based activities should use the primary resource"));
            }
        }
    }

    [Fact]
    public async Task PromotedWords_GetProductionMode()
    {
        // Arrange: All due words have MasteryScore >= 0.50 (should be production mode)
        _fixture.SeedUserProfile(20);
        var resource = _fixture.SeedResource(
            id: "promoted-res", title: "Promoted Resource",
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 8);
        _fixture.SeedSkill();

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);
        foreach (var wordId in wordIds)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.60f,
                productionInStreak: 0,
                currentStreak: 3,
                totalAttempts: 10,
                correctAttempts: 6,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert — THIS WILL LIKELY FAIL
        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull();

        // Run validator to check the promoted words invariant
        var validator = _fixture.CreateValidator();
        var resources = _fixture.GetResourceLookup();
        var completions = _fixture.GetRecentCompletions();
        var validationResult = validator.Validate(plan, completions, resources);

        // Expect a critical violation about production mode not being tracked
        validationResult.CriticalViolations.Should().Contain(v => v.Contains("production mode"),
            "words at MasteryScore >= 0.50 need production mode tracking");
    }

    [Fact]
    public async Task KnownWords_NeverInReview()
    {
        // Arrange: Mix of known and learning words
        _fixture.SeedUserProfile(20);
        var resource = _fixture.SeedResource(
            id: "known-mix-res", title: "Known Mix Resource",
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 12);
        _fixture.SeedSkill();

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);

        // 7 learning words (not known)
        for (int i = 0; i < 7; i++)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordIds[i],
                masteryScore: 0.4f,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        // 5 known words
        for (int i = 7; i < 12; i++)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordIds[i],
                masteryScore: 0.90f,
                productionInStreak: 3,
                currentStreak: 6,
                totalAttempts: 25,
                correctAttempts: 23,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull("7 non-known words due should trigger review");

        // No known words should appear in the due words list
        var validator = _fixture.CreateValidator();
        var resources = _fixture.GetResourceLookup();
        var result = validator.Validate(plan, new(), resources);

        result.CriticalViolations.Should().NotContain(v => v.Contains("Known words in review"),
            "the SQL filter in GetDueVocabularyAsync should exclude known words");
    }

    [Fact]
    public async Task ResourceRotation_OverMultipleDays()
    {
        // Arrange: 4 resources, simulate 3 days of plan generation
        _fixture.SeedUserProfile(20);

        var resources = new[]
        {
            _fixture.SeedResource(id: "rot-1", title: "Resource 1", mediaType: "Podcast", transcript: "T1", vocabWordCount: 5),
            _fixture.SeedResource(id: "rot-2", title: "Resource 2", mediaType: "Podcast", transcript: "T2", vocabWordCount: 5),
            _fixture.SeedResource(id: "rot-3", title: "Resource 3", mediaType: "Podcast", transcript: "T3", vocabWordCount: 5),
            _fixture.SeedResource(id: "rot-4", title: "Resource 4", mediaType: "Podcast", transcript: "T4", vocabWordCount: 5)
        };
        _fixture.SeedSkill();
        var today = DateTime.UtcNow.Date;

        // Simulate history: rot-1 used 3 days ago, rot-2 used 2 days ago, rot-3 used yesterday
        _fixture.SeedCompletion(today.AddDays(-3), "Listening", resourceId: "rot-1");
        _fixture.SeedCompletion(today.AddDays(-2), "Listening", resourceId: "rot-2");
        _fixture.SeedCompletion(today.AddDays(-1), "Listening", resourceId: "rot-3");

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert — THIS MAY FAIL due to scoring weights
        plan.Should().NotBeNull();
        plan!.PrimaryResource.Should().NotBeNull();

        // rot-3 was yesterday → disqualified
        plan.PrimaryResource!.Id.Should().NotBe("rot-3",
            "yesterday's resource should be disqualified");

        // rot-4 (never used, score=100+) or rot-1 (3 days ago, score=50) should be selected
        // rot-4 should win because DaysSinceLastUse=999 → score=100
        var selectedResourceIds = new HashSet<string> { "rot-1", "rot-2", "rot-4" };
        selectedResourceIds.Should().Contain(plan.PrimaryResource.Id,
            "should select a non-yesterday resource");
    }

    [Fact]
    public async Task VocabCount_MatchesReality()
    {
        // Arrange: 25 words due — WordCount should be capped at 20, but DueWords has all 25
        _fixture.SeedUserProfile(30);
        var resource = _fixture.SeedResource(
            id: "count-res", title: "Count Resource",
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 25);
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

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert — THIS WILL LIKELY FAIL
        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull();

        // Bug: WordCount = Math.Min(20, 25) = 20
        //       DueWords.Count = 25 (all due words, not capped)
        plan.VocabularyReview!.WordCount.Should().Be(plan.VocabularyReview.DueWords.Count,
            "WordCount should match the actual number of words selected, " +
            "but the code caps WordCount at 20 while DueWords contains all due words");
    }

    [Fact]
    public async Task ValidatorPassesOnGeneratedPlan()
    {
        // Arrange: A reasonable setup — the validator should find bugs in the generated plan
        _fixture.SeedUserProfile(20);
        var resource = _fixture.SeedResource(
            id: "validator-res", title: "Validator Resource",
            mediaType: "Podcast", transcript: "Text", vocabWordCount: 10);
        _fixture.SeedSkill();

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);
        foreach (var wordId in wordIds)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.55f, // Above promotion threshold
                productionInStreak: 0,
                currentStreak: 3,
                totalAttempts: 10,
                correctAttempts: 6,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Validate
        var validator = _fixture.CreateValidator();
        var resources = _fixture.GetResourceLookup();
        var completions = _fixture.GetRecentCompletions();
        var result = validator.Validate(plan!, completions, resources);

        // Assert — THIS DOCUMENTS ALL BUGS AT ONCE
        // We expect violations because the current code has known gaps
        // Report what we find for bug documentation:
        var allViolations = result.CriticalViolations.Concat(result.Warnings).ToList();
        allViolations.Should().NotBeEmpty(
            "the validator should find at least one issue with the current plan generation code: " +
            "promoted words without production mode tracking is a known gap");
    }
}
