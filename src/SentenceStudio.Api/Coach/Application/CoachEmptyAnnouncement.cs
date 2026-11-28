using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// Recognises a reply that says content is coming and holds none (issue 289): "Here are
/// three example sentences that use the word." and then nothing. The reply is a plain message,
/// or an answer whose only part is that announcement (issue 305).
/// </summary>
/// <remarks>
/// <para>
/// The standing instructions tell the model to put the content in the reply (issues 268 and 281);
/// this is the check behind them. The reply the Captain reported ended with a full stop, not a
/// colon, so both endings are read: a message that ends with a colon, and a message whose last
/// sentence announces ("here are", "the following") with nothing after the announcement.
/// </para>
/// <para>
/// It reads the model's own reply only, and decides one thing: whether the session service asks
/// the model once more. It never changes or refuses a reply, so a wrong yes costs one model call
/// and a wrong no shows the reply as before.
/// </para>
/// </remarks>
public static class CoachEmptyAnnouncement
{
    /// <summary>
    /// Phrases that say content follows. Matched in the last sentence only, case-insensitively.
    /// English and Korean, the two display languages.
    /// </summary>
    private static readonly string[] AnnouncingPhrases =
    [
        "here are", "here is", "here's", "here’s", "below are", "below is", "the following", "as follows",
        "다음은", "다음과 같", "아래는", "아래와 같"
    ];

    private static readonly char[] SentenceEnds = ['.', '!', '?', '。', '\n'];

    private static readonly char[] ContentLeads = [':', '：', '—'];

    /// <summary>Closing marks a message can end with after its last word: quotes, brackets, emphasis.</summary>
    private static readonly char[] Closers = ['"', '\'', '”', '’', ')', ']', '*', '_', '`'];

    /// <summary>
    /// True when <paramref name="intent"/> is a reply that carries nothing but its message, or
    /// nothing but one direct-answer part, and that text announces content it does not hold.
    /// </summary>
    public static bool IsOne(CoachTurnIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        // Only the kinds whose message is the whole reply. A pedagogical answer counts when its
        // parts are empty: the shape repair then shows the message as the answer.
        if (intent.Kind is not (CoachIntentKind.NoChange or CoachIntentKind.GeneralAnswer
            or CoachIntentKind.OffTopic or CoachIntentKind.PedagogicalAnswer))
        {
            return false;
        }

        // Anything else the reply carries may be what the message announces: a vocabulary set to
        // save, an activity to open, something to remember, a follow-up question.
        if (intent.VocabularySet is not null
            || intent.ActivityLaunch is not null
            || intent.MemoryProposal is not null
            || !string.IsNullOrWhiteSpace(intent.ClarifyingQuestion))
        {
            return false;
        }

        var parts = PartsWithText(intent.PedagogicalAnswer);
        if (parts.Count == 0)
        {
            return Announces(intent.CoachMessage);
        }

        // Answer parts are what a message announces, with one exception (issue 305): an answer
        // whose only part is the direct answer, and that part is the announcement. The answer
        // path shows the parts and not the message, so nothing else reaches the learner. A
        // general answer's message is put in front of its parts as a part of its own (the shape
        // repair), so with a message it has two, and two or more are left alone.
        if (parts.Count > 1
            || parts[0].Kind != CoachAnswerBlockKind.Answer
            || (intent.Kind != CoachIntentKind.PedagogicalAnswer
                && !(intent.Kind == CoachIntentKind.GeneralAnswer && string.IsNullOrWhiteSpace(intent.CoachMessage))))
        {
            return false;
        }

        var pieces = parts[0].Spans
            .Where(span => span is not null && !string.IsNullOrWhiteSpace(span.Text))
            .ToList();

        // A part that ends in the language being studied ends with content: "Here is your coffee."
        // is the sentence the learner asked for, not an announcement of one.
        if (pieces[^1].Language == CoachLanguageRole.Target)
        {
            return false;
        }

        // Joined as the answer's plain text joins them.
        return Announces(string.Join(' ', pieces.Select(span => span.Text.Trim())));
    }

