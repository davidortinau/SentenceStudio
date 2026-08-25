using System.ComponentModel;

namespace SentenceStudio.Contracts.Coach.Intent;

/// <summary>
/// A constraint change the model reads from the learner text.
/// The model can set these fields only. The model cannot add a new field.
/// The application validates and clamps every value before a write.
/// </summary>
[Description("A change to the study constraints. Set only the fields the learner mentioned. Leave the other fields empty.")]
public sealed class CoachConstraintDeltaIntent
{
    [Description("The minutes the learner has for this session. The range is 3 to 90. Leave empty if the learner did not state a time.")]
    public int? AvailableMinutes { get; set; }

    [Description("True if the learner can listen to audio. False if the learner cannot listen to audio. Leave empty if the learner did not state this.")]
    public bool? AudioAllowed { get; set; }

    [Description("True if the learner can speak. False if the learner cannot speak. Leave empty if the learner did not state this.")]
    public bool? SpeechAllowed { get; set; }

    [Description("True if the learner can type. False if the learner cannot type. Leave empty if the learner did not state this.")]
    public bool? TypingAllowed { get; set; }

    [Description("The skill the learner wants to work on. Leave empty if the learner did not state a skill.")]
    public CoachSkillEmphasis? SkillEmphasis { get; set; }

    [Description("True if the learner wants no skill emphasis. Leave false in all other cases.")]
    public bool ClearSkillEmphasis { get; set; }

    [Description("The goal the learner named. Use a goal tag from the context, or the value other. Leave empty if the learner did not name a goal.")]
    public string? GoalTag { get; set; }

    [Description("True if the learner wants no goal. Leave false in all other cases.")]
    public bool ClearGoalTag { get; set; }

    [Description("The days until the goal. The range is 1 to 180. Leave empty if the learner did not state a date or a number of days.")]
    public int? GoalHorizonDays { get; set; }

    [Description("True if the learner wants no goal date. Leave false in all other cases.")]
    public bool ClearGoalHorizonDays { get; set; }

    [Description("The energy level of the learner. Use Low only if the learner said they are tired or have low energy. Leave empty if the learner did not state this.")]
    public CoachEnergyLevel? EnergyLevel { get; set; }

    /// <summary>
    /// The learner's own words for a vocabulary focus, at most 80 characters and 8 words, for
    /// example "active verbs".
    /// </summary>
    /// <remarks>
    /// This is the only vocabulary field the model may fill, and it is a description, not a
    /// selection. Naming a part of speech, an identifier, a term, a gloss, a count, or a tag is a
    /// contract violation: the server owns the mapping from these words to a canonical focus, and
    /// owns the query that turns a focus into actual vocabulary.
    /// </remarks>
    [Description("The learner's own words for the kind of word they want to work on, for example \"active verbs\". At most 80 characters and 8 words. Do not name a part of speech tag, a word, a translation, a category, a count, or an identifier. Leave empty if the learner did not ask to focus on a kind of word.")]
    public string? VocabularyFocusDescription { get; set; }

    /// <summary>True to clear the vocabulary focus.</summary>
    [Description("True if the learner wants no vocabulary focus. Leave false in all other cases.")]
    public bool ClearVocabularyFocus { get; set; }
}
