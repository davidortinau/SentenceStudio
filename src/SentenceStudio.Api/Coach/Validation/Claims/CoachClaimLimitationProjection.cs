using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>
/// Turns a capability finding into the typed boundary W7 defined, instead of a sentence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a DTO rather than prose.</b> Plan B11: a limitation answer names a real screen, and the
/// counts live in <see cref="CoachLimitationDto"/> rather than in <c>CoachDeterministicCopy</c>. The
/// engine's span substitution is a sentence and a sentence cannot name a screen without either
/// hard-coding an English label on the server or inventing a route. This projection produces the
/// closed code and the typed destination; the client renders the screen's name in the learner's own
/// language, which is the only place that name is correct.
/// </para>
/// <para>
/// <b>It never invents a route.</b> <see cref="CoachRouteCatalog"/> is the whole route space and a
/// capability descriptor does not yet declare which screen it lives on — that mapping arrives with
/// the action-card work, not here. So a capability that resolves
/// <see cref="CoachCapabilityAvailability.PresentOnAnotherSurface"/> without a declared route
/// produces <see cref="CoachLimitationCode.AvailableOnAnotherSurface"/> and a null destination,
/// which is the truth: the app does it somewhere and this build cannot say where. Filling in a
/// plausible screen would be the fluent-invention failure the whole grounding layer exists to stop.
/// </para>
/// </remarks>
public static class CoachClaimLimitationProjection
{
    /// <summary>
    /// The limitation for the first capability finding on a turn, or null when there is none.
    /// </summary>
    /// <param name="findings">The turn's findings.</param>
    /// <param name="proposedCapabilities">What the turn proposed, in the order it proposed it.</param>
    /// <param name="resolver">The manifest resolver.</param>
    /// <param name="stage">The promoted capability stage.</param>
    /// <param name="handshake">The client's merged handshake for this turn.</param>
    public static CoachLimitationDto? Project(
        IReadOnlyList<CoachClaimFinding> findings,
        IReadOnlyList<string> proposedCapabilities,
        ICoachCapabilityResolver resolver,
        CoachCapabilityStage stage,
        CoachClientCapabilityHandshake? handshake)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(proposedCapabilities);
        ArgumentNullException.ThrowIfNull(resolver);

        var capabilityFinding = findings.FirstOrDefault(finding =>
            finding.Rule is CoachClaimRuleCode.CapabilityAbsent or CoachClaimRuleCode.FalseLimitation);

        if (capabilityFinding is null || proposedCapabilities.Count == 0)
        {
            return null;
        }

        // The first proposal whose resolved availability explains the finding. Taking the first
        // proposal unconditionally would attribute the boundary to whichever capability happened to
        // be named first, which is a different claim than the one the rule made.
        foreach (var capability in proposedCapabilities)
        {
            var availability = resolver.Resolve(capability, stage, handshake);

            var code = Describe(capabilityFinding.Rule, availability);
            if (code is not { } limitationCode)
            {
                continue;
            }

            return new CoachLimitationDto
            {
                Code = limitationCode,

                // No coverage claim: a capability boundary is not a statement about how much of the
                // learner's data was read, and Unknown is the member that says so.
                Coverage = CoachEvidenceCoverage.Unknown,

                // Null on purpose. See the remark: no capability declares a route yet, and a
                // destination this build cannot derive is a destination it must not state.
                Destination = null
            };
        }

        return null;
    }

    /// <summary>
    /// The limitation code a resolved availability justifies, or null when it justifies none.
    /// </summary>
    /// <remarks>
    /// An explicit switch per rule rather than a default arm, so a tenth rule falls out of both and
    /// the census test fails instead of the projection quietly returning null for it.
    /// </remarks>
    private static CoachLimitationCode? Describe(
        CoachClaimRuleCode rule,
        CoachCapabilityAvailability availability) => rule switch
    {
        // Over-claiming. The answer proposed something the manifest will not grant.
        CoachClaimRuleCode.CapabilityAbsent => availability switch
        {
            CoachCapabilityAvailability.PresentOnAnotherSurface =>
                CoachLimitationCode.AvailableOnAnotherSurface,
            CoachCapabilityAvailability.AbsentByDesign => CoachLimitationCode.RefusedByDesign,
            CoachCapabilityAvailability.AbsentUnimplemented => CoachLimitationCode.NotBuilt,

            // Unknown is undeterminable, not absent, and Present contradicts the finding. Neither
            // is a boundary this build can state, so it states none.
            _ => null
        },

        // Under-claiming. The answer refused something the app plainly does, so the honest
        // boundary is the surface it lives on — never "not built".
        CoachClaimRuleCode.FalseLimitation => availability switch
        {
            CoachCapabilityAvailability.PresentOnAnotherSurface =>
                CoachLimitationCode.AvailableOnAnotherSurface,
            _ => null
        },

        _ => null
    };
}
