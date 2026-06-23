using System.Text;
using System.Text.RegularExpressions;
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

    public static string BuildSafetySignature(VocabularyWord word) =>
        string.Join('\u001f',
            NormalizeKeyPart(word.Language),
            ((int)word.LexicalUnitType).ToString());

    public static string BuildSafetySignature(string? language, LexicalUnitType lexicalUnitType) =>
        string.Join('\u001f',
            NormalizeKeyPart(language),
            ((int)lexicalUnitType).ToString());

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();
}
