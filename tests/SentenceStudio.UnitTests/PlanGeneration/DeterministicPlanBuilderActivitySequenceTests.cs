using Xunit;
using FluentAssertions;
using SentenceStudio.Services.PlanGeneration;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Tests BuildActivitySequenceAsync behavior via BuildPlanAsync.
/// Verifies cognitive load ordering, capability checks, and session budget.
/// </summary>
public class DeterministicPlanBuilderActivitySequenceTests : IClassFixture<PlanGenerationTestFixture>, IDisposable
{
    private readonly PlanGenerationTestFixture _fixture;

    public DeterministicPlanBuilderActivitySequenceTests(PlanGenerationTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearAllData();
    }

    public void Dispose() { }

    [Fact]
    public async Task VocabReviewIsFirstActivity()
    {
        // Arrange: Enough vocab to trigger review
        _fixture.SeedUserProfile(30);
        var resource = _fixture.SeedResource(
            title: "Priority Test", mediaType: "Podcast",
            transcript: "Test text", vocabWordCount: 10);
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
        plan!.Activities.Should().NotBeEmpty();

        var firstActivity = plan.Activities.OrderBy(a => a.Priority).First();
        firstActivity.ActivityType.Should().Be("VocabularyReview",
            "vocab review should have priority 1 (first activity)");
    }

    [Fact]
    public async Task InputBeforeOutput()
    {
        // Arrange: Resource with both audio and transcript (enables input + output activities)
        _fixture.SeedUserProfile(30);
        var resource = _fixture.SeedResource(
            title: "Full Resource", mediaType: "Podcast",
            transcript: "Full transcript text", vocabWordCount: 5);
        _fixture.SeedSkill();

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        var activities = plan!.Activities.OrderBy(a => a.Priority).ToList();

        var inputTypes = new HashSet<string> { "Reading", "Listening", "VideoWatching" };
        var outputTypes = new HashSet<string> { "Translation", "Cloze", "Writing", "Shadowing" };

        var inputActivities = activities.Where(a => inputTypes.Contains(a.ActivityType)).ToList();
        var outputActivities = activities.Where(a => outputTypes.Contains(a.ActivityType)).ToList();

        if (inputActivities.Any() && outputActivities.Any())
        {
            var firstInputPriority = inputActivities.Min(a => a.Priority);
            var firstOutputPriority = outputActivities.Min(a => a.Priority);

            firstInputPriority.Should().BeLessThan(firstOutputPriority,
                "input activities (comprehension) should come before output activities (production)");
        }
        else
        {
            // If there's only one type, the test passes trivially
            (inputActivities.Any() || outputActivities.Any()).Should().BeTrue(
                "plan should have at least one input or output activity");
        }
    }

    [Fact]
    public async Task ReadingOnlyWithTranscript()
    {
        // Arrange: Resource WITHOUT transcript
        _fixture.SeedUserProfile(30);
        var resource = _fixture.SeedResource(
            title: "No Transcript Resource", mediaType: "Podcast",
            transcript: null, vocabWordCount: 5);
        _fixture.SeedSkill();

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan!.Activities.Should().NotContain(a => a.ActivityType == "Reading",
            "Reading activity should not appear when resource has no transcript");
    }

    [Fact]
    public async Task VideoWatchingOnlyWithYouTubeUrl()
    {
        // Arrange: Podcast resource (no YouTube URL)
        _fixture.SeedUserProfile(30);
        var resource = _fixture.SeedResource(
            title: "Podcast Only", mediaType: "Podcast",
            transcript: "Transcript", mediaUrl: null, vocabWordCount: 5);
        _fixture.SeedSkill();

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan!.Activities.Should().NotContain(a => a.ActivityType == "VideoWatching",
            "VideoWatching should only appear when resource has a YouTube URL");
    }

    [Fact]
    public async Task ListeningOnlyWithAudio()
    {
        // Arrange: Text-only resource (no audio)
        _fixture.SeedUserProfile(30);
        var resource = _fixture.SeedResource(
            title: "Text Only", mediaType: "Text",
            transcript: "Text content", vocabWordCount: 5);
        _fixture.SeedSkill();

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        plan!.Activities.Should().NotContain(a => a.ActivityType == "Listening",
            "Listening activity should not appear when resource has no audio");
    }

    [Fact]
    public async Task AvoidsYesterdaysActivityTypes()
    {
        // Arrange: Yesterday had Reading and Translation
        _fixture.SeedUserProfile(30);
        var today = DateTime.UtcNow.Date;

        var resource = _fixture.SeedResource(
            title: "Variety Test", mediaType: "Video",
            transcript: "Video transcript",
            mediaUrl: "https://youtube.com/watch?v=test",
            vocabWordCount: 5);
        _fixture.SeedSkill();

        _fixture.SeedCompletion(today.AddDays(-1), "Reading", resourceId: resource.Id);
        _fixture.SeedCompletion(today.AddDays(-1), "Translation", resourceId: resource.Id);

        // Act
        var builder = _fixture.CreateBuilder();
        var plan = await builder.BuildPlanAsync();

        // Assert
        plan.Should().NotBeNull();
        var todayTypes = plan!.Activities.Select(a => a.ActivityType).ToList();

        // Vocab review is exempt from variety check
        var nonVocabTypes = todayTypes.Where(t => t != "VocabularyReview" && t != "VocabularyGame").ToList();

        // With Reading and Translation used yesterday, the builder should prefer
        // alternatives like Listening/VideoWatching for input and Cloze/Writing for output
        nonVocabTypes.Should().NotContain("Reading",
            "Reading was done yesterday — should prefer alternative input activities when available");
    }

    [Fact]
    public async Task TotalMinutesWithinSessionBudget()
    {
        // Arrange: 20-minute session budget
        _fixture.SeedUserProfile(20);
        var resource = _fixture.SeedResource(
            title: "Budget Test", mediaType: "Podcast",
            transcript: "Budget text", vocabWordCount: 8);
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
        var totalMinutes = plan!.Activities.Sum(a => a.EstimatedMinutes);
        totalMinutes.Should().BeLessThanOrEqualTo(20,
            "total activity minutes should not exceed the session budget of 20 minutes");
    }
}
