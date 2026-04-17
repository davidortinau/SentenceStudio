using System.Globalization;

namespace SentenceStudio.Shared.Services;

/// <summary>
/// Helpers for normalizing and validating AI-generated cloze exercise data, and for
/// grading user answers with sensible leniency (e.g. Korean particles).
/// </summary>
public static class ClozureAnswerHelper
{
    // Common Korean particles that attach directly to nouns. Listed longest-first
    // so stripping matches the greediest suffix (e.g. "에서" before "에").
    private static readonly string[] KoreanParticles =
    {
        "에서", "에게", "으로", "이라", "라고", "께서",
        "을", "를", "이", "가", "은", "는", "의", "도", "만",
        "와", "과", "로", "에", "야", "아"
    };

    /// <summary>
    /// Normalizes a Korean cloze answer for equality checks by trimming whitespace
    /// and stripping a single trailing particle. Non-Korean strings are returned
    /// lower-cased and trimmed.
    /// </summary>
    public static string NormalizeForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();

        // Only apply particle-stripping when the string looks Korean.
        if (!ContainsHangul(trimmed))
            return trimmed.ToLowerInvariant();

        foreach (var p in KoreanParticles)
        {
            if (trimmed.Length > p.Length && trimmed.EndsWith(p, StringComparison.Ordinal))
                return trimmed.Substring(0, trimmed.Length - p.Length);
        }

        return trimmed;
    }

    /// <summary>
    /// Lenient equality: two answers are considered equal if they match exactly
    /// (case-insensitive, trimmed) OR their particle-stripped forms match.
    /// </summary>
    public static bool AreAnswersEquivalent(string? userInput, string? expected)
    {
        if (userInput == null || expected == null) return false;
        var u = userInput.Trim();
        var e = expected.Trim();
        if (string.Equals(u, e, StringComparison.OrdinalIgnoreCase)) return true;

        var uNorm = NormalizeForComparison(u);
        var eNorm = NormalizeForComparison(e);
        if (string.IsNullOrEmpty(uNorm) || string.IsNullOrEmpty(eNorm)) return false;
        return string.Equals(uNorm, eNorm, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Attempts to repair a <c>VocabularyWordAsUsed</c> value that isn't a verbatim
    /// substring of <paramref name="sentenceText"/>. Tries, in order:
    ///   1. stripping particles off the supplied as-used form
    ///   2. locating the dictionary form in the sentence and extending the match
    ///      to include any immediately attached Korean particle
    /// Returns null if no repair is possible.
    /// </summary>
    public static string? TryRepairWordAsUsed(string sentenceText, string wordAsUsed, string? dictionaryForm)
    {
        if (string.IsNullOrWhiteSpace(sentenceText)) return null;

        // 1. If as-used has a particle but only the noun appears in sentence, drop particle.
        if (!string.IsNullOrEmpty(wordAsUsed))
        {
            var stripped = NormalizeForComparison(wordAsUsed);
            if (!string.IsNullOrEmpty(stripped) && stripped != wordAsUsed
                && sentenceText.Contains(stripped, StringComparison.Ordinal))
            {
                return ExtendWithAttachedParticle(sentenceText, stripped);
            }
        }

        // 2. Fall back to dictionary form.
        if (!string.IsNullOrWhiteSpace(dictionaryForm)
            && sentenceText.Contains(dictionaryForm, StringComparison.Ordinal))
        {
            return ExtendWithAttachedParticle(sentenceText, dictionaryForm);
        }

        return null;
    }

    /// <summary>
    /// Ensures the guesses list contains the correct answer and has exactly 5 unique,
    /// non-empty entries (padding with the answer if needed). Sets
    /// <paramref name="wasFixed"/> to true if any repair was performed.
    /// </summary>
    public static List<string> EnsureGuessesIncludeAnswer(List<string>? guesses, string correctAnswer, out bool wasFixed)
    {
        wasFixed = false;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (guesses != null)
        {
            foreach (var g in guesses)
            {
                if (string.IsNullOrWhiteSpace(g)) continue;
                var t = g.Trim();
                if (seen.Add(t)) result.Add(t);
            }
        }

        if (string.IsNullOrWhiteSpace(correctAnswer))
            return result;

        // Use lenient equivalence to see if the correct answer is already represented.
        var alreadyPresent = result.Any(g => AreAnswersEquivalent(g, correctAnswer));
        if (!alreadyPresent)
        {
            wasFixed = true;
            if (result.Count >= 5)
            {
                // Replace the last distractor with the correct answer to keep the list at 5.
                result[result.Count - 1] = correctAnswer.Trim();
            }
            else
            {
                result.Insert(0, correctAnswer.Trim());
            }
        }
        else if (!result.Any(g => string.Equals(g, correctAnswer.Trim(), StringComparison.Ordinal)))
        {
            // Present in normalized form but not exact — swap the equivalent entry with the exact answer.
            var idx = result.FindIndex(g => AreAnswersEquivalent(g, correctAnswer));
            if (idx >= 0)
            {
                wasFixed = true;
                result[idx] = correctAnswer.Trim();
            }
        }

        if (result.Count != 5) wasFixed = true;
        return result;
    }

    private static string ExtendWithAttachedParticle(string sentenceText, string noun)
    {
        // Find an occurrence of noun that is a word-boundary start (preceding char is not Hangul),
        // so we don't grab a particle off a longer word that happens to contain the noun as a substring.
        var searchFrom = 0;
        int idx;
        while ((idx = sentenceText.IndexOf(noun, searchFrom, StringComparison.Ordinal)) >= 0)
        {
            if (idx == 0 || !IsHangulSyllable(sentenceText[idx - 1]))
                break;
            searchFrom = idx + 1;
        }
        if (idx < 0) return noun;
        var after = idx + noun.Length;
        if (after >= sentenceText.Length) return noun;

        // If the next character is a Hangul syllable matching one of the particle suffixes,
        // extend the span to cover it so the blank swallows the particle too.
        foreach (var p in KoreanParticles)
        {
            if (after + p.Length <= sentenceText.Length
                && string.CompareOrdinal(sentenceText, after, p, 0, p.Length) == 0)
            {
                // Boundary check: next char after particle should be space/punctuation/end
                var boundary = after + p.Length;
                if (boundary == sentenceText.Length || !IsHangulSyllable(sentenceText[boundary]))
                    return noun + p;
            }
        }

        return noun;
    }

    private static bool ContainsHangul(string s)
    {
        foreach (var c in s)
            if (IsHangulSyllable(c)) return true;
        return false;
    }

    private static bool IsHangulSyllable(char c)
    {
        // Hangul Syllables block
        return c >= 0xAC00 && c <= 0xD7A3;
    }
}
