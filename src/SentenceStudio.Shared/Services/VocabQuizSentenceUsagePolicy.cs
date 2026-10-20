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
        var targetTerm = NormalizeTargetWordValue(word.TargetLanguageTerm);
        if (grade?.VocabularyAnalysis?.Any() != true || targetTerm.Length == 0)
            return null;

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

        return null;
    }

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
