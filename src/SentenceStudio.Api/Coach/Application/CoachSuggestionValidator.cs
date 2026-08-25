using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>Why a model-proposed suggestion was refused before the learner ever saw it.</summary>
public enum CoachSuggestionRejection
{
    /// <summary>The suggestion holds up.</summary>
    None = 0,

    /// <summary>The merged plan is identical to the current one, so there is nothing to accept.</summary>
    NoEffectiveChange,

    /// <summary>The constraints name a skill the resulting remaining work does not contain.</summary>
    EmphasisNotDelivered,

    /// <summary>The resulting remaining work uses a modality the constraints forbid.</summary>
    ModalityNotHonoured
}

/// <summary>The verdict, plus an operator-readable reason that carries no learner text.</summary>
public sealed record CoachSuggestionVerdict(CoachSuggestionRejection Rejection, string Detail)
{
    public static CoachSuggestionVerdict Accepted { get; } = new(CoachSuggestionRejection.None, string.Empty);

    public bool IsEffective => Rejection == CoachSuggestionRejection.None;
}

/// <summary>
/// Checks a proposed suggestion against the plan the planner would actually build, before the
/// suggestion is stored or shown.
/// </summary>
/// <remarks>
/// <para>
/// The model writes the rationale, so the model decides what the change appears to promise.
/// Nothing stops it saying "this adds speaking practice" over a constraint set whose preview
/// contains no speaking activity — the learner then accepts a change that cannot deliver what
/// they were told, and the receipt proves it afterwards.
/// </para>
/// <para>
/// This validator makes the plan itself the authority. A suggestion survives only when the
/// merged remainder really differs from today's remaining work, really contains the emphasised
/// skill, and really respects the modality switches. A suggestion that fails is dropped: the
/// turn ends with no pending suggestion and no write.
/// </para>
/// <para>
/// Direct learner requests deliberately do <b>not</b> pass through here. The learner asked for
/// them, so the planner's best effort is the right answer even when it cannot honour a skill
/// preference; a no-op direct request is already reported as "no change" by the apply path.
/// </para>
/// </remarks>
public sealed class CoachSuggestionValidator
{
    /// <summary>
    /// Compares the plan the learner has now against the plan the suggestion would produce.
    /// </summary>
    /// <param name="current">Today's plan as it stands.</param>
    /// <param name="merged">The same plan after the proposed constraints, already merged.</param>
    /// <param name="proposed">The constraint set the suggestion would apply.</param>
    public CoachSuggestionVerdict Validate(
        PlanSnapshot current,
        PlanSnapshot merged,
        PlanConstraints proposed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(merged);
        ArgumentNullException.ThrowIfNull(proposed);

        if (string.Equals(merged.Hash, current.Hash, StringComparison.Ordinal))
        {
            return new CoachSuggestionVerdict(
                CoachSuggestionRejection.NoEffectiveChange,
                "The suggested constraints produce the plan the learner already has.");
        }

        var remainder = PlanRevisionPreview.Remainder(merged);

        // Emphasising a skill that the resulting work does not contain is the failure this
        // exists for: the rationale promises active-skill balance, the preview delivers
        // vocabulary and reading.
        if (proposed.SkillEmphasis is { } emphasis
            && !remainder.Any(i => PlanActivityModality.MatchesEmphasis(i.ActivityType, emphasis)))
        {
            return new CoachSuggestionVerdict(
                CoachSuggestionRejection.EmphasisNotDelivered,
                $"No remaining activity matches the {emphasis} emphasis the suggestion sets.");
        }

        foreach (var item in remainder)
        {
            if (!PlanActivityModality.IsAllowed(item.ActivityType, proposed))
            {
                return new CoachSuggestionVerdict(
                    CoachSuggestionRejection.ModalityNotHonoured,
                    $"The remaining {item.ActivityType} activity needs a modality the suggestion turns off.");
            }
        }

        return CoachSuggestionVerdict.Accepted;
    }
}
