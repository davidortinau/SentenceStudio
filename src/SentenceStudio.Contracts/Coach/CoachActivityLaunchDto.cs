using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// An activity Sam opens because the learner asked it to ("start a conversation in German about
/// getting directions"). Built by the server from the model's routing marker after checking every
/// part: the activity is one of the app's, the language is a target language, the scenario is a
/// real one, and any resource or skill is the learner's own.
/// </summary>
/// <remarks>
/// The client shows a short countdown with Cancel and then opens the activity the way its tile
/// would. <see cref="AutoStart"/> is false when the learner's own words did not ask to start
/// something (the model offered it); the client then shows a Start button instead of counting
/// down. A client also never counts down for a reply read back from history.
/// </remarks>
public sealed class CoachActivityLaunchDto
{
    public required CoachActivityKind Activity { get; init; }

    /// <summary>The learner's resources to open it with, by server id. Empty opens it without one.</summary>
    public IReadOnlyList<string> ResourceIds { get; init; } = [];

    /// <summary>The learner's skill to open it with, by server id.</summary>
    public string? SkillId { get; init; }

    /// <summary>
    /// The language to practise, one of the profile's target-language choices, when it differs
    /// from the learner's own. Null keeps the learner's target language.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>A conversation scenario's id (built-in ids start <c>scn:</c>), for Conversation.</summary>
    public string? ScenarioId { get; init; }

    /// <summary>The scenario's English name, for the card ("Asking for Directions").</summary>
    public string? ScenarioName { get; init; }

    /// <summary>True when the learner asked to start it, so the client may open it after the countdown.</summary>
    public bool AutoStart { get; init; }
}

/// <summary>The activities Sam can open. Each maps to the route its tile opens.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown is the documented unset value; a client that cannot name the activity opens nothing.")]
public enum CoachActivityKind
{
    Unknown = 0,
    Conversation = 1,
    VocabularyQuiz = 2,
    VocabularyMatching = 3,
    Cloze = 4,
    Translation = 5,
    NumberDrill = 6,
    Writing = 7,
    Reading = 8,
    Shadowing = 9,
    HowDoYouSay = 10,
    WordAssociation = 11,
    Diary = 12,
    Flashcards = 13,
    Scene = 14,
    MinimalPairs = 15
}

/// <summary>The route each activity's tile opens, the same on web and native.</summary>
public static class CoachActivityRoutes
{
    public static string? Route(CoachActivityKind activity) => activity switch
    {
        CoachActivityKind.Conversation => "conversation",
        CoachActivityKind.VocabularyQuiz => "vocab-quiz",
        CoachActivityKind.VocabularyMatching => "vocab-matching",
        CoachActivityKind.Cloze => "cloze",
        CoachActivityKind.Translation => "translation",
        CoachActivityKind.NumberDrill => "numberdrill",
        CoachActivityKind.Writing => "writing",
        CoachActivityKind.Reading => "reading",
        CoachActivityKind.Shadowing => "shadowing",
        CoachActivityKind.HowDoYouSay => "how-do-you-say",
        CoachActivityKind.WordAssociation => "word-association",
        CoachActivityKind.Diary => "diary",
        CoachActivityKind.Flashcards => "flashcard-activity",
        CoachActivityKind.Scene => "scene",
        CoachActivityKind.MinimalPairs => "minimal-pairs",
        _ => null
    };

    /// <summary>The resource text key of the activity's name, as its tile shows it.</summary>
    public static string TitleKey(CoachActivityKind activity) => activity switch
    {
        CoachActivityKind.VocabularyQuiz => "Activity_VocabQuiz",
        CoachActivityKind.VocabularyMatching => "Activity_VocabMatching",
        CoachActivityKind.Translation => "Activity_Translate",
        CoachActivityKind.Scene => "Activity_DescribeScene",
        CoachActivityKind.Flashcards => "Activity_VocabularyReview",
        CoachActivityKind.Unknown => "Activity_Generic",
        _ => "Activity_" + activity
    };
}
