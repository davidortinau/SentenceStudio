namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// The full set of active study constraints for one session.
/// The server normalizes and clamps every value before it sends this set.
/// This set is closed. The coach cannot add a new constraint field.
/// </summary>
public sealed class CoachConstraintSetDto
{
    /// <summary>The session length in minutes. The range is 3 to 90.</summary>
    public required int AvailableMinutes { get; init; }

    /// <summary>True if audio playback is allowed.</summary>
    public required bool AudioAllowed { get; init; }

    /// <summary>True if speech input is allowed.</summary>
    public required bool SpeechAllowed { get; init; }

    /// <summary>True if typed input is allowed.</summary>
    public required bool TypingAllowed { get; init; }

    /// <summary>The skill to emphasize. Null means no emphasis.</summary>
    public CoachSkillEmphasis? SkillEmphasis { get; init; }

    /// <summary>A server-owned goal tag, or "other". Null means no goal tag.</summary>
    public string? GoalTag { get; init; }

    /// <summary>The goal horizon in days. The range is 1 to 180. Null means no horizon.</summary>
    public int? GoalHorizonDays { get; init; }

    /// <summary>The energy level for this session.</summary>
    public required CoachEnergyLevel EnergyLevel { get; init; }

    /// <summary>
    /// The vocabulary focus in force, with the words the server selected. Null when the plan has
    /// no focus.
    /// </summary>
    public CoachVocabularyFocusDto? VocabularyFocus { get; init; }
}
