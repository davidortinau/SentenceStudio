using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>The result of turning a model-proposed delta into an application constraint delta.</summary>
public sealed record CoachConstraintMapResult(
    CoachConstraintDeltaDto? Delta,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0 && Delta is not null;

    public bool HasChange => IsValid && Delta!.ChangedFields.Count > 0;
}

/// <summary>
/// Maps between the model's constraint delta, the application's constraint set, and the
/// planner's <see cref="PlanConstraints"/>.
/// </summary>
/// <remarks>
/// This is the choke point that stops the model widening its own authority. It reads only the
/// eight known constraint fields; a user id, a plan item id, or any other property on the
/// intent object is dropped on the floor because it is never read here.
/// </remarks>
public sealed class CoachConstraintMapper
{
    /// <summary>The constraint set used when a learner has no coach session yet.</summary>
    public static CoachConstraintSetDto Default(int preferredSessionMinutes) => new()
    {
        AvailableMinutes = Math.Clamp(
            preferredSessionMinutes <= 0 ? 15 : preferredSessionMinutes,
            CoachConstraintLimits.MinAvailableMinutes,
            CoachConstraintLimits.MaxAvailableMinutes),
        AudioAllowed = true,
        SpeechAllowed = true,
        TypingAllowed = true,
        SkillEmphasis = null,
        GoalTag = null,
        GoalHorizonDays = null,
        EnergyLevel = CoachEnergyLevel.Normal
    };

