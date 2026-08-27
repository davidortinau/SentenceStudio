using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SentenceStudio.Contracts.Coach.Intent;

/// <summary>
/// A proposal that the learner asked for something to be remembered.
/// This contract is internal. The server never sends this shape to a client.
/// </summary>
/// <remarks>
/// <para>
/// A proposal is not a write and not an activation. The application re-derives every field it
/// trusts, screens the value through the same content policy the memory surface uses, and stores
/// the result as a <c>Candidate</c> that never enters a prompt until the learner approves it in a
/// separate action. The model cannot approve, edit, forget, or read memory: there is no memory
/// tool, and this shape is the only path from a turn to the memory store.
/// </para>
/// <para>
/// <see cref="EvidenceSpan"/> is the load-bearing field. It must be an exact substring of the
/// learner's message for this turn. Without it the model could invent a preference the learner
/// never stated and have the application persist it as "user explicit". The span is verified
/// twice — once here in the application gate, once again inside the memory store — and then
/// discarded; only a count and a pair of dates survive.
/// </para>
/// <para>
/// Every field is typed with an enum declared alongside this shape rather than with the stored
/// memory enums, and that is deliberate twice over. It keeps the learner-memory contracts out of
/// the model's reachable graph, which is the boundary the memory separation tests defend. It also
/// pins the model's vocabulary independently of storage: the stored kinds can gain a member, be
/// reordered, or be renamed without silently widening what a model is allowed to emit. The two
/// vocabularies are joined in exactly one place, the application gate, which is the same place
/// that revalidates the evidence span.
/// </para>
/// <para>
/// Member naming note: the register field is called <see cref="Register"/> rather than
/// <c>ExampleRegister</c> because "example" is an embargoed word on a model-visible shape.
/// </para>
/// </remarks>
[Description("A proposal to remember one learner preference. Return it only when the learner explicitly asked to be remembered.")]
public sealed class CoachMemoryProposalIntent
{
    [Description("Which preference the learner asked to be remembered.")]
    public CoachProposedMemoryKind Kind { get; set; }

    [Description("Whether the preference applies to the language being studied or to every language. Use TargetLanguage unless the learner said all languages.")]
    public CoachProposedMemoryScope Scope { get; set; } = CoachProposedMemoryScope.TargetLanguage;

    [Description("The learner's own words for a PersistentStudyGoal. The largest length is 200 characters. Leave it empty for every other kind.")]
    public string? StudyGoalText { get; set; }

    [Description("How much explanation the learner wants. Set it only for ExplanationDepth.")]
    public CoachProposedExplanationDepth? ExplanationDepth { get; set; }

    [Description("When the learner wants corrections. Set it only for CorrectionTiming.")]
    public CoachProposedCorrectionTiming? CorrectionTiming { get; set; }

    [Description("Which register the learner wants. Set it only for ExampleRegister.")]
    public CoachProposedRegister? Register { get; set; }

    [Description("The exact words from the learner message that asked for this. Copy them character for character. Do not paraphrase.")]
    public string EvidenceSpan { get; set; } = string.Empty;
}

/// <summary>What a model may propose remembering. Mapped to the stored kind in the gate.</summary>
/// <remarks>
/// Held separate from the persisted memory kind on purpose: a model's emitted vocabulary should
/// only change when someone deliberately changes it here, never as a side effect of a storage
/// change. Unmapped members are refused rather than guessed.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachProposedMemoryKind
{
    PersistentStudyGoal = 0,
    ExplanationDepth = 1,
    CorrectionTiming = 2,
    ExampleRegister = 3
}

/// <summary>How widely a proposed preference applies.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachProposedMemoryScope
{
    TargetLanguage = 0,
    Global = 1
}

/// <summary>How much explanation a learner asked for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachProposedExplanationDepth
{
    Concise = 0,
    Balanced = 1,
    Detailed = 2
}

/// <summary>When a learner asked to be corrected.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachProposedCorrectionTiming
{
    Immediate = 0,
    AfterResponse = 1
}

/// <summary>Which register a learner asked for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachProposedRegister
{
    NeutralPolite = 0,
    Casual = 1,
    Formal = 2
}
