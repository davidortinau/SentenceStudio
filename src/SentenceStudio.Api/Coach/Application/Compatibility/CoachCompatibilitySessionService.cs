using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Coach.Application.History;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Application.Compatibility;

/// <summary>
/// Routes the old <c>/api/v1/coach/sessions</c> requests through durable history when the session
/// id names a conversation the caller owns, and through the plain session service when it does not.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the two shapes overlap for one release and the overlap had a hole in it. A
/// session started by a durable-history client gets a conversation with the same id, but a turn
/// posted to the old route went straight to <see cref="ICoachSessionService"/> and appended
/// nothing. The learner saw a normal reply, the conversation existed, and the ledger stayed empty.
/// Nothing failed, so nothing told anyone — which is the worst shape a data-loss bug can take.
/// A client that picks the wrong route is a client bug; losing the learner's history over it is
/// ours.
/// </para>
/// <para>
/// It sits above both services rather than decorating either. <see cref="ICoachConversationService"/>
/// already depends on <see cref="ICoachSessionService"/>, so a decorator registered against the
/// session interface would route the durable service's own inner calls back into itself. Keeping
/// the fork here leaves that dependency edge pointing one way.
/// </para>
/// <para>
/// Every route falls back to the legacy service, never to an error. Durable history is additive:
/// a session that predates it, a host with the flag off, and a conversation that could not be
/// created all keep working exactly as they did.
/// </para>
/// </remarks>
public sealed class CoachCompatibilitySessionService
{
    private readonly ICoachSessionService _sessions;
    private readonly ICoachConversationService _conversations;
    private readonly ILogger<CoachCompatibilitySessionService> _logger;

    public CoachCompatibilitySessionService(
        ICoachSessionService sessions,
        ICoachConversationService conversations,
        ILogger<CoachCompatibilitySessionService> logger)
    {
        _sessions = sessions;
        _conversations = conversations;
        _logger = logger;
    }

    // ------------------------------------------------------------- passthrough

    /// <summary>Starts a session. The session service already opens the matching conversation.</summary>
    public Task<CoachOperationResult<CoachSessionResponse>> StartSessionAsync(
        StartCoachSessionRequest request,
        CancellationToken cancellationToken = default) =>
        _sessions.StartSessionAsync(request, cancellationToken);

    /// <summary>Reads a session. The session response already carries durable messages.</summary>
    public Task<CoachOperationResult<CoachSessionResponse>> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        _sessions.GetSessionAsync(sessionId, cancellationToken);

    /// <summary>
    /// Stops a running turn, durably when this session names an owned conversation.
    /// </summary>
    /// <remarks>
    /// The legacy cancel only ever signalled the in-process run registry, which is enough to
    /// abandon a model call on this replica and nothing more. A durable turn also has to record
    /// the request where the running turn will look for it at its next stage boundary — otherwise
    /// a cancel that lands while the model is answering stops the call but lets the turn go on to
    /// apply its result, which is a cancel button that appears to work and does not.
    /// </remarks>
    public async Task<CoachOperationResult<bool>> CancelAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsDurableAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return await _sessions.CancelAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

        var durable = await _conversations.CancelActiveTurnAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (!durable.IsOk)
        {
            return durable;
        }

        // The legacy answer says whether anything was actually stopped, and a durable turn and a
        // bare session run are both things that count. Ask the session service too rather than
        // reporting "nothing running" for a session whose run the registry still holds.
        var legacy = await _sessions.CancelAsync(sessionId, cancellationToken).ConfigureAwait(false);

