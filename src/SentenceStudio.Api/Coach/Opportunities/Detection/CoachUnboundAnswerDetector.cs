using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Coach.Opportunities.Detection;

/// <summary>
/// What the detector found: which offer the learner's answer was answering, and where both
/// messages live.
/// </summary>
/// <param name="OfferLink">How the prior coach message was graded.</param>
/// <param name="Evidence">Pointers to the learner's message and the coach offer it answered.</param>
public readonly record struct CoachReferentLoss(
    CoachOpportunityOfferLink OfferLink,
    CoachOpportunityEvidencePointer Evidence);

/// <summary>
/// Detects the case where a learner gave the coach a clear answer and the coach had nothing to
/// bind it to.
/// </summary>
/// <remarks>
/// <para>
/// This is the screenshot defect, stated as a predicate. The learner asks their study duration;
/// Sam reads it, then offers in prose to change it to 45 minutes; the learner types "yes"; Sam
/// replies that it cannot tell what they want changed. Nothing was written, nothing was unsafe —
/// and the learner's reasonable answer to Sam's own question did nothing.
/// </para>
/// <para>
/// <b>Six authoritative conjuncts decide whether anything is recorded, and none of them is a
/// prompt heuristic:</b>
/// </para>
/// <list type="number">
/// <item>The input was typed text, not a chip or a structured control.</item>
/// <item><see cref="CoachExplicitAcceptanceClassifier"/> — already the application's authority
/// for "was this a clear yes", and the same gate the write path requires before it will write —
/// read it as decisive.</item>
/// <item>No plan suggestion was open, so the typed-decision shortcut did not apply.</item>
/// <item>No write proposal was open, so the approval routes had nothing waiting either.</item>
/// <item>The turn applied nothing and proposed nothing.</item>
/// <item>And it either stopped by asking <em>another</em> question
/// (<see cref="CoachStopReason.ClarificationRequested"/>), or it completed having declared a
/// settings-change intent — see <see cref="CoachActionIntent"/>.</item>
/// </list>
/// <para>
/// <b>The last conjunct is where this detector was wrong, and how it was corrected.</b> The
/// original rule accepted <see cref="CoachStopReason.ClarificationRequested"/> and nothing else,
/// on the reasoning that a completed turn is ordinary successful tutoring and the server had no
/// authoritative signal that the answer had been ignored. That reasoning holds for the tutoring
/// case and fails for the reproduced one: the learner answered "yes" to an offer to change their
/// study duration, and the application ran
/// <c>ReduceDirectAsync</c>'s no-change branch — a turn that declares
/// <see cref="Contracts.Coach.Intent.CoachIntentKind.DirectConstraintChange"/>, writes nothing,
/// and <em>completes</em> with "Today's Plan is unchanged." Nothing asked a second question, so
/// the stop reason was <see cref="CoachStopReason.Completed"/> and the row was never written.
/// </para>
/// <para>
/// The declared intent is the missing signal, and it is exactly the kind the original comment
/// asked for: an authoritative server-side statement, validated and stored before any of this
/// runs, that the turn was <em>about</em> changing something. A completed turn that declared a
/// settings change and then produced no receipt, no write operation, no proposal and no pending
/// suggestion did not use the learner's answer — the server is its own witness. A completed turn
/// that declared <see cref="Contracts.Coach.Intent.CoachIntentKind.PedagogicalAnswer"/> or
/// <see cref="Contracts.Coach.Intent.CoachIntentKind.NoChange"/> is the coach answering a
/// question, and stays excluded.
/// </para>
/// <para>
/// <b>The response text is still never read to make this decision.</b> The one text-reading step
/// remains <see cref="CoachOfferShape"/>, which grades <em>the server's own prior message</em> to
/// establish there was an offer to bind to at all. Requiring that grade to be something other
/// than <see cref="CoachOpportunityOfferLink.None"/> is what keeps an out-of-the-blue "yes" out
/// of the ledger: unprompted, it is noise, not an opportunity.
/// </para>
/// <para>
/// Reusing the acceptance classifier rather than writing a second one is load-bearing. It means
/// the detector and the write gate can never disagree about what "yes" means — including the
/// hedge, contrast, question-mark, and Korean rules — so the ledger cannot claim a referent was
/// lost for a message the write path would have called ambiguous anyway.
/// </para>
/// </remarks>
public sealed class CoachUnboundAnswerDetector
{
    /// <summary>How many trailing messages are read to find the coach's last one.</summary>
    /// <remarks>
    /// The learner's own message for this turn is normally the last row, and the coach's offer
    /// the one before it. A small window absorbs an interleaved notice without turning this into
    /// a transcript scan.
    /// </remarks>
    public const int LookbackMessages = 6;

    private readonly CoachExplicitAcceptanceClassifier _acceptance;
    private readonly ICoachMessageStore? _messages;
    private readonly ILogger<CoachUnboundAnswerDetector> _logger;

    public CoachUnboundAnswerDetector(
        CoachExplicitAcceptanceClassifier acceptance,
        ILogger<CoachUnboundAnswerDetector> logger,
        ICoachMessageStore? messages = null)
    {
        _acceptance = acceptance ?? throw new ArgumentNullException(nameof(acceptance));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _messages = messages;
    }

