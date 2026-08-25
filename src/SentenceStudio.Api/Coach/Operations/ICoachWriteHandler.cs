using System.Text.Json;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Coach.Operations;

/// <summary>
/// The server-side half of one learner-owned write tool.
/// </summary>
/// <remarks>
/// <para>
/// A tool produces a proposal; a handler is what actually touches learner data, and only ever
/// after the ledger has confirmed the learner approved this exact operation. The split is the
/// point: the model can reach <see cref="PrepareAsync"/> through its tool, but it has no path at
/// all to <see cref="ExecuteAsync"/> or <see cref="UndoAsync"/>, which are reachable only from an
/// authenticated owner-scoped route.
/// </para>
/// <para>
/// Every handler resolves ownership itself, from the identity the ledger passes in. It must never
/// accept an owner, profile, tenant, or email identifier from arguments, and several of the
/// repository methods it calls perform no ownership check of their own, so "the repository will
/// catch it" is not available as a defence.
/// </para>
/// </remarks>
public interface ICoachWriteHandler
{
    /// <summary>The registered tool name this handler serves.</summary>
    string ToolName { get; }

    /// <summary>The registered risk class, which decides the approval channel.</summary>
    CoachToolRiskClass RiskClass { get; }

    /// <summary>Whether — and how — an executed operation can be reversed.</summary>
    CoachWriteUndoKind UndoKind { get; }

    /// <summary>The kind of entity this handler writes.</summary>
    CoachWriteEntityKind EntityKind { get; }

    /// <summary>
    /// Validates arguments and ownership and describes what would change, without changing it.
    /// </summary>
    /// <remarks>
    /// This runs while the model is still in the loop, so it must fail closed on anything the
    /// learner does not own: discovering at execution time that a referenced entity belongs to
    /// somebody else is too late to be a security control, because the preview the learner
    /// approved would already have described it.
    /// </remarks>
    Task<CoachWritePreview> PrepareAsync(string userProfileId, string argumentsJson, CancellationToken cancellationToken);

    /// <summary>Performs the write. Called only after the ledger recorded an approval.</summary>
    Task<CoachWriteExecution> ExecuteAsync(string userProfileId, string argumentsJson, CancellationToken cancellationToken);

    /// <summary>Reverses a write inside its undo window. Never called when <see cref="UndoKind"/> is None.</summary>
    Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        string argumentsJson,
        string priorStateJson,
        CancellationToken cancellationToken);
}

