using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// What a pedagogical answer is about. A closed set: the model classifies into it, it never
/// invents a topic, and the client can style each one without parsing prose.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachAnswerTopic.Other), WireEnumFallbackKind.NeutralMember,
    "Other is literally defined as \u201ca language-learning question that fits none of the above\u201d, "
    + "which is exactly true of a topic this build cannot name. Vocabulary \u2014 the zero value \u2014 "
    + "would mislabel a grammar or pronunciation answer.")]
public enum CoachAnswerTopic
{
    /// <summary>Words, meaning, nuance, and the difference between similar words.</summary>
    Vocabulary = 0,

    /// <summary>Grammar form and how a pattern is built.</summary>
    Grammar,

    /// <summary>When and with whom a form is used, including register and politeness.</summary>
    Usage,

    /// <summary>Sound, spelling-to-sound, and pronunciation guidance.</summary>
    Pronunciation,

    /// <summary>Feedback on target-language text the learner supplied in this turn.</summary>
    LearnerText,

    /// <summary>How to hold or open a conversation.</summary>
    Conversation,

    /// <summary>How to study: sequencing, review, and practice habits.</summary>
    StudyStrategy,

    /// <summary>A language-learning question that fits none of the above.</summary>
    Other
}

/// <summary>
/// The role each block plays in an answer, so a client can order and style them without reading
/// the text.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachAnswerBlockKind.Note), WireEnumFallbackKind.NeutralMember,
    "Note is \u201ca short aside\u201d: it introduces the text without claiming what the text does. "
    + "Answer \u2014 the zero value \u2014 is the worst possible landing spot, because the contract says "
    + "the answer block leads and needs no label, so an unrecognised block would be promoted into the "
    + "position of the direct answer. Server order is preserved either way; the client never re-sorts.")]
public enum CoachAnswerBlockKind
{
    /// <summary>The direct answer. Always first.</summary>
    Answer = 0,

    /// <summary>How the form is built.</summary>
    Form,

    /// <summary>What it means.</summary>
    Meaning,

    /// <summary>When it is used.</summary>
    Use,

    /// <summary>A worked example.</summary>
    Example,

    /// <summary>A contrast between two forms.</summary>
    Contrast,

    /// <summary>A short supportive correction of learner-supplied text.</summary>
    Correction,

    /// <summary>A short aside.</summary>
    Note,

    /// <summary>One optional question that invites the learner to recall.</summary>
    RetrievalPrompt
}

/// <summary>
/// Which language a span is written in. The server resolves the actual BCP-47 tag from the
/// learner's profile; the model only says which role the text plays.
/// </summary>
/// <remarks>
/// The model cannot name a locale. Letting it do so would put an arbitrary, unvalidated string
/// into a field clients use to pick fonts, text direction, and speech voices.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachLanguageRole.Display), WireEnumFallbackKind.DeliberateNeutral,
    "Display is the safe reading for a span whose language role this build cannot name: it inherits the "
    + "answer\u2019s own display language tag, which is what CoachAnswer already does for a span with no "
    + "resolved tag. Target would tell a screen reader to switch to a voice the text may not be in.")]
public enum CoachLanguageRole
{
    /// <summary>The learner's display language: explanations and instructions.</summary>
    Display = 0,

    /// <summary>The language being studied.</summary>
    Target,

    /// <summary>The learner's first language.</summary>
    Native
}
