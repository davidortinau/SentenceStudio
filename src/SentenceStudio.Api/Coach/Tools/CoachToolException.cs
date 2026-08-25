namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// The kind of failure a read-only coach tool reports.
/// A tool always fails with one of these kinds. A tool never returns an empty
/// result to hide an authorization failure or an operational failure.
/// </summary>
public enum CoachToolFailureKind
{
    /// <summary>The request has no trusted user scope.</summary>
    Unauthorized = 0,

    /// <summary>An argument is outside its allowed range or set.</summary>
    InvalidArgument,

    /// <summary>The learner has no profile record, so the tool cannot answer.</summary>
    ProfileMissing,

    /// <summary>The planner produced no plan for the supplied constraints.</summary>
    NoFeasiblePlan,

    /// <summary>A data read failed.</summary>
    DataAccess,

    /// <summary>
    /// The turn has spent its tool-call budget, so the call was refused before it ran.
    /// </summary>
    BudgetExhausted
}

/// <summary>
/// The typed failure of a read-only coach tool.
/// The message holds no learner text, no term, and no identifier.
/// </summary>
public sealed class CoachToolException : Exception
{
    public CoachToolException(CoachToolFailureKind kind, string toolName, string reason, Exception? inner = null)
        : base($"{toolName}: {reason}", inner)
    {
        Kind = kind;
        ToolName = toolName;
        Reason = reason;
    }

    /// <summary>The kind of failure.</summary>
    public CoachToolFailureKind Kind { get; }

    /// <summary>The tool that failed.</summary>
    public string ToolName { get; }

    /// <summary>A short reason with no learner data.</summary>
    public string Reason { get; }

    /// <summary>The stable error code for a telemetry tag or a typed client answer.</summary>
    public string Code => Kind switch
    {
        CoachToolFailureKind.Unauthorized => "unauthorized",
        CoachToolFailureKind.InvalidArgument => "invalid_argument",
        CoachToolFailureKind.ProfileMissing => "profile_missing",
        CoachToolFailureKind.NoFeasiblePlan => "no_feasible_plan",
        CoachToolFailureKind.DataAccess => "data_access_failure",
        CoachToolFailureKind.BudgetExhausted => "tool_budget_exhausted",
        _ => "failed"
    };
}
