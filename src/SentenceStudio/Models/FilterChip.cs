namespace SentenceStudio.Models;

/// <summary>
/// T011: Visual representation of active filter that can be removed by user.
/// Used to display filter chips above the search Entry.
/// </summary>
public class FilterChip
{
    /// <summary>
    /// Filter category (tag, resource, lemma, status)
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Filter value
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// UI label (e.g., "Tag: nature", "Resource: General")
    /// </summary>
    public string DisplayText { get; set; } = string.Empty;

    /// <summary>
    /// Icon glyph for the filter type (optional)
    /// </summary>
    public string? IconGlyph { get; set; }

    public FilterChip() { }

    public FilterChip(string type, string value, string displayText, string? iconGlyph = null)
    {
        Type = type;
        Value = value;
        DisplayText = displayText;
        IconGlyph = iconGlyph;
    }

    /// <summary>
    /// Create a FilterChip from a FilterToken
    /// </summary>
    public static FilterChip FromToken(FilterToken token, LocalizationManager localize)
    {
        var typeDisplay = token.Type.ToLowerInvariant() switch
        {
            "tag" => $"{localize["Tag"]}",
            "resource" => $"{localize["Resource"]}",
            "lemma" => $"{localize["Lemma"]}",
            "status" => $"{localize["Status"]}",
            _ => token.Type
        };

        // Get appropriate icon for filter type
        var iconGlyph = token.Type.ToLowerInvariant() switch
        {
            "tag" => FluentUI.tag_20_regular,
            "resource" => FluentUI.book_20_regular,
            "lemma" => FluentUI.text_case_title_20_regular,
            "status" => FluentUI.checkmark_circle_20_regular,
            _ => null
        };

        return new FilterChip(
            token.Type,
            token.Value,
            $"{typeDisplay}: {token.Value}",
            iconGlyph);
    }

    /// <summary>
    /// Convert back to a FilterToken for query reconstruction
    /// </summary>
    public FilterToken ToToken() => new(Type, Value);

    /// <summary>
    /// Get the search syntax representation (e.g., "tag:nature")
    /// </summary>
    public string ToSearchSyntax() => $"{Type}:{Value}";

    /// <summary>
    /// Check if the chip is valid
    /// </summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Type) &&
        !string.IsNullOrWhiteSpace(Value) &&
        !string.IsNullOrWhiteSpace(DisplayText);
}
