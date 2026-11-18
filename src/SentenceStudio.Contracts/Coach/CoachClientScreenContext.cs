namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// What the client was showing when the learner sent a turn, so a bug report or request filed
/// through Sam can say where it happened.
/// </summary>
/// <remarks>
/// <para>
/// Two halves with different audiences. The <b>public</b> fields describe the build and the
/// screen in identifiers only — no learner text — and may appear in a public GitHub issue. The
/// <see cref="State"/> map and <see cref="Route"/> may carry learner content (an item id, a
/// filter the learner typed) and are only ever stored privately, never published.
/// </para>
/// <para>
/// Optional and advisory. The server bounds and sanitises every field, keeps it for the turn
/// only, and stores it only when the learner approves a feedback report. It is never an
/// authorization source.
/// </para>
/// <para>
/// Sam also reads it, for that turn only, through its current-screen tool, so "this word" or
/// "add it here" can mean what the learner is looking at. That is why <see cref="Items"/> carries
/// the identifiers Sam's other tools take: every one of those tools checks ownership again, so an
/// identifier here grants nothing on its own.
/// </para>
/// </remarks>
public sealed class CoachClientScreenContext
{
    /// <summary>Stable screen identifier, for example <c>native.vocabulary.list</c> or a web route template. Public.</summary>
    public string? ScreenId { get; init; }

    /// <summary>Client surface, for example <c>ios</c>, <c>android</c>, <c>web</c>, <c>maui-ios</c>. Public.</summary>
    public string? Platform { get; init; }

    /// <summary>App version and build configuration, for example <c>1.4.0 (Debug)</c>. Public.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Operating system and version, for example <c>iOS 26.1</c>. Public.</summary>
    public string? OsVersion { get; init; }

    /// <summary>Device model or browser, for example <c>iPhone18,1</c>. Public.</summary>
    public string? Device { get; init; }

    /// <summary>UI culture, for example <c>en-US</c> or <c>ko-KR</c>. Public.</summary>
    public string? Locale { get; init; }

    /// <summary><c>light</c> or <c>dark</c>. Public.</summary>
    public string? Theme { get; init; }

    /// <summary>Automation ids of the controls currently on screen. Ids only, never labels. Public.</summary>
    public IReadOnlyList<string>? VisibleControlIds { get; init; }

    /// <summary>The concrete route or destination, which may contain ids. Private.</summary>
    public string? Route { get; init; }

    /// <summary>Screen state as short key/value pairs (selected item, open sheet, filter). Private.</summary>
    public IReadOnlyList<CoachClientScreenStateEntry>? State { get; init; }

    /// <summary>
    /// What the screen is showing or editing: the word being quizzed, the resource open, the skill
    /// being edited, the activity running. The first item marked <see cref="CoachClientScreenItem.Focus"/>
    /// is what "this" means. Private.
    /// </summary>
    public IReadOnlyList<CoachClientScreenItem>? Items { get; init; }

    /// <summary>
    /// True while the screen keeps an answer from the learner, such as a quiz question not yet
    /// answered. Sam must not reveal it until the learner has answered. Private.
    /// </summary>
    public bool AnswerHidden { get; init; }

    /// <summary>
    /// Short facts about what the page shows (quiz mode, turn, prompt language, a draft), for Sam
    /// only. Kept apart from <see cref="State"/> because State is also stored with a feedback
    /// report, and a learner's draft or quiz state has no place there. Private.
    /// </summary>
    public IReadOnlyList<CoachClientScreenStateEntry>? Facts { get; init; }
}

/// <summary>One piece of private screen state, for example <c>selectedResourceId</c> = <c>42</c>.</summary>
public sealed class CoachClientScreenStateEntry
{
    public required string Key { get; init; }

    public string? Value { get; init; }
}

/// <summary>
/// One thing on screen, named so Sam's tools can act on it. <see cref="Kind"/> is one of
/// <see cref="CoachClientScreenItemKinds"/>; <see cref="Id"/> is the identifier the matching Sam tool
/// takes (a word id for a vocabulary word, a resource id for a resource), or null when the thing
/// has none yet, such as a new word being typed.
/// </summary>
public sealed class CoachClientScreenItem
{
    /// <summary>
    /// One of <see cref="CoachClientScreenItemKinds"/>. Not required on the wire: the screen is
    /// advisory, so an item a client sends without a kind is dropped, not a reason to fail the turn.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    public string? Id { get; init; }

    /// <summary>What the learner sees for it: the word, the resource title, the activity name.</summary>
    public string? Label { get; init; }

    /// <summary>Secondary visible text, such as the meaning once the quiz shows it.</summary>
    public string? Detail { get; init; }

    /// <summary>True for the main thing on screen, the one "this" refers to.</summary>
    public bool Focus { get; init; }
}

/// <summary>The item kinds every client uses, shared so the same thing has the same name everywhere.</summary>
public static class CoachClientScreenItemKinds
{
    public const string VocabularyWord = "vocabulary-word";
    public const string ExampleSentence = "example-sentence";
    public const string LearningResource = "learning-resource";
    public const string Skill = "skill";
    public const string MinimalPair = "minimal-pair";
    public const string Activity = "activity";
    public const string PlanItem = "plan-item";
    public const string YouTubeChannel = "youtube-channel";
    public const string Text = "text";

    public static IReadOnlyList<string> All { get; } =
    [
        VocabularyWord, ExampleSentence, LearningResource, Skill, MinimalPair, Activity, PlanItem,
        YouTubeChannel, Text
    ];
}