    /// <summary>
    /// The six authoritative conjuncts, evaluated without touching the message ledger.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="DetectAsync"/> so the cheap decision runs first: on the
    /// overwhelming majority of turns this returns false and no history read happens at all.
    /// </remarks>
    /// <param name="intent">
    /// The model's declared intent for this turn, or null when it produced none. Required rather
    /// than defaulted: this is the conjunct that decides whether a completed turn counts, and a
    /// default would let a new call site silently reintroduce the bug this parameter fixes.
    /// </param>
    public bool IsUnboundDecisiveAnswer(
        CoachTurnInputKind inputKind,
        string? text,
        string? pendingSuggestionId,
        bool hasOpenWriteProposal,
        bool hasChangeReceipt,
        bool hasWriteOperation,
        CoachStopReason stopReason,
        CoachIntentKind? intent)
    {
        if (inputKind != CoachTurnInputKind.Text)
        {
            return false;
        }

        if (_acceptance.Classify(text) is not (CoachExplicitAcceptance.Affirmative
            or CoachExplicitAcceptance.Negative))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(pendingSuggestionId))
        {
            // The typed-decision shortcut owns this turn: the answer bound to something and the
            // server acted on it. Nothing was lost.
            return false;
        }

        if (hasOpenWriteProposal)
        {
            // The approval routes had a proposal waiting, so the learner had somewhere to say
            // yes even if this turn did not take it.
            return false;
        }

        if (hasChangeReceipt || hasWriteOperation)
        {
            // Something was applied or proposed. The answer landed.
            return false;
        }

        return stopReason switch
        {
            // The turn answered a decisive yes/no by asking the learner what they meant. Accepted
            // regardless of intent, exactly as before — the second question is the whole signal.
            CoachStopReason.ClarificationRequested => true,

            // The turn finished, having declared it was going to change something, and then
            // changed nothing: no receipt, no write operation, no proposal, no pending suggestion,
            // all established above. That combination is the server's own record that the
            // learner's answer was dropped — not a guess about what the response said.
            //
            // Ordinary tutoring completes with an answering intent and is excluded here, which is
            // what keeps working conversations out of a ledger whose rows carry decryptable
            // pointers into learner messages.
            CoachStopReason.Completed => CoachActionIntent.IsSettingsChange(intent),

            // Any other stop reason means the turn failed for a reason the learner was told
            // about, and that is a different signal with its own row.
            _ => false
        };
    }

    /// <summary>
    /// Runs the full detection, including grading the prior coach message.
    /// </summary>
    /// <returns>
    /// Null when this turn is not a referent loss, or when the conversation's prior coach message
    /// was not something an answer could bind to.
    /// </returns>
    /// <remarks>
    /// Fails closed in every direction: no history store, no conversation, no readable prior
    /// message, or a prior message that is not an offer all produce null. A detector that guessed
    /// would fill the ledger with ordinary conversation.
    /// </remarks>
    public async Task<CoachReferentLoss?> DetectAsync(
        CoachOwner owner,
        string? conversationId,
        CoachTurnInputKind inputKind,
        string? text,
        string? pendingSuggestionId,
        bool hasOpenWriteProposal,
        bool hasChangeReceipt,
        bool hasWriteOperation,
        CoachStopReason stopReason,
        CoachIntentKind? intent,
        CancellationToken cancellationToken = default)
    {
        if (!IsUnboundDecisiveAnswer(
                inputKind, text, pendingSuggestionId, hasOpenWriteProposal,
                hasChangeReceipt, hasWriteOperation, stopReason, intent))
        {
            return null;
        }

        if (_messages is null || owner.IsEmpty || string.IsNullOrWhiteSpace(conversationId))
        {
            return null;
        }

        var page = await _messages
            .GetLatestAsync(owner, conversationId, LookbackMessages, cancellationToken)
            .ConfigureAwait(false);

        if (page.Status != CoachHistoryStatus.Success || page.Items.Count == 0)
        {
            return null;
        }

        // Chronological order, oldest first. The learner's own message for this turn is normally
        // last; the coach message before it is the offer.
        CoachMessageRecord? learnerMessage = null;
        CoachMessageRecord? offerMessage = null;

        for (var index = page.Items.Count - 1; index >= 0; index--)
        {
            var item = page.Items[index];

            if (learnerMessage is null)
            {
                if (item.Role == CoachMessageRole.Learner)
                {
                    learnerMessage = item;
                }

                continue;
            }

            if (item.Role == CoachMessageRole.Coach)
            {
                offerMessage = item;
                break;
            }

            // Two learner messages in a row means the coach never answered the first one, so
            // there is no offer between them to bind to.
            break;
        }

        if (offerMessage is null)
        {
            return null;
        }

        var offerLink = CoachOfferShape.Grade(offerMessage.Kind, offerMessage.Payload?.Text);
        if (offerLink is CoachOpportunityOfferLink.None)
        {
            return null;
        }

        if (offerLink is CoachOpportunityOfferLink.OpenPlanSuggestion)
        {
            // A structured suggestion was on screen. The open-suggestion conjunct above already
            // returned false in that case, so arriving here means the suggestion had been
            // answered or withdrawn — which is not a lost referent.
            return null;
        }

        // Counts and enum values only. The message identifiers are opaque and the text is never
        // touched again after grading.
        _logger.LogInformation(
            "[Coach] A decisive learner answer had nothing to bind to. "
            + "OfferLink={OfferLink} StopReason={StopReason} Intent={Intent}",
            offerLink,
            stopReason,
            intent);

        return new CoachReferentLoss(
            offerLink,
            new CoachOpportunityEvidencePointer(
                ConversationId: conversationId,
                MessageId: learnerMessage?.Id,
                MessageSequence: learnerMessage?.Sequence,
                OfferMessageId: offerMessage.Id,
                OfferMessageSequence: offerMessage.Sequence));
    }
}
