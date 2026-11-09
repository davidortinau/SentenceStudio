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
    /// <summary>Nothing to change and nothing to answer, or a topical vocabulary review request.</summary>
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
    PedagogicalAnswer = 7,

    /// <summary>
    /// The turn answers anything else in plain words: a question about the learner's own words,
    /// resources, skills, settings or progress, what a read tool found, what a prepared proposal
    /// will do, or how the app works. Changes nothing about Today's Plan and writes nothing.
    /// </summary>
    /// <remarks>
    /// The general reply, so a result such as "you have 342 words" or "I prepared 사과 for you to
    /// approve" has a kind of its own instead of borrowing a plan kind or a language-teaching kind.
    /// Appended for the same reason as <see cref="PedagogicalAnswer"/>: the ordinal is stored.
    /// </remarks>
    GeneralAnswer = 8
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
