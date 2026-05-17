namespace SentenceStudio.Contracts.Plans;

/// <summary>
/// Localized narrative content. Server resolves language-neutral facts into
/// these prose strings per-request using Accept-Language.
/// </summary>
public sealed class PlanNarrativeDto
{
    /// <summary>1–3 sentence localized story.</summary>
    public required string Story { get; init; }

    /// <summary>Localized focus-area labels.</summary>
    public required List<string> FocusAreas { get; init; }

    public required List<PlanResourceSummaryDto> Resources { get; init; }

    public VocabInsightDto? VocabInsight { get; init; }
}

public sealed class PlanResourceSummaryDto
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    /// <summary>"Story", "Video", etc.</summary>
    public required string MediaType { get; init; }

    /// <summary>Already-localized reason this resource was chosen.</summary>
    public required string SelectionReason { get; init; }
}

public sealed class VocabInsightDto
{
    public required int TotalDue { get; init; }
    public required int ReviewCount { get; init; }
    public required int NewCount { get; init; }

    /// <summary>0..1.</summary>
    public required float AverageMastery { get; init; }

    public required List<TagInsightDto> StrugglingCategories { get; init; }
    public required List<string> SampleStrugglingWords { get; init; }

    /// <summary>Already-localized pattern insight, e.g. "trouble with time-related vocab".</summary>
    public string? PatternInsight { get; init; }
}

public sealed class TagInsightDto
{
    public required string Tag { get; init; }
    public required int WordCount { get; init; }

    /// <summary>0..1.</summary>
    public required float AverageAccuracy { get; init; }

    public required int TotalAttempts { get; init; }
}
