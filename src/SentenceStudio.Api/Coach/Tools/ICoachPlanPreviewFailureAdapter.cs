using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// Explains why a plan preview produced no plan.
/// </summary>
/// <remarks>
/// The deterministic planner returns <c>null</c> today and states no reason.
/// This adapter is the one place that turns that answer into a typed failure.
/// When the planner returns a richer result type with reasons, change this
/// adapter only. The tool and its tests stay the same.
/// </remarks>
public interface ICoachPlanPreviewFailureAdapter
{
    /// <summary>Builds the typed failure for an empty preview.</summary>
    CoachToolException Describe(PlanConstraints constraints, PlanSkeleton? skeleton);
}

/// <summary>
/// The default adapter. It reports <c>no_feasible_plan</c> and names the
/// constraints that most often remove every activity.
/// </summary>
public sealed class DefaultCoachPlanPreviewFailureAdapter : ICoachPlanPreviewFailureAdapter
{
    public CoachToolException Describe(PlanConstraints constraints, PlanSkeleton? skeleton)
    {
        var blocked = new List<string>();
        if (!constraints.AudioAllowed)
        {
            blocked.Add("audio is off");
        }
        if (!constraints.SpeechAllowed)
        {
            blocked.Add("speech is off");
        }
        if (!constraints.TypingAllowed)
        {
            blocked.Add("typing is off");
        }
        if (constraints.AvailableMinutes is { } minutes)
        {
            blocked.Add($"the session is {minutes} minutes");
        }

        var detail = blocked.Count == 0
            ? "The planner produced no plan for these constraints."
            : $"The planner produced no plan because {string.Join(", ", blocked)}.";

        return new CoachToolException(
            CoachToolFailureKind.NoFeasiblePlan,
            CoachToolNames.PreviewPracticePlan,
            detail);
    }
}
