using System.Text;
using System.Text.RegularExpressions;
using SentenceStudio.Services;

namespace SentenceStudio.Evaluation.Tests.Import;

/// <summary>
/// Lemma normalization used for matching. Deliberately simple and predictable:
/// Unicode NFC (composes any decomposed Hangul jamo), drop parenthetical notes such as "먹다 (to eat)",
/// drop quotes and trailing punctuation, drop grammar-pattern markers ("-", "~") and all whitespace
/// (so 다음 달 and 다음달 match), lower-case any Latin text. It does NOT stem or conjugate: whether a
/// conjugated form is acceptable is decided by the hand-curated fixture, not by heuristics here.
/// </summary>
public static partial class KoreanLemma
{
    public static string Normalize(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return string.Empty;
        }

        var value = term.Normalize(NormalizationForm.FormC);
        value = Parenthetical().Replace(value, string.Empty);
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || ch is '\'' or '"' or '‘' or '’' or '“' or '”' or '「' or '」' or '『' or '』'
                or '.' or ',' or '!' or '?' or '…' or '·' or '~' or '-')
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    public static bool ContainsLatin(string? term) =>
        !string.IsNullOrEmpty(term) && LatinLetter().IsMatch(Parenthetical().Replace(term, string.Empty));

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Parenthetical();

    [GeneratedRegex("[A-Za-z]")]
    private static partial Regex LatinLetter();
}

public sealed record ScoredRow(string Term, string Gloss, bool Selected, string Verdict);

public sealed record VocabularyCoverageScore(
    int ExpectedCount,
    IReadOnlyList<ExpectedVocabulary> Found,
    IReadOnlyList<ExpectedVocabulary> Missed,
    int SelectedCount,
    int AcceptableSelectedCount,
    IReadOnlyList<string> UnexpectedSelected,
    IReadOnlyList<string> ForbiddenSelected,
    IReadOnlyList<string> ForbiddenUnchecked,
    IReadOnlyList<ScoredRow> Rows)
{
    public double Recall => ExpectedCount == 0 ? 0 : (double)Found.Count / ExpectedCount;

    public double Precision => SelectedCount == 0 ? 0 : (double)AcceptableSelectedCount / SelectedCount;
}

/// <summary>
/// Deterministic scoring of the app's import preview rows against a fixture.
/// <list type="bullet">
/// <item>Scored rows: the rows the preview pre-selects (IsSelected), which are what a learner imports by
/// pressing Import without editing. The app auto-deselects low-confidence rows; those are reported but not scored.</item>
/// <item>Recall: expected lemmas matched (lemma or any curated alternate) by at least one scored row / expected lemmas.</item>
/// <item>Precision: scored rows whose term is an expected lemma, an alternate, or an allowed extra / scored rows.</item>
/// <item>Forbidden: scored rows that are a curated forbidden term or contain Latin letters (not Korean).</item>
/// </list>
/// </summary>
public static class VocabularyCoverage
{
    public static VocabularyCoverageScore Score(ImportVocabularyFixture fixture, IReadOnlyList<ImportRow> rows)
    {
        var expectedForms = fixture.Expected
            .Select(e => (Item: e, Forms: e.AcceptedForms.Select(KoreanLemma.Normalize).ToHashSet(StringComparer.Ordinal)))
            .ToList();
        var acceptable = new HashSet<string>(
            expectedForms.SelectMany(e => e.Forms).Concat(fixture.Allowed.Select(KoreanLemma.Normalize)),
            StringComparer.Ordinal);
        var forbidden = fixture.Forbidden.ToDictionary(f => KoreanLemma.Normalize(f.Term), f => f, StringComparer.Ordinal);

        var termRows = rows.Where(r => !string.IsNullOrWhiteSpace(r.TargetLanguageTerm)).ToList();
        var selected = termRows.Where(r => r.IsSelected).ToList();
        var selectedNormalized = selected.Select(r => KoreanLemma.Normalize(r.TargetLanguageTerm)).ToHashSet(StringComparer.Ordinal);

        var found = new List<ExpectedVocabulary>();
        var missed = new List<ExpectedVocabulary>();
        foreach (var (item, forms) in expectedForms)
        {
            (forms.Overlaps(selectedNormalized) ? found : missed).Add(item);
        }

        var unexpected = new List<string>();
        var forbiddenSelected = new List<string>();
        var forbiddenUnchecked = new List<string>();
        var scoredRows = new List<ScoredRow>();
        var acceptableCount = 0;

        foreach (var row in termRows)
        {
            var term = row.TargetLanguageTerm!.Trim();
            var normalized = KoreanLemma.Normalize(term);
            string verdict;
            if (forbidden.TryGetValue(normalized, out var hit))
            {
                verdict = $"forbidden: {hit.Why}";
            }
            else if (KoreanLemma.ContainsLatin(term))
            {
                verdict = "forbidden: not a Korean term";
            }
            else if (acceptable.Contains(normalized))
            {
                verdict = expectedForms.Any(e => e.Forms.Contains(normalized)) ? "expected" : "allowed";
            }
            else
            {
                verdict = "unexpected";
            }

            if (row.IsSelected)
            {
                if (verdict is "expected" or "allowed")
                {
                    acceptableCount++;
                }
                else if (verdict.StartsWith("forbidden", StringComparison.Ordinal))
                {
                    forbiddenSelected.Add($"{term} ({verdict["forbidden: ".Length..]})");
                }
                else
                {
                    unexpected.Add(term);
                }
            }
            else if (verdict.StartsWith("forbidden", StringComparison.Ordinal))
            {
                forbiddenUnchecked.Add(term);
            }

            scoredRows.Add(new ScoredRow(term, row.NativeLanguageTerm ?? string.Empty, row.IsSelected, verdict));
        }

        return new VocabularyCoverageScore(
            ExpectedCount: fixture.Expected.Count,
            Found: found,
            Missed: missed,
            SelectedCount: selected.Count,
            AcceptableSelectedCount: acceptableCount,
            UnexpectedSelected: unexpected,
            ForbiddenSelected: forbiddenSelected,
            ForbiddenUnchecked: forbiddenUnchecked,
            Rows: scoredRows);
    }
}
