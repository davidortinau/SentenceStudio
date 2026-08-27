using System.Globalization;
using System.Text;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>How the application read the learner's answer to an open suggestion.</summary>
public enum CoachExplicitAcceptance
{
    /// <summary>The answer is not clearly yes and not clearly no. Never authorises a write.</summary>
    Ambiguous = 0,

    /// <summary>The answer is an unmistakable yes.</summary>
    Affirmative,

    /// <summary>The answer is an unmistakable no.</summary>
    Negative
}

/// <summary>
/// Decides — deterministically, without the model — whether typed text is a clear acceptance
/// or a clear rejection of the open suggestion.
/// </summary>
/// <remarks>
/// <para>
/// The model classifying a turn as <c>AcceptPendingSuggestion</c> is a <b>hint</b>, not
/// authorisation. A plan write needs this classifier to return
/// <see cref="CoachExplicitAcceptance.Affirmative"/> as well. Anything this classifier has
/// not been taught is <see cref="CoachExplicitAcceptance.Ambiguous"/> and cannot write.
/// </para>
/// <para>
/// Structured paths (the Accept and Not-now chips, which post to the tapped-acceptance
/// endpoints) bypass this entirely: a tap is already unambiguous.
/// </para>
/// <para>
/// English and Korean are covered because those are the display languages the coach ships
/// with. Adding a language means adding phrases here — not loosening the matcher.
/// </para>
/// </remarks>
public sealed class CoachExplicitAcceptanceClassifier
{
    /// <summary>
    /// Longer than this and the text is treated as a sentence with content the coach must
    /// read, not a bare yes/no. Deliberately short.
    /// </summary>
    public const int MaxDecisiveLength = 40;

    private static readonly string[] AffirmativePhrases =
    [
        // English
        "yes", "yes please", "yes do it", "yes add it", "yes add that", "yes update it",
        "yeah", "yep", "yup", "sure", "ok", "okay", "k", "affirmative", "correct",
        "do it", "please do", "please do it", "go ahead", "go for it", "add it", "add that",
        "apply it", "apply that", "update it", "update the plan", "accept", "i accept",
        "sounds good", "that works", "lets do it", "let us do it", "confirmed", "confirm",
        // Korean
        "네", "네네", "예", "응", "어", "그래", "그래요", "좋아", "좋아요", "좋습니다",
        "네 좋아요", "네 좋아", "그렇게 해줘", "그렇게 해주세요", "해줘", "해주세요",
        "추가해줘", "추가해주세요", "적용해줘", "적용해주세요", "확인", "동의", "수락"
    ];

    private static readonly string[] NegativePhrases =
    [
        // English
        "no", "no thanks", "no thank you", "nope", "nah", "not now", "not today",
        "dont", "do not", "skip", "skip it", "cancel", "later", "maybe later",
        "leave it", "keep it", "keep todays plan", "keep the plan", "reject", "decline",
        // Korean
        "아니", "아니요", "아니오", "아뇨", "안돼", "안돼요", "안 돼요", "싫어", "싫어요",
        "괜찮아요", "괜찮아", "나중에", "취소", "하지마", "하지마세요", "됐어", "됐어요", "거절"
    ];

    /// <summary>
    /// Words that mean "not decided". Their presence forces
    /// <see cref="CoachExplicitAcceptance.Ambiguous"/> even if a yes/no word is also present.
    /// </summary>
    private static readonly string[] HedgeMarkers =
    [
        "maybe", "perhaps", "possibly", "probably", "not sure", "unsure", "i guess",
        "i think", "kind of", "sort of", "whatever", "if you want", "up to you", "dunno",
        "no idea", "no clue", "either way", "dont mind", "do not mind",
        "아마", "글쎄", "잘 모르겠", "모르겠", "어쩌면", "그냥", "알아서"
    ];

    /// <summary>
    /// Words that introduce a qualification. "Yes, but not the speaking one" is a request,
    /// not an acceptance, so any contrast marker forces a clarification.
    /// </summary>
    private static readonly string[] ContrastMarkers =
    [
        "but", "however", "except", "unless", "although", "though", "instead", "rather",
        "하지만", "그런데", "근데", "다만", "대신"
    ];

    private static readonly HashSet<string> AffirmativeSet =
        new(AffirmativePhrases.Select(Normalize), StringComparer.Ordinal);

    private static readonly HashSet<string> NegativeSet =
        new(NegativePhrases.Select(Normalize), StringComparer.Ordinal);

