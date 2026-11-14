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

    /// <summary>
    /// <paramref name="copies"/> (in merge order) split into sets that differ in nothing
    /// (<see cref="Differences"/>): each copy joins the first set it matches. 약's "approximately"
    /// and its two "medicine" copies make two sets.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<T>> SameWordSets<T>(IEnumerable<T> copies,
        Func<T, VocabularyWord> word)
    {
        bool Fits(IEnumerable<T> set, T copy) =>
            Differences(set.Select(word).Append(word(copy)).ToArray()).Count == 0;
        var sets = new List<List<T>>();
        foreach (var copy in copies)
        {
            var home = sets.FirstOrDefault(set => Fits(set, copy));
            if (home is null)
                sets.Add([copy]);
            else
                home.Add(copy);
        }
        if (sets.Count < 2)
            return sets;
        // With two or more sets, a copy that would also fit another one (an empty meaning beside
        // "medicine" and "approximately") is never merged by guess: it stands alone, for the learner.
        var result = new List<IReadOnlyList<T>>();
        foreach (var set in sets)
        {
            var sure = set.Where(copy => !sets.Any(other => other != set && Fits(other, copy))).ToList();
            result.AddRange(set.Except(sure).Select(copy => (IReadOnlyList<T>)[copy]));
            if (sure.Count > 0)
                result.Add(sure);
        }
        return result;
    }

    /// <summary>
    /// <paramref name="part"/> kept by the copy the learner chose (Keep This) when it is one the
    /// part would remove; the old keeper is then removed in its place.
    /// </summary>
    public static VocabularyDuplicateMergePart WithKeeper(VocabularyDuplicateMergePart part,
        IReadOnlyCollection<string>? keepers)
    {
        var chosen = keepers?.FirstOrDefault(part.RemovableIds.Contains);
        return chosen is null ? part : part with {
            KeeperId = chosen,
            OtherIds = part.OtherIds.Where(id => id != chosen).Append(part.KeeperId).ToArray(),
            RemovableIds = part.RemovableIds.Where(id => id != chosen).Append(part.KeeperId).ToArray()
        };
    }

    /// <summary>
    /// <paramref name="part"/> without the copies the learner left out or skipped; null when its
    /// keeper is left out or no copy it would remove is left (it is then not made at all).
    /// </summary>
    public static VocabularyDuplicateMergePart? WithoutLeftOut(VocabularyDuplicateMergePart part,
        IReadOnlyCollection<string>? leaveOut)
    {
        if (leaveOut is null or { Count: 0 })
            return part;
        if (leaveOut.Contains(part.KeeperId))
            return null;
        var removable = part.RemovableIds.Where(id => !leaveOut.Contains(id)).ToArray();
        return removable.Length == 0 ? null
            : part with { OtherIds = part.OtherIds.Where(id => !leaveOut.Contains(id)).ToArray(),
                RemovableIds = removable };
    }

    /// <summary>
    /// The copies Merge all must not take on a duplicate page: every copy of a skipped group, and
    /// the copies left out of a group that offers a choice (every client asks this).
    /// </summary>
    public static IReadOnlySet<string> LeftOut<TGroup, TCopy>(IEnumerable<TGroup> groups,
        Func<TGroup, IReadOnlyList<TCopy>> copies, Func<TGroup, bool> skipped,
        Func<TCopy, bool> included, Func<TCopy, string> id) =>
        groups.SelectMany(group => skipped(group) ? copies(group)
                : OffersChoice(copies(group).Count) ? copies(group).Where(copy => !included(copy)) : [])
            .Select(id)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The copies Merge all would still remove from <paramref name="loaded"/> groups' parts once
    /// <paramref name="leaveOut"/> is applied, less than their full count: what a page subtracts
    /// from the scan's count so its "Merge N Copies" matches what will merge.
    /// </summary>
    public static int CopiesLeftOut(IEnumerable<VocabularyDuplicateMergePart> loaded,
        IReadOnlyCollection<string> leaveOut, IReadOnlyCollection<string>? keepers = null) =>
        loaded.Sum(part => part.RemovableCount - (ForPage(part, leaveOut, keepers)?.RemovableCount ?? 0));

    /// <summary>
    /// The one rule for what Merge all does with a part given the page's choices (the count and
    /// the merge both use it): a chosen keeper that is not left out keeps it, then the left-out
    /// copies (the old keeper among them, if left out) come out.
    /// </summary>
    public static VocabularyDuplicateMergePart? ForPage(VocabularyDuplicateMergePart part,
        IReadOnlyCollection<string>? leaveOut, IReadOnlyCollection<string>? keepers) =>
        WithoutLeftOut(WithKeeper(part, keepers?.Where(id => leaveOut?.Contains(id) != true).ToArray()),
            leaveOut);

    /// <summary>
    /// How much the learner has learned a copy, for choosing the keeper: mastery weighted by
    /// practice, so one lucky attempt does not outrank a copy practised many times.
    /// </summary>
    public static double LearnedWeight(double mastery, int attempts) =>
        mastery * Math.Min(attempts, 10) / 10.0;

    /// <summary>The copy's history line: "Mastery 100% · attempts: 1", or "Not practised yet".</summary>
    public static string HistoryText(double mastery, int attempts, Func<string, string> text) =>
        attempts > 0 || mastery > 0
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                text("Vocabulary_DuplicatesHistory"), Math.Round(mastery * 100), attempts)
            : text("Vocabulary_DuplicatesNoHistory");

    /// <summary>
    /// A group of three or more copies lets the learner choose which ones merge (Captain
    /// 2026-11-14: "merge some matching duplicates but not all"); two copies merge or not.
    /// </summary>
    public static bool OffersChoice(int copyCount) => copyCount > 2;

    /// <summary>The copies a Merge takes: the chosen ones when the group offers a choice, else all.</summary>
    public static IReadOnlyList<T> ChosenCopies<T>(IReadOnlyList<T> copies, Func<T, bool> included) =>
        OffersChoice(copies.Count) ? copies.Where(included).ToArray() : copies;

    /// <summary>
    /// The copy a Merge keeps, from <paramref name="copies"/> in merge order: the scan's keeper
    /// while every copy is chosen, else the first chosen copy no one else uses; null when none
    /// can be kept. Every client asks this, so they keep the same copy.
    /// </summary>
    /// <param name="isChosenKeeper">The copy the learner chose to keep (Keep This), if any.</param>
    public static T? KeeperOf<T>(IReadOnlyList<T> copies, Func<T, bool> included,
        Func<T, bool> usedByOthers, Func<T, bool> isScanKeeper,
        Func<T, bool>? isChosenKeeper = null) where T : class
    {
        var chosen = ChosenCopies(copies, included);
        if (isChosenKeeper is not null
            && chosen.FirstOrDefault(copy => isChosenKeeper(copy) && !usedByOthers(copy)) is { } keeper)
            return keeper;
        return chosen.Count == copies.Count
            ? copies.FirstOrDefault(isScanKeeper)
            : chosen.FirstOrDefault(copy => !usedByOthers(copy));
    }

    private static int DistinctValues(IEnumerable<VocabularyWord> copies, Func<VocabularyWord, string> value) =>
        copies.Select(value).Where(text => text.Length > 0).Distinct(StringComparer.Ordinal).Count();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();
}
