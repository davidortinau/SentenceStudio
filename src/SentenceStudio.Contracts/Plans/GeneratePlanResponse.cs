namespace SentenceStudio.Contracts.Plans;

public sealed class GeneratePlanResponse
{
    public string PlanId { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
    public List<GeneratePlanActivity> Activities { get; set; } = new();
}

public sealed class GeneratePlanActivity
{
    public string ActivityType { get; set; } = string.Empty;
    public int EstimatedMinutes { get; set; }
    public int Priority { get; set; }
    public int? ResourceId { get; set; }
    public int? SkillId { get; set; }
    public int? VocabWordCount { get; set; }
}