        return CoachOperationResult<bool>.Ok(durable.Value || (legacy.IsOk && legacy.Value));
    }

    // ------------------------------------------------------------------ turns

    /// <summary>
    /// Submits a turn, durably when this session names an owned conversation.
    /// </summary>
    public async Task<CoachOperationResult<CoachTurnResponse>> SubmitTurnAsync(
        string sessionId,
        CoachTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await IsDurableAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return await _sessions.SubmitTurnAsync(sessionId, request, cancellationToken).ConfigureAwait(false);
        }

        // No client turn id means no retry key, which is what the old route always meant. A fresh
        // key per request keeps that: two deliberate sends stay two turns.
        var key = string.IsNullOrWhiteSpace(request.ClientTurnId)
            ? Guid.NewGuid().ToString("N")
            : CoachCompatibilityKeys.IdempotencyKey(sessionId, request.ClientTurnId!);

        var operation = await _conversations.SubmitTurnAsync(
            sessionId,
            new CoachConversationTurnRequest
            {
                IdempotencyKey = key,
                OperationId = CoachCompatibilityKeys.OperationId(sessionId, key),
                Turn = request
            },
            cancellationToken).ConfigureAwait(false);

        if (!operation.IsOk || operation.Value is null)
        {
            return CoachOperationResult<CoachTurnResponse>.Problem(
                operation.Status,
                operation.ProblemType ?? CoachProblemTypes.Unavailable,
                operation.Detail ?? "That turn did not complete.");
        }

        if (operation.Value.Result is { } response)
        {
            return CoachOperationResult<CoachTurnResponse>.Ok(response);
        }

        // An operation that completed without a readable stored outcome cannot be reduced to the
        // old response shape, and re-running it would repeat whatever it already applied. The
        // ledger still holds what was said, so the learner has not lost the turn — only this
        // request's view of it.
        _logger.LogWarning(
            "[Coach] A compatibility turn completed with no replayable outcome. State: {State}.",
            operation.Value.State);

        return CoachOperationResult<CoachTurnResponse>.Problem(
            CoachOperationStatus.Unavailable,
            CoachProblemTypes.Unavailable,
            "That turn completed but its result could not be read back.");
    }

    // -------------------------------------------------------------- decisions

    /// <summary>Accepts the open suggestion, durably when this session names an owned conversation.</summary>
    public async Task<CoachOperationResult<CoachTurnResponse>> AcceptSuggestionAsync(
        string sessionId,
        string suggestionId,
        CoachSuggestionDecisionRequest request,
        CancellationToken cancellationToken = default) =>
        await IsDurableAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ? await _conversations.RunCompatibilityDecisionAsync(
                sessionId,
                new CoachCompatibilityDecision(
                    CoachCompatibilityDecisionKind.AcceptSuggestion, suggestionId, request.ClientTurnId),
                cancellationToken).ConfigureAwait(false)
            : await _sessions.AcceptSuggestionAsync(sessionId, suggestionId, request, cancellationToken)
                .ConfigureAwait(false);

    /// <summary>Rejects the open suggestion, durably when this session names an owned conversation.</summary>
    public async Task<CoachOperationResult<CoachTurnResponse>> RejectSuggestionAsync(
        string sessionId,
        string suggestionId,
        CoachSuggestionDecisionRequest request,
        CancellationToken cancellationToken = default) =>
        await IsDurableAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ? await _conversations.RunCompatibilityDecisionAsync(
                sessionId,
                new CoachCompatibilityDecision(
                    CoachCompatibilityDecisionKind.RejectSuggestion, suggestionId, request.ClientTurnId),
                cancellationToken).ConfigureAwait(false)
            : await _sessions.RejectSuggestionAsync(sessionId, suggestionId, request, cancellationToken)
                .ConfigureAwait(false);

    /// <summary>Undoes the last applied change, durably when this session names an owned conversation.</summary>
    public async Task<CoachOperationResult<CoachTurnResponse>> UndoAsync(
        string sessionId,
        CoachUndoRequest request,
        CancellationToken cancellationToken = default) =>
        await IsDurableAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ? await _conversations.RunCompatibilityDecisionAsync(
                sessionId,
                new CoachCompatibilityDecision(
                    CoachCompatibilityDecisionKind.Undo, null, request.ClientTurnId),
                cancellationToken).ConfigureAwait(false)
            : await _sessions.UndoAsync(sessionId, request, cancellationToken).ConfigureAwait(false);

    // ----------------------------------------------------------------- delete

    /// <summary>
    /// Deletes the session, and the conversation behind it when there is one.
    /// </summary>
    /// <remarks>
    /// Both, because a learner deleting a thread on the old surface means the thread, not the
    /// 24-hour checkpoint over it. Removing only the checkpoint would leave the whole transcript
    /// to reappear the moment they opened the new history surface.
    /// </remarks>
    public async Task<CoachOperationResult<bool>> DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var durable = await IsDurableAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var deleted = await _sessions.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);

        if (!durable)
        {
            return deleted;
        }

        var hidden = await _conversations.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // The conversation is the thing the learner asked to be rid of, so its failure is the one
        // that must be reported. A deleted checkpoint over a surviving transcript is not a
        // success, whatever the session store says.
        if (!hidden.IsOk)
        {
            return hidden;
        }

        // The session may already have expired on its own. That is not a failure of the delete:
        // the transcript is gone, which is what was asked for.
        return deleted.IsOk ? deleted : CoachOperationResult<bool>.Ok(true);
    }

    // ----------------------------------------------------------------- policy

    /// <summary>
    /// Whether this session id names a durable conversation this caller owns.
    /// </summary>
    /// <remarks>
    /// The lookup is owner-scoped, so another learner's conversation reads as absent and the
    /// request falls through to the session service — which is itself owner-scoped and answers
    /// 404. A foreign id therefore cannot be told apart from an unknown one on either path.
    /// </remarks>
    private async Task<bool> IsDurableAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_conversations.IsEnabled || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var found = await _conversations.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return found.IsOk && found.Value is not null;
    }
}
