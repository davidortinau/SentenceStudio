using System.ComponentModel;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// The risk classification for a coach tool. Determines confirmation requirements.
/// </summary>
public enum CoachToolRiskClass
{
    /// <summary>Read-only, auto-execute. No confirmation needed.</summary>
    [Description("Read-only tool that auto-executes.")]
    Read = 0,

    /// <summary>Write with preview receipt. Learner sees a preview and can undo.</summary>
    [Description("Write-soft tool with preview receipt.")]
    WriteSoft = 1,

    /// <summary>Destructive or external-content write. Two-phase confirm token required.</summary>
    [Description("Write-hard tool requiring two-phase confirmation.")]
    WriteHard = 2
}

/// <summary>
/// The registration record for a single coach tool in the registry.
/// Immutable after construction. Frozen after startup validation.
/// </summary>
public sealed record CoachToolRegistration
{
    /// <summary>The snake_case tool name on the wire.</summary>
    public required string Name { get; init; }

    /// <summary>The C# type returned by the tool. Scanned by the embargo contract.</summary>
    public required Type ResultType { get; init; }

    /// <summary>
    /// Which embargo the tool's <see cref="ResultType"/> is scanned under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declaring the scope on the registration is what makes embargo coverage total. The scanner
    /// no longer consults a hand-kept list of types that someone has to remember to extend; it
    /// walks the frozen registry and scans whatever each tool says it returns, under the scope
    /// that tool declared. Adding a tool therefore cannot skip the scan, because the scan reads
    /// the same record the tool was added with.
    /// </para>
    /// <para>
    /// The default is <see cref="CoachEmbargoScope.ModelVisible"/> — the strict scope — so a tool
    /// that says nothing gets the tightest rules rather than the loosest. Choosing
    /// <see cref="CoachEmbargoScope.ToolResult"/> is a deliberate act that says the envelope
    /// carries learner content the learner explicitly asked for.
    /// </para>
    /// <para>
    /// <see cref="CoachEmbargoScope.PublicClient"/> is refused for tools: that scope describes
    /// shapes the server sends to the authenticated owner over HTTPS and the model never sees.
    /// A tool result is by definition model-visible, so accepting it here would be a category
    /// error that silently relaxed the content rules.
    /// </para>
    /// </remarks>
    public CoachEmbargoScope EmbargoScope { get; init; } = CoachEmbargoScope.ModelVisible;

    /// <summary>Read / WriteSoft / WriteHard.</summary>
    public required CoachToolRiskClass RiskClass { get; init; }

    /// <summary>
    /// Feature switches that must be enabled for this tool to appear.
    /// Empty means the tool is always available when the coach is enabled.
    /// </summary>
    public IReadOnlyList<string> RequiredFeatures { get; init; } = [];

    /// <summary>A human-readable description for the model.</summary>
    public required string Description { get; init; }

    // ===========================================================================================
    // Capability declaration — plan §5.2, exactly the nine planned fields.
    //
    // Additive to the frozen registration; §5.2 is explicit that this extends the registry rather
    // than building a rival manifest. RiskClass above is the existing ceremony axis and is
    // unchanged. Nothing here executes anything: W6 is the first consumer.
    //
    // Availability is NOT here. §5.3 computes it per turn from three inputs and never stores it.
    // Handshake authorization is NOT here either: §5.5 grants it to reversible PresentationState
    // alone, so it is derived from EffectClass and Surface rather than declared per registration.
    // ===========================================================================================

    /// <summary>§5.2 — what this does to the substrate. The second axis beside <see cref="RiskClass"/>.</summary>
    /// <remarks>
    /// Defaults to <see cref="CoachCapabilityEffectClass.Read"/> only because it is the one class
    /// that changes nothing; the §5.4 matrix then holds every other field to that choice, so a
    /// write tool that forgot to declare its class fails startup on the ceremony columns rather
    /// than passing as harmless.
    /// </remarks>
    public CoachCapabilityEffectClass EffectClass { get; init; } = CoachCapabilityEffectClass.Read;

    /// <summary>§5.2 — where it executes.</summary>
    public CoachCapabilitySurface Surface { get; init; } = CoachCapabilitySurface.Server;

    /// <summary>§5.2 — the declared ceiling on availability. Never the answer.</summary>
    /// <remarks>
    /// §5.3: register every planned capability now with
    /// <see cref="CoachCapabilityAvailability.AbsentUnimplemented"/>. A declared absence gives an
    /// honest answer today.
    /// </remarks>
    public CoachCapabilityAvailability MaxAvailability { get; init; } = CoachCapabilityAvailability.Present;

    /// <summary>§5.2 — the <c>Coach:Capabilities:Stage</c> value that permits execution.</summary>
    /// <remarks>
    /// §5.3 rule 1: a capability whose required stage exceeds the promoted stage never resolves to
    /// <see cref="CoachCapabilityAvailability.Present"/>. Rule 3: a capability becomes available
    /// when its workstream lands <i>and</i> its stage is promoted — one field change never ships a
    /// capability.
    /// </remarks>
    public CoachCapabilityStage RequiredStage { get; init; } = CoachCapabilityStage.Read;

    /// <summary>§5.2 — how the effect is taken back.</summary>
    public CoachCapabilityReversal Reversal { get; init; } = CoachCapabilityReversal.None;

    /// <summary>§5.2 — what the learner is asked first.</summary>
    public CoachCapabilityConfirmation Confirmation { get; init; } = CoachCapabilityConfirmation.None;

    /// <summary>§5.2 — what record the effect leaves.</summary>
    public CoachCapabilityReceiptKind ReceiptKind { get; init; } = CoachCapabilityReceiptKind.None;

    /// <summary>§5.2 — how far the effect reaches.</summary>
    public CoachCapabilityScope Scope { get; init; } = CoachCapabilityScope.Device;

    /// <summary>
    /// §5.2 — exactly 2 for <see cref="CoachCapabilityEffectClass.CompositeReversiblePair"/>,
    /// otherwise 1.
    /// </summary>
    /// <remarks>
    /// §5.4's second side assertion reads this: a composite pair must declare two atomic steps —
    /// one preview, one Accept, one ledger receipt, one undo. Declaring the count makes "this is a
    /// pair" checkable at startup instead of a property of whatever the tool happens to do.
    /// </remarks>
    public int DeclaredStepCount { get; init; } = 1;
}