    /// <summary>
    /// True when <paramref name="intent"/> is a reply of the same kinds whose message is blank and
    /// which carries nothing else at all: the learner is shown an empty reply. Read for the second
    /// reply of a turn asked for twice, so that the ledger does not count a blank reply as
    /// content (issue 306). It announces nothing, so <see cref="IsOne"/> is false for it.
    /// </summary>
    public static bool IsBlank(CoachTurnIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        return intent.Kind is (CoachIntentKind.NoChange or CoachIntentKind.GeneralAnswer
                or CoachIntentKind.OffTopic or CoachIntentKind.PedagogicalAnswer)
            && string.IsNullOrWhiteSpace(intent.CoachMessage)
            && string.IsNullOrWhiteSpace(intent.ClarifyingQuestion)
            && intent.VocabularySet is null
            && intent.ActivityLaunch is null
            && intent.MemoryProposal is null
            && !(intent.PedagogicalAnswer?.Blocks ?? []).Any(block => block?.Spans is { } spans
                && spans.Any(span => span is not null && !string.IsNullOrWhiteSpace(span.Text)));
    }

    /// <summary>True when <paramref name="message"/> ends by announcing content and stops there.</summary>
    public static bool Announces(string? message)
    {
        var text = (message ?? string.Empty).Trim().TrimEnd(Closers).TrimEnd();
        if (text.Length == 0)
        {
            return false;
        }

        if (text[^1] is ':' or '：')
        {
            return true;
        }

        // A question is an offer or a check, not an announcement.
        if (text[^1] is '?' or '？')
        {
            return false;
        }

        var body = text.TrimEnd(SentenceEnds).TrimEnd();
        var sentenceStart = body.LastIndexOfAny(SentenceEnds) + 1;
        var lineStart = body.LastIndexOf('\n') + 1;

        foreach (var phrase in AnnouncingPhrases)
        {
            // Every place the phrase stands in the last sentence, last first: one that does not
            // count must not hide an earlier one that does.
            for (var at = body.LastIndexOf(phrase, StringComparison.OrdinalIgnoreCase);
                at >= sentenceStart;
                at = at == 0 ? -1 : body.LastIndexOf(phrase, at - 1, StringComparison.OrdinalIgnoreCase))
            {
                if (at > sentenceStart && char.IsLetter(body[at - 1]))
                {
                    // The tail of another word ("where is", "there are").
                    continue;
                }

                // In English you say "Here is your coffee." gives the sentence, it does not
                // announce one (issue 326). So does the sentence set off with emphasis and no
                // quotation marks: In English you say **Here is your coffee.** (issue 331).
                if (InsideQuotation(body, lineStart, at))
                {
                    continue;
                }

                // "Here's a tip: say it with 공." holds what it announces, after the colon.
                if (body.IndexOfAny(ContentLeads, at + phrase.Length) < 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when a quotation that opens at or after <paramref name="from"/> is still open at
    /// <paramref name="at"/>. Read from the start of the line and not of the sentence, so the
    /// second sentence of a quotation is inside it too; a quotation is not followed across lines,
    /// so a stray mark higher up cannot hide an announcement.
    /// </summary>
    /// <remarks>
    /// A run of emphasis marks (<c>**</c>, <c>*</c>, <c>__</c>, <c>_</c>, a backtick) that sets
    /// off part of a sentence is a quotation that closes with the same run (issue 331). One that
    /// opens where a sentence starts is not: a model puts a whole announcement in bold
    /// ("**Here are three example sentences.**"), and that one must still be asked for again.
    /// Nor is one that goes on in small Latin letters, which is the reply's own sentence.
    /// </remarks>
    private static bool InsideQuotation(string text, int from, int at)
    {
        // The mark that closes the open quotation, or none; an emphasis run closes with a run
        // of the same length.
        var closer = '\0';
        var closerLength = 1;

        // Emphasis that opened where a sentence starts and is still open. It hides nothing; it
        // is kept so that its closing run is not read as an opening one ("**차다**를 ...").
        var standing = '\0';
        var standingLength = 0;

        for (var i = from; i < at; i++)
        {
            var mark = text[i];

            if (mark is '*' or '_' or '`')
            {
                var start = i;
                var length = 1;
                while (start + length < text.Length && text[start + length] == mark)
                {
                    length++;
                }

                i = start + length - 1;
                var before = start > 0 ? text[start - 1] : ' ';
                var after = start + length < text.Length ? text[start + length] : ' ';

                // A run closes after text and opens in front of it ("* " is a list bullet), but
                // not from inside a word: 2*3 and kick_ball open nothing. A Korean particle
                // joins a closing run directly (**차다**를), so only Latin letters and digits
                // make a word of it. A run inside a word may still close ("**kick**s"): closing
                // early can only let an announcement be seen. A mark between a word and
                // punctuation ("the note below*.") opens nothing either, as in Markdown.
                var closes = !char.IsWhiteSpace(before);
                var opens = !char.IsWhiteSpace(after)
                    && (mark == '`' || !IsLatinLetterOrDigit(before) || !IsLatinLetterOrDigit(after))
                    && (!IsMark(after) || char.IsWhiteSpace(before) || IsMark(before));

                if (closer != '\0')
                {
                    if (mark == closer && length == closerLength && closes)
                    {
                        closer = '\0';
                    }
                }
                else if (mark == standing && length == standingLength && closes)
                {
                    standing = '\0';
                }
                else if (opens
                    && !StartsSentence(text, from, start)
                    && !(IsLatinLetter(after) && char.IsLower(after)))
                {
                    // A sentence that is given starts with a capital, or in Korean. Emphasis
                    // that goes on in small letters ("Sure, **here are three example
                    // sentences.**") is the reply's own sentence, and still an announcement.
                    closer = mark;
                    closerLength = length;
                }
                else if (opens && standing == '\0')
                {
                    standing = mark;
                    standingLength = length;
                }

                continue;
            }

            var afterWord = i > from && char.IsLetterOrDigit(text[i - 1]);
            var beforeWord = i + 1 < text.Length && char.IsLetter(text[i + 1]);

            if (closer != '\0')
            {
                // A single mark between two Latin letters is an apostrophe ("Don't"), not the
                // end. A Korean particle joins the closing mark directly ('차다'를), so only
                // Latin letters make one.
                var apostrophe = mark is '\'' or '’'
                    && i > from && IsLatinLetter(text[i - 1])
                    && i + 1 < text.Length && IsLatinLetter(text[i + 1]);
                if (mark == closer && !apostrophe)
                {
                    closer = '\0';
                }

                continue;
            }

            closer = mark switch
            {
                '"' => '"',
                '“' => '”',
                '「' => '」',
                '『' => '』',
                '‘' => '’',
                // A straight single mark opens only in front of a word and not after one
                // ("don't", "learners'").
                '\'' when beforeWord && !afterWord => '\'',
                _ => '\0'
            };
        }

        return closer != '\0';
    }

    /// <summary>
    /// True when nothing of a sentence stands between <paramref name="from"/> and
    /// <paramref name="at"/>: the line starts there, or the sentence before has ended. Closing
    /// marks after that sentence's end are stepped over ("**Sure!** *Here are ...").
    /// </summary>
    private static bool StartsSentence(string text, int from, int at)
    {
        var lead = text[from..at].TrimEnd().TrimEnd(Closers).TrimEnd();
        return lead.Length == 0 || Array.IndexOf(SentenceEnds, lead[^1]) >= 0;
    }

    private static bool IsLatinLetter(char letter) => letter < '\u0250' && char.IsLetter(letter);

    private static bool IsLatinLetterOrDigit(char letter) => letter < '\u0250' && char.IsLetterOrDigit(letter);

    private static bool IsMark(char mark) => char.IsPunctuation(mark) || char.IsSymbol(mark);

    /// <summary>The answer's parts that hold text: the ones the shape repair keeps.</summary>
    private static List<CoachAnswerBlockIntent> PartsWithText(CoachPedagogicalAnswerIntent? answer) =>
        (answer?.Blocks ?? [])
            .Where(block => block?.Spans is { } spans
                && spans.Any(span => span is not null && !string.IsNullOrWhiteSpace(span.Text)))
            .ToList();
}
