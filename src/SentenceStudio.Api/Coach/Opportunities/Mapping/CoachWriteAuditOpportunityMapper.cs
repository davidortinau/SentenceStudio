using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Coach.Opportunities.Mapping;

/// <summary>
/// Turns one write-ledger refusal into a ledger signal, or into nothing.
/// </summary>
/// <remarks>
/// <para>
/// A pure static function over a closed vocabulary, written as an exhaustive <c>switch</c> with
/// no default arm that silently records. A new <see cref="CoachWriteFailureCodes"/> constant with
/// no case here falls into <see cref="Unmapped"/>, and
/// <c>CoachOpportunityTriggerMappingTests</c> fails the build until somebody has declared what it
/// means. That is the same "cannot drift silently" property <c>CoachWriteOperationStates</c>
/// already relies on.
/// </para>
/// <para>
/// <b>The model never chooses a category.</b> Every value this returns is decided here, on the
/// server, from a code the server itself wrote.
/// </para>
/// </remarks>
public static class CoachWriteAuditOpportunityMapper
{
    /// <summary>
    /// The declared disposition of every write failure code, including the ones that are
    /// deliberately never recorded.
    /// </summary>
    public enum WriteFailureDisposition
    {
        /// <summary>Individually reviewable, with pointers.</summary>
        Product = 0,

        /// <summary>Counted only.</summary>
        AggregateOnly = 1,

        /// <summary>
        /// Counted only, and with every identifier and pointer forced away, because the row
        /// describes a target that did not resolve. Those are the shapes a cross-tenant probe
        /// produces, and an inspectable row would be an existence oracle for another learner's
        /// identifiers.
        /// </summary>
        AggregateOnlyUnlinked = 2,

        /// <summary>Never recorded. A security event, not a product gap.</summary>
        Never = 3,

        /// <summary>No case exists yet. The build fails rather than guessing.</summary>
        Unmapped = 4
    }

    /// <summary>What the ledger does with a given write failure code.</summary>
    public static WriteFailureDisposition DispositionFor(string? failureCode) => failureCode switch
    {
        // --- capability gaps: the learner named something the server would not act on -------
        CoachWriteFailureCodes.EntityNotOwned => WriteFailureDisposition.Product,
        CoachWriteFailureCodes.EntityMissing => WriteFailureDisposition.Product,
        CoachWriteFailureCodes.InvalidArguments => WriteFailureDisposition.Product,
        CoachWriteFailureCodes.ToolUnavailable => WriteFailureDisposition.Product,

        // --- lifecycle: the learner answered, just not in time or not in the right state ----
        CoachWriteFailureCodes.ProposalExpired => WriteFailureDisposition.Product,
        CoachWriteFailureCodes.ConfirmationExpired => WriteFailureDisposition.Product,
        CoachWriteFailureCodes.NotReversible => WriteFailureDisposition.Product,
        CoachWriteFailureCodes.UndoExpired => WriteFailureDisposition.Product,
        CoachWriteFailureCodes.UndoConsumed => WriteFailureDisposition.Product,
        CoachWriteFailureCodes.UndoUnavailable => WriteFailureDisposition.Product,
        CoachWriteFailureCodes.ProposalBudgetExhausted => WriteFailureDisposition.Product,

        // --- protocol errors: correct refusals of a malformed or replayed approval ----------
        CoachWriteFailureCodes.ConfirmationRequired => WriteFailureDisposition.AggregateOnly,
        CoachWriteFailureCodes.ConfirmationConsumed => WriteFailureDisposition.AggregateOnly,
        CoachWriteFailureCodes.ConfirmationMismatch => WriteFailureDisposition.AggregateOnly,
        CoachWriteFailureCodes.WrongAcceptanceChannel => WriteFailureDisposition.AggregateOnly,
        CoachWriteFailureCodes.InvalidState => WriteFailureDisposition.AggregateOnly,
        CoachWriteFailureCodes.ConcurrencyConflict => WriteFailureDisposition.AggregateOnly,
        CoachWriteFailureCodes.ClaimLost => WriteFailureDisposition.AggregateOnly,

        // --- execution faults: CoachWriteAudit already owns the forensics -------------------
        CoachWriteFailureCodes.ExecutionFailed => WriteFailureDisposition.AggregateOnly,
        CoachWriteFailureCodes.ReceiptNotRecorded => WriteFailureDisposition.AggregateOnly,
        CoachWriteFailureCodes.ExecutionInDoubt => WriteFailureDisposition.AggregateOnly,

        // --- unresolved targets: exactly the shape of a cross-tenant probe ------------------
        CoachWriteFailureCodes.OperationNotFound => WriteFailureDisposition.AggregateOnlyUnlinked,
        CoachWriteFailureCodes.ConversationMismatch => WriteFailureDisposition.AggregateOnlyUnlinked,
        CoachWriteFailureCodes.NoIdentity => WriteFailureDisposition.AggregateOnlyUnlinked,

        null or "" => WriteFailureDisposition.Never,
        _ => WriteFailureDisposition.Unmapped
    };

