namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// One learner turn. The learner sends text, a chip, or a structured constraint action.
/// The request carries no learner identifier. The server reads the learner from the access token.
/// </summary>
public sealed class CoachTurnRequest
{
    /// <summary>What the learner sent.</summary>
    public required CoachTurnInputKind InputKind { get; init; }

    /// <summary>
    /// The learner text. The largest length is 500 characters.
    /// Use this member when InputKind is Text.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// The chip identifier. Use this member when InputKind is Chip.
    /// The server maps the chip to a constraint change.
    /// </summary>
    public string? ChipId { get; init; }

    /// <summary>
    /// The constraint change from a control. Use this member when InputKind is ConstraintAction.
    /// The server treats this change as a direct request.
    /// </summary>
    public CoachConstraintDeltaDto? ConstraintAction { get; init; }

    /// <summary>
    /// The suggestion this turn answers. Null if the turn answers no suggestion.
    /// The server applies a change only when this value matches the current suggestion.
    /// </summary>
    public string? PendingSuggestionId { get; init; }

    /// <summary>
    /// The plan version the client shows now.
    /// The server rejects the turn when this version is old.
    /// </summary>
    public string? ExpectedPlanVersion { get; init; }

    /// <summary>
    /// A client identifier for this turn. The server uses it to drop a repeated request.
    /// </summary>
    public string? ClientTurnId { get; init; }

    /// <summary>
    /// What this client build says it can render, for this turn only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optional. A turn with no handshake is the normal case and loses nothing: the handshake can
    /// only ever raise the ceiling on reversible presentation capabilities, so its absence simply
    /// means none of those are offered.
    /// </para>
    /// <para>
    /// The server merges it for the duration of the turn and discards it. It is never persisted,
    /// never logged, and never an authorization source for a durable write, an external effect, or
    /// an activity launch. See <see cref="CoachClientCapabilityHandshake"/>.
    /// </para>
    /// </remarks>
    public CoachClientCapabilityHandshake? ClientCapabilities { get; init; }
}
