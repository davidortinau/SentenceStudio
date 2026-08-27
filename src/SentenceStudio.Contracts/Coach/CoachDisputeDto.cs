using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// What the learner corrected. Mirrors the server's correction signal.
/// </summary>
/// <remarks>
/// <para>
/// A code rather than the learner's sentence. The learner already has their own words on screen in
/// the conversation; repeating them inside a status notice adds nothing and would put learner prose
/// into a shape that travels beside the answer.
/// </para>
/// <para>
/// Mirrored rather than moved, for the same compile reason the evidence scope enums are mirrored:
/// Contracts cannot reference the API assembly. A census test holds the two vocabularies equal.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachDisputeSignal.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown is the documented unset value and the client renders the generic dispute notice, which "
    + "is true regardless of which kind of correction was made. Guessing a specific one would tell "
    + "the learner the coach understood their correction more precisely than it did.")]
public enum CoachDisputeSignal
{
    /// <summary>Unrecognised. Render the generic notice.</summary>
    Unknown = 0,

    /// <summary>The learner restated what they meant.</summary>
    MeantSomethingElse = 1,

    /// <summary>The learner rejected the reading of their question.</summary>
    NotWhatIAsked = 2,

    /// <summary>The learner rejected the content of the answer.</summary>
    WrongClaim = 3,

    /// <summary>The learner named a different set than the answer used.</summary>
    DifferentCohort = 4
}

/// <summary>How a dispute ended, or that it has not.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachDisputeStatus.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown is the documented unset value and the client renders nothing at all. Guessing Open "
    + "would show a learner an unresolved dispute the server never reported, and guessing a "
    + "resolution would tell them their correction was handled when this build cannot tell.")]
public enum CoachDisputeStatus
{
    /// <summary>Unrecognised. Render no notice.</summary>
    Unknown = 0,

    /// <summary>Still open. The coach owes the learner a re-read, a correction, or a limitation.</summary>
    Open = 1,

    /// <summary>The coach re-read with different parameters.</summary>
    ResolvedByReRead = 2,

    /// <summary>The coach named and corrected its prior claim.</summary>
    ResolvedByCorrection = 3,

    /// <summary>The coach stated an honest limitation instead.</summary>
    ResolvedByLimitation = 4,

    /// <summary>The learner dismissed it.</summary>
    DismissedByLearner = 5
}

/// <summary>
/// An open correction, shown to the learner so they can see it registered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the learner sees this at all.</b> The failure this closes is not that the coach was
/// wrong — it is that a learner who corrected it had no way to tell whether the correction landed.
/// Case D repeated a disputed list more confidently, and from the learner's side that is
/// indistinguishable from not having spoken. A visible dispute is the receipt.
/// </para>
/// <para>
/// <b>Content-free.</b> A signal code, a status, and a bounded message identifier the client uses
/// to anchor the notice next to the disputed message. The learner's correction text lives in the
/// conversation, once, in the encrypted ledger.
/// </para>
/// <para>
/// Additive and tolerant: an older client ignores the whole property and renders the conversation
/// exactly as it does today.
/// </para>
/// </remarks>
public sealed class CoachDisputeDto
{
    /// <summary>What kind of correction the learner made.</summary>
    public CoachDisputeSignal Signal { get; init; } = CoachDisputeSignal.Unknown;

    /// <summary>Whether it is still open, and how it closed if it is not.</summary>
    public CoachDisputeStatus Status { get; init; } = CoachDisputeStatus.Unknown;

    /// <summary>
    /// The coach message under dispute, so the notice renders beside it rather than at the top.
    /// </summary>
    /// <remarks>
    /// An identifier the client already holds for a message it is already rendering. Not a
    /// reference the client resolves against the server, and never the message text.
    /// </remarks>
    public string? DisputedMessageId { get; init; }
}
