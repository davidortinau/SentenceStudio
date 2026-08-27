namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// The problem type identifiers for coach errors.
/// Use these values in the type member of a problem+json body.
/// The set is closed. Do not send a type that is not in this list.
/// </summary>
public static class CoachProblemTypes
{
    private const string Prefix = "https://sentencestudio.dev/problems/";

    /// <summary>The feature flag is off, or the learner is not in the pilot group.</summary>
    public const string Unavailable = Prefix + "coach-unavailable";

    /// <summary>The session does not exist, or the learner does not own it.</summary>
    public const string SessionNotFound = Prefix + "coach-session-not-found";

    /// <summary>The session is past its expiry time.</summary>
    public const string SessionExpired = Prefix + "coach-session-expired";

    /// <summary>The turn input is empty, too long, or not allowed.</summary>
    public const string InvalidTurnInput = Prefix + "coach-invalid-turn-input";

    /// <summary>A constraint value is outside its allowed range.</summary>
    public const string InvalidConstraint = Prefix + "coach-invalid-constraint";

    /// <summary>The suggestion does not exist, or it is not the current suggestion.</summary>
    public const string SuggestionNotFound = Prefix + "coach-suggestion-not-found";

    /// <summary>The client sent an old plan version. The server did not write.</summary>
    public const string PlanVersionConflict = Prefix + "coach-plan-version-conflict";

    /// <summary>The deterministic plan check failed. The server did not write.</summary>
    public const string PlanValidationFailed = Prefix + "coach-plan-validation-failed";

    /// <summary>There is no revision to undo.</summary>
    public const string NothingToUndo = Prefix + "coach-nothing-to-undo";

    /// <summary>The learner hit the daily or the weekly run limit.</summary>
    public const string RateLimited = Prefix + "coach-rate-limited";

    /// <summary>Another run for the same learner is in progress.</summary>
    public const string RunInProgress = Prefix + "coach-run-in-progress";

    /// <summary>The turn did not finish in the allowed time.</summary>
    public const string Timeout = Prefix + "coach-timeout";

    /// <summary>A read-only tool failed.</summary>
    public const string ToolFailure = Prefix + "coach-tool-failure";

    /// <summary>The conversation does not exist for this learner, or was deleted.</summary>
    /// <remarks>
    /// Another learner's conversation returns this too. A distinct "forbidden" answer would
    /// confirm the id exists, which is the whole thing owner scoping is meant to hide.
    /// </remarks>
    public const string ConversationNotFound = Prefix + "coach-conversation-not-found";

    /// <summary>The conversation changed since the version the request carried.</summary>
    public const string ConversationStateConflict = Prefix + "coach-conversation-state-conflict";

    /// <summary>The same idempotency key arrived with a different request body.</summary>
    public const string IdempotencyConflict = Prefix + "coach-idempotency-conflict";

    /// <summary>A page cursor was tampered with, expired, or belongs to another learner.</summary>
    public const string InvalidCursor = Prefix + "coach-invalid-cursor";

    /// <summary>The turn was cancelled before anything was applied.</summary>
    public const string RunCancelled = Prefix + "coach-run-cancelled";
}
