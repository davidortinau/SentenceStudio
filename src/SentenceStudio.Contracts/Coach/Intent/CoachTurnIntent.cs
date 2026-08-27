using System.ComponentModel;

namespace SentenceStudio.Contracts.Coach.Intent;

/// <summary>
/// The typed result of one coach turn from the model.
/// This contract is internal. The server never sends this shape to a client.
/// </summary>
/// <remarks>
/// The application owns every write:
/// <list type="bullet">
/// <item>A direct constraint change applies after validation.</item>
/// <item>A suggestion creates a read-only preview. A suggestion never writes.</item>
/// <item>An acceptance applies only when it names the current suggestion.</item>
/// <item>An unclear answer asks one question and writes nothing.</item>
/// </list>
/// This contract carries no learner identifier, no plan item selection, and no command.
/// </remarks>
[Description("The result of one coach turn. Return one object only.")]
public sealed class CoachTurnIntent
{
    [Description("What the learner asked for. Use DirectConstraintChange only when the learner asks for the change now. Use SuggestConstraintChange when you propose the change.")]
    public CoachIntentKind Kind { get; set; }

    [Description("The constraint change for this turn. Set it for DirectConstraintChange and for SuggestConstraintChange. Leave it empty for all other kinds.")]
    public CoachConstraintDeltaIntent? ConstraintDelta { get; set; }

    [Description("The identifier of the suggestion this turn answers. Copy the identifier from the context. Leave it empty if the turn answers no suggestion.")]
    public string? PendingSuggestionId { get; set; }

    [Description("How clear the learner answer to the suggestion is. Use Ambiguous if the answer is not clear.")]
    public CoachAcceptanceState AcceptanceState { get; set; }

    [Description("One short question for the learner. Set it only for AskClarification. The largest length is 200 characters.")]
    public string? ClarifyingQuestion { get; set; }

    [Description("A short message for the learner. Use simple words. The largest length is 400 characters. Do not add a target-language word that is due for review.")]
    public string CoachMessage { get; set; } = string.Empty;

    [Description("The facts you used for this answer. Leave the list empty if you used no facts.")]
    public List<CoachEvidenceReferenceIntent> EvidenceReferences { get; set; } = new();

    /// <summary>
    /// The answer to a language-learning question, when the learner asked one.
    /// </summary>
    /// <remarks>
    /// Required for <see cref="CoachIntentKind.PedagogicalAnswer"/>. Allowed on
    /// <see cref="CoachIntentKind.SuggestConstraintChange"/> so a turn that both asks a language
    /// question and requests a plan change can be answered and previewed in one reply — the
    /// answer is delivered, the plan change waits for an explicit acceptance. Forbidden on every
    /// other kind, so an answer can never ride along with a write.
    /// </remarks>
    [Description("The answer to a language question. Set it for PedagogicalAnswer, and for SuggestConstraintChange when the learner asked a language question and requested a plan change in the same message. Leave it empty otherwise.")]
    public CoachPedagogicalAnswerIntent? PedagogicalAnswer { get; set; }

    /// <summary>
    /// A proposal to remember one learner preference, when the learner asked for it outright.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Allowed on every kind, because "remember that I want short answers" can arrive alongside a
    /// question, alongside a plan suggestion, or on its own. It is deliberately orthogonal to
    /// <see cref="Kind"/>: a memory proposal is never a plan write and never an acceptance, so
    /// forcing it into the kind enum would make the two decisions share one slot and let a
    /// proposal displace an answer.
    /// </para>
    /// <para>
    /// The application discards this outright unless the learner's own message contains an
    /// explicit remember-marker and <see cref="CoachMemoryProposalIntent.EvidenceSpan"/> is an
    /// exact substring of it. A proposal writes nothing the learner has not approved.
    /// </para>
    /// </remarks>
    [Description("A preference the learner explicitly asked you to remember. Set it only when their message says to remember something, such as 'remember that' or 'from now on'. Leave it empty otherwise.")]
    public CoachMemoryProposalIntent? MemoryProposal { get; set; }
}
