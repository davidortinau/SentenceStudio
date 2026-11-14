using System.Text;
using System.Text.RegularExpressions;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

public static partial class VocabularyDuplicatePolicy
{
    public static string NormalizeKeyPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim().Normalize(NormalizationForm.FormKC);
        normalized = WhitespaceRegex().Replace(normalized, " ");
        return normalized.ToLowerInvariant();
    }

    public static string NormalizeTargetTerm(string? targetTerm) => NormalizeKeyPart(targetTerm);

    public static string BuildTargetKey(VocabularyWord word) =>
        NormalizeTargetTerm(word.TargetLanguageTerm);

    /// <summary>
    /// A native meaning as merging compares it: <see cref="NormalizeKeyPart"/> (case, spacing, line
    /// endings), without trailing punctuation or a leading "to " ("To eat.", " to eat", "eat").
    /// </summary>
    public static string NormalizeMeaning(string? value)
    {
        var key = NormalizeKeyPart(value).TrimEnd('.', '!', '?', ';', ',', ' ');
        if (key.StartsWith("to ", StringComparison.Ordinal))
            key = key[3..];
        return key.Trim();
    }

    /// <summary>
    /// What merging <paramref name="copies"/> into one word would drop (docs/duplicate-merge-plan.md):
    /// a second meaning, memory aid or base form, or a different language, kind or part of speech.
    /// An empty field, an unknown kind and an unclassified part of speech never differ: the merge
    /// fills them from another copy.
    /// </summary>
    public static IReadOnlyList<VocabularyDuplicateDifference> Differences(IReadOnlyCollection<VocabularyWord> copies)
    {
        var differences = new List<VocabularyDuplicateDifference>();
        if (DistinctValues(copies, word => NormalizeMeaning(word.NativeLanguageTerm)) > 1)
            differences.Add(VocabularyDuplicateDifference.Meaning);
        if (DistinctValues(copies, word => NormalizeKeyPart(word.MnemonicText)) > 1
            || DistinctValues(copies, word => word.MnemonicImageUri?.Trim() ?? string.Empty) > 1)
            differences.Add(VocabularyDuplicateDifference.MemoryAid);
        if (DistinctValues(copies, word => NormalizeKeyPart(word.Lemma)) > 1)
            differences.Add(VocabularyDuplicateDifference.BaseForm);
        if (DistinctValues(copies, word => NormalizeKeyPart(word.Language)) > 1
            || copies.Select(word => word.LexicalUnitType)
                .Where(kind => kind != LexicalUnitType.Unknown).Distinct().Count() > 1
            || copies.Where(word => word.PartOfSpeech is not (null or VocabularyPartOfSpeech.Unknown))
                .Select(word => word.PartOfSpeech).Distinct().Count() > 1)
            differences.Add(VocabularyDuplicateDifference.LanguageOrKind);
        return differences;
    }

    private static int DistinctValues(IEnumerable<VocabularyWord> copies, Func<VocabularyWord, string> value) =>
        copies.Select(value).Where(text => text.Length > 0).Distinct(StringComparer.Ordinal).Count();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();
}
