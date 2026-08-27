using System.Text.Json.Serialization;

namespace SentenceStudio.Contracts.Coach.Intent;

/// <summary>
/// What the learner asked for in one turn.
/// The application, not the model, decides if a write occurs.
/// The zero value is NoChange. An unset value never changes the plan.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachIntentKind
{
    /// <summary>The learner asked for no change. Answer only.</summary>
    NoChange = 0,

    /// <summary>The learner asked to change a constraint now.</summary>
    DirectConstraintChange,

    /// <summary>The coach proposes a constraint change. The learner must accept it first.</summary>
    SuggestConstraintChange,

    /// <summary>The learner accepted the suggestion that waits for a decision.</summary>
    AcceptPendingSuggestion,

    /// <summary>The learner rejected the suggestion that waits for a decision.</summary>
    RejectPendingSuggestion,

    /// <summary>The learner text is not clear. Ask one short question.</summary>
    AskClarification,

    /// <summary>The learner text is not about study plans or study constraints.</summary>
    OffTopic,

    /// <summary>
    /// The turn answers a language-learning question and changes nothing about Today's Plan.
    /// </summary>
    /// <remarks>
    /// Appended, never inserted. <c>CoachPlanRevision.IntentKind</c> is stored as an ordinal, so
    /// renumbering an existing member would silently re-label every revision already written.
    /// </remarks>
    PedagogicalAnswer = 7
}

/// <summary>
/// How clear the learner answer to a suggestion is.
/// The zero value is NotApplicable. An unset value never changes the plan.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachAcceptanceState
{
    /// <summary>The turn does not answer a suggestion.</summary>
    NotApplicable = 0,

    /// <summary>The answer is not clear. The application asks a question and writes nothing.</summary>
    Ambiguous,

    /// <summary>The answer accepts the suggestion clearly.</summary>
    Accepted,

    /// <summary>The answer rejects the suggestion clearly.</summary>
    Rejected
}