    /// <summary>Validates and normalizes a model-proposed delta.</summary>
    public CoachConstraintMapResult FromIntent(CoachConstraintDeltaIntent? intent)
    {
        if (intent is null)
        {
            return new CoachConstraintMapResult(Empty(), Array.Empty<string>());
        }

        var errors = new List<string>();
        var changed = new List<CoachConstraintField>();

        if (intent.AvailableMinutes is { } minutes)
        {
            if (minutes < CoachConstraintLimits.MinAvailableMinutes || minutes > CoachConstraintLimits.MaxAvailableMinutes)
            {
                errors.Add(
                    $"AvailableMinutes must be between {CoachConstraintLimits.MinAvailableMinutes} and {CoachConstraintLimits.MaxAvailableMinutes}.");
            }
            else
            {
                changed.Add(CoachConstraintField.AvailableMinutes);
            }
        }

        if (intent.AudioAllowed is not null)
        {
            changed.Add(CoachConstraintField.AudioAllowed);
        }

        if (intent.SpeechAllowed is not null)
        {
            changed.Add(CoachConstraintField.SpeechAllowed);
        }

        if (intent.TypingAllowed is not null)
        {
            changed.Add(CoachConstraintField.TypingAllowed);
        }

        if (intent.SkillEmphasis is { } emphasis)
        {
            if (!Enum.IsDefined(emphasis))
            {
                errors.Add("SkillEmphasis is not one of the allowed skills.");
            }
            else if (intent.ClearSkillEmphasis)
            {
                errors.Add("SkillEmphasis cannot be set and cleared in the same change.");
            }
            else
            {
                changed.Add(CoachConstraintField.SkillEmphasis);
            }
        }
        else if (intent.ClearSkillEmphasis)
        {
            changed.Add(CoachConstraintField.SkillEmphasis);
        }

        var goalTag = NormalizeGoalTag(intent.GoalTag);
        if (goalTag is not null)
        {
            if (intent.ClearGoalTag)
            {
                errors.Add("GoalTag cannot be set and cleared in the same change.");
            }
            else if (goalTag.Length > CoachConstraintLimits.MaxGoalTagLength)
            {
                errors.Add($"GoalTag must be {CoachConstraintLimits.MaxGoalTagLength} characters or fewer.");
            }
            else
            {
                changed.Add(CoachConstraintField.GoalTag);
            }
        }
        else if (intent.ClearGoalTag)
        {
            changed.Add(CoachConstraintField.GoalTag);
        }

        if (intent.GoalHorizonDays is { } horizon)
        {
            if (intent.ClearGoalHorizonDays)
            {
                errors.Add("GoalHorizonDays cannot be set and cleared in the same change.");
            }
            else if (horizon < CoachConstraintLimits.MinGoalHorizonDays || horizon > CoachConstraintLimits.MaxGoalHorizonDays)
            {
                errors.Add(
                    $"GoalHorizonDays must be between {CoachConstraintLimits.MinGoalHorizonDays} and {CoachConstraintLimits.MaxGoalHorizonDays}.");
            }
            else
            {
                changed.Add(CoachConstraintField.GoalHorizonDays);
            }
        }
        else if (intent.ClearGoalHorizonDays)
        {
            changed.Add(CoachConstraintField.GoalHorizonDays);
        }

        if (intent.EnergyLevel is { } energy)
        {
            if (!Enum.IsDefined(energy))
            {
                errors.Add("EnergyLevel is not one of the allowed levels.");
            }
            else
            {
                changed.Add(CoachConstraintField.EnergyLevel);
            }
        }

        // The model may describe a focus; it may not select one. The description is bounded here
        // and mapped by the controlled registry later, after the whole intent has been validated.
        var focusDescription = CoachVocabularyFocusAliases.Normalize(intent.VocabularyFocusDescription);
        if (!string.IsNullOrWhiteSpace(intent.VocabularyFocusDescription))
        {
            if (intent.ClearVocabularyFocus)
            {
                errors.Add("VocabularyFocus cannot be set and cleared in the same change.");
            }
            else if (focusDescription is null)
            {
                errors.Add(
                    $"VocabularyFocusDescription must be {CoachVocabularyFocusAliases.MaxDescriptionLength} " +
                    $"characters or fewer and {CoachVocabularyFocusAliases.MaxDescriptionWords} words or fewer.");
            }
            else
            {
                changed.Add(CoachConstraintField.VocabularyFocus);
            }
        }
        else if (intent.ClearVocabularyFocus)
        {
            changed.Add(CoachConstraintField.VocabularyFocus);
        }

        if (errors.Count > 0)
        {
            return new CoachConstraintMapResult(null, errors);
        }

        var delta = new CoachConstraintDeltaDto
        {
            VocabularyFocusDescription = intent.ClearVocabularyFocus ? null : focusDescription,
            ClearVocabularyFocus = intent.ClearVocabularyFocus,
            AvailableMinutes = intent.AvailableMinutes,
            AudioAllowed = intent.AudioAllowed,
            SpeechAllowed = intent.SpeechAllowed,
            TypingAllowed = intent.TypingAllowed,
            SkillEmphasis = intent.ClearSkillEmphasis ? null : intent.SkillEmphasis,
            ClearSkillEmphasis = intent.ClearSkillEmphasis,
            GoalTag = intent.ClearGoalTag ? null : goalTag,
            ClearGoalTag = intent.ClearGoalTag,
            GoalHorizonDays = intent.ClearGoalHorizonDays ? null : intent.GoalHorizonDays,
            ClearGoalHorizonDays = intent.ClearGoalHorizonDays,
            EnergyLevel = intent.EnergyLevel,
            ChangedFields = changed
        };

        return new CoachConstraintMapResult(delta, Array.Empty<string>());
    }

    /// <summary>
    /// Validates and normalizes a delta that arrived straight from the client as a structured
    /// UI constraint action. The client is no more trusted than the model here.
    /// </summary>
    public CoachConstraintMapResult FromClient(CoachConstraintDeltaDto? delta)
    {
        if (delta is null)
        {
            return new CoachConstraintMapResult(null, new[] { "A constraint action needs a constraint change." });
        }

        return FromIntent(new CoachConstraintDeltaIntent
        {
            AvailableMinutes = delta.AvailableMinutes,
            AudioAllowed = delta.AudioAllowed,
            SpeechAllowed = delta.SpeechAllowed,
            TypingAllowed = delta.TypingAllowed,
            SkillEmphasis = delta.SkillEmphasis,
            ClearSkillEmphasis = delta.ClearSkillEmphasis,
            GoalTag = delta.GoalTag,
            ClearGoalTag = delta.ClearGoalTag,
            GoalHorizonDays = delta.GoalHorizonDays,
            ClearGoalHorizonDays = delta.ClearGoalHorizonDays,
            EnergyLevel = delta.EnergyLevel,
            VocabularyFocusDescription = delta.VocabularyFocusDescription,
            ClearVocabularyFocus = delta.ClearVocabularyFocus
        });
    }

