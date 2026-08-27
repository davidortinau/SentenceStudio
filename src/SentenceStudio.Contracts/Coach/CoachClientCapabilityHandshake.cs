namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// What the client build sending this turn says it can do.
/// </summary>
/// <remarks>
/// <para>
/// <b>Turn-scoped, and that is a security property rather than a convenience.</b> The handshake is
/// merged for the duration of one turn and then dropped. It is never written to the conversation,
/// never written to the protected outcome, never logged, and never read back on a later turn. A
/// client cannot raise its own privileges once and have them stick.
/// </para>
/// <para>
/// <b>It authorizes reversible presentation state and nothing else.</b> A client saying "I can
/// apply a theme" is a statement about its own rendering. It is not consent, not ownership, and
/// not authority: it can never be the reason a durable write, an external effect, or an activity
/// launch is allowed. The resolver enforces that by only letting a handshake raise the ceiling for
/// <see cref="CoachCapabilityEffect.ReversiblePresentation"/>; every other effect ignores the
/// handshake entirely.
/// </para>
/// <para>
/// <b>It can only ever lower or leave a ceiling.</b> The derived availability is the minimum of
/// three ceilings, and the handshake is one of them. A handshake claiming a capability the server
/// has not implemented changes nothing, because the stage ceiling still applies.
/// </para>
/// </remarks>
public sealed class CoachClientCapabilityHandshake
{
    /// <summary>
    /// The handshake schema version the client speaks.
    /// </summary>
    /// <remarks>
    /// Versioned separately from the codes so the <i>shape</i> of the handshake can change without
    /// a client having to guess. A capability may declare a minimum version; a client below it is
    /// treated as not having the capability at all rather than as having a partial one, because
    /// "half a handshake" is not a state the resolver should have to reason about.
    /// </remarks>
    public required int Version { get; init; }

    /// <summary>
    /// The capabilities this client build declares. Order is not significant and duplicates are
    /// ignored. Unrecognised values arrive as <see cref="CoachClientCapabilityCode.Unknown"/> and
    /// are dropped.
    /// </summary>
    public IReadOnlyList<CoachClientCapabilityCode> Codes { get; init; } = [];

    /// <summary>
    /// The version below which no capability is granted. A handshake claiming version zero or less
    /// is malformed and is treated as absent.
    /// </summary>
    public const int MinimumSupportedVersion = 1;

    /// <summary>
    /// True when this handshake is structurally usable at all. A malformed handshake is treated as
    /// absent rather than rejected: refusing the turn over a presentation hint would be a worse
    /// outcome for the learner than quietly withholding one affordance.
    /// </summary>
    public bool IsUsable => Version >= MinimumSupportedVersion;
}
