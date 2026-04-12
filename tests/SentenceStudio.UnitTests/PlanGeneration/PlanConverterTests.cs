using Xunit;
using FluentAssertions;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models.DailyPlanGeneration;

namespace SentenceStudio.UnitTests.PlanGeneration;

/// <summary>
/// Tests PlanConverter.ConvertToTodaysPlan — the static conversion from
/// DailyPlanResponse (LLM/deterministic output) to TodaysPlan (UI model).
/// </summary>
public class PlanConverterTests
{
    private static DailyPlanResponse CreateTestResponse(params PlanActivity[] activities)
    {
        return new DailyPlanResponse
        {
            Activities = activities.ToList(),
            Rationale = "Test rationale"
        };
    }

    private static Dictionary<string, string> TestResourceTitles => new()
    {
        ["res-1"] = "Korean Podcast Episode 1",
        ["res-2"] = "Video Lesson 2"
    };

    private static Dictionary<string, string> TestSkillNames => new()
    {
        ["skill-1"] = "Korean Grammar",
        ["skill-2"] = "Vocabulary Building"
    };

    [Fact]
    public void RoutesVocabReviewToQuizPage()
    {
        var response = CreateTestResponse(
            new PlanActivity
            {
                ActivityType = "VocabularyReview",
                ResourceId = "res-1",
                SkillId = null,
                EstimatedMinutes = 5,
                Priority = 1,
                VocabWordCount = 15
            });

        var plan = PlanConverter.ConvertToTodaysPlan(
            response, DateTime.Today, TestResourceTitles, TestSkillNames, 20);

        plan.Items.Should().HaveCount(1);
        plan.Items[0].Route.Should().Be("/vocabulary-quiz",
            "VocabularyReview should route to /vocabulary-quiz");
    }

    [Fact]
    public void VocabReviewHasSRSModeParam()
    {
        var response = CreateTestResponse(
            new PlanActivity
            {
                ActivityType = "VocabularyReview",
                ResourceId = "res-1",
                SkillId = null,
                EstimatedMinutes = 5,
                Priority = 1,
                VocabWordCount = 15
            });

        var plan = PlanConverter.ConvertToTodaysPlan(
            response, DateTime.Today, TestResourceTitles, TestSkillNames, 20);

        plan.Items[0].RouteParameters.Should().ContainKey("Mode");
        plan.Items[0].RouteParameters!["Mode"].Should().Be("SRS",
            "VocabularyReview route params should include Mode=SRS");
        plan.Items[0].RouteParameters.Should().ContainKey("DueOnly");
        plan.Items[0].RouteParameters["DueOnly"].Should().Be(true,
            "VocabularyReview route params should include DueOnly=true");
    }

    [Fact]
    public void VocabWordCount_MatchesPlanActivity()
    {
        var response = CreateTestResponse(
            new PlanActivity
            {
                ActivityType = "VocabularyReview",
                ResourceId = null,
                SkillId = null,
                EstimatedMinutes = 5,
                Priority = 1,
                VocabWordCount = 15 // Explicit count from deterministic builder
            });

        var plan = PlanConverter.ConvertToTodaysPlan(
            response, DateTime.Today, TestResourceTitles, TestSkillNames, vocabDueCount: 25);

        plan.Items[0].VocabDueCount.Should().Be(15,
            "VocabDueCount should use the activity's VocabWordCount (15) not the total due count (25)");
    }

    [Fact]
    public void VocabWordCount_FallsBackToTotalWhenNull()
    {
        var response = CreateTestResponse(
            new PlanActivity
            {
                ActivityType = "VocabularyReview",
                ResourceId = null,
                SkillId = null,
                EstimatedMinutes = 5,
                Priority = 1,
                VocabWordCount = null // No explicit count
            });

        var plan = PlanConverter.ConvertToTodaysPlan(
            response, DateTime.Today, TestResourceTitles, TestSkillNames, vocabDueCount: 25);

        plan.Items[0].VocabDueCount.Should().Be(25,
            "when VocabWordCount is null, should fallback to total vocabDueCount");
    }

