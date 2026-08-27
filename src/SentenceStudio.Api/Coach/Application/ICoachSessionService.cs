using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// The application-owned Learning Coach state machine.
/// </summary>
/// <remarks>
/// Every plan write in the coach feature goes through this service and nowhere else. The
/// agent returns an intent; this service decides whether that intent is allowed to become a
/// revision, and calls <c>IPlanService</c> to make it.
/// </remarks>
public interface ICoachSessionService
{
    /// <summary>
    /// The correction state the last turn ended with, for the layer that persists it.
    /// </summary>
    /// <remarks>
    /// The server-side state, not the wire DTO: the DTO drops the opened-at instant and the
    /// disputed definition codes, and those are exactly what the next turn's repeat check needs.
    /// Reconstructing the state from the DTO would silently weaken the check to "was something
    /// disputed" from "was this claim, read this way, disputed".
    /// </remarks>
    Persistence.History.CoachTurnDisputeState? CurrentTurnDispute { get; }

    /// <summary>
    /// What the grounding layer did to the last turn, for the layer that persists it.
    /// </summary>
    /// <remarks>
    /// Null when the ladder did not run — an Off deployment produces no record, and writing an
    /// all-zero summary instead would read as "the layer looked and found nothing", which is a
    /// stronger claim than the truth.
    /// </remarks>
    Validation.Claims.CoachGroundingTurnSummary? CurrentTurnGrounding { get; }

    /// <summary>
    /// Answers whether the learner can open the coach. Never resolves a chat client and never
    /// builds an agent, so a host with no AI configuration still answers this correctly.
    /// </summary>
    Task<CoachOperationResult<CoachAvailabilityResponse>> GetAvailabilityAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts a session, or resumes the learner's active one when asked to.</summary>
    Task<CoachOperationResult<CoachSessionResponse>> StartSessionAsync(
        StartCoachSessionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads an owned session and the current plan canvas state.</summary>
    Task<CoachOperationResult<CoachSessionResponse>> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Submits text, a chip, or a structured constraint action.</summary>
    Task<CoachOperationResult<CoachTurnResponse>> SubmitTurnAsync(
        string sessionId,
        CoachTurnRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a turn on behalf of the durable-history layer, which supplies the rebuild context
    /// and owns idempotency itself.
    /// </summary>
    /// <remarks>
    /// Same reducer, same write authority, same validation. The only difference is where the
    /// conversation context came from and which store answers a retry.
    /// </remarks>
    Task<CoachOperationResult<CoachTurnResponse>> SubmitTurnAsync(
        string sessionId,
        CoachTurnRequest request,
        CoachTurnExecutionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Applies the exact stored delta for a tapped Accept. No model call.</summary>
    Task<CoachOperationResult<CoachTurnResponse>> AcceptSuggestionAsync(
        string sessionId,
        string suggestionId,
        CoachSuggestionDecisionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the pending suggestion for a tapped Not now. No model call, no write.</summary>
    Task<CoachOperationResult<CoachTurnResponse>> RejectSuggestionAsync(
        string sessionId,
        string suggestionId,
        CoachSuggestionDecisionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Undoes the most recent applied, not-yet-undone coach revision.</summary>
    Task<CoachOperationResult<CoachTurnResponse>> UndoAsync(
        string sessionId,
        CoachUndoRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Stops the in-flight run for an owned session.</summary>
    Task<CoachOperationResult<bool>> CancelAsync(string sessionId, CancellationToken cancellationToken = default);
    /// <summary>Deletes conversation state and any pending suggestion. Applied revisions remain.</summary>
    Task<CoachOperationResult<bool>> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the checkpoint with this id, creating one when it is missing, expired, or written
    /// under an incompatible agent configuration.
    /// </summary>
    /// <remarks>
    /// A row that survived but has had its serialized agent session cleared — what memory rotation
    /// does — is returned as-is and reported as rebuilt, so the caller seeds the turn from the
    /// ledger instead of resuming an empty conversation.
    /// </remarks>
    Task<CoachCheckpointState> EnsureCheckpointAsync(
        string checkpointId,
        CoachCheckpointCoverage? required = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records what the checkpoint now covers. Called only after output has been validated and
    /// committed to the ledger, so a rejected turn can never advance coverage.
    /// </summary>
    Task StampCheckpointAsync(
        string checkpointId,
        CoachCheckpointCoverage coverage,
        CancellationToken cancellationToken = default);

    /// <summary>The configuration identity a checkpoint built right now would carry.</summary>
    CoachCheckpointCoverage CheckpointIdentity(string conversationId, long coveredSequence);

    /// <summary>
    /// The plan revision a durable turn operation produced, or null when it produced none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The durable turn saga needs this to answer one question after a crash: did the plan write
    /// land before the process died? The revision audit is the canonical evidence, so recovery
    /// reads it rather than guessing, and never repeats a plan write to find out.
    /// </para>
    /// <para>
    /// Keyed by the operation rather than by time. A time window cannot distinguish this
    /// conversation's revision from a concurrent one against the same plan, and cannot tell a
    /// late retry apart from a turn that changed nothing.
    /// </para>
    /// </remarks>
    Task<CoachPlanRevision?> GetRevisionByOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default);
}

/// <summary>The checkpoint a durable turn will run against.</summary>
/// <param name="Session">The owned session row.</param>
/// <param name="AgentSessionJson">
/// The decrypted agent session to resume, or null when the turn must start a fresh one.
/// </param>
/// <param name="Rebuilt">
/// True when there is no agent memory to resume, which is the signal to seed the turn from the
/// message ledger instead.
/// </param>
/// <remarks>
/// <see cref="Rebuilt"/> tracks the agent's memory, not the database row. A checkpoint that was
/// deleted and recreated has none, and so does a row that survived with
/// <see cref="AgentSessionJson"/> cleared in place — which is exactly what memory rotation does.
/// Both must seed from the ledger, so both report true.
/// </remarks>
/// <param name="PreviousStatus">Why the previous checkpoint was not usable, for telemetry.</param>
public sealed record CoachCheckpointState(
    CoachSession Session,
    string? AgentSessionJson,
    bool Rebuilt,
    CoachSessionLoadStatus PreviousStatus);
