namespace SentenceStudio.Shared.Models;

/// <summary>
/// T008: Individual structured filter extracted from search query (e.g., tag:nature).
/// Represents a single filter specification with type and value.
/// </summary>
public class FilterToken
{
    /// <summary>
    /// Filter category: tag, resource, lemma, or status
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Filter value (e.g., "nature", "general", "가다", "learning")
    /// </summary>
    public string Value { get; set; } = string.Empty;

    public FilterToken() { }

    public FilterToken(string type, string value)
    {
        Type = type.ToLowerInvariant();
        Value = value;
    }

    /// <summary>
    /// Valid filter types for vocabulary search
    /// </summary>
    public static readonly string[] ValidTypes = { "tag", "resource", "lemma", "status" };

    /// <summary>
    /// Check if the filter token is valid
    /// </summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Type) &&
        !string.IsNullOrWhiteSpace(Value) &&
        Value.Length <= 100 &&
        ValidTypes.Contains(Type.ToLowerInvariant());

    /// <summary>
    /// Two FilterTokens are equal if Type and Value match (case-insensitive)
    /// </summary>
    public override bool Equals(object? obj)
    {
        if (obj is FilterToken other)
        {
            return string.Equals(Type, other.Type, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Type?.ToLowerInvariant(),
            Value?.ToLowerInvariant());
    }

    public override string ToString() => $"{Type}:{Value}";
}
