using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Coach.Opportunities.Detection;

/// <summary>
/// Which declared intents mean the turn set out to change something about the learner's setup.
/// </summary>
/// <remarks>
/// <para>
/// This is the authoritative discriminant that lets <see cref="CoachUnboundAnswerDetector"/> accept
/// a <see cref="Contracts.Coach.CoachStopReason.Completed"/> turn without accepting ordinary
/// tutoring with it. The intent is the model's own declaration, validated by
/// <c>CoachIntentValidator</c> and stored by the application before any of this runs — it is not a
/// reading of the response text, and nothing here looks at a message.
/// </para>
/// <para>
/// <b>The two groups are decided by what the application does with the intent, not by what it
/// sounds like.</b> The four members below are the ones <c>CoachSessionService</c> routes into a
/// reducer that can write, propose, or resolve a pending decision. When one of those completes and
/// the turn still produced no receipt, no write operation, no proposal and no pending suggestion,
/// the server itself is the witness that the learner's answer changed nothing — which is precisely
/// the evidence the previous revision said it was waiting for.
/// </para>
/// <para>
/// Everything else answers rather than acts. <see cref="CoachIntentKind.PedagogicalAnswer"/> and
/// <see cref="CoachIntentKind.NoChange"/> are ordinary tutoring: the coach was asked a question and
/// answered it, and a completed turn there is the system working.
/// <see cref="CoachIntentKind.AskClarification"/> is excluded because a turn that asked another
/// question is already admitted through the
/// <see cref="Contracts.Coach.CoachStopReason.ClarificationRequested"/> branch, where it belongs;
/// admitting it here as well would let a completed clarification double as a loss.
/// <see cref="CoachIntentKind.OffTopic"/> has its own aggregate-only row and is not a lost referent.
/// </para>
/// <para>
/// <b>Fails closed.</b> An unset intent returns false: with no declaration there is no authoritative
/// signal, and the design would rather miss a row than invent one. A member added to
/// <see cref="CoachIntentKind"/> later also returns false until somebody classifies it here — and
/// <c>CoachActionIntentTests</c> enumerates the enum so that decision cannot be skipped silently.
/// </para>
/// </remarks>
public static class CoachActionIntent
{
    /// <summary>
    /// True when <paramref name="intent"/> declared a change to the learner's plan or constraints.
    /// </summary>
    /// <param name="intent">
    /// The model's declared intent for the turn, or null when the turn produced none.
    /// </param>
    public static bool IsSettingsChange(CoachIntentKind? intent) => intent switch
    {
        // Routed into a reducer that writes, proposes, or resolves a pending decision.
        CoachIntentKind.DirectConstraintChange => true,
        CoachIntentKind.SuggestConstraintChange => true,
        CoachIntentKind.AcceptPendingSuggestion => true,
        CoachIntentKind.RejectPendingSuggestion => true,

        // Answering, not acting.
        CoachIntentKind.NoChange => false,
        CoachIntentKind.PedagogicalAnswer => false,
        CoachIntentKind.AskClarification => false,
        CoachIntentKind.OffTopic => false,

        // Unset, or a member added after this rule was written.
        _ => false
    };
}
