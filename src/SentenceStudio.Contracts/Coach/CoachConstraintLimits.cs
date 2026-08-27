namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// The bounds for coach input and coach constraints.
/// The server applies these bounds. The client uses them for early validation.
/// </summary>
public static class CoachConstraintLimits
{
    /// <summary>The smallest session length in minutes.</summary>
    public const int MinAvailableMinutes = 3;

    /// <summary>The largest session length in minutes.</summary>
    public const int MaxAvailableMinutes = 90;

    /// <summary>The smallest goal horizon in days.</summary>
    public const int MinGoalHorizonDays = 1;

    /// <summary>The largest goal horizon in days.</summary>
    public const int MaxGoalHorizonDays = 180;

    /// <summary>The largest length of a goal tag.</summary>
    public const int MaxGoalTagLength = 40;

    /// <summary>The goal tag for a goal that has no server-owned tag.</summary>
    public const string OtherGoalTag = "other";

    /// <summary>The largest length of learner text in one turn.</summary>
    public const int MaxTurnTextLength = 500;

    /// <summary>The largest length of a chip identifier.</summary>
    public const int MaxChipIdLength = 64;

    /// <summary>The largest number of clarification questions in one session.</summary>
    public const int MaxClarificationsPerSession = 2;
}
