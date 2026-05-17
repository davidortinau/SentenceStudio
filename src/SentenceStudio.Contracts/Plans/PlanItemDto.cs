namespace SentenceStudio.Contracts.Plans;

/// <summary>
/// A single plan item. Title and Description are already localized server-side.
/// Route / RouteParameters are intentionally NOT carried — each client maps
/// (ActivityType, ResourceId, SkillId) to its own navigation surface.
/// </summary>
public sealed class PlanItemDto
{
    /// <summary>Deterministic SHA256-derived id stable across regenerations.</summary>
    public required string Id { get; init; }

    /// <summary>Exact PlanActivityType enum string (case-sensitive).</summary>
    public required string ActivityType { get; init; }

    /// <summary>Localized display title.</summary>
    public required string Title { get; init; }

    /// <summary>Localized display description.</summary>
    public required string Description { get; init; }

    public required int Priority { get; init; }
    public required int EstimatedMinutes { get; init; }

    /// <summary>Server-clamped to 0..240. Set-style (not additive).</summary>
    public int MinutesSpent { get; init; }

    public bool IsCompleted { get; init; }
    public DateTime? CompletedAtUtc { get; init; }

    public string? ResourceId { get; init; }
    public string? ResourceTitle { get; init; }
    public string? SkillId { get; init; }
    public string? SkillName { get; init; }
    public int? VocabDueCount { get; init; }
    public string? DifficultyLevel { get; init; }
}
