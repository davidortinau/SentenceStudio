using Xunit;
using FluentAssertions;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Tests the GeneratedPlanValidator against well-formed and malformed plans.
/// These tests verify the validator itself — separate from DeterministicPlanBuilder tests.
/// </summary>
public class PlanValidationTests
{
    private readonly GeneratedPlanValidator _validator = new();

    private static PlanSkeleton CreateValidPlan()
    {
        var resourceId = "valid-resource-1";

        return new PlanSkeleton
        {
            PrimaryResource = new SelectedResource
            {
                Id = resourceId,
                Title = "Valid Resource",
                MediaType = "Podcast",
                Language = "Korean",
                VocabularyCount = 10,
                DaysSinceLastUse = 5,
                SelectionReason = "Fresh resource",
                HasAudio = true,
                HasTranscript = true,
                YouTubeUrl = null
            },
            VocabularyReview = new VocabularyReviewBlock
            {
                WordCount = 8,
                TotalDue = 8,
                ResourceId = resourceId,
                IsContextual = true,
                EstimatedMinutes = 3,
                DueWords = Enumerable.Range(1, 8).Select(i => new VocabularyProgress
                {
                    Id = Guid.NewGuid().ToString(),
                    VocabularyWordId = $"word-{i}",
                    MasteryScore = 0.3f,
                    ProductionInStreak = 0,
                    TotalAttempts = 3,
                    CorrectAttempts = 1,
                    LearningContexts = new List<VocabularyLearningContext>
                    {
                        new()
                        {
                            Id = Guid.NewGuid().ToString(),
                            LearningResourceId = resourceId
                        }
                    }
                }).ToList()
            },
            Activities = new List<PlannedActivity>
            {
                new() { ActivityType = "VocabularyReview", ResourceId = resourceId, EstimatedMinutes = 3, Priority = 1, Rationale = "Review due words" },
                new() { ActivityType = "Listening", ResourceId = resourceId, EstimatedMinutes = 10, Priority = 2, Rationale = "Input practice" },
                new() { ActivityType = "Translation", ResourceId = resourceId, EstimatedMinutes = 10, Priority = 3, Rationale = "Output practice" }
            },
            TotalMinutes = 23
        };
    }

    private static Dictionary<string, LearningResource> CreateResourceLookup()
    {
        return new Dictionary<string, LearningResource>
        {
            ["valid-resource-1"] = new LearningResource
            {
                Id = "valid-resource-1",
                Title = "Valid Resource",
                MediaType = "Podcast",
                Transcript = "Transcript text",
                Language = "Korean"
            }
        };
    }

    [Fact]
    public void ValidPlan_PassesAllChecks()
    {
        var plan = CreateValidPlan();
        var resources = CreateResourceLookup();
        var completions = new List<DailyPlanCompletion>();

        var result = _validator.Validate(plan, completions, resources);

        result.IsValid.Should().BeTrue("a well-formed plan should have no critical violations");
        result.CriticalViolations.Should().BeEmpty();
    }

    [Fact]
    public void KnownWordsInReview_CriticalViolation()
    {
        var plan = CreateValidPlan();
        // Inject a known word (MasteryScore >= 0.85 AND ProductionInStreak >= 2)
        plan.VocabularyReview!.DueWords.Add(new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = "known-word",
            MasteryScore = 0.90f,
            ProductionInStreak = 3,
            TotalAttempts = 20,
            CorrectAttempts = 18
        });
        plan.VocabularyReview.WordCount = plan.VocabularyReview.DueWords.Count;

        var result = _validator.Validate(plan, new(), CreateResourceLookup());

