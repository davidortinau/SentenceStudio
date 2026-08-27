namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// A change to one or more study constraints.
/// A null member means "do not change this field".
/// The server validates every member before it applies the change.
/// </summary>
public sealed class CoachConstraintDeltaDto
{
    /// <summary>The new session length in minutes. The range is 3 to 90.</summary>
    public int? AvailableMinutes { get; init; }

    /// <summary>The new value for audio playback.</summary>
    public bool? AudioAllowed { get; init; }

    /// <summary>The new value for speech input.</summary>
    public bool? SpeechAllowed { get; init; }

    /// <summary>The new value for typed input.</summary>
    public bool? TypingAllowed { get; init; }

    /// <summary>The new skill emphasis.</summary>
    public CoachSkillEmphasis? SkillEmphasis { get; init; }

    /// <summary>True to clear the skill emphasis. Use this member to set no emphasis.</summary>
    public bool ClearSkillEmphasis { get; init; }

    /// <summary>The new goal tag. Use a server-owned tag or "other".</summary>
    public string? GoalTag { get; init; }

    /// <summary>True to clear the goal tag.</summary>
    public bool ClearGoalTag { get; init; }

    /// <summary>The new goal horizon in days. The range is 1 to 180.</summary>
    public int? GoalHorizonDays { get; init; }

    /// <summary>True to clear the goal horizon.</summary>
    public bool ClearGoalHorizonDays { get; init; }

    /// <summary>The new energy level.</summary>
    public CoachEnergyLevel? EnergyLevel { get; init; }

    /// <summary>
    /// The learner's own words for a vocabulary focus, for example "active verbs". The server maps
    /// this to a canonical focus code through a controlled registry and then resolves it against
    /// the learner's vocabulary; it is never matched against terms or glosses directly.
    /// </summary>
    public string? VocabularyFocusDescription { get; init; }

    /// <summary>True to clear the vocabulary focus.</summary>
    public bool ClearVocabularyFocus { get; init; }

    /// <summary>
    /// The fields this change touches. The server fills this list.
    /// An empty list means the change is empty and the server does not write.
    /// </summary>
    public IReadOnlyList<CoachConstraintField> ChangedFields { get; init; } = Array.Empty<CoachConstraintField>();
}
