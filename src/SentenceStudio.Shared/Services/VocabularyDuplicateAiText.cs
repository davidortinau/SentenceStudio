using System.Globalization;

namespace SentenceStudio.Services;

/// <summary>
/// How every client words what the AI did with a duplicate group and what it suggests for one
/// left to the learner (docs/duplicate-merge-ai-plan.md), from the shared resource keys.
/// </summary>
public static class VocabularyDuplicateAiText
{
    /// <summary>"소리: merged as "sound; noise"", "달: kept apart as "month", "moon"".</summary>
    public static string Applied(VocabularyDuplicateAiVerdict verdict, string term,
        IReadOnlyList<string> meanings, Func<string, string> text) =>
        string.Format(CultureInfo.CurrentCulture, text(verdict switch
        {
            VocabularyDuplicateAiVerdict.SameWord => "Vocabulary_DuplicatesAiMerged",
            VocabularyDuplicateAiVerdict.DifferentWords => "Vocabulary_DuplicatesAiKeptApart",
            _ => "Vocabulary_DuplicatesAiMixed"
        }), term, Meanings(verdict, meanings));

    /// <summary>The AI's call on a group it was not sure of, with its reason.</summary>
    public static string Suggestion(bool usable, VocabularyDuplicateAiVerdict verdict,
        IReadOnlyList<string> meanings, string reason, Func<string, string> text) =>
        !usable ? text("Vocabulary_DuplicatesAiUnsure")
            : string.Format(CultureInfo.CurrentCulture, text(verdict switch
            {
                VocabularyDuplicateAiVerdict.SameWord => "Vocabulary_DuplicatesAiSuggestsMerge",
                VocabularyDuplicateAiVerdict.DifferentWords => "Vocabulary_DuplicatesAiSuggestsApart",
                _ => "Vocabulary_DuplicatesAiSuggestsMixed"
            }), Meanings(verdict, meanings), reason).Trim();

    /// <summary>One merged meaning as it is (the string quotes it), else each meaning quoted.</summary>
    private static string Meanings(VocabularyDuplicateAiVerdict verdict, IReadOnlyList<string> meanings) =>
        verdict == VocabularyDuplicateAiVerdict.SameWord
            ? meanings.FirstOrDefault() ?? string.Empty
            : string.Join(", ", meanings.Select(meaning => $"\"{meaning}\""));
}
