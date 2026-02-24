namespace SentenceStudio.Shared.Models;

public class ParsedQuery
{
    public const int MaxFilters = 8;
    public const int MaxFreeTextLength = 80;

    public List<FilterToken> Filters { get; set; } = new();
    public List<string> FreeTextTerms { get; set; } = new();
}
