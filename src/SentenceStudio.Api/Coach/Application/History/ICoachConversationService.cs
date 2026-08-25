using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Application.History;

/// <summary>
/// The durable coach conversation surface: permanent threads, a canonical encrypted message
/// ledger, and turn operations that survive a restart.
/// </summary>
/// <remarks>
/// <para>
/// This sits <em>above</em> <see cref="ICoachSessionService"/>, never beside it. The session
/// service remains the only writer of plan state and the only place an agent intent is validated;
/// this service decides what is durable, what a retry means, and what the learner can read back.
/// </para>
/// <para>
/// Every method is owner-scoped from the trusted user scope. A conversation belonging to another
/// learner is indistinguishable from one that does not exist.
/// </para>
/// </remarks>
public interface ICoachConversationService
{
    /// <summary>True when durable history is switched on for this host.</summary>
    bool IsEnabled { get; }

    /// <summary>Creates a conversation, or replays the one this idempotency key already created.</summary>
    Task<CoachOperationResult<CoachConversationDto>> CreateAsync(
        StartCoachConversationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the learner's conversations, newest first.</summary>
    Task<CoachOperationResult<CoachConversationPageDto>> ListAsync(
        int? pageSize,
        string? cursor,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one conversation's metadata.</summary>
    Task<CoachOperationResult<CoachConversationDto>> GetAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a page of messages, newest page by default.</summary>
    Task<CoachOperationResult<CoachMessagePageDto>> GetMessagesAsync(
        string conversationId,
        int? pageSize,
        string? before,
        CancellationToken cancellationToken = default);

    /// <summary>Renames a conversation, closes its checkpoint, or both.</summary>
    Task<CoachOperationResult<CoachConversationDto>> UpdateAsync(
        string conversationId,
        UpdateCoachConversationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Runs one durable, idempotent turn.</summary>
    Task<CoachOperationResult<CoachTurnOperationDto>> SubmitTurnAsync(
        string conversationId,
        CoachConversationTurnRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one turn operation, including its replayable result.</summary>
    Task<CoachOperationResult<CoachTurnOperationDto>> GetOperationAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>Requests cancellation durably and signals the local run registry.</summary>
    Task<CoachOperationResult<CoachTurnOperationDto>> CancelOperationAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>Hides a conversation immediately, then purges it. Idempotent.</summary>
    Task<CoachOperationResult<bool>> DeleteAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a streaming export of one owned conversation.</summary>
    Task<CoachOperationResult<CoachConversationExport>> OpenExportAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one deterministic decision from a compatibility <c>/sessions</c> route inside the
    /// durable envelope, and returns the legacy turn shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Accept, reject, and undo write plan state and produce a receipt. Run outside the envelope
    /// they leave the plan changed and the ledger silent, so the learner's own history disagrees
    /// with their plan — the same class of loss as a turn that appends nothing, just quieter.
    /// </para>
    /// <para>
    /// It returns <see cref="CoachTurnResponse"/> rather than an operation, because the old
    /// routes' response shape is fixed and this exists to keep it working unchanged. The durable
    /// operation is still written; it is simply not what the old client is handed.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Cancels whatever turn is running in a conversation, for callers that have no operation id.
    /// </summary>
    /// <remarks>
    /// The compatibility <c>/sessions/{id}/cancel</c> route is the only such caller: it predates
    /// durable operations and carries a session id alone. Returns false when there was nothing to
    /// cancel, which is not an error — a learner tapping cancel just after a turn finished has not
    /// done anything wrong.
    /// </remarks>
    Task<CoachOperationResult<bool>> CancelActiveTurnAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<CoachOperationResult<CoachTurnResponse>> RunCompatibilityDecisionAsync(
        string conversationId,
        CoachCompatibilityDecision decision,
        CancellationToken cancellationToken = default);
}
