namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// The words that mean a learner is asking <b>about</b> the language rather than instructing the
/// planner or answering an offer.
/// </summary>
/// <remarks>
/// <para>
/// One list, two readers. <see cref="CoachWriteAuthority"/> uses it to refuse a typed plan write,
/// and <see cref="CoachExplicitAcceptanceClassifier"/> uses it to refuse a typed acceptance. They
/// are separate decisions with separate rules, so they share the vocabulary rather than the
/// logic — a coupling that keeps the two from drifting without pretending they are the same
/// check.
/// </para>
/// <para>
/// Punctuation is a weak signal on its own: "좋아요 뜻이 뭐예요" and "does 좋아요 mean good" are
/// questions with no question mark. These words are what catch them.
/// </para>
/// </remarks>
public static class CoachQuestionMarkers
{
    /// <summary>Words that make a message a question about language, punctuated or not.</summary>
    public static IReadOnlyList<string> Words { get; } =
    [
        // English
        "what", "whats", "why", "how", "when", "which", "who", "whose", "where",
        "difference", "differ", "mean", "means", "meaning", "explain", "translate",
        "translation", "pronounce", "pronunciation", "grammar", "conjugate", "conjugation",
        "usage", "correct", "say", "says", "said", "versus", "vs", "between", "define",
        "definition", "example", "spell", "spelling", "sound", "sounds",

        // Korean
        "뭐", "뭐예요", "뭔가", "무엇", "무슨", "어떻게", "어떤", "왜", "언제", "어느", "누구",
        "어디", "차이", "뜻", "의미", "설명", "번역", "발음", "문법", "맞아", "맞나", "맞나요",
        "알려", "알려줘", "가르쳐", "예문", "쓰나", "써요"
    ];

    /// <summary>
    /// True when any question word appears in an already-normalized message.
    /// </summary>
    /// <param name="normalized">
    /// Lower-cased, letters and digits only, single-spaced. Latin markers match whole tokens so
    /// "say" does not fire inside "essay"; Korean markers match as substrings, because Korean
    /// does not separate words with spaces and attaches particles directly to the stem.
    /// </param>
    public static bool ContainsQuestionWord(string normalized)
    {
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var marker in Words)
        {
            if (!char.IsAscii(marker[0]))
            {
                if (normalized.Contains(marker, StringComparison.Ordinal))
                {
                    return true;
                }

                continue;
            }

            foreach (var token in tokens)
            {
                if (string.Equals(token, marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when raw text carries a question mark or a quotation mark.
    /// </summary>
    /// <remarks>
    /// Checked before normalization, which removes exactly the punctuation that separates
    /// "좋아요" from "좋아요?" and yes from "yes".
    /// </remarks>
    public static bool HasQuestionOrQuotationMark(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is '?' or '\uFF1F'
                or '"' or '\'' or '`'
                or '\u201C' or '\u201D' or '\u2018' or '\u2019'
                or '\u300C' or '\u300D' or '\u300E' or '\u300F')
            {
                return true;
            }
        }

        return false;
    }
}
