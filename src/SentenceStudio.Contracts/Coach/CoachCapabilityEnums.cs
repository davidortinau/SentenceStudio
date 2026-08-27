using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// What a capability does to the substrate. Plan §5.2.
/// </summary>
/// <remarks>
/// The second axis beside <c>RiskClass</c>. Risk class describes ceremony — what the learner is
/// asked before it happens. Effect class describes substrate — what actually changes and where.
/// Two axes beat one multiplied axis: the §5.4 matrix is readable precisely because each row fixes
/// an effect class and then constrains the ceremony around it.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachCapabilityEffectClass.Read), WireEnumFallbackKind.SafeZero,
    "The zero member is the only effect class that changes nothing, so an unreadable effect class "
    + "lands on the one value that cannot authorize a write, a launch, or an external call.")]
public enum CoachCapabilityEffectClass
{
    /// <summary>Reads only. Changes nothing.</summary>
    Read = 0,

    /// <summary>Reversible presentation state on the connected device or browser.</summary>
    PresentationState,

    /// <summary>Durable learner data.</summary>
    LearnerData,

    /// <summary>A two-step reversible pair: one preview, one accept.</summary>
    CompositeReversiblePair,

    /// <summary>Reaches outside the app.</summary>
    ExternalEffect,

    /// <summary>Starts a timed activity.</summary>
    ActivityLaunch
}

/// <summary>How a capability's effect is taken back. Plan §5.2.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachCapabilityReversal.None), WireEnumFallbackKind.SafeZero,
    "The zero member already means 'there is no undo'. An unreadable reversal landing there "
    + "understates what the learner can take back, which withholds a comfort rather than "
    + "promising one that does not exist.")]
public enum CoachCapabilityReversal
{
    /// <summary>No undo.</summary>
    None = 0,

    /// <summary>The client reverts it locally.</summary>
    ClientRevert,

    /// <summary>The ledger holds an undo.</summary>
    LedgerUndo,

    /// <summary>The server discards it.</summary>
    ServerDiscard
}

/// <summary>What the learner is asked before the effect happens. Plan §5.2.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachCapabilityConfirmation.Confirm), WireEnumFallbackKind.NeutralMember,
    "Deliberately NOT the zero member. Confirmation's zero is None — ask the learner nothing — and "
    + "collapsing an unreadable value there would silently remove a gate. Confirm is the strictest "
    + "member, so an unreadable confirmation asks for more rather than less.")]
public enum CoachCapabilityConfirmation
{
    /// <summary>Nothing is asked.</summary>
    None = 0,

    /// <summary>A tap or equivalent direct gesture.</summary>
    Gesture,

    /// <summary>An explicit learner command in words.</summary>
    Imperative,

    /// <summary>The learner accepts a proposal.</summary>
    Accept,

    /// <summary>The learner confirms a destructive or external step.</summary>
    Confirm
}

/// <summary>What record the effect leaves. Plan §5.2.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachCapabilityReceiptKind.None), WireEnumFallbackKind.SafeZero,
    "The zero member already means no receipt was written. Claiming a receipt exists when the "
    + "value could not be read would point a learner at a record that may not be there.")]
public enum CoachCapabilityReceiptKind
{
    /// <summary>No receipt.</summary>
    None = 0,

    /// <summary>A client-side receipt.</summary>
    Client,

    /// <summary>A ledger receipt.</summary>
    Ledger
}

/// <summary>How far the effect reaches. Plan §5.2.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachCapabilityScope.Device), WireEnumFallbackKind.SafeZero,
    "The zero member is the narrowest reach. An unreadable scope confined to the device cannot be "
    + "mistaken for something that touches the account.")]
public enum CoachCapabilityScope
{
    /// <summary>The connected device or browser only.</summary>
    Device = 0,

    /// <summary>The current session.</summary>
    Session,

    /// <summary>The learner's account.</summary>
    Account
}

/// <summary>Where a capability executes. Plan §5.2.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachCapabilitySurface.Server), WireEnumFallbackKind.SafeZero,
    "The zero member is the surface the server can actually speak for. Landing an unreadable "
    + "surface on Client would make the handshake the deciding factor for something whose location "
    + "is unknown.")]
public enum CoachCapabilitySurface
{
    /// <summary>The API process.</summary>
    Server = 0,

    /// <summary>The learner's device or browser.</summary>
    Client,

    /// <summary>Outside the app.</summary>
    External
}

