using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;

namespace SentenceStudio.Services.Api;

/// <summary>
/// Client for the authenticated coach API group (<c>/api/v1/coach</c>).
/// </summary>
/// <remarks>
/// <para>
/// Every method maps 1:1 onto an approved coach endpoint. No method invents a route that the
/// implementation plan does not define.
/// </para>
/// <para>
/// Availability: the feature is config- and cohort-gated on the server. When it is off, the whole
/// route group answers 404, so <see cref="GetAvailabilityAsync"/> converts a 404 into a
/// <see cref="CoachAvailabilityResponse"/> with <c>IsAvailable = false</c> rather than throwing.
/// Callers use that to hide the entry point.
/// </para>
/// <para>
/// Errors: any other non-success response raises <see cref="CoachApiException"/> carrying the
/// RFC 7807 problem type so the UI can pick a specific state (expired, limited, plan conflict).
/// </para>
/// <para>
/// Cancellation: all methods honor the supplied token. Cancelling a turn abandons the client-side
/// run; the server result is discarded on arrival (there is no cancel endpoint in v1).
/// </para>
/// </remarks>
public interface ICoachApiClient
{
    /// <summary>
    /// GET /api/v1/coach/availability. Never throws for an unavailable feature; returns a
    /// response with <c>IsAvailable = false</c> instead.
    /// </summary>
    Task<CoachAvailabilityResponse> GetAvailabilityAsync(CancellationToken cancellationToken = default);

