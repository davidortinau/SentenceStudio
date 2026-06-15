using SentenceStudio.Shared.Models;

namespace SentenceStudio.Shared.Services;

/// <summary>
/// Vocabulary filter scoped to the Writing activity. Writing practice is about producing
/// original sentences — phrases and full sentences from the user's vocabulary aren't
/// appropriate building blocks (they would short-circuit the productive task).
///
/// Per Captain directive (June 2026 — "the Write activity should not load full sentences or
/// even phrases. Filter to Word for now"), restrict to <see cref="LexicalUnitType.Word"/>.
/// Unclassified entries (<see cref="LexicalUnitType.Unknown"/>) are also excluded by design:
/// "for now" means strict — if the strict filter empties out a resource, the caller is
/// expected to log a diagnostic warning so the data quality issue surfaces.
/// </summary>
public static class WritingActivityVocabularyFilter
{
    /// <summary>
    /// Returns only entries whose <see cref="VocabularyWord.LexicalUnitType"/> equals
    /// <see cref="LexicalUnitType.Word"/>. Order is preserved.
    /// </summary>
    public static List<VocabularyWord> FilterToWordsOnly(IEnumerable<VocabularyWord> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(IsWordEntry).ToList();
    }

    /// <summary>
    /// Predicate form of <see cref="FilterToWordsOnly"/> for use in LINQ pipelines.
    /// </summary>
    public static bool IsWordEntry(VocabularyWord word) =>
        word is not null && word.LexicalUnitType == LexicalUnitType.Word;
}
