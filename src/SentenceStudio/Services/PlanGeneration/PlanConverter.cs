using System;
using System.Collections.Generic;
using System.Linq;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models.DailyPlanGeneration;

namespace SentenceStudio.Services.PlanGeneration;

public static class PlanConverter
{
    public static TodaysPlan ConvertToTodaysPlan(
        DailyPlanResponse llmResponse,
        DateTime date,
        Dictionary<int, string> resourceTitles,
        Dictionary<int, string> skillNames,
        int vocabDueCount)
    {
        var planItems = new List<DailyPlanItem>();

        foreach (var activity in llmResponse.Activities.OrderBy(a => a.Priority))
        {
            var activityType = ParseActivityType(activity.ActivityType);
            var route = GetRouteForActivity(activityType);
            var routeParams = BuildRouteParameters(activity, activityType);

            var resourceTitle = activity.ResourceId.HasValue && resourceTitles.ContainsKey(activity.ResourceId.Value)
                ? resourceTitles[activity.ResourceId.Value]
                : null;

            var skillName = activity.SkillId.HasValue && skillNames.ContainsKey(activity.SkillId.Value)
                ? skillNames[activity.SkillId.Value]
                : null;

            planItems.Add(new DailyPlanItem(
                Id: GeneratePlanItemId(date, activityType, activity.ResourceId, activity.SkillId),
                TitleKey: GetTitleKeyForActivity(activityType),
                DescriptionKey: GetDescriptionKeyForActivity(activityType),
                ActivityType: activityType,
                EstimatedMinutes: activity.EstimatedMinutes,
                Priority: activity.Priority,
                IsCompleted: false,
                CompletedAt: null,
                Route: route,
                RouteParameters: routeParams,
                ResourceId: activity.ResourceId,
                ResourceTitle: resourceTitle,
                SkillId: activity.SkillId,
                SkillName: skillName,
                VocabDueCount: activityType == PlanActivityType.VocabularyReview ? vocabDueCount : null,
                DifficultyLevel: null
            ));
        }

        return new TodaysPlan(
            GeneratedForDate: date,
            Items: planItems,
            EstimatedTotalMinutes: planItems.Sum(i => i.EstimatedMinutes),
            CompletedCount: 0,
            TotalCount: planItems.Count,
            CompletionPercentage: 0.0,
            Streak: new StreakInfo(0, 0, null),
            ResourceTitles: null,
            SkillTitle: null
        );
    }

    private static PlanActivityType ParseActivityType(string activityType)
    {
        return activityType switch
        {
            "VocabularyReview" => PlanActivityType.VocabularyReview,
            "Reading" => PlanActivityType.Reading,
            "Listening" => PlanActivityType.Listening,
            "Shadowing" => PlanActivityType.Shadowing,
            "Cloze" => PlanActivityType.Cloze,
            "Translation" => PlanActivityType.Translation,
            "VocabularyGame" => PlanActivityType.VocabularyGame,
            _ => throw new ArgumentException($"Unknown activity type: {activityType}")
        };
    }

    private static string GetRouteForActivity(PlanActivityType activityType)
    {
        return activityType switch
        {
            PlanActivityType.VocabularyReview => "/vocabulary-quiz",
            PlanActivityType.Reading => "/reading",
            PlanActivityType.Listening => "/listening",
            PlanActivityType.Shadowing => "/shadowing",
            PlanActivityType.Cloze => "/cloze",
            PlanActivityType.Translation => "/translation",
            PlanActivityType.VocabularyGame => "/vocabulary-matching",
            _ => throw new ArgumentException($"Unknown activity type: {activityType}")
        };
    }

    private static Dictionary<string, object> BuildRouteParameters(PlanActivity activity, PlanActivityType activityType)
    {
        var parameters = new Dictionary<string, object>();

        if (activityType == PlanActivityType.VocabularyReview)
        {
            parameters["Mode"] = "SRS";
            parameters["DueOnly"] = true;
            // NEW: If ResourceId is provided, scope vocabulary to that resource
            if (activity.ResourceId.HasValue)
                parameters["ResourceId"] = activity.ResourceId.Value;
        }
        else if (activityType == PlanActivityType.VocabularyGame)
        {
            if (activity.SkillId.HasValue)
                parameters["SkillId"] = activity.SkillId.Value;
        }
        else
        {
            // Resource-based activities
            if (activity.ResourceId.HasValue)
                parameters["ResourceId"] = activity.ResourceId.Value;
            if (activity.SkillId.HasValue)
                parameters["SkillId"] = activity.SkillId.Value;
        }

        return parameters;
    }

    private static string GetTitleKeyForActivity(PlanActivityType activityType)
    {
        return activityType switch
        {
            PlanActivityType.VocabularyReview => "plan_item_vocab_review_title",
            PlanActivityType.Reading => "plan_item_reading_title",
            PlanActivityType.Listening => "plan_item_listening_title",
            PlanActivityType.Shadowing => "plan_item_shadowing_title",
            PlanActivityType.Cloze => "plan_item_cloze_title",
            PlanActivityType.Translation => "plan_item_translation_title",
            PlanActivityType.VocabularyGame => "plan_item_vocab_game_title",
            _ => "plan_item_unknown_title"
        };
    }

    private static string GetDescriptionKeyForActivity(PlanActivityType activityType)
    {
        return activityType switch
        {
            PlanActivityType.VocabularyReview => "plan_item_vocab_review_desc",
            PlanActivityType.Reading => "plan_item_reading_desc",
            PlanActivityType.Listening => "plan_item_listening_desc",
            PlanActivityType.Shadowing => "plan_item_shadowing_desc",
            PlanActivityType.Cloze => "plan_item_cloze_desc",
            PlanActivityType.Translation => "plan_item_translation_desc",
            PlanActivityType.VocabularyGame => "plan_item_vocab_game_desc",
            _ => "plan_item_unknown_desc"
        };
    }

    private static string GeneratePlanItemId(DateTime date, PlanActivityType activityType, int? resourceId, int? skillId)
    {
        var components = new List<string>
        {
            date.ToString("yyyy-MM-dd"),
            activityType.ToString()
        };

        if (resourceId.HasValue)
            components.Add($"R{resourceId.Value}");
        if (skillId.HasValue)
            components.Add($"S{skillId.Value}");

        var combined = string.Join("_", components);
        
        // Use deterministic hash to create stable ID
        // This ensures same inputs always produce same ID
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(combined));
        var guid = new Guid(hash.Take(16).ToArray());
        
        return guid.ToString("N")[..16]; // Take first 16 chars for reasonable length
    }
}