    /// <summary>Applies a validated delta to the active constraint set.</summary>
    public CoachConstraintSetDto Apply(CoachConstraintSetDto current, CoachConstraintDeltaDto delta) =>
        Apply(current, delta, null);

    /// <summary>
    /// Applies a validated delta, carrying a focus the application resolved for this change.
    /// </summary>
    /// <remarks>
    /// A focus survives a change that does not mention it. Dropping it on an unrelated edit would
    /// silently widen the plan back to all vocabulary, which is a change the learner never asked
    /// for and the receipt would not disclose.
    /// </remarks>
    public CoachConstraintSetDto Apply(
        CoachConstraintSetDto current,
        CoachConstraintDeltaDto delta,
        CoachVocabularyFocusDto? resolvedFocus)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(delta);

        return new CoachConstraintSetDto
        {
            VocabularyFocus = delta.ClearVocabularyFocus
                ? null
                : resolvedFocus ?? current.VocabularyFocus,
            AvailableMinutes = delta.AvailableMinutes ?? current.AvailableMinutes,
            AudioAllowed = delta.AudioAllowed ?? current.AudioAllowed,
            SpeechAllowed = delta.SpeechAllowed ?? current.SpeechAllowed,
            TypingAllowed = delta.TypingAllowed ?? current.TypingAllowed,
            SkillEmphasis = delta.ClearSkillEmphasis ? null : delta.SkillEmphasis ?? current.SkillEmphasis,
            GoalTag = delta.ClearGoalTag ? null : delta.GoalTag ?? current.GoalTag,
            GoalHorizonDays = delta.ClearGoalHorizonDays ? null : delta.GoalHorizonDays ?? current.GoalHorizonDays,
            EnergyLevel = delta.EnergyLevel ?? current.EnergyLevel
        };
    }

    /// <summary>Projects the constraint set onto the planner's value type.</summary>
    public PlanConstraints ToPlanConstraints(CoachConstraintSetDto constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);

        return new PlanConstraints
        {
            AvailableMinutes = constraints.AvailableMinutes,
            AudioAllowed = constraints.AudioAllowed,
            SpeechAllowed = constraints.SpeechAllowed,
            TypingAllowed = constraints.TypingAllowed,
            SkillEmphasis = constraints.SkillEmphasis switch
            {
                CoachSkillEmphasis.Listening => PlanSkillEmphasis.Listening,
                CoachSkillEmphasis.Speaking => PlanSkillEmphasis.Speaking,
                CoachSkillEmphasis.Reading => PlanSkillEmphasis.Reading,
                CoachSkillEmphasis.Writing => PlanSkillEmphasis.Writing,
                CoachSkillEmphasis.Vocabulary => PlanSkillEmphasis.Vocabulary,
                _ => null
            },
            GoalTag = string.IsNullOrWhiteSpace(constraints.GoalTag) ? null : constraints.GoalTag,
            GoalHorizonDays = constraints.GoalHorizonDays,
            EnergyLevel = constraints.EnergyLevel == CoachEnergyLevel.Low
                ? PlanEnergyLevel.Low
                : PlanEnergyLevel.Normal
        };
    }

    /// <summary>A one-line, enum-only summary of what a delta changed. Carries no learner text.</summary>
    public static string Summarize(CoachConstraintDeltaDto delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        return delta.ChangedFields.Count == 0
            ? "No constraints changed."
            : "Updated " + string.Join(", ", delta.ChangedFields.Select(f => f.ToString())) + ".";
    }

    private static CoachConstraintDeltaDto Empty() => new() { ChangedFields = Array.Empty<CoachConstraintField>() };

    private static string? NormalizeGoalTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var buffer = new char[trimmed.Length];
        var length = 0;
        foreach (var c in trimmed)
        {
            buffer[length++] = char.IsControl(c) ? ' ' : c;
        }

        return new string(buffer, 0, length).Trim();
    }
}
