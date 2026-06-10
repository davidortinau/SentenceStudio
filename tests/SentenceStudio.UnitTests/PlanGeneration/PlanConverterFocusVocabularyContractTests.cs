using FluentAssertions;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models.DailyPlanGeneration;
using SentenceStudio.UnitTests;

namespace SentenceStudio.UnitTests.PlanGeneration;

public sealed class PlanConverterFocusVocabularyContractTests
{
    private static readonly Dictionary<string, string> ResourceTitles = new()
    {
        ["focus-resource"] = "Focus Resource",
    };

    private static readonly Dictionary<string, string> SkillTitles = new()
    {
        ["focus-skill"] = "Focus Skill",
    };

    [Fact]
    public void FocusVocabulary_PlanConverterPropagatesFocusIdsForVocabularyAlignedActivities()
    {
        var focusIds = new List<string> { "focus-word-1", "focus-word-2", "focus-word-3", "focus-word-4", "focus-word-5" };
        var activityTypes = new[]
        {
            "VocabularyReview",
            "VocabularyGame",
            "Cloze",
            "Writing",
            "Translation",
            "Reading",
        };

        foreach (var activityType in activityTypes)
        {
            var planItem = ConvertSingle(CreateActivity(activityType, priority: 1, focusIds));

            var routeFocusIds = FocusVocabularyContractTestHelpers.GetRequiredRouteFocusIds(
                planItem.RouteParameters,
                activityType);
            routeFocusIds.Should().Equal(focusIds,
                "{0} is vocabulary-aligned and the launch route must receive the plan focus IDs", activityType);
        }
    }

    [Fact]
    public void FocusVocabulary_PlanConverterOmitsFocusIdsForNonVocabularyActivities()
    {
        var focusIds = new List<string> { "focus-word-1", "focus-word-2", "focus-word-3", "focus-word-4", "focus-word-5" };
        var activityTypes = new[]
        {
            "Listening",
            "VideoWatching",
            "Shadowing",
            "NumberDrill",
        };

        foreach (var activityType in activityTypes)
        {
            var planItem = ConvertSingle(CreateActivity(activityType, priority: 1, focusIds));

            var routeFocusIds = FocusVocabularyContractTestHelpers.GetOptionalRouteFocusIds(planItem.RouteParameters);
            routeFocusIds.Should().BeEmpty(
                "{0} is not a focus-vocabulary consumer and should not receive focus IDs through route parameters", activityType);
        }
    }

    [Fact]
    public void FocusVocabulary_GeneratePlanItemIdIgnoresFocusIds()
    {
        var date = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc);
        var first = CreateActivity(
            "VocabularyReview",
            priority: 1,
            new List<string> { "focus-word-1", "focus-word-2", "focus-word-3", "focus-word-4", "focus-word-5" });
        var second = CreateActivity(
            "VocabularyReview",
            priority: 1,
            new List<string> { "focus-word-6", "focus-word-7", "focus-word-8", "focus-word-9", "focus-word-10" });

        var firstPlan = PlanConverter.ConvertToTodaysPlan(
            new DailyPlanResponse { Activities = new List<PlanActivity> { first }, Rationale = "focus test" },
            date,
            ResourceTitles,
            SkillTitles,
            vocabDueCount: 10);
        var secondPlan = PlanConverter.ConvertToTodaysPlan(
            new DailyPlanResponse { Activities = new List<PlanActivity> { second }, Rationale = "focus test" },
            date,
            ResourceTitles,
            SkillTitles,
            vocabDueCount: 10);

        var directStableId = PlanConverter.GeneratePlanItemId(
            date,
            PlanActivityType.VocabularyReview,
            resourceId: null,
            skillId: null);

        firstPlan.Items.Single().Id.Should().Be(secondPlan.Items.Single().Id,
            "focus vocabulary can change daily, so item identity must stay keyed by date, activity, resource, and skill only");
        firstPlan.Items.Single().Id.Should().Be(directStableId,
            "PlanConverter.GeneratePlanItemId should remain the stable identity source and should not hash focus IDs");
    }

    private static PlanActivity CreateActivity(string activityType, int priority, List<string> focusIds)
    {
        var activity = new PlanActivity
        {
            ActivityType = activityType,
            ResourceId = activityType is "VocabularyReview" or "VocabularyGame" or "Cloze" or "NumberDrill"
                ? null
                : "focus-resource",
            SkillId = activityType == "VocabularyReview" ? null : "focus-skill",
            EstimatedMinutes = 10,
            Priority = priority,
            VocabWordCount = activityType == "VocabularyReview" ? focusIds.Count : null,
        };

        FocusVocabularyContractTestHelpers.SetRequiredFocusVocabularyIds(
            activity,
            focusIds,
            "PlanConverter must read focus IDs from PlanActivity and thread them to launch parameters");
        return activity;
    }

    private static DailyPlanItem ConvertSingle(PlanActivity activity)
    {
        var plan = PlanConverter.ConvertToTodaysPlan(
            new DailyPlanResponse { Activities = new List<PlanActivity> { activity }, Rationale = "focus test" },
            new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc),
            ResourceTitles,
            SkillTitles,
            vocabDueCount: 10);

        plan.Items.Should().ContainSingle();
        return plan.Items.Single();
    }
}
