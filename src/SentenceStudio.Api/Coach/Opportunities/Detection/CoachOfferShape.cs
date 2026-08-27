using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Opportunities.Detection;

/// <summary>
/// Grades whether the coach's own last message was something a "yes" could have been answering.
/// </summary>
/// <remarks>
/// <para>
/// <b>This predicate runs over the server's own prior output, never over learner input.</b> That
/// distinction is what keeps the referent-loss trigger from being a prompt heuristic: the five
/// authoritative conjuncts in <see cref="CoachUnboundAnswerDetector"/> decide <em>whether</em>
/// anything is recorded, and this only grades <em>what it was answering</em> into a single enum
/// value.
/// </para>
/// <para>
/// The structural case is preferred and needs no text at all: a message whose stored
/// <see cref="CoachMessageKind"/> is <see cref="CoachMessageKind.Clarification"/> is a question by
/// construction. The textual case exists because the screenshot's offer was prose in an ordinary
/// <see cref="CoachMessageKind.Text"/> message — Sam offered "shall I change it to 45 minutes?"
/// without any structured suggestion behind it, which is precisely why the learner's "yes" had
/// nothing to bind to.
/// </para>
/// </remarks>
public static class CoachOfferShape
{
    /// <summary>
    /// How much of the tail of a coach message is examined for a closing question.
    /// </summary>
    /// <remarks>
    /// An offer is made at the end of a message, not in the middle of one. Bounding the scan to
    /// the tail is what stops a long explanation that happens to contain the word "how" from
    /// grading as an offer.
    /// </remarks>
    public const int TailScanLength = 240;

    /// <summary>
    /// True when a coach-authored message reads as a question the learner could answer.
    /// </summary>
    /// <param name="text">The coach's own prior message text, already decrypted.</param>
    public static bool EndsWithQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimEnd();
        var tail = trimmed.Length <= TailScanLength
            ? trimmed
            : trimmed[^TailScanLength..];

        // A literal question mark at the very end is the unambiguous case, and the one the
        // screenshot exchange actually produced.
        var last = trimmed[^1];
        if (last is '?' or '\uFF1F')
        {
            return true;
        }

        // A question mark anywhere in the tail still means the message asked something, even
        // when a closing courtesy line follows it.
        if (CoachQuestionMarkers.HasQuestionOrQuotationMark(tail) && tail.Contains('?'))
        {
            return true;
        }

        // A question with no question mark is still a question. The vocabulary is shared with
        // the acceptance classifier and the write authority so the three cannot drift.
        return CoachQuestionMarkers.ContainsQuestionWord(Normalize(tail));
    }

    /// <summary>
    /// Grades the conversation's most recent coach message into an offer link.
    /// </summary>
    /// <param name="kind">The stored message kind — structural, and preferred.</param>
    /// <param name="text">The message text, consulted only when the kind is not decisive.</param>
    /// <returns>
    /// <see cref="CoachOpportunityOfferLink.None"/> when nothing preceded the learner's answer
    /// that it could have been answering. Callers treat that as "record nothing": an
    /// out-of-the-blue "yes" is noise, not an opportunity.
    /// </returns>
    public static CoachOpportunityOfferLink Grade(CoachMessageKind kind, string? text) => kind switch
    {
        // Structural. No text is inspected at all.
        CoachMessageKind.Clarification => CoachOpportunityOfferLink.PriorClarification,

        // A structured suggestion is its own link, and its presence also means the acceptance
        // shortcut would have handled the answer — so this value is recorded for completeness
        // and the detector's open-suggestion conjunct is what actually keeps it out of the
        // ledger.
        CoachMessageKind.Suggestion => CoachOpportunityOfferLink.OpenPlanSuggestion,

        CoachMessageKind.Text or CoachMessageKind.PedagogicalAnswer =>
            EndsWithQuestion(text)
                ? CoachOpportunityOfferLink.PriorCoachQuestion
                : CoachOpportunityOfferLink.None,

        // A receipt or a notice is a statement, not an offer.
        _ => CoachOpportunityOfferLink.None
    };

    /// <summary>
    /// Lower-cases and strips punctuation so the shared question vocabulary matches whole tokens.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>CoachExplicitAcceptanceClassifier.Normalize</c>: Korean characters pass through
    /// unchanged and only separators are removed, because Korean does not separate words with
    /// spaces.
    /// </remarks>
    private static string Normalize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var lastWasSpace = true;

        foreach (var rune in value.Normalize(System.Text.NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(rune))
            {
                builder.Append(char.ToLowerInvariant(rune));
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
}
