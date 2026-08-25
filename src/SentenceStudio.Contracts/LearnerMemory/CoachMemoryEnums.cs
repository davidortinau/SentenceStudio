using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.LearnerMemory;

/// <summary>
/// The closed set of things Sam is allowed to remember about a learner.
/// </summary>
/// <remarks>
/// <para>
/// These ordinals are persisted in <c>CoachMemoryFact.Kind</c>. <b>Append only.</b> Never renumber
/// and never remove a member: a stored row would silently change meaning.
/// </para>
/// <para>
/// There is deliberately no <c>Other</c> bucket. A free-form bucket is how a typed memory system
/// turns into a transcript store, and a transcript store is exactly what this design rejects.
/// Anything that does not fit one of these kinds is not remembered.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachMemoryKind.PersistentStudyGoal), WireEnumFallbackKind.DeliberateNeutral,
    "There is deliberately no Other bucket \u2014 the design rejects a free-form kind \u2014 and the "
    + "ordinal is persisted through the memory store, so a sentinel would reach the server\u2019s memory "
    + "validation, selector and text policy. Safe to collapse because CoachMemoryFactDto carries the "
    + "learner\u2019s own approved text; the kind picks a label and a value branch, and a fact whose "
    + "branch does not match its kind is already refused as MissingValue/WrongBranch, so an unreadable "
    + "kind lands on a card the learner can still see and forget. The write path stays closed too: an "
    + "edit that echoed the collapsed kind back is rejected by the server, which refuses a kind change "
    + "outright rather than treating it as an implicit new fact.")]
public enum CoachMemoryKind
{
    /// <summary>A short, learner-authored study goal for one target language.</summary>
    PersistentStudyGoal = 0,

    /// <summary>How much explanation the learner wants.</summary>
    ExplanationDepth = 1,

    /// <summary>When the learner wants corrections delivered.</summary>
    CorrectionTiming = 2,

    /// <summary>Which register the learner wants examples written in.</summary>
    ExampleRegister = 3
}

/// <summary>
/// The lifecycle of one remembered fact.
/// The zero value is <see cref="Candidate"/>: an unset value is never used in a prompt.
/// </summary>
/// <remarks>Ordinals are persisted. Append only.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachMemoryStatus.Candidate), WireEnumFallbackKind.SafeZero,
    "Candidate is documented as never entering a prompt, so an unreadable status is the least trusted "
    + "state rather than Active.")]
public enum CoachMemoryStatus
{
    /// <summary>Proposed from an explicit learner statement. Never enters a prompt.</summary>
    Candidate = 0,

    /// <summary>Approved by the learner. Eligible for prompt context.</summary>
    Active = 1,

    /// <summary>Replaced by a newer approved fact of the same kind and scope.</summary>
    Superseded = 2,

    /// <summary>Past its expiry. Never enters a prompt.</summary>
    Expired = 3,

    /// <summary>
    /// A candidate that contradicts an existing active fact of the same kind and scope.
    /// It waits for an explicit decision; nothing is overwritten before approval.
    /// </summary>
    ConflictPending = 4
}

/// <summary>
/// How a fact came to exist. v1 has no inferred or model-proposed provenance.
/// </summary>
/// <remarks>Ordinals are persisted. Append only.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachMemoryProvenance.UserExplicit), WireEnumFallbackKind.SafeZero,
    "UserExplicit is the weaker of the two claims: the learner said it. UserConfirmed additionally "
    + "asserts they approved it through the memory surface, which is exactly the claim a client must "
    + "not invent.")]
public enum CoachMemoryProvenance
{
    /// <summary>The learner stated it outright in a message they sent.</summary>
    UserExplicit = 0,

    /// <summary>The learner confirmed it through the memory surface.</summary>
    UserConfirmed = 1
}

/// <summary>
/// What a fact applies to.
/// </summary>
/// <remarks>Ordinals are persisted. Append only.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachMemoryScope.TargetLanguage), WireEnumFallbackKind.SafeZero,
    "TargetLanguage is the narrower scope, and Global is documented as \u201cmust be chosen explicitly, "
    + "never inferred\u201d. An unreadable scope must therefore never widen to every language.")]
public enum CoachMemoryScope
{
    /// <summary>Applies only while studying one target language.</summary>
    TargetLanguage = 0,

    /// <summary>Applies to every language. Must be chosen explicitly, never inferred.</summary>
    Global = 1
}

/// <summary>How much explanation the learner wants.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachMemoryExplanationDepth.Concise), WireEnumFallbackKind.SafeZero,
    "Concise is the minimal reading \u2014 answer, then stop. An unreadable preference should not have "
    + "the client display the learner as having asked for more than they did.")]
public enum CoachMemoryExplanationDepth
{
    /// <summary>Answer, then stop.</summary>
    Concise = 0,

    /// <summary>Answer plus a short reason.</summary>
    Balanced = 1,

    /// <summary>Answer, reason, and contrasting examples.</summary>
    Detailed = 2
}

/// <summary>When the learner wants corrections.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachMemoryCorrectionTiming.Immediate), WireEnumFallbackKind.SafeZero,
    "Display-only on the client: this renders the learner\u2019s stored preference on a memory card and "
    + "drives no control. Immediate is the documented zero value and neither member is unsafe.")]
public enum CoachMemoryCorrectionTiming
{
    /// <summary>Correct as soon as the error appears.</summary>
    Immediate = 0,

    /// <summary>Let the learner finish, then correct.</summary>
    AfterResponse = 1
}

/// <summary>Which register examples should use.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachMemoryExampleRegister.NeutralPolite), WireEnumFallbackKind.SafeZero,
    "NeutralPolite is the neutral register by name and by definition: everyday polite speech is the "
    + "safe default for a preference this build cannot read, where Casual and Formal are both specific "
    + "social claims.")]
public enum CoachMemoryExampleRegister
{
    /// <summary>Everyday polite speech.</summary>
    NeutralPolite = 0,

    /// <summary>Casual speech among peers.</summary>
    Casual = 1,

    /// <summary>Formal or written register.</summary>
    Formal = 2
}

/// <summary>
/// The closed classification of the current turn, supplied by the calling application layer.
/// </summary>
/// <remarks>
/// The selector takes this instead of the learner's raw text on purpose. A free-text query would
/// make retrieval a function of untrusted input; a closed category keeps selection deterministic
/// and auditable.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachMemoryTurnCategory.Unspecified), WireEnumFallbackKind.SafeZero,
    "Unspecified is documented as \u201cnothing is known about the turn; only the safest kinds are "
    + "considered\u201d, which is precisely the fail-closed reading.")]
public enum CoachMemoryTurnCategory
{
    /// <summary>Nothing is known about the turn. Only the safest kinds are considered.</summary>
    Unspecified = 0,

    /// <summary>The learner asked why something works the way it does.</summary>
    GrammarExplanation = 1,

    /// <summary>The learner asked for vocabulary help.</summary>
    VocabularyHelp = 2,

    /// <summary>The learner asked for example sentences.</summary>
    ExampleRequest = 3,

    /// <summary>The learner produced language and expects feedback.</summary>
    CorrectionFeedback = 4,

    /// <summary>The learner is discussing what to study.</summary>
    StudyPlanning = 5,

    /// <summary>Open conversation with no narrower classification.</summary>
    GeneralConversation = 6
}
