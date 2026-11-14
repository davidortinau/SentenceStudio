using System.Globalization;

namespace SentenceStudio.Services;

/// <summary>
/// How every client words a standing duplicate decision and what the AI suggests for a group
/// left to the learner (docs/duplicate-merge-ai-plan.md), from the shared resource keys.
/// </summary>
public static class VocabularyDuplicateAiText
{
    /// <summary>
    /// A standing decision's row: "소리: merged as "sound; noise"" (the kept word's meaning now),
    /// or "달: kept apart as "month", "moon"".
    /// </summary>
    public static string Decision(SentenceStudio.Shared.Models.VocabularyDuplicateDecisionKind kind, string term,
        IReadOnlyList<string> meanings, Func<string, string> text) =>
        kind == SentenceStudio.Shared.Models.VocabularyDuplicateDecisionKind.Merged
            ? string.Format(CultureInfo.CurrentCulture, text("Vocabulary_DuplicatesAiMerged"), term,
                meanings.FirstOrDefault() ?? string.Empty)
            : string.Format(CultureInfo.CurrentCulture, text("Vocabulary_DuplicatesAiKeptApart"), term,
                Meanings(VocabularyDuplicateAiVerdict.DifferentWords, meanings));

    /// <summary>
    /// The AI's call on a group it was not sure of, with its reason; or that it gave no usable
    /// call, or (<paramref name="unanswered"/>) did not answer for this group at all.
    /// </summary>
    public static string Suggestion(bool usable, bool unanswered, VocabularyDuplicateAiVerdict verdict,
        IReadOnlyList<string> meanings, string reason, Func<string, string> text) =>
        unanswered ? text("Vocabulary_DuplicatesAiNoAnswer")
        : !usable ? text("Vocabulary_DuplicatesAiUnsure")
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
