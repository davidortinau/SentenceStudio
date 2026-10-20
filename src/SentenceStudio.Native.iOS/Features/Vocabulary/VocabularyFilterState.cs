using SentenceStudio.Native;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Features.Vocabulary;

internal sealed class VocabularyFilterState
{
    private readonly ISearchQueryParser _parser = new SearchQueryParser();
    private readonly Action<string> _committed;
    private string? _phoneDraft;

    internal VocabularyFilterState(string? restoredQuery, Action<string> committed)
    {
        CommittedQuery = restoredQuery ?? string.Empty;
        _committed = committed;
    }

    internal string CommittedQuery { get; private set; }
    internal string VisibleQuery => _phoneDraft ?? CommittedQuery;
    internal bool PhoneSheetOpen => _phoneDraft is not null;
    internal bool HasActiveFilters => !string.IsNullOrWhiteSpace(CommittedQuery);
    internal int ActiveFilterCount => _parser.Parse(CommittedQuery).Filters.Count;

    internal void Search(string? query) => Commit(query ?? string.Empty);

    internal void OpenPhoneSheet() => _phoneDraft = CommittedQuery;

    internal void ChangeFilter(string type, string? value, bool live)
    {
        var query = live ? CommittedQuery : _phoneDraft;
        if (query is null)
            return;
        var parsed = _parser.Parse(query);
        parsed.Filters.RemoveAll(filter =>
            filter.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(value))
            parsed.Filters.Add(new FilterToken(type, value));
        var changed = _parser.Reconstruct(parsed);
        if (live)
            Commit(changed);
        else
            _phoneDraft = changed;
    }

    internal string FilterValue(string type, bool useDraft = false) =>
        _parser.Parse(useDraft && _phoneDraft is not null
                ? _phoneDraft : CommittedQuery)
            .Filters.FirstOrDefault(filter => filter.Type.Equals(
                type, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

    internal void ApplyPhoneSheet()
    {
        if (_phoneDraft is null)
            return;
        Commit(_phoneDraft);
        _phoneDraft = null;
    }

    internal void CancelPhoneSheet() => _phoneDraft = null;

    internal void ClearAll()
    {
        Commit(string.Empty);
        if (_phoneDraft is not null)
            _phoneDraft = string.Empty;
    }

    internal void RemoveFilter(FilterToken filter)
    {
        Commit(_parser.RemoveFilter(CommittedQuery, filter));
    }

    internal IReadOnlyList<FilterToken> CommittedFilters =>
        _parser.Parse(CommittedQuery).Filters;

    private void Commit(string query)
    {
        if (string.Equals(CommittedQuery, query, StringComparison.Ordinal))
            return;
        CommittedQuery = query;
        _committed(query);
    }

    internal IReadOnlyList<NativeVocabularyEntry> Filter(
        IReadOnlyList<NativeVocabularyEntry> words,
        string sortField, bool descending)
    {
        IEnumerable<NativeVocabularyEntry> filtered = words;
        var parsed = _parser.Parse(CommittedQuery);
        foreach (var filter in parsed.Filters)
        {
            var value = filter.Value;
            filtered = filter.Type.ToLowerInvariant() switch
            {
                "association" when value.Equals("associated",
                    StringComparison.OrdinalIgnoreCase) =>
                    filtered.Where(word => word.AssociatedResources.Count > 0),
                "association" when value.Equals("orphaned",
                    StringComparison.OrdinalIgnoreCase) =>
                    filtered.Where(word => word.AssociatedResources.Count == 0),
                "status" => filtered.Where(word =>
                    word.Status.ToString().Equals(value,
                        StringComparison.OrdinalIgnoreCase)),
                "resource" => filtered.Where(word =>
                    word.AssociatedResources.Any(resource =>
                        resource.Title.Equals(value,
                            StringComparison.OrdinalIgnoreCase))),
                "tag" => filtered.Where(word =>
                    word.Tags?.Split(',', StringSplitOptions.TrimEntries |
                            StringSplitOptions.RemoveEmptyEntries)
                        .Any(tag => tag.Equals(value,
                            StringComparison.OrdinalIgnoreCase)) == true),
                "lemma" => filtered.Where(word =>
                    word.Lemma?.Equals(value,
                        StringComparison.OrdinalIgnoreCase) == true),
                "language" => filtered.Where(word =>
                    word.Language?.Equals(value,
                        StringComparison.OrdinalIgnoreCase) == true
                    || word.Language is null
                        && word.AssociatedResources.Any(resource =>
                            resource.Language?.Equals(value,
                                StringComparison.OrdinalIgnoreCase) == true)),
                "encoding" => filtered.Where(word =>
                    word.EncodingLabel.Equals(value,
                        StringComparison.OrdinalIgnoreCase)),
                "type" => filtered.Where(word =>
                    word.LexicalUnitType.ToString().Equals(value,
                        StringComparison.OrdinalIgnoreCase)),
                _ => filtered
            };
        }

        foreach (var term in parsed.FreeTextTerms)
            filtered = filtered.Where(word =>
                word.TargetTerm.Contains(term, StringComparison.OrdinalIgnoreCase)
                || word.NativeTerm.Contains(term, StringComparison.OrdinalIgnoreCase)
                || word.Tags?.Contains(term,
                    StringComparison.OrdinalIgnoreCase) == true
                || word.Lemma?.Contains(term,
                    StringComparison.OrdinalIgnoreCase) == true);

        return (sortField, descending) switch
        {
            ("updated", true) => filtered.OrderByDescending(word => word.UpdatedAt)
                .ThenBy(word => word.Id, StringComparer.Ordinal).ToArray(),
            ("updated", false) => filtered.OrderBy(word => word.UpdatedAt)
                .ThenBy(word => word.Id, StringComparer.Ordinal).ToArray(),
            (_, false) => filtered.OrderBy(word => word.CreatedAt)
                .ThenBy(word => word.Id, StringComparer.Ordinal).ToArray(),
            _ => filtered.OrderByDescending(word => word.CreatedAt)
                .ThenBy(word => word.Id, StringComparer.Ordinal).ToArray(),
        };
    }
}
