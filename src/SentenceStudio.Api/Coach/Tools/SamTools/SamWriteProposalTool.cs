using System.Text.Json;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Services.Plans;
using SentenceStudio.Api.Coach.Persistence;

namespace SentenceStudio.Api.Coach.Tools.SamTools;

/// <summary>
/// The single entry point every write-intent tool goes through.
/// </summary>
/// <remarks>
/// <para>
/// There is one of these rather than twelve hand-written tool classes, and that is a security
/// property rather than a convenience. Each tool the model sees differs only in its name, its
/// argument type, and its description; the identity check, the conversation binding, the
/// ownership proof, the idempotency digest, and the refusal to execute are all the same code
/// path. A reviewer confirms them once. Twelve copies would each need confirming, and the one
/// that quietly diverged would be the one that mattered.
/// </para>
/// <para>
/// Nothing here writes. Every call produces a proposal and returns it; execution happens later,
/// on a request the learner makes, through a route the model cannot reach.
/// </para>
/// </remarks>
public sealed class SamWriteProposalTool : CoachToolBase
{
    private readonly ICoachWriteProposer _operations;
    private readonly CoachWriteTurnScope _turn;

    public SamWriteProposalTool(
        IUserScopeProvider userScope,
        ICoachWriteProposer operations,
        CoachWriteTurnScope turn)
        : base(userScope)
    {
        _operations = operations;
        _turn = turn;
    }

    /// <summary>
    /// Not a single tool name: this class backs every write-intent tool.
    /// </summary>
    /// <remarks>
    /// The base class wants a name for its failure messages. The real name travels with each
    /// call, because one instance serves all of them.
    /// </remarks>
    public override string ToolName => "propose_write";

    /// <summary>
    /// Turns a model tool call into a stored proposal.
    /// </summary>
    /// <param name="toolName">
    /// Which write tool was called. Supplied by the factory from the registration, never by the
    /// model — the model's arguments contain only the domain fields.
    /// </param>
    /// <param name="arguments">The tool's own typed arguments.</param>
    public async Task<CoachWriteProposalResult> ProposeAsync<TArgs>(
        string toolName,
        TArgs arguments,
        CancellationToken cancellationToken = default)
        where TArgs : class
    {
        // Identity first, before the arguments are even looked at. RequireUserProfileId throws
        // when the scope is empty, so an unauthenticated call cannot reach a query.
        _ = RequireUserProfileId();

        if (!_turn.IsActive)
        {
            throw new CoachToolException(
                CoachToolFailureKind.InvalidArgument,
                toolName,
                "This tool is only available during a conversation turn.");
        }

        if (arguments is null)
        {
            throw InvalidArgument("The tool arguments are required.");
        }

        var argumentsJson = JsonSerializer.Serialize(arguments, CoachNormalizedJson.Options);

        return await _operations
            .ProposeAsync(_turn.ConversationId!, _turn.TurnId, toolName, argumentsJson, cancellationToken)
            .ConfigureAwait(false);
    }
}
