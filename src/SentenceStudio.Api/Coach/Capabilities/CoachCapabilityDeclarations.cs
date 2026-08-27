using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.Api.Coach.Capabilities;

/// <summary>
/// Planned capabilities with no tool behind them yet. Plan §5.3: "Register every planned capability
/// now with <c>MaxAvailability = AbsentUnimplemented</c>. A declared absence gives an honest answer
/// today."
/// </summary>
public static class CoachCapabilityDeclarations
{
    /// <summary>The capability name for reading and applying appearance metadata.</summary>
    public const string ThemeMetadataCapabilityName = "get_theme_metadata";

    /// <summary>
    /// Appearance metadata for the connected device or browser, declared against the shared
    /// catalogue P1 extracted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Declared absent.</b> <see cref="CoachCapabilityAvailability.AbsentUnimplemented"/> is the
    /// ceiling and <see cref="CoachCapabilityStage.Presentation"/> is the required stage. Both must
    /// move before anything can run: the C1 workstream has to land <i>and</i>
    /// <c>Coach:Capabilities:Stage</c> has to be promoted past <c>Read</c>. §5.3 rule 3 — one field
    /// change never ships a capability.
    /// </para>
    /// <para>
    /// <b>The §5.4 PresentationState row, exactly.</b> <c>ClientRevert</c>, <c>Gesture</c>,
    /// <c>Client</c> receipt, and a scope that is not <c>Account</c> — appearance is a per-device
    /// preference, which is the same decision P1 settled when it put the value in a per-browser
    /// cookie rather than the learner's profile.
    /// </para>
    /// <para>
    /// <b>Declared against, not copied from.</b> <see cref="ThemeCatalog"/> is read-only for this
    /// workstream. This holds no theme id; the startup matrix asserts only that the catalogue is
    /// non-empty and its default resolves, which is the whole of the dependency.
    /// </para>
    /// </remarks>
    public static CoachCapabilityDescriptor ThemeMetadata { get; } = new()
    {
        Name = ThemeMetadataCapabilityName,
        IsToolBacked = false,
        EffectClass = CoachCapabilityEffectClass.PresentationState,
        Surface = CoachCapabilitySurface.Client,
        MaxAvailability = CoachCapabilityAvailability.AbsentUnimplemented,
        RequiredStage = CoachCapabilityStage.Presentation,
        Reversal = CoachCapabilityReversal.ClientRevert,
        Confirmation = CoachCapabilityConfirmation.Gesture,
        ReceiptKind = CoachCapabilityReceiptKind.Client,
        Scope = CoachCapabilityScope.Device,
        DeclaredStepCount = 1,
        RiskClass = CoachToolRiskClass.Read,
        ClientCapabilityCode = CoachClientCapabilityCode.ThemeMetadata
    };

    /// <summary>Every capability declared without a tool, in declaration order.</summary>
    public static IReadOnlyList<CoachCapabilityDescriptor> All { get; } = [ThemeMetadata];
}