    /// <summary>POST /api/v1/coach/sessions. Starts or resumes the learner's coach session.</summary>
    Task<CoachSessionResponse> StartSessionAsync(StartCoachSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/v1/coach/sessions/{id}. Returns null when the session is gone (404) so the UI can
    /// fall back to starting a new one instead of showing a failure.
    /// </summary>
    Task<CoachSessionResponse?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>POST /api/v1/coach/sessions/{id}/turns. Submits text, a chip, or a structured constraint action.</summary>
    Task<CoachTurnResponse> SubmitTurnAsync(string sessionId, CoachTurnRequest request, CancellationToken cancellationToken = default);

    /// <summary>POST /api/v1/coach/sessions/{id}/suggestions/{suggestionId}/accept. Deterministic tapped acceptance.</summary>
    Task<CoachTurnResponse> AcceptSuggestionAsync(string sessionId, string suggestionId, CoachSuggestionDecisionRequest request, CancellationToken cancellationToken = default);

    /// <summary>POST /api/v1/coach/sessions/{id}/suggestions/{suggestionId}/reject. Deterministic rejection.</summary>
    Task<CoachTurnResponse> RejectSuggestionAsync(string sessionId, string suggestionId, CoachSuggestionDecisionRequest request, CancellationToken cancellationToken = default);

    /// <summary>POST /api/v1/coach/sessions/{id}/undo. Undoes the most recent applied revision.</summary>
    Task<CoachTurnResponse> UndoAsync(string sessionId, CoachUndoRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/v1/coach/sessions/{id}/cancel. Stops the in-flight run server-side so it stops
    /// holding the learner's single concurrency slot. Returns 204 with no body.
    /// </summary>
    /// <remarks>
    /// Best effort: a 404 (session gone, or nothing running) is treated as already stopped and
    /// does not throw, because the learner pressed Stop and the UI must always release.
    /// </remarks>
    Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// DELETE /api/v1/coach/sessions/{id}. Removes coach conversation state and pending suggestions.
    /// Today's Plan is not reverted. A 404 is treated as already deleted.
    /// </summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    // ------------------------------------------------------------------ conversations
    //
    // The durable history surface. A session is a 24-hour checkpoint; a conversation is the
    // thread that outlives it. The methods above keep working unchanged for one release.

    /// <summary>
    /// POST /api/v1/coach/conversations. Creates a conversation, or returns the one this
    /// idempotency key already created, so a retry never leaves a second empty thread behind.
    /// </summary>
    Task<CoachConversationDto> CreateConversationAsync(
        StartCoachConversationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>GET /api/v1/coach/conversations. The learner's own conversations, newest first.</summary>
    /// <remarks>
    /// <para>
    /// Returns <see langword="null"/> when the route answers 404. The server maps a disabled
    /// durable-history flag and an unresolvable owner to the same "unavailable" status, so this is
    /// also the client's feature probe: a caller that gets null must fall back to the
    /// session-only experience rather than surfacing an error. A missing feature is not a fault,
    /// and the alternative — throwing — would force every call site to catch an exception to
    /// answer a question that has a perfectly ordinary answer.
    /// </para>
    /// </remarks>
    Task<CoachConversationPageDto?> ListConversationsAsync(
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/v1/coach/conversations/{id}. Returns null when it is gone or owned by someone
    /// else — the two are deliberately indistinguishable.
    /// </summary>
    Task<CoachConversationDto?> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/v1/coach/conversations/{id}/messages. The newest page by default; pass
    /// <paramref name="before"/> to walk backwards through the thread.
    /// </summary>
    Task<CoachMessagePageDto?> GetConversationMessagesAsync(
        string conversationId,
        int? pageSize = null,
        string? before = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// PATCH /api/v1/coach/conversations/{id}. Renames, closes, or reopens. The expected state
    /// version makes the write conditional, so two devices cannot silently overwrite each other.
    /// </summary>
    Task<CoachConversationDto> UpdateConversationAsync(
        string conversationId,
        UpdateCoachConversationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/v1/coach/conversations/{id}/turns. One durable, idempotent turn. Retrying with
    /// the same key replays the stored result instead of running the turn again.
    /// </summary>
    Task<CoachTurnOperationDto> SubmitConversationTurnAsync(
        string conversationId,
        CoachConversationTurnRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/v1/coach/conversations/{id}/operations/{operationId}. Recovers the result of a
    /// turn whose response the client never received.
    /// </summary>
    Task<CoachTurnOperationDto?> GetConversationOperationAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/v1/coach/conversations/{id}/operations/{operationId}/cancel. Records the cancel
    /// durably, so it is honored even when the run is on another replica.
    /// </summary>
    Task<CoachTurnOperationDto?> CancelConversationTurnAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// DELETE /api/v1/coach/conversations/{id}. Hides the thread immediately and purges it
    /// afterwards. A 404 means already gone, which is the desired end state.
    /// </summary>
    Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/v1/coach/conversations/{id}/export. Opens the export stream. The caller owns the
    /// returned stream and must dispose it.
    /// </summary>
    /// <remarks>
    /// Returned as a stream rather than a materialized string: a long thread should start saving
    /// immediately and must never be held whole in the memory of a phone.
    /// </remarks>
    Task<Stream?> ExportConversationAsync(
        string conversationId,
        CoachExportFormat format = CoachExportFormat.Json,
        CancellationToken cancellationToken = default);

    // ================================================================ proposed changes
    //
    // Sam proposes; the learner decides. Every method here is a learner action on an
    // authenticated request, which is the whole reason a proposal is safe to produce: the model
    // has no route to any of them.

    /// <summary>
    /// GET /api/v1/coach/conversations/{id}/writes/{operationId}. The authoritative state of one
    /// proposed change.
    /// </summary>
    /// <returns>
    /// The state, or <see langword="null"/> when the route answers 404 — which covers a change
    /// that never existed, one belonging to another learner, and one addressed through the wrong
    /// conversation. Those are deliberately indistinguishable and must stay that way.
    /// </returns>
    Task<CoachWriteOperationDto?> GetWriteOperationAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/v1/coach/conversations/{id}/writes/{operationId}/accept. Approves a reversible
    /// change and answers with the state that acceptance produced.
    /// </summary>
    /// <remarks>
    /// No request body, deliberately. The server already holds the arguments the change was
    /// proposed with; restating them here would open a window in which the accepted thing differs
    /// from the previewed thing.
    /// </remarks>
    Task<CoachWriteOperationDto?> AcceptWriteAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/v1/coach/conversations/{id}/writes/{operationId}/reject. Declines a proposal.
    /// </summary>
    Task<CoachWriteOperationDto?> RejectWriteAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/v1/coach/conversations/{id}/writes/{operationId}/confirmation. Asks the server
    /// to mint the one-use value a protected change needs.
    /// </summary>
    /// <remarks>
    /// The response carries the only copy. Asking again rotates it, which makes the previous one
    /// permanently unusable — so a caller must not ask twice and keep the first.
    /// </remarks>
    Task<CoachWriteConfirmation?> RequestWriteConfirmationAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/v1/coach/conversations/{id}/writes/{operationId}/confirm. Carries out a
    /// protected change, sending the one-use value as a request header.
    /// </summary>
    /// <remarks>
    /// A header rather than a body field, matching the server: the value stays out of request
    /// bodies, which are the part of a request most likely to be logged, traced, or retained by
    /// an intermediary, and out of the URL, which is logged by everything.
    /// </remarks>
    Task<CoachWriteOperationDto?> ConfirmWriteAsync(
        string conversationId,
        string operationId,
        CoachWriteConfirmation confirmation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/v1/coach/conversations/{id}/writes/{operationId}/undo. Reverses an executed
    /// change inside its window and answers with the state after the reversal.
    /// </summary>
    Task<CoachWriteOperationDto?> UndoWriteAsync(
        string conversationId,
        string operationId,
        CancellationToken cancellationToken = default);

    // ================================================================ what Sam remembers

    /// <summary>
    /// GET /api/v1/coach/memories. The facts that are eligible for prompt context right now.
    /// </summary>
    /// <returns>
    /// The page, or <see langword="null"/> when the route group answers 404.
    /// </returns>
    /// <remarks>
    /// The feature being off, the learner being outside the cohort, and the learner not owning the
    /// data all produce the same 404. That is deliberate on the server and must stay
    /// indistinguishable here: a client that told those cases apart would leak whether a fact
    /// exists for somebody else.
    /// </remarks>
    Task<CoachMemoryPageDto?> ListActiveMemoriesAsync(
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/v1/coach/memories/candidates. Facts proposed from an explicit learner statement
    /// and waiting for a decision. A candidate never enters a prompt.
    /// </summary>
    Task<CoachMemoryPageDto?> ListMemoryCandidatesAsync(
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/v1/coach/memories/{factId}/approve. Approves a candidate, optionally replacing
    /// its value first.
    /// </summary>
    /// <returns>The stored fact, or <see langword="null"/> when the fact is gone.</returns>
    Task<CoachMemoryFactDto?> ApproveMemoryAsync(
        string factId,
        CoachMemoryApproveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/v1/coach/memories/{factId}/reject. Declines a candidate. Nothing is remembered.
    /// </summary>
    Task RejectMemoryAsync(
        string factId,
        CoachMemoryRejectRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// PUT /api/v1/coach/memories/{factId}. Replaces the value of a fact the learner already
    /// approved. The kind cannot change.
    /// </summary>
    Task<CoachMemoryFactDto?> EditMemoryAsync(
        string factId,
        CoachMemoryEditRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// DELETE /api/v1/coach/memories/{factId}?expectedVersion=N. Forgets one fact.
    /// A 404 means already forgotten, which is the desired end state.
    /// </summary>
    Task ForgetMemoryAsync(string factId, int expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// DELETE /api/v1/coach/memories. Forgets everything Sam remembers about this learner.
    /// </summary>
    /// <returns>
    /// How many facts were removed, or <see langword="null"/> when the route group answers 404.
    /// </returns>
    Task<CoachMemoryForgetAllResponse?> ForgetAllMemoriesAsync(CancellationToken cancellationToken = default);

    // ------------------------------------------------------------------ response reports

    /// <summary>
    /// GET /api/v1/coach/conversations/{id}/responses/reported. Which of this conversation's
    /// coach responses the learner has already reported.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the route answers 404, which is what a deployment with
    /// reporting switched off does. A caller reads that as "do not offer the control" rather than
    /// as a failure — the same feature-probe shape the conversation list uses. A real conversation
    /// with nothing reported answers an empty list, and so does an unknown one.
    /// </remarks>
    Task<CoachReportedResponsesDto?> GetReportedResponsesAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/v1/coach/conversations/{id}/responses/{messageId}/report. Reports one coach
    /// response.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the route answers 404 — reporting is off, or the
    /// conversation is unknown or owned by somebody else, which are deliberately the same answer.
    /// A repeat is a success carrying
    /// <see cref="CoachResponseReportState.AlreadyReported"/>, never a conflict.
    /// </remarks>
    Task<CoachResponseReportResponse?> ReportResponseAsync(
        string conversationId,
        string messageId,
        CoachResponseReportRequest request,
        CancellationToken cancellationToken = default);
}