        result.IsValid.Should().BeFalse();
        result.CriticalViolations.Should().Contain(v => v.Contains("Known words in review"),
            "plan with IsKnown words in review violates invariant 1");
    }

    [Fact]
    public void PromotedWordsInRecognitionMode_CriticalViolation()
    {
        var plan = CreateValidPlan();
        // Add words with MasteryScore >= 0.50 that should need production mode
        plan.VocabularyReview!.DueWords = Enumerable.Range(1, 8).Select(i => new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = $"promoted-{i}",
            MasteryScore = 0.55f, // Above promotion threshold
            ProductionInStreak = 0, // Not yet known
            TotalAttempts = 10,
            CorrectAttempts = 6,
            LearningContexts = new List<VocabularyLearningContext>
            {
                new() { Id = Guid.NewGuid().ToString(), LearningResourceId = "valid-resource-1" }
            }
        }).ToList();

        var result = _validator.Validate(plan, new(), CreateResourceLookup());

        result.IsValid.Should().BeFalse();
        result.CriticalViolations.Should().Contain(v => v.Contains("production mode"),
            "words with MasteryScore >= 0.50 should be flagged for production mode");
    }

    [Fact]
    public void LowOverlapContextualReview_CriticalViolation()
    {
        var plan = CreateValidPlan();
        // Set IsContextual = true but give words no context links to the resource
        plan.VocabularyReview!.IsContextual = true;
        plan.VocabularyReview.ResourceId = "valid-resource-1";
        plan.VocabularyReview.DueWords = Enumerable.Range(1, 10).Select(i => new VocabularyProgress
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyWordId = $"no-context-{i}",
            MasteryScore = 0.3f,
            TotalAttempts = 3,
            LearningContexts = new List<VocabularyLearningContext>
            {
                // Only 2 out of 10 words overlap with the resource (20% < 40%)
                new()
                {
                    Id = Guid.NewGuid().ToString(),
                    LearningResourceId = i <= 2 ? "valid-resource-1" : "other-resource"
                }
            }
        }).ToList();
        plan.VocabularyReview.WordCount = 10;

        var result = _validator.Validate(plan, new(), CreateResourceLookup());

        result.IsValid.Should().BeFalse();
        result.CriticalViolations.Should().Contain(v => v.Contains("Low contextual overlap"),
            "contextual review with < 40% overlap violates invariant 3");
    }

    [Fact]
    public void WordCountMismatch_CriticalViolation()
    {
        var plan = CreateValidPlan();
        // Set WordCount to not match DueWords.Count
        plan.VocabularyReview!.WordCount = 20;
        // DueWords has 8 items

        var result = _validator.Validate(plan, new(), CreateResourceLookup());

        result.IsValid.Should().BeFalse();
        result.CriticalViolations.Should().Contain(v => v.Contains("WordCount mismatch"),
            "WordCount=20 but DueWords.Count=8 violates invariant 4");
    }

    [Fact]
    public void YesterdaysResource_CriticalViolation()
    {
        var plan = CreateValidPlan();
        var yesterday = DateTime.UtcNow.Date.AddDays(-1);

        var completions = new List<DailyPlanCompletion>
        {
            new()
            {
                Id = Guid.NewGuid().ToString(),
                Date = yesterday,
                ActivityType = "Listening",
                ResourceId = plan.PrimaryResource!.Id, // Same resource!
                UserProfileId = "test-user",
                PlanItemId = "item-1"
            }
        };

        var result = _validator.Validate(plan, completions, CreateResourceLookup());

        result.IsValid.Should().BeFalse();
        result.CriticalViolations.Should().Contain(v => v.Contains("Yesterday's resource reused"),
            "using yesterday's primary resource violates invariant 6");
    }

    [Fact]
    public void IncompatibleActivityType_CriticalViolation()
    {
        var plan = CreateValidPlan();
        // Add Reading activity but resource has no transcript
        var noTranscriptResources = new Dictionary<string, LearningResource>
        {
            ["valid-resource-1"] = new LearningResource
            {
                Id = "valid-resource-1",
                Title = "No Transcript Resource",
                MediaType = "Podcast",
                Transcript = null, // No transcript!
                Language = "Korean"
            }
        };

        plan.Activities.Add(new PlannedActivity
        {
            ActivityType = "Reading",
            ResourceId = "valid-resource-1",
            EstimatedMinutes = 10,
            Priority = 4,
            Rationale = "Should not happen"
        });

        var result = _validator.Validate(plan, new(), noTranscriptResources);

        result.IsValid.Should().BeFalse();
        result.CriticalViolations.Should().Contain(v => v.Contains("Reading requires transcript"),
            "Reading without transcript violates invariant 7");
    }

    [Fact]
    public void RepeatedActivityFromYesterday_Warning()
    {
        var plan = CreateValidPlan();
        var yesterday = DateTime.UtcNow.Date.AddDays(-1);

        var completions = new List<DailyPlanCompletion>
        {
            new()
            {
                Id = Guid.NewGuid().ToString(),
                Date = yesterday,
                ActivityType = "Listening", // Same as plan's activity
                ResourceId = "other-resource", // Different resource (not a critical violation)
                UserProfileId = "test-user",
                PlanItemId = "item-1"
            }
        };

        var result = _validator.Validate(plan, completions, CreateResourceLookup());

        // Resource is different from yesterday, so no critical violation for resource reuse
        result.Warnings.Should().Contain(w => w.Contains("Repeated activity types"),
            "repeating Listening from yesterday should be a warning");
    }
}
