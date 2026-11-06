using System.Globalization;
using System.Text;

namespace SentenceStudio.Services;

/// <summary>
/// Which Import Content rows keep the sentence their term came from as the word's example (B72).
/// </summary>
/// <remarks>
/// <para>
/// A kept sentence becomes a Curated example, and Curated examples are the Vocabulary Quiz's
/// sentence hints, shown only with a target-language prompt (TargetToNative), where the learner
/// has to recall the meaning. A "sentence" that carries the meaning, such as the line
/// <c>사과 - apple</c> from a word list, would show the answer next to the question, so the AI's
/// sentence is kept only when it is a real target-language sentence from the pasted text:
/// </para>
/// <list type="bullet">
/// <item>it appears in the pasted text (ignoring spacing, punctuation and case), so it is not invented;</item>
/// <item>it is more than the term itself (a Phrase row that is the whole line restates itself);</item>
/// <item>no word of the term's translation (the row's, or the existing word's) is one of its words;</item>
/// <item>it does not contain its own translation, and at most half its words appear in that
/// translation (a native-language or mixed line shares most of its words with it).</item>
/// </list>
/// <para>
/// The word itself must also appear in the sentence (checked by
/// <see cref="SentenceStudio.Data.ExampleSentenceRepository"/>), except on a row whose term the
/// learner edited in a reviewed preview (<see cref="TermEdited"/>).
/// </para>
/// </remarks>
public static class ImportExampleSentencePolicy
{
    /// <summary>True when <paramref name="sentence"/> may be kept as the example of an imported term.</summary>
    /// <param name="terms">The row's term, its parsed term and the saved word's term and lemma.</param>
    /// <param name="nativeTerms">The row's translation and the saved word's translation.</param>
    public static bool KeepsSentence(
        string? sentence,
        string? translation,
        string? sourceText,
        IEnumerable<string?> terms,
        IEnumerable<string?> nativeTerms)
    {
        var compactSentence = Compact(sentence);
        var compactSource = Compact(sourceText);
        if (compactSentence.Length == 0 || compactSource.Length == 0
            || !compactSource.Contains(compactSentence, StringComparison.Ordinal))
            return false;

        if (terms.Any(term => Compact(term) is { Length: > 0 } compactTerm && compactTerm == compactSentence))
            return false;

        var sentenceWords = Words(sentence);
        foreach (var nativeTerm in nativeTerms)
        {
            var nativeWords = Words(nativeTerm);
            // A one-letter word of a longer translation ("a lot") says too little to reject on.
            if (nativeWords.Any(word => (word.Length > 1 || nativeWords.Count == 1)
                    && sentenceWords.Contains(word)))
                return false;
        }

        var compactTranslation = Compact(translation);
        if (compactTranslation.Length > 0)
        {
            if (compactSentence.Contains(compactTranslation, StringComparison.Ordinal))
                return false;
            var translationWords = Words(translation);
            var shared = sentenceWords.Count(translationWords.Contains);
            if (shared * 2 > sentenceWords.Count)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The learner changed the row's term in the preview: its sentence no longer has to contain the
    /// term, because it is the sentence the term came from. A row with no parsed term (a client that
    /// does not carry it) counts as not edited.
    /// </summary>
    public static bool TermEdited(ImportRow row) =>
        row.ParsedTargetLanguageTerm is { } parsed
        && !string.Equals(
            ContentImportService.NormalizeTargetTerm(parsed),
            ContentImportService.NormalizeTargetTerm(row.TargetLanguageTerm),
            StringComparison.Ordinal);

    /// <summary>Letters and digits only, lower case, composed (NFC): spacing and punctuation do not count.</summary>
    private static string Compact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    /// <summary>
    /// The words of a text: runs of letters and digits, lower case, also split where Latin letters
    /// meet another script (so <c>사과apple</c> is two words and <c>TV를</c> is <c>tv</c> and <c>를</c>).
    /// </summary>
    private static HashSet<string> Words(string? text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
            return words;
        var current = new StringBuilder();
        bool? currentIsLatin = null;
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }
            var isLatin = c <= 'ɏ';
            if (currentIsLatin is { } latin && latin != isLatin)
                Flush();
            currentIsLatin = isLatin;
            current.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        }
        Flush();
        return words;

        void Flush()
        {
            if (current.Length > 0)
                words.Add(current.ToString());
            current.Clear();
            currentIsLatin = null;
        }
    }
}
