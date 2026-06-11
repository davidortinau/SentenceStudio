using Xunit;
using FluentAssertions;
using SentenceStudio.Services.PlanGeneration;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Tests SelectPrimaryResourceAsync behavior via BuildPlanAsync.
/// Several tests EXPECTED TO FAIL — documenting known bugs.
/// </summary>
public class DeterministicPlanBuilderResourceSelectionTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private readonly PlanGenerationTestFixture _fixture;

    public DeterministicPlanBuilderResourceSelectionTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
    }

    public void Dispose() { }

    [Fact]
    public async Task NeverSelectsYesterdaysResource()
    {
        // Arrange: 2 resources, one used yesterday
        _fixture.SeedUserProfile(20);
        var yesterday = DateTime.UtcNow.Date.AddDays(-1);

        var resYesterday = _fixture.SeedResource(
            id: "res-yesterday", title: "Yesterday's Podcast",
            mediaType: "Podcast", transcript: "Some text", vocabWordCount: 10);
        var resFresh = _fixture.SeedResource(
            id: "res-fresh", title: "Fresh Podcast",
            mediaType: "Podcast", transcript: "Some other text", vocabWordCount: 10);
        _fixture.SeedSkill();

        // Mark yesterday's resource as used
        _fixture.SeedCompletion(yesterday, "Listening", resourceId: resYesterday.Id);

        // Seed some vocab so the plan has something to work with
        var wordIds = _fixture.GetResourceVocabularyWordIds(resFresh.Id);
        foreach (var wordId in wordIds.Take(6))
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.3f,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resFresh.Id);
        }

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan!.PrimaryResource.Should().NotBeNull();
        plan.PrimaryResource!.Id.Should().NotBe(resYesterday.Id,
            "yesterday's resource should get -1000 score and be disqualified");
        plan.PrimaryResource.Id.Should().Be(resFresh.Id);
    }

    [Fact]
    public async Task PrefersFreshResources()
    {
        // Arrange: Resource A unused for 6 days, Resource B used 2 days ago
        _fixture.SeedUserProfile(20);
        var today = DateTime.UtcNow.Date;

        var resStale = _fixture.SeedResource(
            id: "res-stale", title: "Recently Used",
            mediaType: "Podcast", transcript: "Text A", vocabWordCount: 0);
        var resFresh = _fixture.SeedResource(
            id: "res-old", title: "Fresh Resource",
            mediaType: "Podcast", transcript: "Text B", vocabWordCount: 0);
        _fixture.SeedSkill();

        // Resource A used 2 days ago, Resource B used 6 days ago
        _fixture.SeedCompletion(today.AddDays(-2), "Listening", resourceId: resStale.Id);
        _fixture.SeedCompletion(today.AddDays(-6), "Listening", resourceId: resFresh.Id);

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan!.PrimaryResource.Should().NotBeNull();
        plan.PrimaryResource!.Id.Should().Be(resFresh.Id,
            "resource unused for 6 days (score +100) should beat one used 2 days ago (score +50)");
    }

    [Fact]
    public async Task ResourceUsed15DaysAgo_ShouldNotBeTreatedAsNeverUsed()
    {
        // Arrange: Resource used 15 days ago — falls outside the 14-day window
        // BUG: The code only looks back 14 days, so a resource used 15 days ago
        //       is treated as "never used" (DaysSinceLastUse = 999)
        _fixture.SeedUserProfile(20);
        var today = DateTime.UtcNow.Date;

        var res15Days = _fixture.SeedResource(
            id: "res-15d", title: "Used 15 Days Ago",
            mediaType: "Podcast", transcript: "Text 15", vocabWordCount: 10);
        var resNeverUsed = _fixture.SeedResource(
            id: "res-never", title: "Never Used",
            mediaType: "Podcast", transcript: "Text never", vocabWordCount: 10);
        _fixture.SeedSkill();

        _fixture.SeedCompletion(today.AddDays(-15), "Listening", resourceId: res15Days.Id);

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan!.PrimaryResource.Should().NotBeNull();

        // The 15-day resource should have DaysSinceLastUse = 15, not 999
        plan.PrimaryResource!.DaysSinceLastUse.Should().NotBe(999,
            "a resource used 15 days ago should have DaysSinceLastUse=15, not 999 (never-used sentinel)");
    }

    [Fact]
    public async Task ResourceUsed31DaysAgo_ShouldNotReportAs999Days()
    {
        _fixture.SeedUserProfile(20);
        var today = DateTime.UtcNow.Date;

        var res31Days = _fixture.SeedResource(
            id: "res-31d", title: "Used 31 Days Ago",
            mediaType: "Podcast", transcript: "Text 31", vocabWordCount: 10);
        _fixture.SeedSkill();

        _fixture.SeedCompletion(today.AddDays(-31), "Listening", resourceId: res31Days.Id);

        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        plan.Should().NotBeNull();
        plan!.PrimaryResource.Should().NotBeNull();
        plan.PrimaryResource!.Id.Should().Be(res31Days.Id);
        plan.PrimaryResource.DaysSinceLastUse.Should().Be(31,
            "resources outside the freshness lookback should still report actual last-use age");
        plan.PrimaryResource.SelectionReason.Should().NotContain("999");
        plan.PrimaryResource.SelectionReason.Should().Be("Resource not used recently");
    }

    [Fact]
    public async Task ResourceUsed8DaysAgo_ShouldUseNeutralSelectionReason_NotFresh()
    {
        _fixture.SeedUserProfile(20);
        var today = DateTime.UtcNow.Date;

        var res8Days = _fixture.SeedResource(
            id: "res-8d", title: "Used 8 Days Ago",
            mediaType: "Podcast", transcript: "Text 8", vocabWordCount: 0);
        _fixture.SeedSkill();

        _fixture.SeedCompletion(today.AddDays(-8), "Listening", resourceId: res8Days.Id);

        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        plan.Should().NotBeNull();
        plan!.PrimaryResource.Should().NotBeNull();
        plan.PrimaryResource!.Id.Should().Be(res8Days.Id);
        plan.PrimaryResource.DaysSinceLastUse.Should().Be(8);
        plan.PrimaryResource.SelectionReason.Should().NotContain("Fresh");
        plan.PrimaryResource.SelectionReason.Should().Be("Last used 8 days ago");
    }

    [Fact]
    public async Task VocabOverlapBonus_ShouldBeatFreshnessAlone()
    {
        // Arrange: Resource A has vocab overlap (+75) but was used 3 days ago (+50)
        //          Resource B has no vocab overlap but is "fresh" (never used, +100)
        // Total: A = 125, B = 100 — A should win
        // But if the freshness bonus for never-used is 999 days → +100, and
        // A's DaysSinceLastUse is 3 → +50, then A = 50+75 = 125, B = 100.
        // Actually, never-used = DaysSinceLastUse >= 5 → +100. So B = 100.
        // A = 50 + 75 = 125. A should win.
        _fixture.SeedUserProfile(20);
        var today = DateTime.UtcNow.Date;

        var resWithVocab = _fixture.SeedResource(
            id: "res-vocab", title: "Vocab Overlap Resource",
            mediaType: "Podcast", transcript: "Text vocab", vocabWordCount: 10);
        var resFreshNoVocab = _fixture.SeedResource(
            id: "res-fresh-no-vocab", title: "Fresh No Vocab",
            mediaType: "Podcast", transcript: "Text fresh", vocabWordCount: 5);
        _fixture.SeedSkill();

        // Resource A used 3 days ago
        _fixture.SeedCompletion(today.AddDays(-3), "Listening", resourceId: resWithVocab.Id);

        // Seed vocab due for Resource A's words (makes it the vocab resource)
        var wordIds = _fixture.GetResourceVocabularyWordIds(resWithVocab.Id);
        foreach (var wordId in wordIds)
        {
            _fixture.SeedVocabularyProgress(
                vocabularyWordId: wordId,
                masteryScore: 0.3f,
                nextReviewDate: DateTime.UtcNow.AddDays(-1),
                resourceId: resWithVocab.Id);
        }

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        // With current scoring: A = 50 (3 days ago) + 75 (vocab match) + log(11)*5 ≈ 137
        //                       B = 100 (never used) + 0 (no vocab match) + log(6)*5 ≈ 109
        // A should win, so this test should PASS with current scoring
        plan.Should().NotBeNull();
        plan!.PrimaryResource.Should().NotBeNull();
        plan.PrimaryResource!.Id.Should().Be(resWithVocab.Id,
            "vocab overlap bonus (+75) combined with recency (+50) should beat freshness alone (+100)");
    }

    [Fact]
    public async Task DoesNotRepeatSameResourceConsecutively()
    {
        // Arrange: 3 resources, one used for 2 consecutive days
        _fixture.SeedUserProfile(20);
        var today = DateTime.UtcNow.Date;

        var resOverused = _fixture.SeedResource(
            id: "res-overused", title: "Overused Resource",
            mediaType: "Podcast", transcript: "Text A", vocabWordCount: 10);
        var resB = _fixture.SeedResource(
            id: "res-B-alt", title: "Alternative B",
            mediaType: "Podcast", transcript: "Text B", vocabWordCount: 10);
        var resC = _fixture.SeedResource(
            id: "res-C-alt", title: "Alternative C",
            mediaType: "Podcast", transcript: "Text C", vocabWordCount: 10);
        _fixture.SeedSkill();

        // Overused resource: used day before yesterday and yesterday
        _fixture.SeedCompletion(today.AddDays(-2), "Listening", resourceId: resOverused.Id);
        _fixture.SeedCompletion(today.AddDays(-1), "Listening", resourceId: resOverused.Id);

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan!.PrimaryResource.Should().NotBeNull();
        plan.PrimaryResource!.Id.Should().NotBe(resOverused.Id,
            "resource used yesterday should be disqualified (score = -1000)");
    }

    [Fact]
    public async Task AudioResourcesGetBonus()
    {
        // Arrange: Video resource vs Text resource, both unused
        _fixture.SeedUserProfile(20);

        var resAudio = _fixture.SeedResource(
            id: "res-audio", title: "Audio Resource",
            mediaType: "Video", transcript: "Audio transcript",
            mediaUrl: "https://youtube.com/watch?v=123", vocabWordCount: 0);
        var resText = _fixture.SeedResource(
            id: "res-text-only", title: "Text Only Resource",
            mediaType: "Text", transcript: "Text only content", vocabWordCount: 0);
        _fixture.SeedSkill();

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan!.PrimaryResource.Should().NotBeNull();
        plan.PrimaryResource!.Id.Should().Be(resAudio.Id,
            "audio resources get +20 bonus, all else being equal");
    }

    [Fact]
    public async Task SelectionIsDeterministic_WithSameInputs()
    {
        // Arrange: Two resources with identical characteristics
        _fixture.SeedUserProfile(20);

        _fixture.SeedResource(
            id: "res-tie-1", title: "Tied Resource 1",
            mediaType: "Podcast", transcript: "Same text", vocabWordCount: 10);
        _fixture.SeedResource(
            id: "res-tie-2", title: "Tied Resource 2",
            mediaType: "Podcast", transcript: "Same text", vocabWordCount: 10);
        _fixture.SeedSkill();

        // Act: Run BuildPlanAsync multiple times with same inputs
        var results = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var builder = _fixture.CreateBuilder();
            var plan = await builder.BuildPlanAsync();
            plan.Should().NotBeNull();
            results.Add(plan!.PrimaryResource!.Id);
        }

        // Assert — THIS WILL LIKELY FAIL
        // Bug: .ThenBy(c => Guid.NewGuid()) makes selection non-deterministic on ties
        results.Distinct().Count().Should().Be(1,
            "same inputs should always produce same resource selection, " +
            "but Guid.NewGuid() tiebreaker introduces randomness");
    }

    [Fact]
    public async Task PenalizesFrequentlyUsedResource_InLastWeek()
    {
        // Arrange: both resources are otherwise similar, but one was used on
        // multiple recent days. The less-reused resource should be selected.
        _fixture.SeedUserProfile(20);
        var today = DateTime.UtcNow.Date;

        var resFrequent = _fixture.SeedResource(
            id: "res-frequent-weekly", title: "Frequently Used",
            mediaType: "Podcast", transcript: "Frequent text", vocabWordCount: 10);
        var resLessUsed = _fixture.SeedResource(
            id: "res-light-weekly", title: "Lightly Used",
            mediaType: "Podcast", transcript: "Light text", vocabWordCount: 10);
        _fixture.SeedSkill();

        // Frequent resource appeared on 3 separate days in the last week.
        _fixture.SeedCompletion(today.AddDays(-6), "Listening", resourceId: resFrequent.Id);
        _fixture.SeedCompletion(today.AddDays(-5), "Reading", resourceId: resFrequent.Id);
        _fixture.SeedCompletion(today.AddDays(-4), "Translation", resourceId: resFrequent.Id);

        // Less-used resource appeared once.
        _fixture.SeedCompletion(today.AddDays(-6), "Listening", resourceId: resLessUsed.Id);

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan!.PrimaryResource.Should().NotBeNull();
        plan.PrimaryResource!.Id.Should().Be(resLessUsed.Id,
            "resource reuse penalty in the last week should favor the less-reused alternative");
    }
}
