namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// A request to start a coach session or to resume one.
/// The server reads the learner from the access token. The request carries no learner identifier.
/// </summary>
public sealed class StartCoachSessionRequest
{
    /// <summary>
    /// True to resume an active session for the same plan date.
    /// False to start a new session and to close the old one.
    /// </summary>
    public bool Resume { get; init; } = true;

    /// <summary>
    /// The user-local plan date. Null tells the server to use the current user-local date.
    /// </summary>
    public DateOnly? PlanDate { get; init; }

    /// <summary>
    /// The first learner message. The largest length is 500 characters.
    /// Null starts the session without a turn.
    /// </summary>
    public string? InitialText { get; init; }
}
