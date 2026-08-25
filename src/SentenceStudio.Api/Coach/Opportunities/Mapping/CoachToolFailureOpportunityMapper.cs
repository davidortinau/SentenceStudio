using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Coach.Opportunities.Mapping;

/// <summary>
/// Turns one tool-boundary failure into a ledger signal, or into nothing.
/// </summary>
/// <remarks>
/// <para>
/// Exhaustive over <see cref="CoachToolFailureKind"/>, with no silently-recording default arm.
/// A new member with no case here falls into <see cref="ToolFailureDisposition.Unmapped"/> and
/// <c>CoachOpportunityTriggerMappingTests</c> fails the build.
/// </para>
/// <para>
/// <b><see cref="CoachToolFailureKind.Unauthorized"/> is never recorded.</b> That is a security
/// event, and the tool boundary is the one place a cross-tenant probe reaches: writing an
/// inspectable row for it would build the attacker a receipt.
/// </para>
/// </remarks>
public static class CoachToolFailureOpportunityMapper
{
    /// <summary>The declared disposition of every tool failure kind.</summary>
    public enum ToolFailureDisposition
    {
        /// <summary>Individually reviewable.</summary>
        Product = 0,

        /// <summary>Counted only.</summary>
        AggregateOnly = 1,

        /// <summary>Never recorded.</summary>
        Never = 2,

        /// <summary>No case exists yet. The build fails rather than guessing.</summary>
        Unmapped = 3
    }

    /// <summary>What the ledger does with a given tool failure kind.</summary>
    public static ToolFailureDisposition DispositionFor(CoachToolFailureKind kind) => kind switch
    {
        // A security event. Recording it would turn a refusal into an artifact somebody can read
        // back, which is exactly what the cross-tenant tests exist to prevent.
        CoachToolFailureKind.Unauthorized => ToolFailureDisposition.Never,

        // The learner asked for something the planner could not satisfy. That is a product
        // signal about constraints, not a fault.
        CoachToolFailureKind.NoFeasiblePlan => ToolFailureDisposition.Product,

        // The turn ran out of tool calls with the learner's question unanswered.
        CoachToolFailureKind.BudgetExhausted => ToolFailureDisposition.Product,

        // A refused argument is usually the model reaching for a capability the tool does not
        // expose — the closed preference allow-list arrives here.
        CoachToolFailureKind.InvalidArgument => ToolFailureDisposition.Product,

        // Operational. Counted so a spike is visible; not individually reviewable, because the
        // fix is in the data layer and the conversation adds nothing.
        CoachToolFailureKind.DataAccess => ToolFailureDisposition.AggregateOnly,
        CoachToolFailureKind.ProfileMissing => ToolFailureDisposition.AggregateOnly,

        _ => ToolFailureDisposition.Unmapped
    };

    /// <summary>
    /// Maps one tool failure to a signal, or returns null when nothing should be recorded.
    /// </summary>
    /// <param name="kind">The failure kind the tool raised.</param>
    /// <param name="toolName">
    /// The registered tool name. Taken from the registration held at tool-build time, never from
    /// anything the model supplied.
    /// </param>
    /// <param name="conversationId">The conversation, used only for Product rows.</param>
    /// <param name="turnId">The turn identity, used only for Product rows.</param>
    /// <param name="settingName">
    /// The preference setting the call named, when the tool was
    /// <c>propose_preference_change</c>. Collapsed to the unknown bucket unless it is a
    /// server-owned candidate.
    /// </param>
    public static CoachOpportunitySignal? Map(
        CoachToolFailureKind kind,
        string? toolName,
        string? conversationId,
        string? turnId,
        string? settingName = null)
    {
        var disposition = DispositionFor(kind);
        if (disposition is ToolFailureDisposition.Never or ToolFailureDisposition.Unmapped)
        {
            return null;
        }

        var (opportunityKind, capability) = Classify(kind, toolName, settingName);
        var isProduct = disposition == ToolFailureDisposition.Product;

        var signal = new CoachOpportunitySignal(
            opportunityKind,
            capability,
            CoachOpportunitySurface.ToolInvocation,
            isProduct ? CoachOpportunityDisposition.Product : CoachOpportunityDisposition.AggregateOnly,
            OfferLink: CoachOpportunityOfferLink.None,
            ToolName: toolName,
            FailureCode: CoachOpportunityFailureCodes.ForToolFailure(kind));

        return isProduct
            ? signal with
            {
                Evidence = new CoachOpportunityEvidencePointer(ConversationId: conversationId),
                TurnId = turnId
            }
            : signal;
    }

    private static (CoachOpportunityKind Kind, string Capability) Classify(
        CoachToolFailureKind kind,
        string? toolName,
        string? settingName)
    {
        if (kind == CoachToolFailureKind.InvalidArgument
            && string.Equals(toolName, CoachToolNames.ProposePreferenceChange, StringComparison.Ordinal))
        {
            // Two different refusals arrive here and they are not the same product question.
            // With the allow-list closed (RFC 6.5), every candidate setting is refused before the
            // profile is even loaded — that is a policy decision waiting to be made. A name that
            // is not a candidate at all is the model reaching for a setting that does not exist.
            var capability = CoachOpportunityCapabilityCodes.ForPreferenceSetting(settingName);
            var kindForPreference =
                string.Equals(
                    capability,
                    CoachOpportunityCapabilityCodes.PreferenceSettingUnknown,
                    StringComparison.Ordinal)
                    ? CoachOpportunityKind.UnsupportedCapability
                    : CoachOpportunityKind.ProposalRefusedByPolicy;

            return (kindForPreference, capability);
        }

        return kind switch
        {
            CoachToolFailureKind.NoFeasiblePlan =>
                (CoachOpportunityKind.ToolExecutionFailure,
                 CoachOpportunityCapabilityCodes.NoFeasiblePlan),

            CoachToolFailureKind.BudgetExhausted =>
                (CoachOpportunityKind.CapacityOrBudgetRefusal,
                 CoachOpportunityCapabilityCodes.ToolCallBudgetExhausted),

            CoachToolFailureKind.DataAccess =>
                (CoachOpportunityKind.ToolExecutionFailure,
                 CoachOpportunityCapabilityCodes.ToolDataAccess),

            CoachToolFailureKind.ProfileMissing =>
                (CoachOpportunityKind.ToolExecutionFailure,
                 CoachOpportunityCapabilityCodes.ToolProfileMissing),

            // InvalidArgument on any tool other than the preference tool.
            _ =>
                (CoachOpportunityKind.ValidationFailure,
                 CoachOpportunityCapabilityCodes.WriteArgumentsInvalid)
        };
    }
}
