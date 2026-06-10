using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SentenceStudio.Services.Plans;

/// <summary>
/// Canonical (and ONLY) serializer for <c>DailyPlan.FocusVocabularyFacts</c>,
/// <c>DailyPlan.NarrativeFacts</c>, and <c>DailyPlan.RationaleFacts</c>.
///
/// Both the API path (<see cref="PlanService"/>) and the mobile path
/// (<c>ProgressService</c>) MUST persist + read via this class. Drift between
/// the two paths historically caused the "preview vocab ≠ quiz vocab" bug
/// because:
///   1) PlanService wrote camelCase JSON via its private DTOs + FactsJsonOptions
///   2) ProgressService wrote PascalCase JSON via its private DTOs + default options
///   3) The webapp's PlanService read camelCase (case-SENSITIVE) and got NULL
///      narratives for any row a mobile device had written, silently breaking
///      the Preview button on the dashboard.
///
/// Wire format:
///   - WRITE  → camelCase (matches the original PlanService output that's
///              already in Postgres for historical rows).
///   - READ   → case-INSENSITIVE so we can deserialize both modern camelCase
///              writes AND any legacy PascalCase rows that the now-deleted
///              ProgressService private DTOs may have written during the Phase
///              2 implementation window.
/// </summary>
internal static class PlanFactsSerializer
{
    /// <summary>Options for writing. CamelCase to match the canonical wire format.</summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Options for reading. Case-insensitive so we can read both new camelCase
    /// writes and any legacy PascalCase rows.
    /// </summary>
    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static string? SerializeFocusVocabularyFacts(IEnumerable<string>? vocabularyIds, string source = "deterministic-srs")
    {
        var normalized = NormalizeFocusVocabularyIds(vocabularyIds);
        if (normalized.Count == 0)
        {
            return null;
        }
        return JsonSerializer.Serialize(new FocusVocabularyFactsDto
        {
            VocabularyIds = normalized,
            Source = source,
        }, WriteOptions);
    }

    public static List<string> DeserializeFocusVocabularyFacts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<string>();
        }

        try
        {
            var facts = JsonSerializer.Deserialize<FocusVocabularyFactsDto>(json, ReadOptions);
            return NormalizeFocusVocabularyIds(facts?.VocabularyIds);
        }
        catch (JsonException)
        {
            // Legacy fallback: raw JSON array (no envelope object) written by very early versions.
            return DeserializeFocusVocabularyIdsLegacyArray(json);
        }
    }

    public static string? SerializeRationaleFacts(string? resourceSelectionReason)
    {
        if (string.IsNullOrWhiteSpace(resourceSelectionReason))
        {
            return null;
        }
        return JsonSerializer.Serialize(new RationaleFactsDto
        {
            ResourceSelectionReason = resourceSelectionReason,
        }, WriteOptions);
    }

    public static RationaleFactsDto? DeserializeRationaleFacts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<RationaleFactsDto>(json, ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? SerializeNarrativeFacts(NarrativeFactsDto? facts)
    {
        if (facts is null)
        {
            return null;
        }
        return JsonSerializer.Serialize(facts, WriteOptions);
    }

    public static NarrativeFactsDto? DeserializeNarrativeFacts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<NarrativeFactsDto>(json, ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static List<string> NormalizeFocusVocabularyIds(IEnumerable<string>? vocabularyIds)
    {
        return vocabularyIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(System.StringComparer.Ordinal)
            .ToList() ?? new List<string>();
    }

    private static List<string> DeserializeFocusVocabularyIdsLegacyArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<string>();
        }
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(json, ReadOptions);
            return NormalizeFocusVocabularyIds(ids);
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }
}

internal sealed class FocusVocabularyFactsDto
{
    public List<string> VocabularyIds { get; set; } = new();
    public string Source { get; set; } = string.Empty;
}

internal sealed class RationaleFactsDto
{
    public string? ResourceSelectionReason { get; set; }
}

internal sealed class NarrativeFactsDto
{
    public string? Story { get; set; }
    public List<string>? FocusAreas { get; set; }
    public List<NarrativeResourceFactsDto>? Resources { get; set; }
    public NarrativeVocabInsightFactsDto? VocabInsight { get; set; }
}

internal sealed class NarrativeResourceFactsDto
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? MediaType { get; set; }
    public string? SelectionReason { get; set; }
}

internal sealed class NarrativeVocabInsightFactsDto
{
    public int TotalDue { get; set; }
    public int ReviewCount { get; set; }
    public int NewCount { get; set; }
    public float AverageMastery { get; set; }
    public List<NarrativeTagInsightFactsDto>? StrugglingCategories { get; set; }
    public List<string>? SampleStrugglingWords { get; set; }
    public List<NarrativePreviewWordFactsDto>? PreviewWords { get; set; }
    public string? PatternInsight { get; set; }
}

internal sealed class NarrativePreviewWordFactsDto
{
    public string? WordId { get; set; }
    public string? TargetTerm { get; set; }
    public string? NativeTerm { get; set; }
}

internal sealed class NarrativeTagInsightFactsDto
{
    public string? Tag { get; set; }
    public int WordCount { get; set; }
    public float AverageAccuracy { get; set; }
    public int TotalAttempts { get; set; }
}
