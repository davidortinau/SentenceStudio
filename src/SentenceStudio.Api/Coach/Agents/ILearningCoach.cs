using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Coach.Agents;

/// <summary>
/// Everything the agent is allowed to see for one turn. The application composes this;
/// the agent never reads the database, the request, or the authenticated principal.
/// </summary>
/// <remarks>
/// There is deliberately no <c>UserProfileId</c> here. Tools resolve the trusted scope
/// themselves through <c>IUserScopeProvider</c>, so nothing the model returns can widen
/// or redirect the data it sees.
/// </remarks>
public sealed record CoachAgentTurnRequest
{
    /// <summary>The owned coach session id. Used for logging correlation only.</summary>
    public required string SessionId { get; init; }

    /// <summary>The previously serialized <c>AgentSession</c>, or null for a new conversation.</summary>
    public string? AgentSessionJson { get; init; }

    /// <summary>
    /// Bounded prior conversation, oldest first, for a turn that could not resume a serialized
    /// agent session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the application-owned context mechanism for rebuilding a conversation after the
    /// 24-hour checkpoint expired or was written under an incompatible agent configuration. The
    /// framework's <c>AgentSession</c> cannot be reconstructed from decrypted history — its
    /// serialized form is the only thing that rehydrates it — so instead a new session is created
    /// and this bounded transcript is rendered into the turn message by
    /// <see cref="CoachInstructions.BuildTurnMessage"/>, fenced and role-tagged.
    /// </para>
    /// <para>
    /// Empty on the normal path. When populated it is conversation <em>data</em>: it is never
    /// replayed as developer instructions, and receipts, notices, and suggestion snapshots are
    /// filtered out before it is built so past server actions cannot read as new commands.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CoachPriorMessage> PriorMessages { get; init; } = Array.Empty<CoachPriorMessage>();

    /// <summary>The learner's raw text for this turn, already length-validated by the application.</summary>
    public required string LearnerText { get; init; }

    /// <summary>
    /// Saved learning preferences the learner approved earlier, rendered as one labelled
    /// untrusted data block. Null on every turn that selected nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the application-owned mechanism for memory context, and it is deliberately the
    /// same mechanism as <see cref="PriorMessages"/>: a block inside the turn message, rendered by
    /// <see cref="CoachInstructions.BuildTurnMessage"/>. It is never a system message, never a
    /// developer message, never a tool argument, and never part of the agent's instructions. The
    /// framework's instruction surface is built once from <see cref="CoachInstructions"/> and does
    /// not vary by learner, which is what keeps a saved preference from being able to rewrite what
    /// the coach is.
    /// </para>
    /// <para>
    /// The text is produced only by <c>CoachMemoryPromptFormatter.Format</c>, which owns the
    /// heading and the handling rules and emits learner words only as JSON string literals on a
    /// labelled line. The application never concatenates learner content into this itself.
    /// </para>
    /// </remarks>
    public string? MemoryBlock { get; init; }

    /// <summary>The constraints currently applied to Today's Plan.</summary>
    public required CoachConstraintSetDto ActiveConstraints { get; init; }

    /// <summary>The id of the single pending suggestion, if one is open.</summary>
    public string? PendingSuggestionId { get; init; }

    /// <summary>The delta the pending suggestion would apply, if one is open.</summary>
    public CoachConstraintDeltaDto? PendingSuggestionDelta { get; init; }

    /// <summary>How many clarifying questions the coach may still ask in this session.</summary>
    public required int ClarificationsRemaining { get; init; }

    /// <summary>The learner's local date, so evidence windows can be described without a clock read.</summary>
    public required DateOnly UserLocalDate { get; init; }
}

/// <summary>
/// One earlier visible message, as conversation data for a rebuilt turn.
/// </summary>
/// <param name="Role">Who said it.</param>
/// <param name="Text">What was said, already bounded by the caller.</param>
public readonly record struct CoachPriorMessage(CoachMessageRole Role, string Text);

/// <summary>Why a coach agent turn ended. Only <see cref="Completed"/> carries an intent.</summary>
public enum CoachAgentOutcome
{
    /// <summary>The run failed for an unexpected reason.</summary>
    Failed = 0,

    /// <summary>The run produced a well-formed typed intent.</summary>
    Completed,

    /// <summary>No chat client is configured on this host, so no agent could be built.</summary>
    ModelUnavailable,

    /// <summary>The run exceeded the configured request timeout.</summary>
    Timeout,

    /// <summary>The caller (or a Stop action) cancelled the run.</summary>
    Cancelled,

    /// <summary>The model answered, but the answer did not deserialize into a turn intent.</summary>
    InvalidOutput,

    /// <summary>
    /// The response stopped at the output-token cap before a readable answer existed.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="InvalidOutput"/> on purpose. A malformed answer is a model
    /// behaviour problem; this is a budget problem with a configuration fix, and on a reasoning
    /// model it can happen with no visible output at all. Reporting both as "invalid output"
    /// hid a mis-sized cap behind a schema complaint for a whole live session.
    /// </remarks>
    OutputLimitReached
}

/// <summary>The result of one coach agent turn.</summary>
public sealed record CoachAgentTurnResult
{
    public required CoachAgentOutcome Outcome { get; init; }

    /// <summary>The typed intent. Non-null only when <see cref="Outcome"/> is <see cref="CoachAgentOutcome.Completed"/>.</summary>
    public CoachTurnIntent? Intent { get; init; }

    /// <summary>The serialized <c>AgentSession</c> to persist, or null when the run produced no resumable state.</summary>
    public string? AgentSessionJson { get; init; }

    /// <summary>Token and cost counters for budget accounting.</summary>
    public CoachRunUsage Usage { get; init; } = CoachRunUsage.None;

    /// <summary>A short, non-learner-derived reason for a non-completed outcome.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// True when the serialized <c>AgentSession</c> was present but could not be deserialized,
    /// and the turn carried no prior messages to fall back on. The caller must rebuild context
    /// from the ledger and retry exactly once.
    /// </summary>
    public bool RequiresRebuild { get; init; }

    public static CoachAgentTurnResult Failure(CoachAgentOutcome outcome, string reason) =>
        new() { Outcome = outcome, FailureReason = reason };
}

/// <summary>
/// One coach implementation. The baseline plain-agent arm and the (later) harness arm both
/// satisfy this contract, use the same tools, instructions, limits, and typed intent, and are
/// interchangeable behind the <c>Coach:Implementation</c> flag.
/// </summary>
public interface ILearningCoach
{
    /// <summary>Which arm this instance is.</summary>
    CoachImplementation Implementation { get; }

    /// <summary>Runs one turn. Never writes application data — it only returns an intent.</summary>
    Task<CoachAgentTurnResult> RunTurnAsync(CoachAgentTurnRequest request, CancellationToken cancellationToken = default);
}
