namespace SentenceStudio.Models;

/// <summary>
/// T010: Available filter value displayed to user during autocomplete.
/// Used to show suggestions when user types filter prefixes like "tag:nat".
/// </summary>
public class AutocompleteSuggestion
{
    /// <summary>
    /// Filter category this suggestion belongs to (tag, resource, lemma, status)
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Actual filter value (e.g., "nature", "General Vocabulary")
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// User-friendly display text (may include count: "nature (12 words)")
    /// </summary>
    public string DisplayText { get; set; } = string.Empty;

    /// <summary>
    /// Optional: number of items matching this filter
    /// </summary>
    public int? Count { get; set; }

    public AutocompleteSuggestion() { }

    public AutocompleteSuggestion(string type, string value, string displayText, int? count = null)
    {
        Type = type;
        Value = value;
        DisplayText = displayText;
        Count = count;
    }

    /// <summary>
    /// Create a tag suggestion
    /// </summary>
    public static AutocompleteSuggestion ForTag(string tag, int count) =>
        new("tag", tag, count > 0 ? $"{tag} ({count})" : tag, count);

    /// <summary>
    /// Create a resource suggestion
    /// </summary>
    public static AutocompleteSuggestion ForResource(string resourceTitle, int? count = null) =>
        new("resource", resourceTitle, count.HasValue ? $"{resourceTitle} ({count})" : resourceTitle, count);

    /// <summary>
    /// Create a lemma suggestion
    /// </summary>
    public static AutocompleteSuggestion ForLemma(string lemma, int count) =>
        new("lemma", lemma, count > 0 ? $"{lemma} ({count})" : lemma, count);

    /// <summary>
    /// Create a status suggestion
    /// </summary>
    public static AutocompleteSuggestion ForStatus(string status, string localizedDisplay) =>
        new("status", status, localizedDisplay, null);

    /// <summary>
    /// Check if the suggestion is valid
    /// </summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Type) &&
        !string.IsNullOrWhiteSpace(Value) &&
        !string.IsNullOrWhiteSpace(DisplayText) &&
        (!Count.HasValue || Count >= 0);
}
