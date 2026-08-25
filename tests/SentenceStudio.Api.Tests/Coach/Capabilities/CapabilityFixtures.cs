using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Capabilities;

/// <summary>
/// Shared synthetic fixtures. Every one is a legal §5.4 row; a failing fixture breaks exactly one
/// cell, so a failure names the rule it broke rather than a soup of them.
/// </summary>
internal static class CapabilityFixtures
{
    public static CoachOptions AllToolsEnabled() => new()
    {
        DurableHistory = new CoachFeatureSwitch { Enabled = true },
        SamOverlay = new CoachFeatureSwitch { Enabled = true },
        SamReadTools = new CoachFeatureSwitch { Enabled = true },
        SamWriteTools = new CoachFeatureSwitch { Enabled = true }
    };

    public static ICoachToolRegistry FrozenRegistry() =>
        CoachToolServiceCollectionExtensions.BuildValidatedRegistry(AllToolsEnabled());

    public static CoachCapabilityManifest ShippedManifest() => new(FrozenRegistry());

    public static CoachCapabilityManifest ManifestWith(params CoachCapabilityDescriptor[] declarations) =>
        new(FrozenRegistry(), declarations);

    /// <summary>The §5.4 <c>Read</c> row.</summary>
    public static CoachCapabilityDescriptor LegalRead(string name = "synthetic_read") => new()
    {
        Name = name,
        IsToolBacked = true,
        EffectClass = CoachCapabilityEffectClass.Read,
        Surface = CoachCapabilitySurface.Server,
        MaxAvailability = CoachCapabilityAvailability.Present,
        RequiredStage = CoachCapabilityStage.Read,
        Reversal = CoachCapabilityReversal.None,
        Confirmation = CoachCapabilityConfirmation.None,
        ReceiptKind = CoachCapabilityReceiptKind.None,
        Scope = CoachCapabilityScope.Device,
        DeclaredStepCount = 1,
        RiskClass = CoachToolRiskClass.Read
    };

    /// <summary>The §5.4 <c>PresentationState</c> row.</summary>
    public static CoachCapabilityDescriptor LegalPresentationState(string name = "synthetic_presentation") => new()
    {
        Name = name,
        IsToolBacked = true,
        EffectClass = CoachCapabilityEffectClass.PresentationState,
        Surface = CoachCapabilitySurface.Client,
        MaxAvailability = CoachCapabilityAvailability.Present,
        RequiredStage = CoachCapabilityStage.Presentation,
        Reversal = CoachCapabilityReversal.ClientRevert,
        Confirmation = CoachCapabilityConfirmation.Gesture,
        ReceiptKind = CoachCapabilityReceiptKind.Client,
        Scope = CoachCapabilityScope.Device,
        DeclaredStepCount = 1,
        RiskClass = CoachToolRiskClass.Read,
        ClientCapabilityCode = CoachClientCapabilityCode.ThemeMetadata
    };

    /// <summary>The §5.4 <c>LearnerData</c> row.</summary>
    public static CoachCapabilityDescriptor LegalLearnerData(string name = "synthetic_learner_data") => new()
    {
        Name = name,
        IsToolBacked = true,
        EffectClass = CoachCapabilityEffectClass.LearnerData,
        Surface = CoachCapabilitySurface.Server,
        MaxAvailability = CoachCapabilityAvailability.Present,
        RequiredStage = CoachCapabilityStage.Semantic,
        Reversal = CoachCapabilityReversal.LedgerUndo,
        Confirmation = CoachCapabilityConfirmation.Accept,
        ReceiptKind = CoachCapabilityReceiptKind.Ledger,
        Scope = CoachCapabilityScope.Account,
        DeclaredStepCount = 1,
        RiskClass = CoachToolRiskClass.WriteSoft
    };

    /// <summary>The §5.4 <c>CompositeReversiblePair</c> row.</summary>
    public static CoachCapabilityDescriptor LegalCompositePair(string name = "synthetic_pair") =>
        LegalLearnerData(name) with
        {
            EffectClass = CoachCapabilityEffectClass.CompositeReversiblePair,
            DeclaredStepCount = 2
        };

    /// <summary>The §5.4 <c>ExternalEffect</c> row.</summary>
    public static CoachCapabilityDescriptor LegalExternalEffect(string name = "synthetic_external") =>
        LegalLearnerData(name) with
        {
            EffectClass = CoachCapabilityEffectClass.ExternalEffect,
            Surface = CoachCapabilitySurface.External,
            RequiredStage = CoachCapabilityStage.External,
            Reversal = CoachCapabilityReversal.None,
            Confirmation = CoachCapabilityConfirmation.Confirm,
            RiskClass = CoachToolRiskClass.WriteHard
        };

    /// <summary>The §5.4 <c>ActivityLaunch</c> row.</summary>
    public static CoachCapabilityDescriptor LegalActivityLaunch(string name = "synthetic_launch") =>
        LegalRead(name) with
        {
            EffectClass = CoachCapabilityEffectClass.ActivityLaunch,
            Surface = CoachCapabilitySurface.Client,
            RequiredStage = CoachCapabilityStage.Launch,
            Reversal = CoachCapabilityReversal.ServerDiscard,
            Confirmation = CoachCapabilityConfirmation.Gesture,
            ReceiptKind = CoachCapabilityReceiptKind.Client,
            Scope = CoachCapabilityScope.Session
        };

    /// <summary>One legal row per effect class, so a sweep can cover the whole table.</summary>
    public static IReadOnlyList<CoachCapabilityDescriptor> OneLegalRowPerEffectClass() =>
    [
        LegalRead(), LegalPresentationState(), LegalLearnerData(),
        LegalCompositePair(), LegalExternalEffect(), LegalActivityLaunch()
    ];

    public static CoachClientCapabilityHandshake Handshake(
        int version = CoachClientCapabilityHandshake.MinimumSupportedVersion,
        params CoachClientCapabilityCode[] codes) =>
        new() { Version = version, Codes = codes };
}
