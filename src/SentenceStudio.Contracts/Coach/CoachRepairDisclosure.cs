using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// What the grounding layer did to an answer the learner is about to read.
/// </summary>
/// <remarks>
/// <para>
/// <b>One closed value, and deliberately nothing else.</b> No counts, no rule codes, no span
/// coordinates, no compliance wording, no server-authored sentence. A learner does not need to know
/// that three rules fired or which; they need to know whether what they are reading is exactly what
/// the coach produced. Everything narrower than that belongs to the operator surfaces, which
/// already have it, and putting it here would turn a disclosure into an audit log the learner
/// cannot act on.
/// </para>
/// <para>
/// <b>Separate from the refusal path.</b> A refused turn carries
/// <see cref="CoachLimitationDto"/> and no disclosure — the learner received no answer, so there is
/// nothing to disclose <em>about</em>. This shape only ever describes an answer that shipped.
/// </para>
/// <para>
/// <b>The client owns the words.</b> The server states the state; the sentence lives in the
/// client's resource file, in the learner's own language. That is the same rule the typed refusal
/// established, and it exists because a server string in a notice bypasses localisation entirely.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachRepairDisclosure.Unknown), WireEnumFallbackKind.SafeZero,
    "An unrecognised disclosure renders as a neutral note rather than as a claim about the answer. "
    + "Guessing which of the two real states a newer server meant would be worse than saying "
    + "nothing: one of them tells the learner their answer was rewritten.")]
public enum CoachRepairDisclosure
{
    /// <summary>Unrecognised. Render a neutral note; never infer one of the states below.</summary>
    Unknown = 0,

    /// <summary>
    /// Nothing to disclose. The answer is exactly what the coach produced.
    /// </summary>
    /// <remarks>
    /// Distinct from the property being null, which means the layer did not run at all — Off,
    /// Observe, or a host with no grounding. Both render nothing, and keeping them apart lets a
    /// client tell "checked and clean" from "not checked".
    /// </remarks>
    None = 1,

    /// <summary>
    /// Part of this answer was replaced, because what the coach originally wrote was not supported.
    /// </summary>
    AnswerAltered = 2,

    /// <summary>
    /// Something needed replacing and the replacement does not exist in this learner's language, so
    /// the answer was left as written.
    /// </summary>
    /// <remarks>
    /// The honest half of a limitation the product has rather than the learner does: the repair
    /// sentences are English constants and the server does not localise. A learner reading in
    /// Korean is told the coach is less sure of this answer than usual, which is true, rather than
    /// being handed an English sentence in the middle of a Korean one.
    /// </remarks>
    RepairSuppressedForLanguage = 3
}
