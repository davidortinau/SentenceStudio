namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// A tapped decision on the suggestion that waits for an answer.
/// The route holds the session identifier and the suggestion identifier.
/// The server applies the change once, even if the client sends the request twice.
/// </summary>
public sealed class CoachSuggestionDecisionRequest
{
    /// <summary>
    /// The plan version the client shows now.
    /// The server rejects the request when this version is old.
    /// </summary>
    public string? ExpectedPlanVersion { get; init; }

    /// <summary>
    /// A client identifier for this decision. The server uses it to drop a repeated request.
    /// </summary>
    public string? ClientTurnId { get; init; }
}

/// <summary>
/// A request to undo the last applied coach revision.
/// An undo never changes completed work. An undo never lowers the logged minutes.
/// The server applies the undo once, even if the client sends the request twice.
/// </summary>
public sealed class CoachUndoRequest
{
    /// <summary>
    /// The revision to undo. Null tells the server to undo the last revision.
    /// </summary>
    public string? RevisionId { get; init; }

    /// <summary>
    /// The plan version the client shows now.
    /// The server rejects the request when this version is old.
    /// </summary>
    public string? ExpectedPlanVersion { get; init; }

    /// <summary>
    /// A client identifier for this undo. The server uses it to drop a repeated request.
    /// </summary>
    public string? ClientTurnId { get; init; }
}
