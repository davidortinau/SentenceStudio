using SentenceStudio.Shared.Models;

namespace SentenceStudio.Shared.Services;

public static class FocusVocabularySelection
{
    public const string QueryParameterName = "FocusVocabularyIds";
    public const int DefaultPromptVocabularyLimit = 40;

    public static List<string> ParseFocusVocabularyIds(string? focusVocabularyIds)
    {
        if (string.IsNullOrWhiteSpace(focusVocabularyIds))
        {
            return new List<string>();
        }

        return NormalizeFocusVocabularyIds(focusVocabularyIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static List<string> NormalizeFocusVocabularyIds(IEnumerable<string>? focusVocabularyIds)
    {
        return focusVocabularyIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? new List<string>();
    }

    public static List<VocabularyWord> SelectFocusWords(IEnumerable<VocabularyWord> vocabularyWords, IEnumerable<string>? focusVocabularyIds)
    {
        var orderedIds = NormalizeFocusVocabularyIds(focusVocabularyIds);
        if (orderedIds.Count == 0)
        {
            return new List<VocabularyWord>();
        }

        var wordsById = vocabularyWords
            .Where(IsUsableVocabularyWord)
            .GroupBy(word => word.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var selected = new List<VocabularyWord>(orderedIds.Count);
        foreach (var id in orderedIds)
        {
            if (wordsById.TryGetValue(id, out var word))
            {
                selected.Add(word);
            }
        }

        return selected;
    }

    public static List<VocabularyWord> BuildRequiredFirstPromptVocabulary(
        IEnumerable<VocabularyWord> requiredFocusWords,
        IEnumerable<VocabularyWord>? contextWords,
        int maxVocabularyWords = DefaultPromptVocabularyLimit)
    {
        var selected = new List<VocabularyWord>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddWords(requiredFocusWords, selected, seenIds, seenTerms, int.MaxValue);

        var targetCount = Math.Max(maxVocabularyWords, selected.Count);
        AddWords(contextWords ?? Enumerable.Empty<VocabularyWord>(), selected, seenIds, seenTerms, targetCount);

        return selected;
    }

    public static HashSet<string> BuildFocusVocabularyIdSet(IEnumerable<string>? focusVocabularyIds) =>
        NormalizeFocusVocabularyIds(focusVocabularyIds).ToHashSet(StringComparer.Ordinal);

    public static bool IsFocusVocabularyWord(VocabularyWord word, ISet<string> focusVocabularyIdSet) =>
        !string.IsNullOrWhiteSpace(word.Id) && focusVocabularyIdSet.Contains(word.Id);

    public static bool IsUsableVocabularyWord(VocabularyWord word) =>
        !string.IsNullOrWhiteSpace(word.Id)
        && !string.IsNullOrWhiteSpace(word.TargetLanguageTerm)
        && !string.IsNullOrWhiteSpace(word.NativeLanguageTerm);

    private static void AddWords(
        IEnumerable<VocabularyWord> words,
        List<VocabularyWord> selected,
        HashSet<string> seenIds,
        HashSet<string> seenTerms,
        int targetCount)
    {
        foreach (var word in words)
        {
            if (selected.Count >= targetCount)
            {
                return;
            }

            if (!IsUsableVocabularyWord(word))
            {
                continue;
            }

            var termKey = word.TargetLanguageTerm!.Trim();
            if (!seenIds.Add(word.Id) || !seenTerms.Add(termKey))
            {
                continue;
            }

            selected.Add(word);
        }
    }
}
