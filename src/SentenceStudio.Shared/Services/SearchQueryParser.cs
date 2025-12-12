using System.Text.RegularExpressions;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Shared.Services;

/// <summary>
/// T007: Parses GitHub-style search syntax for vocabulary filtering.
/// Supports: tag:value, resource:value, lemma:value, status:value, and free-text terms.
/// 
/// Examples:
/// - "tag:nature 단풍" → Filter by tag "nature" AND free text "단풍"
/// - "resource:general tag:season" → Filter by resource AND tag
/// - "status:learning" → Filter by learning status
/// - "lemma:가다" → Filter by dictionary form
/// </summary>
public class SearchQueryParser : ISearchQueryParser
{
    // Regex pattern to match filter tokens: (tag|resource|lemma|status):value
    // Supports quoted values for multi-word filters: tag:"multi word"
    // Also supports unquoted values that end at whitespace
    private static readonly Regex FilterPattern = new(
        @"(tag|resource|lemma|status):(?:""([^""]*)""|(\S+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parse raw search string into structured query components
    /// </summary>
    /// <param name="rawQuery">User-entered search text (e.g., "tag:nature 단풍")</param>
    /// <returns>Parsed query with filters and free text terms</returns>
    public ParsedQuery Parse(string rawQuery)
    {
        var result = new ParsedQuery();

        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return result;
        }

        var workingText = rawQuery;

        // Extract all filter tokens
        var matches = FilterPattern.Matches(rawQuery);
        foreach (Match match in matches)
        {
            if (result.Filters.Count >= ParsedQuery.MaxFilters)
            {
                break; // Enforce maximum filter limit
            }

            var filterType = match.Groups[1].Value.ToLowerInvariant();
            // Group 2 is quoted value, Group 3 is unquoted value
            var filterValue = !string.IsNullOrEmpty(match.Groups[2].Value)
                ? match.Groups[2].Value
                : match.Groups[3].Value;

            if (!string.IsNullOrWhiteSpace(filterValue))
            {
                var token = new FilterToken(filterType, filterValue);
                if (token.IsValid && !result.Filters.Contains(token))
                {
                    result.Filters.Add(token);
                }
            }

            // Remove matched filter from working text
            workingText = workingText.Replace(match.Value, " ");
        }

        // Extract free text terms (everything that's not a filter)
        var freeTextParts = workingText
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Where(term => term.Length <= ParsedQuery.MaxFreeTextLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        result.FreeTextTerms = freeTextParts;

        return result;
    }

    /// <summary>
    /// Validate that a filter token is well-formed
    /// </summary>
    public bool IsValidFilterToken(string filterType, string filterValue)
    {
        if (string.IsNullOrWhiteSpace(filterType) || string.IsNullOrWhiteSpace(filterValue))
        {
            return false;
        }

        var normalizedType = filterType.ToLowerInvariant();
        if (!FilterToken.ValidTypes.Contains(normalizedType))
        {
            return false;
        }

        if (filterValue.Length > 100)
        {
            return false;
        }

        // Validate status values
        if (normalizedType == "status")
        {
            var validStatuses = new[] { "known", "learning", "unknown" };
            return validStatuses.Contains(filterValue.ToLowerInvariant());
        }

        return true;
    }

    /// <summary>
    /// Detect if user is currently typing a filter prefix at cursor position.
    /// Returns the filter type and partial value if detected.
    /// </summary>
    /// <param name="text">Full search text</param>
    /// <param name="cursorPosition">Current cursor position (0-based)</param>
    /// <returns>Tuple of (filterType, partialValue) or (null, null) if no filter prefix detected</returns>
    public (string? FilterType, string? PartialValue) DetectActiveFilter(string text, int cursorPosition)
    {
        if (string.IsNullOrEmpty(text) || cursorPosition < 0 || cursorPosition > text.Length)
        {
            return (null, null);
        }

        // Get text before cursor
        var textBeforeCursor = text.Substring(0, cursorPosition);

        // Match filter prefix at end of text: tag:partial or tag:"partial
        var activeFilterPattern = new Regex(@"(tag|resource|lemma|status):(?:""([^""]*)|(\S*))$", RegexOptions.IgnoreCase);
        var match = activeFilterPattern.Match(textBeforeCursor);

        if (match.Success)
        {
            var filterType = match.Groups[1].Value.ToLowerInvariant();
            // Group 2 is quoted partial, Group 3 is unquoted partial
            var partialValue = !string.IsNullOrEmpty(match.Groups[2].Value)
                ? match.Groups[2].Value
                : match.Groups[3].Value;

            return (filterType, partialValue ?? string.Empty);
        }

        return (null, null);
    }

    /// <summary>
    /// Reconstruct search query string from ParsedQuery (for updating Entry text after chip removal)
    /// </summary>
    public string Reconstruct(ParsedQuery query)
    {
        var parts = new List<string>();

        // Add filter tokens
        foreach (var filter in query.Filters)
        {
            // Quote values with spaces
            var value = filter.Value.Contains(' ') ? $"\"{filter.Value}\"" : filter.Value;
            parts.Add($"{filter.Type}:{value}");
        }

        // Add free text terms
        parts.AddRange(query.FreeTextTerms);

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Remove a specific filter from a query string
    /// </summary>
    public string RemoveFilter(string rawQuery, FilterToken filterToRemove)
    {
        var parsed = Parse(rawQuery);
        parsed.Filters.RemoveAll(f => f.Equals(filterToRemove));
        return Reconstruct(parsed);
    }

    /// <summary>
    /// Add a filter to a query string (used when autocomplete suggestion is selected)
    /// </summary>
    public string InsertFilter(string rawQuery, int cursorPosition, string filterType, string filterValue)
    {
        // Find the partial filter being typed and replace it with the complete filter
        var (activeType, partialValue) = DetectActiveFilter(rawQuery, cursorPosition);

        if (activeType != null)
        {
            // Remove the partial filter being typed
            var partialPattern = new Regex($@"{activeType}:(?:""[^""]*|[^\s]*)$", RegexOptions.IgnoreCase);
            var beforeCursor = rawQuery.Substring(0, cursorPosition);
            var afterCursor = cursorPosition < rawQuery.Length ? rawQuery.Substring(cursorPosition) : "";

            beforeCursor = partialPattern.Replace(beforeCursor, "").TrimEnd();

            // Build the complete filter
            var completeFilter = filterValue.Contains(' ')
                ? $"{filterType}:\"{filterValue}\""
                : $"{filterType}:{filterValue}";

            return $"{beforeCursor} {completeFilter} {afterCursor}".Trim();
        }

        // No active filter, just append
        var newFilter = filterValue.Contains(' ')
            ? $"{filterType}:\"{filterValue}\""
            : $"{filterType}:{filterValue}";

        return string.IsNullOrWhiteSpace(rawQuery)
            ? newFilter
            : $"{rawQuery} {newFilter}";
    }
}

/// <summary>
/// Interface for SearchQueryParser (for dependency injection and testing)
/// </summary>
public interface ISearchQueryParser
{
    ParsedQuery Parse(string rawQuery);
    bool IsValidFilterToken(string filterType, string filterValue);
    (string? FilterType, string? PartialValue) DetectActiveFilter(string text, int cursorPosition);
    string Reconstruct(ParsedQuery query);
    string RemoveFilter(string rawQuery, FilterToken filterToRemove);
    string InsertFilter(string rawQuery, int cursorPosition, string filterType, string filterValue);
}
