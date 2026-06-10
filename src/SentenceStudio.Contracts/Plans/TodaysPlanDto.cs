namespace SentenceStudio.Contracts.Plans;

/// <summary>
/// Fully-baked daily plan payload. Every string is already localized by the
/// server using the request's Accept-Language. The client renders only.
/// </summary>
public sealed class TodaysPlanDto
{
    /// <summary>User-local date this plan was generated for (yyyy-MM-dd).</summary>
    public required DateOnly GeneratedForDate { get; init; }

    /// <summary>UTC timestamp the plan was produced.</summary>
    public required DateTime GeneratedAtUtc { get; init; }

    /// <summary>The strategy that produced this plan ("deterministic" | "llm").</summary>
    public required string Strategy { get; init; }

    public required List<PlanItemDto> Items { get; init; }

    /// <summary>Ordered vocabulary word IDs selected as today's deterministic focus set.</summary>
    public List<string> FocusVocabularyIds { get; init; } = new();

    public required int EstimatedTotalMinutes { get; init; }
    public required int CompletedCount { get; init; }
    public required int TotalCount { get; init; }

    /// <summary>0..100.</summary>
    public required double CompletionPercentage { get; init; }

    public required StreakDto Streak { get; init; }

    public PlanNarrativeDto? Narrative { get; init; }

    /// <summary>Localized prose explaining why this plan was created.</summary>
    public string? Rationale { get; init; }
}