    /// <summary>Classifies typed learner text against the open suggestion.</summary>
    public CoachExplicitAcceptance Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return CoachExplicitAcceptance.Ambiguous;
        }

        // Punctuation is read before it is normalized away. Once the coach also answers
        // language questions, "좋아요?" is a question about a word, not agreement to change a
        // plan — and "yes" inside quotation marks is material being discussed, not a decision.
        if (CoachQuestionMarkers.HasQuestionOrQuotationMark(text))
        {
            return CoachExplicitAcceptance.Ambiguous;
        }

        var normalized = Normalize(text);
        if (normalized.Length == 0 || normalized.Length > MaxDecisiveLength)
        {
            return CoachExplicitAcceptance.Ambiguous;
        }

        foreach (var hedge in HedgeMarkers)
        {
            if (normalized.Contains(Normalize(hedge), StringComparison.Ordinal))
            {
                return CoachExplicitAcceptance.Ambiguous;
            }
        }

        foreach (var contrast in ContrastMarkers)
        {
            if (ContainsToken(normalized, Normalize(contrast)))
            {
                return CoachExplicitAcceptance.Ambiguous;
            }
        }

        // A question with no question mark is still a question. "좋아요 뜻이 뭐예요" and
        // "does 좋아요 mean good" both contain a decisive word and are both asking about it.
        if (ContainsQuestionWord(normalized))
        {
            return CoachExplicitAcceptance.Ambiguous;
        }

        // Defence in depth over the exact-phrase lookup below. Today every allow-listed phrase
        // is built from these tokens, so this changes no answer; it is here so that adding a
        // phrase later cannot quietly admit a token that carries meaning of its own. A decisive
        // message is made only of decisive words.
        if (!AllTokensAreDecisive(normalized))
        {
            return CoachExplicitAcceptance.Ambiguous;
        }

        var affirmative = AffirmativeSet.Contains(normalized);
        var negative = NegativeSet.Contains(normalized);

        // "yes no", "네 아니요" and friends: two opposite signals cannot authorise anything.
        if (affirmative && negative)
        {
            return CoachExplicitAcceptance.Ambiguous;
        }

        if (affirmative)
        {
            return CoachExplicitAcceptance.Affirmative;
        }

        if (negative)
        {
            return CoachExplicitAcceptance.Negative;
        }

        // Nothing else decides. The classifier previously searched for a decisive word inside a
        // longer message, which meant any sentence containing "yes" could authorise a write.
        // A decision is now the whole message or it is not a decision.
        return CoachExplicitAcceptance.Ambiguous;
    }

    /// <summary>
    /// Lower-cases, strips punctuation and emphasis, and collapses whitespace so
    /// "Yes!!", "yes.", and "  YES  " are one token. Korean characters pass through
    /// unchanged; only separators are removed.
    /// </summary>
    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true;

        foreach (var rune in value.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(rune))
            {
                builder.Append(char.ToLower(rune, CultureInfo.InvariantCulture));
                lastWasSpace = false;
                continue;
            }

            if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Every token that appears in an allow-listed affirmative or negative phrase, plus the
    /// politeness words that may accompany one.
    /// </summary>
    /// <remarks>
    /// Derived from the phrase lists rather than written out again, so the two cannot disagree.
    /// </remarks>
    private static readonly HashSet<string> DecisiveTokens = BuildDecisiveTokens();

    private static HashSet<string> BuildDecisiveTokens()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        foreach (var phrase in AffirmativePhrases.Concat(NegativePhrases))
        {
            foreach (var token in Normalize(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                tokens.Add(token);
            }
        }

        // Politeness that carries no meaning of its own and may trail a decision.
        foreach (var token in new[] { "please", "thanks", "thank", "you", "sure", "요", "네요" })
        {
            tokens.Add(token);
        }

        return tokens;
    }

    /// <summary>
    /// Question markers, minus any word that is itself part of an allow-listed decision.
    /// </summary>
    /// <remarks>
    /// "sounds good" and "correct" are decisions; "sounds" and "correct" are also plausible
    /// question words. A word cannot be both here, and the decision wins — otherwise the phrase
    /// list would silently stop working. The words that remain ("mean", "difference", "뜻",
    /// "뭐예요") never appear in a decision.
    /// </remarks>
    private static readonly IReadOnlyList<string> QuestionOnlyMarkers = CoachQuestionMarkers.Words
        .Where(w => !DecisiveTokens.Contains(w))
        .ToList();

    /// <summary>True when a question word the decisive vocabulary does not claim is present.</summary>
    private static bool ContainsQuestionWord(string normalized)
    {
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var marker in QuestionOnlyMarkers)
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

    /// <summary>True when every token in the message is one of the decisive words.</summary>
    private static bool AllTokensAreDecisive(string normalized)
    {
        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!DecisiveTokens.Contains(token))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whole-token containment, so "b" never matches inside "but".</summary>
    private static bool ContainsToken(string normalized, string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(word, token, StringComparison.Ordinal))
            {
                return true;
            }

            // Korean markers attach to the clause without a space ("근데요"). Latin markers
            // keep whole-word matching so "but" never fires inside "button".
            if (!char.IsAscii(token[0]) && word.StartsWith(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
