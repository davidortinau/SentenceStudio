using System.ComponentModel;

namespace SentenceStudio.Contracts.Coach.Intent;

/// <summary>A model-routed request to open an activity. The application checks and builds the launch.</summary>
public sealed class CoachActivityLaunchIntent
{
    [Description("The activity: Conversation, VocabularyQuiz, VocabularyMatching, Cloze, Translation, NumberDrill, Writing, Reading, Shadowing, HowDoYouSay, WordAssociation, Diary, Flashcards, Scene or MinimalPairs.")]
    public string Activity { get; set; } = string.Empty;

    [Description("The language to practise, in English (for example 'German'), only when the learner named one other than the language they study. Empty otherwise.")]
    public string? Language { get; set; }

    [Description("For Conversation: the situation in the learner's own words, such as 'getting directions' or 'ordering coffee'. Empty otherwise.")]
    public string? Scenario { get; set; }

    [Description("A learning resource's identifier from a resource tool or the screen, when the learner named one. Empty otherwise.")]
    public string? ResourceId { get; set; }

    [Description("A skill's identifier from a skill tool or the screen, when the learner named one. Empty otherwise.")]
    public string? SkillId { get; set; }
}
