using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

public sealed record VocabularyDuplicateScanResult(
    IReadOnlyList<VocabularyDuplicateGroup> Groups,
    int TotalGroupCount,
    int ExtraRecordCount);

public sealed record VocabularyDuplicateGroup(
    string NormalizedTerm,
    string DisplayTerm,
    IReadOnlyList<VocabularyDuplicateWordInfo> Words,
    bool CanMergeAutomatically,
    string? MergeBlockedReason)
{
    public string RecommendedKeeperId => RecommendedKeeper?.Word.Id ?? string.Empty;

    public VocabularyDuplicateWordInfo? RecommendedKeeper => OrderWordsByMergePreference(Words).FirstOrDefault();

    public static IReadOnlyList<VocabularyDuplicateWordInfo> OrderWordsByMergePreference(IEnumerable<VocabularyDuplicateWordInfo> words)
    {
        return words
            .OrderByDescending(info => info.EncodingScore)
            .ThenByDescending(info => HasText(info.Word.MnemonicText))
            .ThenByDescending(info => HasText(info.Word.MnemonicImageUri))
            .ThenByDescending(info => HasText(info.Word.AudioPronunciationUri))
            .ThenByDescending(info => info.Word.UpdatedAt)
            .ThenByDescending(info => info.Word.CreatedAt)
            .ThenBy(info => info.Word.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
}

public sealed record VocabularyDuplicateWordInfo(
    VocabularyWord Word,
    int ResourceCount,
    double EncodingScore);
