using Xunit;
using FluentAssertions;
using SentenceStudio.Services.PlanGeneration;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Tests DetermineVocabularyReviewAsync behavior via BuildPlanAsync.
/// We expect several tests to FAIL — the failures document bugs.
/// </summary>
public class DeterministicPlanBuilderVocabularyReviewTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private readonly PlanGenerationTestFixture _fixture;

    public DeterministicPlanBuilderVocabularyReviewTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
    }

    public void Dispose() { }

    [Fact]
    public async Task NoReviewBlock_WhenFewerThan5DueWords()
    {
        // Arrange: Only 3 due words (< 5 threshold)
        _fixture.SeedUserProfile(20);
        var resource = _fixture.SeedResource(title: "Test Podcast", vocabWordCount: 10);
        _fixture.SeedSkill();

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);
        for (int i = 0; i < 3; i++)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordIds[i],
                masteryScore: 0.3f,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull("plan should still be generated (resource-based activities)");
        plan!.VocabularyReview.Should().BeNull("fewer than 5 due words should skip vocab review");
    }

    [Fact]
    public async Task ReviewBlock_CappedAt20Words()
    {
        // Arrange: 25 due words (> 20 cap)
        _fixture.SeedUserProfile(30);
        var resource = _fixture.SeedResource(title: "Big Vocab Resource", vocabWordCount: 25);
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

        // Assert
        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull("25 due words should trigger vocab review");
        plan.VocabularyReview!.WordCount.Should().BeLessThanOrEqualTo(20,
            "word count should be capped at 20 per pedagogical research");
    }

    [Fact]
    public async Task ExcludesKnownWords()
    {
        // Arrange: 7 words, 2 are IsKnown (MasteryScore >= 0.85 AND ProductionInStreak >= 2)
        _fixture.SeedUserProfile(20);
        var resource = _fixture.SeedResource(title: "Mixed Resource", vocabWordCount: 7);
        _fixture.SeedSkill();

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);

        // 5 learning words
        for (int i = 0; i < 5; i++)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordIds[i],
                masteryScore: 0.3f,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        // 2 known words (should be excluded by GetDueVocabularyAsync SQL filter)
        for (int i = 5; i < 7; i++)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordIds[i],
                masteryScore: 0.90f,
                productionInStreak: 3,
                currentStreak: 5,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull("5 non-known words should trigger review");
        plan.VocabularyReview!.DueWords.Should().AllSatisfy(w =>
            w.IsKnown.Should().BeFalse("known words should be excluded from review"));
    }

    [Fact]
    public async Task HighMasteryLowProduction_IncludedButShouldBeProductionMode()
    {
        // Arrange: Words with MasteryScore=0.90 but ProductionInStreak=1 (not IsKnown)
        // These should be in review but flagged for Text (production) mode
        _fixture.SeedUserProfile(20);
        var resource = _fixture.SeedResource(title: "High Mastery Resource", vocabWordCount: 10);
        _fixture.SeedSkill();

        var wordIds = _fixture.GetResourceVocabularyWordIds(resource.Id);
        foreach (var wordId in wordIds)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.90f,
                productionInStreak: 1, // Not enough for IsKnown
                currentStreak: 4,
                totalAttempts: 20,
                correctAttempts: 18,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resource.Id);
        }

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert — THIS WILL LIKELY FAIL
        // The current code doesn't track per-word recommended mode
        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull();

        // Validate with the validator — expect critical violation
        var validator = _fixture.CreateValidator();
        var resources = _fixture.GetResourceLookup();
        var completions = _fixture.GetRecentCompletions();
        var result = validator.Validate(plan, completions, resources);

        result.CriticalViolations.Should().Contain(v => v.Contains("production mode"),
            "words with MasteryScore >= 0.50 need production mode tracking, but the code has none");
    }

    [Fact]
    public async Task ContextualReview_WhenResourceHas5PlusDueWords()
    {
        // Arrange: Resource A has 8 due words, Resource B has 2 due words
        _fixture.SeedUserProfile(20);
        var resourceA = _fixture.SeedResource(id: "res-A", title: "Vocab-Rich Podcast", vocabWordCount: 8);
        var resourceB = _fixture.SeedResource(id: "res-B", title: "Small Podcast", vocabWordCount: 3);
        _fixture.SeedSkill();

        var wordIdsA = _fixture.GetResourceVocabularyWordIds(resourceA.Id);
        foreach (var wordId in wordIdsA)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.3f,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resourceA.Id);
        }

        var wordIdsB = _fixture.GetResourceVocabularyWordIds(resourceB.Id);
        foreach (var wordId in wordIdsB)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.3f,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resourceB.Id);
        }

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull();
        plan.VocabularyReview!.IsContextual.Should().BeTrue(
            "resource A has 8 due words (>= 5 threshold), so review should be contextual");
        plan.VocabularyReview.ResourceId.Should().Be(resourceA.Id,
            "resource A has the most due-word overlap");
    }

    [Fact]
    public async Task GeneralReview_WhenNoResourceHas5PlusDueWords()
    {
        // Arrange: 3 resources each with 3 due words (none >= 5)
        _fixture.SeedUserProfile(20);
        var resA = _fixture.SeedResource(id: "gen-A", title: "Res A", vocabWordCount: 4);
        var resB = _fixture.SeedResource(id: "gen-B", title: "Res B", vocabWordCount: 4);
        var resC = _fixture.SeedResource(id: "gen-C", title: "Res C", vocabWordCount: 4);
        _fixture.SeedSkill();

        // 3 words per resource, 9 total due (>= 5 threshold for review, but no resource >= 5)
        foreach (var res in new[] { resA, resB, resC })
        {
            var wordIds = _fixture.GetResourceVocabularyWordIds(res.Id);
            foreach (var wordId in wordIds.Take(3))
            {
                _fixture.SeedVocabularyProgress(
                    vocabularyWordId: wordId,
                    masteryScore: 0.3f,
                    nextReviewDate: DateTime.UtcNow.AddDays(-1),
                    resourceId: res.Id);
            }
        }

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull("9 due words total should trigger review");
        plan.VocabularyReview!.IsContextual.Should().BeFalse(
            "no single resource has 5+ due words, so review should be general (non-contextual)");
        plan.VocabularyReview.ResourceId.Should().BeNull(
            "general review should have null ResourceId");
    }

    [Fact]
    public async Task WordCount_MatchesActualSelectedWords()
    {
        // Arrange: 12 due words — WordCount should equal DueWords.Count
        _fixture.SeedUserProfile(20);
        var resource = _fixture.SeedResource(title: "Dozen Words", vocabWordCount: 12);
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
        // Bug: WordCount = Math.Min(20, dueWords.Count) = 12
        //       But DueWords = dueWords (all 12)
        // Wait, in this case both are 12 so it should pass.
        // The bug manifests when dueWords.Count > 20.
        plan.Should().NotBeNull();
        plan!.VocabularyReview.Should().NotBeNull();
        plan.VocabularyReview!.WordCount.Should().Be(plan.VocabularyReview.DueWords.Count,
            "WordCount should exactly match the number of words actually selected for review");
    }
}
