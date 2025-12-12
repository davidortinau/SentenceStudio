namespace SentenceStudio.Shared.Models;

/// <summary>
/// T009: Represents a parsed user search query containing filter tokens and free-text components.
/// This is the output of SearchQueryParser.Parse() method.
/// </summary>
public class ParsedQuery
{
    /// <summary>
    /// Structured filter specifications (tag, resource, lemma, status)
    /// </summary>
    public List<FilterToken> Filters { get; set; } = new();

    /// <summary>
    /// Unstructured search terms (words without prefix syntax)
    /// </summary>
    public List<string> FreeTextTerms { get; set; } = new();

    /// <summary>
    /// Maximum allowed filter tokens (prevents query complexity explosion)
    /// </summary>
    public const int MaxFilters = 10;

    /// <summary>
    /// Maximum characters per free-text term
    /// </summary>
    public const int MaxFreeTextLength = 50;

    /// <summary>
    /// Check if the query has any filters or free text
    /// </summary>
    public bool HasContent => Filters.Any() || FreeTextTerms.Any();

    /// <summary>
    /// Get filters of a specific type
    /// </summary>
    public IEnumerable<FilterToken> GetFiltersByType(string type) =>
        Filters.Where(f => string.Equals(f.Type, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Get all tag filter values
    /// </summary>
    public IEnumerable<string> TagFilters => GetFiltersByType("tag").Select(f => f.Value);

    /// <summary>
    /// Get all resource filter values
    /// </summary>
    public IEnumerable<string> ResourceFilters => GetFiltersByType("resource").Select(f => f.Value);

    /// <summary>
    /// Get all lemma filter values
    /// </summary>
    public IEnumerable<string> LemmaFilters => GetFiltersByType("lemma").Select(f => f.Value);

    /// <summary>
    /// Get all status filter values
    /// </summary>
    public IEnumerable<string> StatusFilters => GetFiltersByType("status").Select(f => f.Value);

    /// <summary>
    /// Combined free text for LIKE queries
    /// </summary>
    public string CombinedFreeText => string.Join(" ", FreeTextTerms);

    /// <summary>
    /// Check if query is valid (within limits)
    /// </summary>
    public bool IsValid =>
        Filters.Count <= MaxFilters &&
        FreeTextTerms.All(t => t.Length <= MaxFreeTextLength) &&
        Filters.All(f => f.IsValid);
}
