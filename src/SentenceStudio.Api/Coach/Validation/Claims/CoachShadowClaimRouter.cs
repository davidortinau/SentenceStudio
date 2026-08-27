using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Validation.Claims;

/// <summary>
/// An optional pre-classifier that labels a turn before the rules run. Plan D4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shadow only, and removable at any time.</b> D4 is explicit on both counts. The router's whole
/// value is that it might one day let the engine skip rules that cannot apply — but a router that
/// gates rule execution is a router that can silence a rule by mislabelling a turn, and the model
/// is the thing being audited.
/// </para>
/// <para>
/// So the label is produced, recorded, and never read by a rule. Plan B5 states the invariant from
/// the other side: honesty rules trigger on claim shape and on the trace, never on a router label.
/// <see cref="CoachClaimRuleContext"/> has no member a label could occupy, which is what turns B5
/// from a convention into a compile-time fact.
/// </para>
/// <para>
/// <b>The test that matters is the equivalence test.</b> Every rule must fire identically with the
/// router present and absent. If that test ever fails, the router has acquired influence it was
/// never granted, and the correct response is deletion rather than repair — this whole file is
/// designed to be removable in one commit.
/// </para>
/// </remarks>
public interface ICoachShadowClaimRouter
{
    /// <summary>Labels a turn. The label is recorded and never consulted by a rule.</summary>
    CoachShadowRouteLabel Classify(CoachClaimRuleContext context);
}

/// <summary>
/// What the shadow router thinks a turn was. Telemetry only.
/// </summary>
/// <remarks>
/// Deliberately coarse. A finer taxonomy would invite somebody to route on it, and the point of a
/// shadow label is that it is not load-bearing.
/// </remarks>
/// <remarks>
/// String-serialized because W9 R0 persists this label in the protected turn outcome. An ordinal
/// in a stored payload is coupled to declaration order, so inserting a label would silently
/// reinterpret every stored summary as a different one.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoachShadowRouteLabel
{
    /// <summary>Not classified.</summary>
    Unknown = 0,

    /// <summary>The answer appears to be about the learner's own data.</summary>
    LearnerState = 1,

    /// <summary>The answer appears to be teaching material.</summary>
    Instructional = 2,

    /// <summary>The answer appears to propose an action.</summary>
    CapabilityProposal = 3,

    /// <summary>The answer appears to state a boundary.</summary>
    Limitation = 4
}

/// <summary>
/// The shipped shadow router.
/// </summary>
/// <remarks>
/// Written from the same primitives the rules use, so its label is at least consistent with what
/// the rules see. It still has no path into a rule.
/// </remarks>
public sealed class CoachShadowClaimRouter : ICoachShadowClaimRouter
{
    /// <inheritdoc />
    public CoachShadowRouteLabel Classify(CoachClaimRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ProposedCapabilities.Count > 0)
        {
            return CoachShadowRouteLabel.CapabilityProposal;
        }

        var spans = context.Spans;

        if (spans.Count == 0)
        {
            return CoachShadowRouteLabel.Unknown;
        }

        if (spans.Any(span => CoachLearnerStateReferent.IsLearnerStateClaim(span.Text)))
        {
            return CoachShadowRouteLabel.LearnerState;
        }

        // A teaching answer is the residual, not a positive match. Claiming to recognise
        // instruction would be the router pretending to more certainty than it has.
        return context.Answer?.Topic is CoachAnswerTopic.Grammar
            or CoachAnswerTopic.Usage
            or CoachAnswerTopic.Pronunciation
            or CoachAnswerTopic.Vocabulary
            ? CoachShadowRouteLabel.Instructional
            : CoachShadowRouteLabel.Unknown;
    }
}