/// <summary>
/// The declared ceiling on availability. Plan §5.2. Members and order exactly as planned.
/// </summary>
/// <remarks>
/// A ceiling, never an answer, and never stored as one. See
/// <see cref="CoachCapabilityAvailabilityRank"/> for the ordering the §5.3 minimum needs.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachCapabilityAvailability.Unknown), WireEnumFallbackKind.AppendedSentinel,
    "The zero member is Present, the most permissive value in the set, so a tolerant read must not "
    + "land there. Unknown is the planned sentinel and is last in the planned member order, so it "
    + "is appended rather than inserted and stored ordinals keep their meaning.")]
public enum CoachCapabilityAvailability
{
    /// <summary>Usable here, now.</summary>
    Present = 0,

    /// <summary>Implemented, but on a screen rather than here. The answer names the screen.</summary>
    PresentOnAnotherSurface,

    /// <summary>Deliberately not offered. Not a gap; a decision.</summary>
    AbsentByDesign,

    /// <summary>Not built yet.</summary>
    AbsentUnimplemented,

    /// <summary>Could not be determined.</summary>
    Unknown
}

/// <summary>
/// The <c>Coach:Capabilities:Stage</c> ladder, plan §16 line 484:
/// Off → Read → Presentation → Launch → Semantic → External, ordered by effect severity.
/// </summary>
/// <remarks>
/// Ordinals follow the ladder, so <c>&gt;=</c> is the promotion test the plan writes it as. Unlike
/// <see cref="CoachCapabilityAvailability"/>, the ordinal order here is meaningful and load-bearing.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachCapabilityStage.Off), WireEnumFallbackKind.SafeZero,
    "The zero member is the bottom of the ladder, so an unreadable stage permits nothing. Any "
    + "other landing spot would promote a capability by accident, which is what §5.3 rule 3 "
    + "forbids.")]
public enum CoachCapabilityStage
{
    /// <summary>Nothing is permitted.</summary>
    Off = 0,

    /// <summary>Read capabilities are permitted.</summary>
    Read,

    /// <summary>Reversible presentation state is permitted.</summary>
    Presentation,

    /// <summary>Activity launch is permitted.</summary>
    Launch,

    /// <summary>Structured semantic writes are permitted.</summary>
    Semantic,

    /// <summary>External effects are permitted. The last rung.</summary>
    External
}

/// <summary>
/// A capability a connected client build declares it can execute. Plan §5.5.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachClientCapabilityCode.Unknown), WireEnumFallbackKind.SafeZero,
    "An unrecognised code grants nothing. The zero member is dropped from the merged handshake, so "
    + "an unreadable code is exactly as powerless as an absent one and the turn still renders.")]
public enum CoachClientCapabilityCode
{
    /// <summary>Unrecognised. Ignored.</summary>
    Unknown = 0,

    /// <summary>The build can read and apply appearance metadata for its own device or browser.</summary>
    ThemeMetadata
}

/// <summary>
/// The capability ordering the §5.3 minimum needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> §5.2 lists availability most-capable-first, so <c>Present</c> is the
/// zero member. A naive ordinal <c>min</c> would pick <c>Present</c> as the lowest and invert the
/// whole rule. The planned member order is kept exactly as written and the ordering lives here.
/// </para>
/// <para>
/// <b>The one judgement in it.</b> §5.3 does not rank the three absent members against each other.
/// This ranks <c>AbsentByDesign</c> below <c>AbsentUnimplemented</c>, so a capability the product
/// has decided against keeps that answer even when the stage would also have said "not yet" —
/// "we do not do that" is permanent and true, where "not built yet" implies a plan that does not
/// exist. <c>Unknown</c> ranks lowest of all, which means the minimum returns it whenever it
/// appears: an undeterminable input <i>dominates</i> a definite one rather than yielding to it.
/// That is the intended direction. The minimum answers "what may we do", so the least capable
/// input must win, and an input we could not read is the least capable thing there is — the result
/// is fail-closed and nothing is granted on the strength of a value nobody could interpret.
/// <b>Derived, not planned. Flagged for Simon.</b>
/// </para>
/// </remarks>
public static class CoachCapabilityAvailabilityRank
{
    /// <summary>Higher is more capable.</summary>
    public static int Of(CoachCapabilityAvailability value) => value switch
    {
        CoachCapabilityAvailability.Present => 4,
        CoachCapabilityAvailability.PresentOnAnotherSurface => 3,
        CoachCapabilityAvailability.AbsentUnimplemented => 2,
        CoachCapabilityAvailability.AbsentByDesign => 1,
        CoachCapabilityAvailability.Unknown => 0,
        _ => 0
    };

    /// <summary>The less capable of two availabilities. The <c>min</c> of plan §5.3.</summary>
    public static CoachCapabilityAvailability Min(
        CoachCapabilityAvailability left,
        CoachCapabilityAvailability right) => Of(left) <= Of(right) ? left : right;
}
