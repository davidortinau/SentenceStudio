using SentenceStudio.Native;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Features.Vocabulary;

internal sealed class VocabularyFilterState
{
    private readonly SearchQueryParser _parser = new();
    private readonly Action<string> _committed;
    private string? _phoneDraft;

    /// <summary>
    /// The filters shown as chips in the search bar (chosen from the filter menu, or restored),
    /// and the bar's own text. The committed query is the chips' tokens, then the text
    /// (Captain 2026-11-12: filters as chips, not text). A whole-query search (the wide page's
    /// field, Android, the phone sheet) commits exactly what was typed, and its filters become
    /// the bar's chips beside the rest of the text.
    /// </summary>
    private readonly List<FilterToken> _chips = [];
    private string _fieldText;

    internal VocabularyFilterState(string? restoredQuery, Action<string> committed)
    {
        CommittedQuery = restoredQuery ?? string.Empty;
        _committed = committed;
        _fieldText = string.Empty;
        SplitIntoChips(CommittedQuery);
    }

    /// <summary>The search bar's chips (see <see cref="_chips"/>).</summary>
    internal IReadOnlyList<FilterToken> ChipFilters => _chips;

    /// <summary>The search bar's text beside its chips.</summary>
    internal string FieldText => _fieldText;

    internal string CommittedQuery { get; private set; }
    internal string VisibleQuery => _phoneDraft ?? CommittedQuery;
    internal bool PhoneSheetOpen => _phoneDraft is not null;
    internal bool HasActiveFilters => !string.IsNullOrWhiteSpace(CommittedQuery);
    internal int ActiveFilterCount => _parser.Parse(CommittedQuery).Filters.Count;

    /// <summary>The search's words outside its filters (source GetInitialTargetTermForAdd).</summary>
    internal IReadOnlyList<string> FreeTextTerms => _parser.Parse(CommittedQuery).FreeTextTerms;

    internal void Search(string? query) => SetWhole(query ?? string.Empty);

    /// <summary>The search bar's text changed; its chips stay.</summary>
    internal void SearchField(string? text)
    {
        _fieldText = text ?? string.Empty;
        Commit(Compose());
    }

    internal void OpenPhoneSheet() => _phoneDraft = CommittedQuery;

    internal void ChangeFilter(string type, string? value, bool live)
    {
        if (live)
        {
            // The choice becomes the type's one chip; a token of that type typed into the bar
            // gives way to it.
            _chips.RemoveAll(filter => filter.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
            _fieldText = WithoutType(_fieldText, type);
            // The parser reads at most MaxFilters filters; a chip past them would show but not filter.
            if (!string.IsNullOrWhiteSpace(value)
                && _parser.Parse(Compose()).Filters.Count < ParsedQuery.MaxFilters)
                _chips.Add(new FilterToken(type, value));
            Commit(Compose());
            return;
        }
        var query = _phoneDraft;
        if (query is null)
            return;
        var parsed = _parser.Parse(query);
        parsed.Filters.RemoveAll(filter =>
            filter.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(value))
            parsed.Filters.Add(new FilterToken(type, value));
        _phoneDraft = _parser.Reconstruct(parsed);
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
        SetWhole(_phoneDraft);
        _phoneDraft = null;
    }

    internal void CancelPhoneSheet() => _phoneDraft = null;

    internal void ClearAll()
    {
        SetWhole(string.Empty);
        if (_phoneDraft is not null)
            _phoneDraft = string.Empty;
    }

    internal void RemoveFilter(FilterToken filter)
    {
        _chips.RemoveAll(chip => chip.Equals(filter));
        _fieldText = Strip(_fieldText, token => token.Equals(filter));
        Commit(Compose());
    }

    /// <summary>
    /// The whole query, as typed into one field (the wide page, Android, the phone sheet): it
    /// commits as typed, and the bottom bar shows its filters as chips beside the rest.
    /// </summary>
    private void SetWhole(string query)
    {
        SplitIntoChips(query);
        Commit(query);
    }

    /// <summary>Every filter in <paramref name="query"/> becomes a chip; the rest is the bar's text.</summary>
    private void SplitIntoChips(string query)
    {
        _chips.Clear();
        _chips.AddRange(_parser.Parse(query).Filters);
        // Only the tokens the parser accepted move to chips; anything else stays as typed.
        _fieldText = Strip(query, _chips.Contains);
    }

    /// <summary>The chips' tokens, then the bar's text (exactly as typed).</summary>
    private string Compose()
    {
        if (_chips.Count == 0)
            return _fieldText;
        var chips = new ParsedQuery();
        chips.Filters.AddRange(_chips);
        var tokens = _parser.Reconstruct(chips);
        return string.IsNullOrEmpty(_fieldText) ? tokens : $"{tokens} {_fieldText}";
    }

    private string WithoutType(string text, string type) =>
        Strip(text, token => token.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Cuts the matching filter tokens (as <see cref="SearchQueryParser"/> finds them) out of
    /// <paramref name="text"/>, each with one space after it, and leaves everything else exactly
    /// as typed (words, repeats, spacing), so the field never rewrites the person's text.
    /// </summary>
    private string Strip(string text, Func<FilterToken, bool> remove)
    {
        var tokens = _parser.FindTokens(text);
        if (tokens.Count == 0)
            return text;
        var kept = new System.Text.StringBuilder(text.Length);
        var at = 0;
        foreach (var (token, index, length) in tokens)
        {
            if (!remove(token))
                continue;
            kept.Append(text, at, index - at);
            at = index + length;
            if (at < text.Length && char.IsWhiteSpace(text[at]))
                at++;
        }
        kept.Append(text, at, text.Length - at);
        return kept.ToString();
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
