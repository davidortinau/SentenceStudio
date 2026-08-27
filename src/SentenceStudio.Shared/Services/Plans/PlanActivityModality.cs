using SentenceStudio.Services.Progress;

namespace SentenceStudio.Services.Plans;

/// <summary>
/// Explicit modality classification for every <see cref="PlanActivityType"/>.
/// </summary>
/// <remarks>
/// <para>
/// "Required" means the activity delivers no target-language learning value
/// without that modality — not merely that the activity offers it. An activity
/// that ships a non-typing response path (multiple choice, tap-to-match) is NOT
/// typing-required even though it also accepts typed answers. This is the
/// learning-value reading of the constraint: excluding an activity a learner
/// could still complete would silently shrink their session for no pedagogical
/// reason, while keeping an activity they cannot perceive or answer would
/// produce a plan item with zero L2 exposure.
/// </para>
/// <para>
/// Evidence for each classification (source of truth is the activity page):
/// <list type="bullet">
/// <item><description><c>Listening</c> — audio is the sole L2 input channel.</description></item>
/// <item><description><c>VideoWatching</c> — the deterministic builder only offers it for resources with audio plus a YouTube URL; the L2 signal is the spoken track.</description></item>
/// <item><description><c>Shadowing</c> — <c>Shadowing.razor</c> plays synthesized target-language audio and the learner repeats it aloud: audio AND speech.</description></item>
/// <item><description><c>Writing</c> — <c>Writing.razor</c> exposes a single free-text field, no choice path.</description></item>
/// <item><description><c>SceneDescription</c> — <c>Scene.razor</c> exposes a single free-text field, no choice path.</description></item>
/// <item><description><c>Conversation</c> — <c>Conversation.razor</c> takes typed learner turns; audio is text-to-speech playback only, so it is typing-required and not speech-required.</description></item>
/// <item><description><c>Cloze</c>, <c>Translation</c> — both expose a MultipleChoice/blocks toggle alongside the text field, so neither is typing-required.</description></item>
/// <item><description><c>VocabularyReview</c> — <c>VocabQuiz.razor</c> defaults to MultipleChoice.</description></item>
/// <item><description><c>Reading</c>, <c>VocabularyGame</c>, <c>NumberDrill</c> — recognition/tap activities with no hard modality requirement.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class PlanActivityModality
{
    private static readonly IReadOnlySet<PlanActivityType> AudioRequired = new HashSet<PlanActivityType>
    {
        PlanActivityType.Listening,
        PlanActivityType.VideoWatching,
        PlanActivityType.Shadowing
    };

    private static readonly IReadOnlySet<PlanActivityType> SpeechRequired = new HashSet<PlanActivityType>
    {
        PlanActivityType.Shadowing
    };

    private static readonly IReadOnlySet<PlanActivityType> TypingRequired = new HashSet<PlanActivityType>
    {
        PlanActivityType.Writing,
        PlanActivityType.SceneDescription,
        PlanActivityType.Conversation
    };

    public static bool RequiresAudio(PlanActivityType activityType) => AudioRequired.Contains(activityType);

    public static bool RequiresSpeech(PlanActivityType activityType) => SpeechRequired.Contains(activityType);

    public static bool RequiresTyping(PlanActivityType activityType) => TypingRequired.Contains(activityType);

    /// <summary>
    /// True when the activity type is permitted under the supplied constraints.
    /// <c>null</c> constraints permit everything.
    /// </summary>
    public static bool IsAllowed(PlanActivityType activityType, PlanConstraints? constraints)
    {
        if (constraints is null)
        {
            return true;
        }

        if (!constraints.AudioAllowed && RequiresAudio(activityType))
        {
            return false;
        }

        if (!constraints.SpeechAllowed && RequiresSpeech(activityType))
        {
            return false;
        }

        if (!constraints.TypingAllowed && RequiresTyping(activityType))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// String overload for the deterministic builder, which carries activity
    /// types as strings that match <see cref="PlanActivityType"/> names exactly.
    /// An unrecognized name is permitted so an unclassified future activity
    /// fails open rather than silently vanishing from every constrained plan.
    /// </summary>
    public static bool IsAllowed(string activityType, PlanConstraints? constraints)
    {
        if (constraints is null)
        {
            return true;
        }

        return !TryParse(activityType, out var parsed) || IsAllowed(parsed, constraints);
    }

    /// <summary>
    /// True when the activity type is one the supplied emphasis prefers.
    /// Used as a deterministic ordering key — never as a filter, so emphasis
    /// can reweight but cannot remove work from a plan.
    /// </summary>
    public static bool MatchesEmphasis(PlanActivityType activityType, PlanSkillEmphasis emphasis) => emphasis switch
    {
        PlanSkillEmphasis.Listening => activityType
            is PlanActivityType.Listening
            or PlanActivityType.VideoWatching
            or PlanActivityType.Shadowing,
        PlanSkillEmphasis.Speaking => activityType
            is PlanActivityType.Shadowing
            or PlanActivityType.Conversation,
        PlanSkillEmphasis.Reading => activityType
            is PlanActivityType.Reading
            or PlanActivityType.Cloze,
        PlanSkillEmphasis.Writing => activityType
            is PlanActivityType.Writing
            or PlanActivityType.Translation
            or PlanActivityType.SceneDescription,
        PlanSkillEmphasis.Vocabulary => activityType
            is PlanActivityType.VocabularyReview
            or PlanActivityType.VocabularyGame
            or PlanActivityType.Cloze,
        _ => false
    };

    /// <summary>String overload of <see cref="MatchesEmphasis(PlanActivityType, PlanSkillEmphasis)"/>.</summary>
    public static bool MatchesEmphasis(string activityType, PlanSkillEmphasis emphasis) =>
        TryParse(activityType, out var parsed) && MatchesEmphasis(parsed, emphasis);

    private static bool TryParse(string activityType, out PlanActivityType parsed) =>
        Enum.TryParse(activityType, ignoreCase: false, out parsed);
}
