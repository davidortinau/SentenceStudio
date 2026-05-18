namespace SentenceStudio.Contracts.Plans;

public sealed class GenerateTodaysPlanRequest
{
    /// <summary>
    /// Optional explicit resource scope. If null/empty, server uses the
    /// user's currently selected resources. Server validates ownership.
    /// </summary>
    public List<string>? ResourceIds { get; set; }

    /// <summary>
    /// Optional explicit skill profile id. If null, server picks from
    /// the user's currently selected skill profile. Server validates ownership.
    /// </summary>
    public string? SkillProfileId { get; set; }

    /// <summary>
    /// "deterministic" | "llm" | "auto" (default).
    /// "auto" = LLM with deterministic fallback when available, else deterministic.
    /// </summary>
    public string Strategy { get; set; } = "auto";

    /// <summary>
    /// Optional override for total session minutes. If null, server uses
    /// the user's PreferredSessionMinutes.
    /// </summary>
    public int? TargetMinutes { get; set; }
}

public sealed class PlanItemProgressRequest
{
    /// <summary>
    /// Set-style: server clamps to 0..240 and overwrites the stored value.
    /// Idempotent.
    /// </summary>
    public int MinutesSpent { get; set; }
}
