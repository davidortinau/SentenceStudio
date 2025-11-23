using System.ComponentModel;

namespace SentenceStudio.Shared.Models.DailyPlanGeneration;

public class DailyPlanResponse
{
    [Description("List of 1-5 learning activities for today, ordered by priority (1=first, 2=second, etc)")]
    public List<PlanActivity> Activities { get; set; } = new();

    [Description("Brief explanation of why these activities were chosen (optional, for debugging)")]
    public string Rationale { get; set; } = string.Empty;
}

public class PlanActivity
{
    [Description("Type: VocabularyReview, Reading, Listening, Shadowing, Cloze, Translation, VocabularyGame")]
    public string ActivityType { get; set; } = string.Empty;

    [Description("Resource ID to use (null for vocabulary-only activities like VocabularyReview or VocabularyGame)")]
    public int? ResourceId { get; set; }

    [Description("Skill ID to practice (null for VocabularyReview)")]
    public int? SkillId { get; set; }

    [Description("Estimated minutes for this activity (5-15 range)")]
    public int EstimatedMinutes { get; set; }

    [Description("Priority order (1 = do first, 2 = second, etc)")]
    public int Priority { get; set; }

    [Description("For VocabularyReview: actual number of words selected for review (may be capped for pedagogical reasons)")]
    public int? VocabWordCount { get; set; }
}
