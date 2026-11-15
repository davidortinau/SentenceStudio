using System.Text;
using System.Text.RegularExpressions;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Shared.Services;

/// <summary>The source quiz's sentence splitting and target-word matching rules.</summary>
public static class VocabQuizSentenceUsagePolicy
{
    public static IReadOnlyList<string> SplitEntries(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];
        return Regex.Split(input.Trim(), @"(?:\r?\n)+|(?<=[.!?。！？])")
            .Select(part => part.Trim())
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static VocabularyAnalysis? GetTargetWordAnalysis(
        GradeResponse? grade, VocabularyWord word)
    {
        var candidates = TargetTermCandidates(word.TargetLanguageTerm);
        if (grade?.VocabularyAnalysis?.Any() != true || candidates.Count == 0)
            return null;

        // Candidates in order: the term without its parentheticals first, across every analysis
        // item, then the forms written inside the parentheses (#221). So "(을/를) 좋아하다" finds
        // the 좋아하다 item before an item for the particle.
        foreach (var targetTerm in candidates)
        {
            foreach (var item in grade.VocabularyAnalysis)
            {
                if (NormalizeTargetWordValue(item.DictionaryForm) == targetTerm
                    || NormalizeTargetWordValue(item.UsedForm) == targetTerm)
                    return item;

                var usedFormTokens = Regex
                    .Split(item.UsedForm ?? string.Empty, @"[\s\p{P}\p{S}]+")
                    .Select(NormalizeTargetWordValue)
                    .Where(token => !string.IsNullOrWhiteSpace(token));

                if (usedFormTokens.Any(token => token == targetTerm))
                    return item;
            }
        }

        return null;
    }

    /// <summary>
    /// The normalized forms a sentence may use for <paramref name="term"/>. A term often carries
    /// a parenthetical, such as "맛있다 (맛있어요)" or "(을/를) 좋아하다"; normalizing it whole
    /// gives "맛있다 맛있어요", which no analysis ever matches (#221). The first candidate is the
    /// term without its parentheticals. Forms inside the parentheses follow, split on "/" and
    /// ",", but only those in the same script as the term and at least two letters long, so a
    /// native-language hint ("배 (pear)") or a lone particle never counts as the target word.
    /// </summary>
    internal static IReadOnlyList<string> TargetTermCandidates(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return [];

        var text = term.Normalize(NormalizationForm.FormKC);
        var inner = Parenthetical.Matches(text).Select(match => match.Groups[1].Value).ToList();
        var main = NormalizeTargetWordValue(Parenthetical.Replace(text, " "));

        var candidates = new List<string>();
        if (main.Length > 0)
            candidates.Add(main);

        foreach (var part in inner.SelectMany(group => group.Split('/', ',', '、')))
        {
            var form = NormalizeTargetWordValue(part);
            if (form.Count(char.IsLetter) < 2 || candidates.Contains(form))
                continue;
            if (main.Length > 0 && (form.Any(IsHangul) != main.Any(IsHangul)
                || form.Any(IsLatin) != main.Any(IsLatin)))
                continue;
            candidates.Add(form);
        }

        return candidates;
    }

    private static readonly Regex Parenthetical = new(@"[(\uFF08]([^()\uFF08\uFF09]*)[)\uFF09]");

    private static bool IsLatin(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    public static bool HasVerifiedTargetUse(
        string sentence, VocabularyWord word, string? targetLanguage, string? usedForm)
    {
        ArgumentNullException.ThrowIfNull(word);
        var targetTerm = word.TargetLanguageTerm;
        if (string.IsNullOrWhiteSpace(sentence)
            || string.IsNullOrWhiteSpace(targetTerm)
            || string.IsNullOrWhiteSpace(usedForm))
            return false;

        var text = sentence.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var form = usedForm.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        if (!form.Any(char.IsLetter))
            return false;

        var korean = string.Equals(targetLanguage, "Korean", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetLanguage, "ko", StringComparison.OrdinalIgnoreCase)
            || targetLanguage?.StartsWith("ko-", StringComparison.OrdinalIgnoreCase) == true;
        if (korean && (!text.Any(IsHangul)
            || targetTerm.Any(IsHangul) && !form.Any(IsHangul)))
            return false;

        for (var start = 0; (start = text.IndexOf(form, start, StringComparison.Ordinal)) >= 0;
            start += form.Length)
        {
            var end = start + form.Length;
            var beginsToken = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
            var endsToken = end == text.Length || !char.IsLetterOrDigit(text[end]);
            var koreanSuffix = false;
            if (korean && form.Any(IsHangul)
                && end < text.Length && IsHangul(text[end]))
            {
                var tokenEnd = end;
                while (tokenEnd < text.Length && char.IsLetterOrDigit(text[tokenEnd]))
                    tokenEnd++;
                koreanSuffix = ClozureAnswerHelper.AreAnswersEquivalent(
                    text[start..tokenEnd], form);
            }
            if (beginsToken && (endsToken || koreanSuffix))
                return true;
        }

        return false;
    }

    private static bool IsHangul(char value) =>
        value is >= '\uAC00' and <= '\uD7A3'
            or >= '\u1100' and <= '\u11FF'
            or >= '\u3130' and <= '\u318F';

    private static string NormalizeTargetWordValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim().Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString().Trim();
    }
}