/// <summary>
/// Base class that handles argument deserialization, bounds, and the failures a handler shares.
/// </summary>
/// <typeparam name="TArgs">The handler's typed argument record.</typeparam>
public abstract class CoachWriteHandlerBase<TArgs> : ICoachWriteHandler
    where TArgs : class
{
    /// <inheritdoc />
    public abstract string ToolName { get; }

    /// <inheritdoc />
    public abstract CoachToolRiskClass RiskClass { get; }

    /// <inheritdoc />
    public virtual CoachWriteUndoKind UndoKind => CoachWriteUndoKind.None;

    /// <inheritdoc />
    public abstract CoachWriteEntityKind EntityKind { get; }

    /// <inheritdoc />
    public Task<CoachWritePreview> PrepareAsync(
        string userProfileId, string argumentsJson, CancellationToken cancellationToken) =>
        PrepareAsync(RequireOwner(userProfileId), Bind(argumentsJson), cancellationToken);

    /// <inheritdoc />
    public Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, string argumentsJson, CancellationToken cancellationToken) =>
        ExecuteAsync(RequireOwner(userProfileId), Bind(argumentsJson), cancellationToken);

    /// <inheritdoc />
    public Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        string argumentsJson,
        string priorStateJson,
        CancellationToken cancellationToken) =>
        UndoAsync(RequireOwner(userProfileId), Bind(argumentsJson), priorStateJson, cancellationToken);

    /// <summary>Validates and previews, with arguments already bound.</summary>
    protected abstract Task<CoachWritePreview> PrepareAsync(
        string userProfileId, TArgs args, CancellationToken cancellationToken);

    /// <summary>Writes, with arguments already bound.</summary>
    protected abstract Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, TArgs args, CancellationToken cancellationToken);

    /// <summary>
    /// Reverses a write, with arguments already bound. Refuses by default, so a handler is
    /// irreversible unless it deliberately says otherwise.
    /// </summary>
    protected virtual Task<CoachWriteExecution> UndoAsync(
        string userProfileId,
        TArgs args,
        string priorStateJson,
        CancellationToken cancellationToken) =>
        throw new CoachToolException(
            CoachToolFailureKind.InvalidArgument, ToolName, "This change cannot be reversed.");

    /// <summary>Raises a typed invalid-argument failure that carries no learner text.</summary>
    protected CoachToolException InvalidArgument(string reason) =>
        new(CoachToolFailureKind.InvalidArgument, ToolName, reason);

    /// <summary>Raises the refusal used when a referenced entity is not the learner's.</summary>
    /// <remarks>
    /// Deliberately the same message whether the row belongs to somebody else or does not exist.
    /// A distinguishable answer would turn the tool into an existence oracle for other learners'
    /// identifiers.
    /// </remarks>
    protected CoachToolException NotFoundOrNotOwned() =>
        new(CoachToolFailureKind.InvalidArgument, ToolName, "No such item for this learner.");

    /// <summary>Wraps a data failure without letting the inner message reach a caller.</summary>
    protected CoachToolException DataAccessFailure(Exception inner) =>
        new(CoachToolFailureKind.DataAccess, ToolName, "The write failed.", inner);

    /// <summary>Serializes a value with the deterministic coach options.</summary>
    protected static string Canonical<T>(T value) => CoachNormalizedJson.Serialize(value);

    /// <summary>Deserializes handler-owned prior state, failing closed on an unreadable payload.</summary>
    protected TState BindPriorState<TState>(string priorStateJson) where TState : class
    {
        try
        {
            return CoachNormalizedJson.Deserialize<TState>(priorStateJson)
                ?? throw new CoachToolException(
                    CoachToolFailureKind.InvalidArgument, ToolName, "The undo record is unreadable.");
        }
        catch (JsonException)
        {
            throw new CoachToolException(
                CoachToolFailureKind.InvalidArgument, ToolName, "The undo record is from an older version.");
        }
    }

    /// <summary>
    /// Cleans one learner-supplied field: control characters removed, length capped, surrounding
    /// whitespace trimmed. Applied to everything a handler stores or echoes.
    /// </summary>
    protected static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Span<char> buffer = value.Length <= 512 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;
        foreach (var c in value)
        {
            buffer[length++] = char.IsControl(c) ? ' ' : c;
        }

        var cleaned = new string(buffer[..length]).Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private string RequireOwner(string userProfileId)
    {
        // The ledger already resolved this from the authenticated principal. Re-checking here
        // means a handler can never be wired into a code path that forgot to.
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            throw new CoachToolException(
                CoachToolFailureKind.Unauthorized, ToolName, "The request has no user scope.");
        }

        return userProfileId;
    }

    /// <summary>
    /// Binds the arguments a tool call supplied, refusing any member the contract does not
    /// declare.
    /// </summary>
    /// <remarks>
    /// Strict on purpose. An undeclared member means the payload was written against a different
    /// contract than the one about to be previewed, and taking the members that happened to match
    /// would produce a preview the learner approves without the rest of the request ever being
    /// read. It also closes the shape a smuggled identity field would take: the field cannot be
    /// silently dropped, because its presence is itself the refusal.
    /// </remarks>
    private TArgs Bind(string argumentsJson)
    {
        try
        {
            return CoachNormalizedJson.DeserializeStrict<TArgs>(argumentsJson)
                ?? throw InvalidArgument("The arguments are missing.");
        }
        catch (JsonException)
        {
            throw InvalidArgument("The arguments could not be read.");
        }
    }
}