    /// <summary>
    /// Maps one write-ledger refusal to a signal, or returns null when nothing should be
    /// recorded.
    /// </summary>
    /// <param name="failureCode">The closed-vocabulary refusal code the ledger wrote.</param>
    /// <param name="toolName">The registered tool name, when the refusal named one.</param>
    /// <param name="conversationId">The conversation, used only for Product rows.</param>
    /// <param name="turnId">The turn identity, used only for Product rows.</param>
    /// <param name="operationId">The write operation, used only for Product rows.</param>
    /// <param name="settingName">
    /// The preference setting the proposal named, when the tool was
    /// <c>propose_preference_change</c>. Collapsed to the unknown bucket unless it is a
    /// server-owned candidate, so a model-invented name never widens the column's cardinality.
    /// </param>
    public static CoachOpportunitySignal? Map(
        string? failureCode,
        string? toolName,
        string? conversationId,
        string? turnId,
        string? operationId,
        string? settingName = null)
    {
        var disposition = DispositionFor(failureCode);
        if (disposition is WriteFailureDisposition.Never or WriteFailureDisposition.Unmapped)
        {
            return null;
        }

        var (kind, capability) = Classify(failureCode!, toolName, settingName);
        var isProduct = disposition == WriteFailureDisposition.Product;

        var signal = new CoachOpportunitySignal(
            kind,
            capability,
            CoachOpportunitySurface.WriteLedger,
            isProduct ? CoachOpportunityDisposition.Product : CoachOpportunityDisposition.AggregateOnly,
            OfferLink: CoachOpportunityOfferLink.None,
            ToolName: toolName,
            FailureCode: failureCode);

        if (!isProduct)
        {
            // AggregateOnlyUnlinked and AggregateOnly both end up here; the recorder strips
            // pointers from every aggregate-only signal regardless, so the distinction exists to
            // document intent and to be assertable, not to be the only thing standing between an
            // unresolved-target refusal and an inspectable row.
            return signal;
        }

        return signal with
        {
            Evidence = new CoachOpportunityEvidencePointer(ConversationId: conversationId),
            TurnId = turnId,
            WriteOperationId = operationId
        };
    }

    private static (CoachOpportunityKind Kind, string Capability) Classify(
        string failureCode,
        string? toolName,
        string? settingName)
    {
        var isPreferenceTool = string.Equals(
            toolName, CoachToolNames.ProposePreferenceChange, StringComparison.Ordinal);

        return failureCode switch
        {
            CoachWriteFailureCodes.EntityNotOwned or CoachWriteFailureCodes.EntityMissing =>
                (CoachOpportunityKind.UnsupportedCapability,
                 CoachOpportunityCapabilityCodes.EntityLookupByName),

            // A refused preference change is a policy decision waiting to be made, not a bad
            // argument: the allow-list is empty by design (RFC 6.5), so every candidate setting
            // arrives here. Naming the setting is the whole point — "learners keep asking for
            // session_minutes" is the signal Captain needs to decide on.
            CoachWriteFailureCodes.InvalidArguments when isPreferenceTool =>
                (CoachOpportunityKind.ProposalRefusedByPolicy,
                 CoachOpportunityCapabilityCodes.ForPreferenceSetting(settingName)),

            CoachWriteFailureCodes.InvalidArguments =>
                (CoachOpportunityKind.ValidationFailure,
                 CoachOpportunityCapabilityCodes.WriteArgumentsInvalid),

            CoachWriteFailureCodes.ToolUnavailable =>
                (CoachOpportunityKind.ToolUnavailable,
                 CoachOpportunityCapabilityCodes.WriteToolsDisabled),

            CoachWriteFailureCodes.ProposalExpired or CoachWriteFailureCodes.ConfirmationExpired =>
                (CoachOpportunityKind.ConfirmationLifecycleFailure,
                 CoachOpportunityCapabilityCodes.ApprovalWindowElapsed),

            CoachWriteFailureCodes.NotReversible
                or CoachWriteFailureCodes.UndoExpired
                or CoachWriteFailureCodes.UndoConsumed
                or CoachWriteFailureCodes.UndoUnavailable =>
                (CoachOpportunityKind.ConfirmationLifecycleFailure,
                 CoachOpportunityCapabilityCodes.UndoUnavailable),

            CoachWriteFailureCodes.ProposalBudgetExhausted =>
                (CoachOpportunityKind.CapacityOrBudgetRefusal,
                 CoachOpportunityCapabilityCodes.OneProposalPerTurn),

            CoachWriteFailureCodes.ExecutionFailed
                or CoachWriteFailureCodes.ReceiptNotRecorded
                or CoachWriteFailureCodes.ExecutionInDoubt =>
                (CoachOpportunityKind.ToolExecutionFailure,
                 CoachOpportunityCapabilityCodes.WriteExecutionFailed),

            CoachWriteFailureCodes.OperationNotFound
                or CoachWriteFailureCodes.ConversationMismatch
                or CoachWriteFailureCodes.NoIdentity =>
                (CoachOpportunityKind.ConfirmationLifecycleFailure,
                 CoachOpportunityCapabilityCodes.ApprovalTargetUnresolved),

            _ =>
                (CoachOpportunityKind.ConfirmationLifecycleFailure,
                 CoachOpportunityCapabilityCodes.ApprovalProtocolError)
        };
    }
}
