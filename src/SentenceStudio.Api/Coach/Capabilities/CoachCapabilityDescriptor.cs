using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Capabilities;

/// <summary>
/// One capability, as plan §5.2 describes it: the frozen registration extended by the nine
/// capability fields, plus the read metadata for reads.
/// </summary>
/// <remarks>
/// A capability is not always a tool. Every tool projects into one of these; a planned capability
/// with no tool behind it is declared directly and lists at
/// <see cref="CoachCapabilityAvailability.AbsentUnimplemented"/> until its workstream lands and its
/// stage is promoted (§5.3 rule 3).
/// </remarks>
public sealed record CoachCapabilityDescriptor
{
    /// <summary>From the registry at build time, never a model string.</summary>
    public required string Name { get; init; }

    /// <summary>True when a registered tool backs this capability.</summary>
    public required bool IsToolBacked { get; init; }

    // ---- the nine, §5.2 ------------------------------------------------------------------
    public required CoachCapabilityEffectClass EffectClass { get; init; }
    public required CoachCapabilitySurface Surface { get; init; }
    public required CoachCapabilityAvailability MaxAvailability { get; init; }
    public required CoachCapabilityStage RequiredStage { get; init; }
    public required CoachCapabilityReversal Reversal { get; init; }
    public required CoachCapabilityConfirmation Confirmation { get; init; }
    public required CoachCapabilityReceiptKind ReceiptKind { get; init; }
    public required CoachCapabilityScope Scope { get; init; }
    public required int DeclaredStepCount { get; init; }

    /// <summary>The existing ceremony axis, unchanged.</summary>
    public required CoachToolRiskClass RiskClass { get; init; }

    /// <summary>
    /// §5.2 line 160 — read metadata. Null for a capability that is not a read.
    /// </summary>
    /// <remarks>
    /// Sourced from <see cref="CoachReadCapabilityMetadataTable"/>, which transcribes each value
    /// from the tool that emits it. It is a lookup, not a second manifest: nothing resolves
    /// against it.
    /// </remarks>
    public CoachReadCapabilityMetadata? ReadMetadata { get; init; }

    /// <summary>
    /// The handshake code a client must advertise, for client-surface capabilities only.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> on <see cref="CoachToolRegistration"/>. §5.5 grants the handshake
    /// authority over reversible presentation state alone, so putting a handshake field on every
    /// registration would imply that any capability could be unlocked by a client claim. It lives
    /// here, populated only where <see cref="Surface"/> is
    /// <see cref="CoachCapabilitySurface.Client"/>.
    /// </remarks>
    public CoachClientCapabilityCode ClientCapabilityCode { get; init; } = CoachClientCapabilityCode.Unknown;

    /// <summary>
    /// Whether the client handshake may raise this capability's ceiling. §5.5.
    /// </summary>
    /// <remarks>
    /// One expression, one place. Reversible presentation state only: never a learner-data write,
    /// never an external effect, never a launch.
    /// </remarks>
    public bool IsHandshakeAuthorizable =>
        EffectClass == CoachCapabilityEffectClass.PresentationState
        && Surface == CoachCapabilitySurface.Client;

    /// <summary>Projects a frozen registration, attaching read metadata when the class is Read.</summary>
    public static CoachCapabilityDescriptor FromRegistration(CoachToolRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        return new CoachCapabilityDescriptor
        {
            Name = registration.Name,
            IsToolBacked = true,
            EffectClass = registration.EffectClass,
            Surface = registration.Surface,
            MaxAvailability = registration.MaxAvailability,
            RequiredStage = registration.RequiredStage,
            Reversal = registration.Reversal,
            Confirmation = registration.Confirmation,
            ReceiptKind = registration.ReceiptKind,
            Scope = registration.Scope,
            DeclaredStepCount = registration.DeclaredStepCount,
            RiskClass = registration.RiskClass,
            ReadMetadata = registration.EffectClass == CoachCapabilityEffectClass.Read
                ? CoachReadCapabilityMetadataTable.Find(registration.Name)
                : null
        };
    }
}
