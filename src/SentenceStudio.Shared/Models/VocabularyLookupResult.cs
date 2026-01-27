using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SentenceStudio.Shared.Models;

/// <summary>
/// Result from the vocabulary lookup tool.
/// </summary>
public class VocabularyLookupResult
{
    [Description("List of vocabulary words matching the search")]
    [JsonPropertyName("matches")]
    public List<VocabularyMatch> Matches { get; set; } = new();

    [Description("The original search term")]
    [JsonPropertyName("search_term")]
    public string SearchTerm { get; set; } = string.Empty;

    [Description("Total number of matches found")]
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }
}

/// <summary>
/// A single vocabulary word match from the lookup.
/// </summary>
public class VocabularyMatch
{
    [Description("The word in the target language (Korean)")]
    [JsonPropertyName("target_term")]
    public string TargetTerm { get; set; } = string.Empty;

    [Description("The word in the native language (English)")]
    [JsonPropertyName("native_term")]
    public string NativeTerm { get; set; } = string.Empty;

    [Description("Example sentences using this word")]
    [JsonPropertyName("examples")]
    public List<string> Examples { get; set; } = new();

    [Description("Mnemonic or memory aid for this word")]
    [JsonPropertyName("mnemonic")]
    public string? Mnemonic { get; set; }
}
