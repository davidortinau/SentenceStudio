using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Capabilities;

/// <summary>Computes effective availability for a turn. Plan §5.3.</summary>
public interface ICoachCapabilityResolver
{
    /// <summary>
    /// <c>min(MaxAvailability, availability permitted by the current stage, availability permitted
    /// by the client handshake)</c>.
    /// </summary>
    CoachCapabilityAvailability Resolve(
        string name,
        CoachCapabilityStage currentStage,
        CoachClientCapabilityHandshake? handshake);
}

/// <summary>
/// The §5.3 minimum, written as three ceilings and one <c>min</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Availability is never stored.</b> Nothing here writes back to the descriptor or the registry;
/// the answer is computed per turn from the declared ceiling plus two runtime facts. That is why
/// there is no <c>Availability</c> field anywhere in §5.2.
/// </para>
/// <para>
/// Written literally so reading the method is enough to see that no path can raise an answer. The
/// ordering comes from <see cref="CoachCapabilityAvailabilityRank"/>, because §5.2 lists
/// availability most-capable-first and an ordinal comparison would invert the rule.
/// </para>
/// </remarks>
public sealed class CoachCapabilityResolver : ICoachCapabilityResolver
{
    private readonly ICoachCapabilityManifest _manifest;

    public CoachCapabilityResolver(ICoachCapabilityManifest manifest)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
    }

    /// <inheritdoc />
    public CoachCapabilityAvailability Resolve(
        string name,
        CoachCapabilityStage currentStage,
        CoachClientCapabilityHandshake? handshake)
    {
        var descriptor = _manifest.Find(name);
        if (descriptor is null)
        {
            // A model naming a capability this build does not declare is ordinary, not
            // exceptional. Answer absent; never fail the turn.
            return CoachCapabilityAvailability.AbsentUnimplemented;
        }

        return CoachCapabilityAvailabilityRank.Min(
            descriptor.MaxAvailability,
            CoachCapabilityAvailabilityRank.Min(
                StageCeiling(descriptor, currentStage),
                HandshakeCeiling(descriptor, handshake)));
    }

    /// <summary>
    /// §5.3 rule 1 — a capability whose <c>RequiredStage</c> exceeds the promoted stage never
    /// resolves to <c>Present</c>.
    /// </summary>
    /// <remarks>
    /// It resolves to <c>AbsentUnimplemented</c>, or to <c>PresentOnAnotherSurface</c> when the app
    /// ships the operation on a screen — which is what a client surface means here.
    /// </remarks>
    private static CoachCapabilityAvailability StageCeiling(
        CoachCapabilityDescriptor descriptor,
        CoachCapabilityStage currentStage)
    {
        if (currentStage >= descriptor.RequiredStage)
        {
            return CoachCapabilityAvailability.Present;
        }

        return descriptor.Surface == CoachCapabilitySurface.Client
            ? CoachCapabilityAvailability.PresentOnAnotherSurface
            : CoachCapabilityAvailability.AbsentUnimplemented;
    }

    /// <summary>
    /// §5.3 rule 2 — a client-surface capability never resolves to <c>Present</c> when the
    /// handshake does not advertise it.
    /// </summary>
    /// <remarks>
    /// §5.5 bounds this to reversible presentation state. A client-surface capability that is not
    /// presentation state is capped at <c>PresentOnAnotherSurface</c> no matter what the handshake
    /// claims, so a client's statement about its own rendering can never authorize a learner-data
    /// write, an external effect, or a launch.
    /// </remarks>
    private static CoachCapabilityAvailability HandshakeCeiling(
        CoachCapabilityDescriptor descriptor,
        CoachClientCapabilityHandshake? handshake)
    {
        if (descriptor.Surface != CoachCapabilitySurface.Client)
        {
            return CoachCapabilityAvailability.Present;
        }

        if (!descriptor.IsHandshakeAuthorizable)
        {
            return CoachCapabilityAvailability.PresentOnAnotherSurface;
        }

        if (handshake is null || !handshake.IsUsable)
        {
            return CoachCapabilityAvailability.PresentOnAnotherSurface;
        }

        foreach (var code in handshake.Codes)
        {
            if (code != CoachClientCapabilityCode.Unknown
                && code == descriptor.ClientCapabilityCode)
            {
                return CoachCapabilityAvailability.Present;
            }
        }

        return CoachCapabilityAvailability.PresentOnAnotherSurface;
    }
}
