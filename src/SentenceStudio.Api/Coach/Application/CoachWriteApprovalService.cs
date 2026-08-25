using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// The learner-facing side of the write ledger.
/// </summary>
/// <remarks>
/// <para>
/// Everything a proposal needs after it exists — accepting it, asking for a confirmation,
/// confirming it, rejecting it, undoing it — arrives here, and every one of those arrives on an
/// authenticated HTTP request the learner made. That separation is the whole design: the model
/// can propose and cannot approve, because approval is not reachable from a tool.
/// </para>
/// <para>
/// This type decides nothing. It translates the ledger's typed refusals into the status vocabulary
/// the endpoints already speak, so a route stays a route. The ledger remains the only place that
/// knows whether an operation may proceed.
/// </para>
/// </remarks>
public interface ICoachWriteApprovalService
{
    /// <summary>Reads one operation's authoritative client-facing state.</summary>
    Task<CoachOperationResult<CoachWriteOperationDto>> GetAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default);

    /// <summary>Reads the receipt for an operation that has run. Not found until it has.</summary>
    Task<CoachOperationResult<CoachWriteReceiptDto>> GetReceiptAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default);

    /// <summary>Accepts a soft change and answers with the state that acceptance produced.</summary>
    Task<CoachOperationResult<CoachWriteOperationDto>> AcceptAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default);

    Task<CoachOperationResult<CoachWriteConfirmationChallenge>> IssueConfirmationAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default);

    /// <summary>Confirms a protected change and answers with the state it produced.</summary>
    Task<CoachOperationResult<CoachWriteOperationDto>> ConfirmAsync(
        string conversationId, string operationId, string? confirmationSecret,
        CancellationToken cancellationToken = default);

    /// <summary>Declines a proposal and answers with the state the decline left behind.</summary>
    Task<CoachOperationResult<CoachWriteOperationDto>> RejectAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default);

    /// <summary>Reverses an executed change and answers with the state after the reversal.</summary>
    Task<CoachOperationResult<CoachWriteOperationDto>> UndoAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class CoachWriteApprovalService : ICoachWriteApprovalService
{
    private readonly CoachWriteOperationService _operations;
    private readonly ILogger<CoachWriteApprovalService> _logger;

    public CoachWriteApprovalService(
        CoachWriteOperationService operations,
        ILogger<CoachWriteApprovalService> logger)
    {
        _operations = operations;
        _logger = logger;
    }

    public Task<CoachOperationResult<CoachWriteOperationDto>> GetAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default) =>
        ReadStateAsync(conversationId, operationId, cancellationToken);

    /// <summary>
    /// Reads one operation's state, answering not-found when there is nothing to read.
    /// </summary>
    /// <remarks>
    /// An operation that never existed, one belonging to another learner, and one addressed
    /// through the wrong conversation all end here, and they are all the same answer. Telling
    /// them apart would turn this route into a probe for whether somebody else's change exists.
    /// </remarks>
    private async Task<CoachOperationResult<CoachWriteOperationDto>> ReadStateAsync(
        string conversationId, string operationId, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            () => _operations.GetStateAsync(conversationId, operationId, cancellationToken))
            .ConfigureAwait(false);

        if (!result.IsOk)
        {
            return Refusal<CoachWriteOperationDto>(result.Status, result.ProblemType, result.Detail);
        }

        return result.Value is { } state
            ? CoachOperationResult<CoachWriteOperationDto>.Ok(state)
            : CoachOperationResult<CoachWriteOperationDto>.Problem(
                CoachOperationStatus.SessionNotFound,
                CoachProblemTypes.SessionNotFound,
                "No such pending change for this learner.");
    }

    public async Task<CoachOperationResult<CoachWriteReceiptDto>> GetReceiptAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(conversationId, operationId, cancellationToken)
            .ConfigureAwait(false);

        if (!state.IsOk)
        {
            return Refusal<CoachWriteReceiptDto>(state.Status, state.ProblemType, state.Detail);
        }

        // A receipt that does not exist yet is not an error and is not an empty receipt: the
        // change simply has not run. Answering "not found" keeps a client from ever rendering a
        // blank applied state on the strength of a 200.
        return state.Value?.Receipt is { } receipt
            ? CoachOperationResult<CoachWriteReceiptDto>.Ok(receipt)
            : CoachOperationResult<CoachWriteReceiptDto>.Problem(
                CoachOperationStatus.SessionNotFound,
                CoachProblemTypes.SessionNotFound,
                "There is no receipt for that change.");
    }

    public Task<CoachOperationResult<CoachWriteOperationDto>> AcceptAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default) =>
        SettleAsync(
            conversationId,
            operationId,
            async () => await _operations.AcceptAsync(conversationId, operationId, cancellationToken)
                .ConfigureAwait(false),
            cancellationToken);

    public async Task<CoachOperationResult<CoachWriteConfirmationChallenge>> IssueConfirmationAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default)
    {
        var issued = await RunAsync(
            () => _operations.IssueConfirmationAsync(conversationId, operationId, cancellationToken))
            .ConfigureAwait(false);

        if (!issued.IsOk)
        {
            return Refusal<CoachWriteConfirmationChallenge>(
                issued.Status, issued.ProblemType, issued.Detail);
        }

        return issued.Value is { } challenge
            ? CoachOperationResult<CoachWriteConfirmationChallenge>.Ok(challenge)
            : Refusal<CoachWriteConfirmationChallenge>(
                CoachOperationStatus.SessionNotFound,
                CoachProblemTypes.SessionNotFound,
                "No such pending change for this learner.");
    }

    public Task<CoachOperationResult<CoachWriteOperationDto>> ConfirmAsync(
        string conversationId, string operationId, string? confirmationSecret,
        CancellationToken cancellationToken = default) =>
        SettleAsync(
            conversationId,
            operationId,
            async () => await _operations.ConfirmAsync(
                    conversationId, operationId, confirmationSecret, cancellationToken)
                .ConfigureAwait(false),
            cancellationToken);

    public Task<CoachOperationResult<CoachWriteOperationDto>> RejectAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default) =>
        SettleAsync(
            conversationId,
            operationId,
            async () => await _operations.RejectAsync(conversationId, operationId, cancellationToken)
                .ConfigureAwait(false),
            cancellationToken);

    public Task<CoachOperationResult<CoachWriteOperationDto>> UndoAsync(
        string conversationId, string operationId, CancellationToken cancellationToken = default) =>
        SettleAsync(
            conversationId,
            operationId,
            async () => await _operations.UndoAsync(conversationId, operationId, cancellationToken)
                .ConfigureAwait(false),
            cancellationToken);

    /// <summary>
    /// Runs a transition, then answers with the state the ledger holds afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The re-read is the point. A transition's own return value describes what that call did;
    /// the state afterwards describes what is true, and those differ in exactly the cases that
    /// matter — a replayed acceptance, a reversal that closed the undo window, a decline that
    /// found the change already applied. A client that renders from the transition's word alone
    /// eventually shows a receipt for something that did not happen.
    /// </para>
    /// <para>
    /// A transition that refuses is translated to a problem and the read never runs, so a refusal
    /// can never be dressed up as a state change.
    /// </para>
    /// </remarks>
    private async Task<CoachOperationResult<CoachWriteOperationDto>> SettleAsync(
        string conversationId,
        string operationId,
        Func<Task> transition,
        CancellationToken cancellationToken)
    {
        var outcome = await RunAsync<bool>(async () =>
        {
            await transition().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

        if (!outcome.IsOk)
        {
            return Refusal<CoachWriteOperationDto>(outcome.Status, outcome.ProblemType, outcome.Detail);
        }

        return await ReadStateAsync(conversationId, operationId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Carries a refusal across to another result type without inventing any of its fields.
    /// </summary>
    /// <remarks>
    /// A refusal has already been shaped by <see cref="Translate{T}"/>; re-deriving it at each
    /// hand-off is how one route ends up describing an ownership refusal differently from the
    /// next. The fallbacks only fire for the impossible case of a refusal with no problem type,
    /// and they fail towards the least informative answer.
    /// </remarks>
    private static CoachOperationResult<T> Refusal<T>(
        CoachOperationStatus status, string? problemType, string? detail) =>
        CoachOperationResult<T>.Problem(
            status,
            problemType ?? CoachProblemTypes.ToolFailure,
            detail ?? "The request could not be completed.");

    /// <summary>
    /// Runs a ledger call and translates its refusals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason the refusal is caught here rather than thrown to the endpoint is uniformity:
    /// every route gets the same mapping, so no single route can accidentally surface a ledger
    /// message that the others hide.
    /// </para>
    /// <para>
    /// Unexpected exceptions are logged by type only and answered as a plain failure. A ledger
    /// exception message is written by us and is safe; an arbitrary exception's is not, and the
    /// difference is not worth guessing about at the boundary.
    /// </para>
    /// </remarks>
    private async Task<CoachOperationResult<T>> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return CoachOperationResult<T>.Ok(await action().ConfigureAwait(false));
        }
        catch (CoachToolException ex)
        {
            return Translate<T>(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnauthorizedAccessException)
        {
            _logger.LogError(
                "[Coach] A write approval request failed: {Failure}.",
                CoachExceptionSanitizer.Describe(ex));

            return CoachOperationResult<T>.Problem(
                CoachOperationStatus.Failed,
                CoachProblemTypes.ToolFailure,
                "The request could not be completed.");
        }
    }

    /// <summary>
    /// Maps a ledger refusal onto the coach status vocabulary.
    /// </summary>
    /// <remarks>
    /// The ledger's reasons are already learner-safe — they name states and never data — so they
    /// are passed through as the detail. What they must not do is vary by whether a record exists,
    /// and they do not: a record owned by someone else and a record that never existed produce the
    /// same not-found reason from the ledger itself.
    /// </remarks>
    private static CoachOperationResult<T> Translate<T>(CoachToolException ex)
    {
        var (status, problem) = ex.Kind switch
        {
            CoachToolFailureKind.Unauthorized =>
                (CoachOperationStatus.Unavailable, CoachProblemTypes.Unavailable),
            CoachToolFailureKind.ProfileMissing =>
                (CoachOperationStatus.SessionNotFound, CoachProblemTypes.SessionNotFound),
            CoachToolFailureKind.InvalidArgument =>
                (CoachOperationStatus.InvalidInput, CoachProblemTypes.InvalidTurnInput),
            CoachToolFailureKind.BudgetExhausted =>
                (CoachOperationStatus.RateLimited, CoachProblemTypes.RateLimited),
            CoachToolFailureKind.DataAccess =>
                (CoachOperationStatus.Failed, CoachProblemTypes.ToolFailure),
            _ => (CoachOperationStatus.InvalidInput, CoachProblemTypes.InvalidTurnInput)
        };

        return CoachOperationResult<T>.Problem(status, problem, ex.Reason);
    }
}