    [Fact]
    public void AllActivitiesHaveValidRoutes()
    {
        var activityTypes = new[]
        {
            "VocabularyReview", "Reading", "Listening", "VideoWatching",
            "Shadowing", "Cloze", "Translation", "Writing",
            "SceneDescription", "Conversation", "VocabularyGame"
        };

        var expectedRoutes = new Dictionary<string, string>
        {
            ["VocabularyReview"] = "/vocabulary-quiz",
            ["Reading"] = "/reading",
            ["Listening"] = "/shadowing",
            ["VideoWatching"] = "/video-watching",
            ["Shadowing"] = "/shadowing",
            ["Cloze"] = "/cloze",
            ["Translation"] = "/translation",
            ["Writing"] = "/writing",
            ["SceneDescription"] = "/scene",
            ["Conversation"] = "/conversation",
            ["VocabularyGame"] = "/vocabulary-matching"
        };

        foreach (var activityType in activityTypes)
        {
            var response = CreateTestResponse(
                new PlanActivity
                {
                    ActivityType = activityType,
                    ResourceId = "res-1",
                    SkillId = "skill-1",
                    EstimatedMinutes = 10,
                    Priority = 1
                });

            var plan = PlanConverter.ConvertToTodaysPlan(
                response, DateTime.Today, TestResourceTitles, TestSkillNames, 10);

            plan.Items.Should().HaveCount(1);
            plan.Items[0].Route.Should().Be(expectedRoutes[activityType],
                $"activity type '{activityType}' should map to route '{expectedRoutes[activityType]}'");
        }
    }

    [Fact]
    public void ResourceTitlesResolved()
    {
        var response = CreateTestResponse(
            new PlanActivity
            {
                ActivityType = "Reading",
                ResourceId = "res-1",
                SkillId = "skill-1",
                EstimatedMinutes = 10,
                Priority = 1
            },
            new PlanActivity
            {
                ActivityType = "Translation",
                ResourceId = "res-2",
                SkillId = "skill-2",
                EstimatedMinutes = 10,
                Priority = 2
            });

        var plan = PlanConverter.ConvertToTodaysPlan(
            response, DateTime.Today, TestResourceTitles, TestSkillNames, 10);

        plan.Items.Should().HaveCount(2);
        plan.Items[0].ResourceTitle.Should().Be("Korean Podcast Episode 1",
            "resource titles should be resolved from the lookup dictionary");
        plan.Items[1].ResourceTitle.Should().Be("Video Lesson 2");
        plan.Items[0].SkillName.Should().Be("Korean Grammar",
            "skill names should be resolved from the lookup dictionary");
        plan.Items[1].SkillName.Should().Be("Vocabulary Building");
    }

    [Fact]
    public void ActivitiesOrderedByPriority()
    {
        var response = CreateTestResponse(
            new PlanActivity { ActivityType = "Translation", ResourceId = "res-1", EstimatedMinutes = 10, Priority = 3 },
            new PlanActivity { ActivityType = "VocabularyReview", ResourceId = null, EstimatedMinutes = 5, Priority = 1, VocabWordCount = 10 },
            new PlanActivity { ActivityType = "Reading", ResourceId = "res-1", EstimatedMinutes = 10, Priority = 2 });

        var plan = PlanConverter.ConvertToTodaysPlan(
            response, DateTime.Today, TestResourceTitles, TestSkillNames, 10);

        plan.Items.Should().HaveCount(3);
        plan.Items[0].ActivityType.Should().Be(PlanActivityType.VocabularyReview, "priority 1 first");
        plan.Items[1].ActivityType.Should().Be(PlanActivityType.Reading, "priority 2 second");
        plan.Items[2].ActivityType.Should().Be(PlanActivityType.Translation, "priority 3 third");
    }

    [Fact]
    public void EstimatedTotalMinutes_SumsCorrectly()
    {
        var response = CreateTestResponse(
            new PlanActivity { ActivityType = "VocabularyReview", EstimatedMinutes = 5, Priority = 1, VocabWordCount = 10 },
            new PlanActivity { ActivityType = "Reading", ResourceId = "res-1", EstimatedMinutes = 10, Priority = 2 },
            new PlanActivity { ActivityType = "Translation", ResourceId = "res-1", EstimatedMinutes = 8, Priority = 3 });

        var plan = PlanConverter.ConvertToTodaysPlan(
            response, DateTime.Today, TestResourceTitles, TestSkillNames, 10);

        plan.EstimatedTotalMinutes.Should().Be(23, "total should be 5 + 10 + 8 = 23");
        plan.TotalCount.Should().Be(3);
        plan.CompletedCount.Should().Be(0);
    }
}
