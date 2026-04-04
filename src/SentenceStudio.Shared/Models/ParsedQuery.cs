namespace SentenceStudio.Shared.Models;

public class ParsedQuery
{
    public const int MaxFilters = 8;
    public const int MaxFreeTextLength = 80;

    public List<FilterToken> Filters { get; set; } = new();
    public List<string> FreeTextTerms { get; set; } = new();

    // Convenience computed properties
    public bool HasContent => Filters.Count > 0 || FreeTextTerms.Count > 0;
    public bool IsValid => HasContent;
    public string CombinedFreeText => string.Join(" ", FreeTextTerms);

    public IReadOnlyList<string> TagFilters =>
        Filters.Where(f => f.Type == "tag").Select(f => f.Value).ToList();
    public IReadOnlyList<string> ResourceFilters =>
        Filters.Where(f => f.Type == "resource").Select(f => f.Value).ToList();
    public IReadOnlyList<string> LemmaFilters =>
        Filters.Where(f => f.Type == "lemma").Select(f => f.Value).ToList();
    public IReadOnlyList<string> StatusFilters =>
        Filters.Where(f => f.Type == "status").Select(f => f.Value).ToList();
}
