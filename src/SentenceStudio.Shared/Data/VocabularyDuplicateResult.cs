using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

/// <param name="SafeGroupCount">Groups "Merge all" merges without review (every page).</param>
/// <param name="SafeCopyCount">The copies those merges remove.</param>
/// <param name="ReviewGroupCount">Groups whose merge would lose something, for the learner to decide.</param>
/// <param name="BlockedGroupCount">Groups the merge cannot change.</param>
public sealed record VocabularyDuplicateScanResult(
    IReadOnlyList<VocabularyDuplicateGroup> Groups,
    int TotalGroupCount,
    int ExtraRecordCount,
    int SafeGroupCount = 0,
    int SafeCopyCount = 0,
    int ReviewGroupCount = 0,
    int BlockedGroupCount = 0);

/// <summary>How a duplicate group merges (docs/duplicate-merge-plan.md).</summary>
public enum VocabularyDuplicateRisk
{
    /// <summary>Nothing is lost: merged by "Merge all" without review.</summary>
    Safe,
    /// <summary>The copies differ (<see cref="VocabularyDuplicateGroup.Differences"/>); the learner decides.</summary>
    Review,
    /// <summary>The merge cannot change this group (<see cref="VocabularyDuplicateGroup.MergeBlockedReason"/>).</summary>
    Blocked,
}

/// <summary>What a merge of the group would drop, so it needs the learner's review.</summary>
public enum VocabularyDuplicateDifference
{
    /// <summary>The copies' native meanings differ (e.g. 맞다 "to fit" and "to be right").</summary>
    Meaning,
    /// <summary>More than one distinct memory aid text or image.</summary>
    MemoryAid,
    /// <summary>More than one distinct base form (lemma).</summary>
    BaseForm,
    /// <summary>A different language or kind (word, phrase, sentence).</summary>
    LanguageOrKind,
}

/// <summary>
/// The learner's own words (the one ownership rule) that share a target term; another
/// learner's word, or one recorded to no one, is never in a group.
/// </summary>
/// <param name="MergeBlockedReason">Why the merge cannot change the group, or null.</param>
/// <param name="Risk">Safe, review or blocked; a group built without it is rated from its fields.</param>
/// <param name="KeeperId">The copy the merge keeps: the first in merge order it can keep.</param>
/// <param name="RemovableIds">The copies the merge removes (no other learner's data uses them).</param>
/// <param name="Differences">What a merge would drop, when <paramref name="Risk"/> is review.</param>
public sealed record VocabularyDuplicateGroup(
    string NormalizedTerm,
    string DisplayTerm,
    IReadOnlyList<VocabularyDuplicateWordInfo> Words,
    bool CanMergeAutomatically,
    string? MergeBlockedReason,
    VocabularyDuplicateRisk? Risk = null,
    string? KeeperId = null,
    IReadOnlyList<string>? RemovableIds = null,
    IReadOnlyList<VocabularyDuplicateDifference>? Differences = null)
{
    public VocabularyDuplicateRisk EffectiveRisk => Risk
        ?? (CanMergeAutomatically ? VocabularyDuplicateRisk.Safe : VocabularyDuplicateRisk.Blocked);

    public string RecommendedKeeperId => KeeperId ?? RecommendedKeeper?.Word.Id ?? string.Empty;

    /// <summary>The copies a merge removes: the finder's set, else every copy but the keeper.</summary>
    public IReadOnlyList<string> MergeRemovableIds => RemovableIds
        ?? Words.Select(info => info.Word.Id).Where(id => id != RecommendedKeeperId).ToArray();

    /// <summary>
    /// The copy a merge keeps. A rated group with no keeper (every copy is used by someone else)
    /// has none, so no caller merges into a shared copy.
    /// </summary>
    public VocabularyDuplicateWordInfo? RecommendedKeeper => KeeperId is { } keeper
        ? Words.FirstOrDefault(info => info.Word.Id == keeper)
        : Risk is not null ? null : OrderWordsByMergePreference(Words).FirstOrDefault();

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

/// <summary>One copy in a duplicate group: always one of the learner's own words.</summary>
public sealed record VocabularyDuplicateWordInfo(
    VocabularyWord Word,
    int ResourceCount,
    double EncodingScore);

/// <summary>
/// What a merge did: the duplicate copies removed, and whether anything was saved (links and
/// history can move to the keeper even when a copy is kept because other data references it).
/// </summary>
/// <param name="RefusalKey">The resource key of why nothing changed, or null.</param>
public sealed record VocabularyMergeResult(int Deleted, bool Changed, string? RefusalKey = null)
{
    public static VocabularyMergeResult None { get; } = new(0, false);

    public static VocabularyMergeResult Refused(string key) => new(0, false, key);
}

/// <summary>What "Merge all" did: the groups merged, the copies removed, and any it could not merge.</summary>
public sealed record VocabularyMergeAllResult(
    int MergedGroups,
    int RemovedCopies,
    IReadOnlyList<(string DisplayTerm, string RefusalKey)> Refused)
{
    public static VocabularyMergeAllResult None { get; } = new(0, 0, []);
}
