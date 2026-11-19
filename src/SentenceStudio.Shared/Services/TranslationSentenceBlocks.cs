using System.Globalization;

namespace SentenceStudio.Shared.Services;

/// <summary>
/// The word blocks Translation offers for one sentence: the words of the target sentence
/// itself, in a shuffled order, so tapping them in the right order writes the sentence.
/// </summary>
public static class TranslationSentenceBlocks
{
    private const string LeadingMarks = "\"'¿¡";
    private const string TrailingMarks = "\"'.,!?;:…~。、！？，；：～";

    /// <param name="targetSentence">The target-language sentence the learner is asked to produce.</param>
    /// <param name="sentenceTerms">
    /// The components the AI listed for the sentence. Used only for a sentence written without
    /// spaces (it would be a single block: the whole answer), and only when the components,
    /// in their order, are that sentence.
    /// </param>
    /// <param name="random">The shuffle's source; tests pass a seeded one.</param>
    public static List<string> Build(
        string? targetSentence, IEnumerable<string?>? sentenceTerms = null, Random? random = null)
    {
        var blocks = SentenceWords(targetSentence);
        if (blocks.Count == 1)
        {
            var terms = (sentenceTerms ?? [])
                .Select(term => term?.Trim() ?? string.Empty)
                .Where(term => term.Length > 0)
                .ToList();
            if (terms.Count > 1 && Letters(string.Concat(terms)) == Letters(blocks[0]))
                blocks = terms;
        }

        // A plain shuffle: the order shown says nothing about the sentence's. (Ruling out the
        // sentence's own order would make a two-word sentence always appear reversed.)
        var shuffled = blocks.ToArray();
        (random ?? Random.Shared).Shuffle(shuffled);
        return [.. shuffled];
    }

    /// <summary>
    /// The sentence split at whitespace, without the sentence punctuation and quotation marks
    /// around each word (a block that kept the full stop would mark itself as the last one).
    /// Marks that belong to the word stay ("50%", "-5도"). A repeated word stays repeated.
    /// </summary>
    public static List<string> SentenceWords(string? sentence) =>
        (sentence ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(TrimMarks)
            .Where(word => word.Length > 0)
            .ToList();

    private static string TrimMarks(string word)
    {
        var start = 0;
        var end = word.Length;
        while (start < end && (LeadingMarks.Contains(word[start]) || IsQuote(word[start])))
            start++;
        while (end > start && (TrailingMarks.Contains(word[end - 1]) || IsQuote(word[end - 1])))
            end--;
        return word[start..end];
    }

    private static bool IsQuote(char mark) => char.GetUnicodeCategory(mark)
        is UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation;

    /// <summary>The text as compared between a sentence and its components: letters and digits only.</summary>
    private static string Letters(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit)).ToUpperInvariant();
}
