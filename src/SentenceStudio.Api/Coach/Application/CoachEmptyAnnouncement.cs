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
        var lastSentence = body[(body.LastIndexOfAny(SentenceEnds) + 1)..];

        foreach (var phrase in AnnouncingPhrases)
        {
            var at = lastSentence.LastIndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (at < 0 || (at > 0 && char.IsLetter(lastSentence[at - 1])))
            {
                // Not there, or the tail of another word ("where is", "there are").
                continue;
            }

            // "Here's a tip: say it with 공." holds what it announces, after the colon.
            if (lastSentence.IndexOfAny(ContentLeads, at + phrase.Length) < 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The answer's parts that hold text: the ones the shape repair keeps.</summary>
    private static List<CoachAnswerBlockIntent> PartsWithText(CoachPedagogicalAnswerIntent? answer) =>
        (answer?.Blocks ?? [])
            .Where(block => block?.Spans is { } spans
                && spans.Any(span => span is not null && !string.IsNullOrWhiteSpace(span.Text)))
            .ToList();
}
