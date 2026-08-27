namespace SentenceStudio.Api.Coach.Operations;

/// <summary>
/// The closed vocabulary of refusal reasons the write audit may record.
/// </summary>
/// <remarks>
/// Codes are constants rather than free text so that an audit row can never accidentally carry a
/// message built from learner input, a model completion, or an exception. A refusal that does not
/// fit one of these should get a new constant here, reviewed like any other schema change, rather
/// than a string literal at the call site.
/// </remarks>
public static class CoachWriteFailureCodes
{
    /// <summary>No authenticated learner identity was present.</summary>
    public const string NoIdentity = "no_identity";

    /// <summary>The operation id does not exist, or does not belong to this learner.</summary>
    public const string OperationNotFound = "operation_not_found";

    /// <summary>The operation belongs to a different conversation than the one on the route.</summary>
    public const string ConversationMismatch = "conversation_mismatch";

    /// <summary>The proposal window elapsed before the learner answered.</summary>
    public const string ProposalExpired = "proposal_expired";

    /// <summary>The operation is no longer in a state that accepts this transition.</summary>
    public const string InvalidState = "invalid_state";

    /// <summary>A protected operation was accepted without presenting a confirmation secret.</summary>
    public const string ConfirmationRequired = "confirmation_required";

    /// <summary>The presented confirmation secret did not match the stored binding.</summary>
    public const string ConfirmationMismatch = "confirmation_mismatch";

    /// <summary>The confirmation secret was already redeemed.</summary>
    public const string ConfirmationConsumed = "confirmation_consumed";

    /// <summary>The confirmation window elapsed.</summary>
    public const string ConfirmationExpired = "confirmation_expired";

    /// <summary>A soft proposal was sent to the protected confirmation route, or the reverse.</summary>
    public const string WrongAcceptanceChannel = "wrong_acceptance_channel";

    /// <summary>The operation is not reversible and undo was requested anyway.</summary>
    public const string NotReversible = "not_reversible";

    /// <summary>The undo window elapsed.</summary>
    public const string UndoExpired = "undo_expired";

    /// <summary>The operation was already reversed.</summary>
    public const string UndoConsumed = "undo_consumed";

    /// <summary>The stored undo payload could not be read — usually an older payload schema.</summary>
    public const string UndoUnavailable = "undo_unavailable";

    /// <summary>Another writer changed the operation concurrently.</summary>
    public const string ConcurrencyConflict = "concurrency_conflict";

    /// <summary>The referenced entity is not owned by this learner, or no longer exists.</summary>
    public const string EntityNotOwned = "entity_not_owned";

    /// <summary>The entity the operation targeted has been removed since the proposal.</summary>
    public const string EntityMissing = "entity_missing";

    /// <summary>The arguments failed validation for the tool's declared shape.</summary>
    public const string InvalidArguments = "invalid_arguments";

    /// <summary>The learner already has this many unanswered proposals in the turn.</summary>
    public const string ProposalBudgetExhausted = "proposal_budget_exhausted";

    /// <summary>The tool named on the operation is no longer registered or enabled.</summary>
    public const string ToolUnavailable = "tool_unavailable";

    /// <summary>The write itself failed inside the application service.</summary>
    public const string ExecutionFailed = "execution_failed";

    /// <summary>Another approval took the execution claim first, so this one did not run.</summary>
    public const string ClaimLost = "claim_lost";

    /// <summary>
    /// The operation holds an execution claim whose outcome was never recorded. It is neither
    /// retried nor reported as done.
    /// </summary>
    public const string ExecutionInDoubt = "execution_in_doubt";

    /// <summary>The domain write completed but the ledger could not record its receipt.</summary>
    public const string ReceiptNotRecorded = "receipt_not_recorded";
}
