using SentenceStudio.Shared.Models;

namespace SentenceStudio.Shared.Services;

/// <summary>
/// Result of turning an example sentence into a fill-in-the-blank (cloze) item.
/// </summary>
/// <param name="BlankedText">The target sentence with the answer replaced by <see cref="SentenceClozeBuilder.Blank"/>.</param>
/// <param name="Answer">The surface form the learner must supply (dictionary form, particles left in place).</param>
/// <param name="Translation">Native-language translation, if the sentence had one.</param>
/// <param name="Options">Word-bank options (answer + distractors) when distractors were supplied; otherwise null for open typing.</param>
public sealed record SentenceCloze(string BlankedText, string Answer, string? Translation, IReadOnlyList<string>? Options);

/// <summary>
/// Builds open-typing or word-bank cloze items from curated example sentences — no AI call.
/// Blanks only the target word (dictionary form), leaving Korean particles attached in place,
/// per SLA guidance. Only quiz-eligible (curated/verified, non-flagged) sentences are used.
/// </summary>
public static class SentenceClozeBuilder
{
    public const string Blank = "____";

    /// <summary>
    /// Attempt to build a cloze item. Returns null when the sentence is not quiz-eligible,
    /// is empty, or the target word cannot be located within it.
    /// </summary>
    public static SentenceCloze? TryBuild(ExampleSentence sentence, VocabularyWord word, IReadOnlyList<string>? distractors = null)
    {
        if (sentence is null || word is null)
            return null;
        if (!sentence.IsQuizEligible)
            return null;

        var text = sentence.TargetSentence?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var answer = FindSurface(text, word);
        if (answer is null)
            return null;

        var blanked = ReplaceFirst(text, answer, Blank);

        IReadOnlyList<string>? options = null;
        if (distractors is { Count: > 0 })
        {
            var set = new List<string> { answer };
            foreach (var d in distractors)
            {
                var t = d?.Trim();
                if (!string.IsNullOrEmpty(t) && !set.Contains(t))
                    set.Add(t);
            }
            options = set;
        }

        var translation = string.IsNullOrWhiteSpace(sentence.NativeSentence) ? null : sentence.NativeSentence.Trim();
        return new SentenceCloze(blanked, answer, translation, options);
    }

    /// <summary>
    /// Locate the form of the word to blank. Prefers the dictionary target term, then the lemma
    /// (both matched as substrings so attached particles stay in the sentence). Falls back to the
    /// whole space-delimited token (eojeol) that contains either form.
    /// </summary>
    private static string? FindSurface(string text, VocabularyWord word)
    {
        foreach (var candidate in new[] { word.TargetLanguageTerm, word.Lemma })
        {
            var c = candidate?.Trim();
            if (!string.IsNullOrEmpty(c) && text.Contains(c, StringComparison.Ordinal))
                return c;
        }

        // Fall back: a token that contains the dictionary form somewhere inside it.
        foreach (var candidate in new[] { word.TargetLanguageTerm, word.Lemma })
        {
            var c = candidate?.Trim();
            if (string.IsNullOrEmpty(c))
                continue;
            foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Contains(c, StringComparison.Ordinal))
                    return token;
            }
        }

        return null;
    }

    private static string ReplaceFirst(string text, string search, string replacement)
    {
        var idx = text.IndexOf(search, StringComparison.Ordinal);
        if (idx < 0)
            return text;
        return text[..idx] + replacement + text[(idx + search.Length)..];
    }
}
