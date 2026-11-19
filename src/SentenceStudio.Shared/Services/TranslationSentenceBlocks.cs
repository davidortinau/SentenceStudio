namespace SentenceStudio.Shared.Services;

/// <summary>
/// The word blocks Translation offers for one sentence: the words of the target sentence
/// itself, in a shuffled order, so tapping them in the right order writes the sentence.
/// </summary>
public static class TranslationSentenceBlocks
{
    /// <param name="targetSentence">The target-language sentence the learner is asked to produce.</param>
    /// <param name="sentenceTerms">
    /// The components the AI listed for the sentence. Used only when the sentence is not
    /// written with spaces between its words (it would be a single block: the whole answer).
    /// </param>
    /// <param name="random">The shuffle's source; tests pass a seeded one.</param>
    public static List<string> Build(
        string? targetSentence, IEnumerable<string?>? sentenceTerms = null, Random? random = null)
    {
        var blocks = SentenceWords(targetSentence);
        if (blocks.Count < 2)
        {
            var terms = (sentenceTerms ?? [])
                .Select(term => term?.Trim() ?? string.Empty)
                .Where(term => term.Length > 0)
                .ToList();
            if (terms.Count > blocks.Count)
                blocks = terms;
        }

        var shuffled = blocks.ToArray();
        (random ?? Random.Shared).Shuffle(shuffled);
        // Never the sentence's own order: that would be the answer, read left to right.
        if (shuffled.SequenceEqual(blocks, StringComparer.Ordinal)
            && blocks.Distinct(StringComparer.Ordinal).Skip(1).Any())
            shuffled = [.. blocks.Skip(1), blocks[0]];
        return [.. shuffled];
    }

    /// <summary>
    /// The sentence split at whitespace, without the punctuation around each word (a block
    /// that kept the full stop would mark itself as the last one). A repeated word stays repeated.
    /// </summary>
    public static List<string> SentenceWords(string? sentence) =>
        (sentence ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(TrimPunctuation)
            .Where(word => word.Length > 0)
            .ToList();

    private static string TrimPunctuation(string word)
    {
        var start = 0;
        var end = word.Length;
        while (start < end && char.IsPunctuation(word[start]))
            start++;
        while (end > start && char.IsPunctuation(word[end - 1]))
            end--;
        return word[start..end];
    }
}
