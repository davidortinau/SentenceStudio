# Service Contracts: Vocabulary Search Syntax

**Feature**: 002-vocabulary-search-syntax  
**Date**: 2025-12-12

## SearchQueryParser Service

Parses raw user input into structured query components (filters + free text).

### Interface

```csharp
public interface ISearchQueryParser
{
    /// <summary>
    /// Parse raw search string into structured query components
    /// </summary>
    /// <param name="rawQuery">User-entered search text (e.g., "tag:nature 단풍")</param>
    /// <returns>Parsed query with filters and free text terms</returns>
    ParsedQuery Parse(string rawQuery);
    
    /// <summary>
    /// Validate that a filter token is well-formed
    /// </summary>
    bool IsValidFilterToken(string filterType, string filterValue);
}

public class ParsedQuery
{
    public List<FilterToken> Filters { get; set; } = new();
    public List<string> FreeTextTerms { get; set; } = new();
}

public class FilterToken
{
    public string Type { get; set; } = string.Empty;  // tag, resource, lemma, status
    public string Value { get; set; } = string.Empty;
    
    public FilterToken(string type, string value)
    {
        Type = type;
        Value = value;
    }
}
```

(Full service contracts file - 8500 characters total)
