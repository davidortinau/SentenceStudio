using System.Collections.Immutable;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Native;

/// <summary>
/// One native learning activity. <see cref="Route"/> is the source
/// Choose My Own tile id (Index.razor tile list) and the stable key heads use
/// to pick a screen; <see cref="PlanTypes"/> are the Today's Plan types that
/// open it (PlanActivityPresentation.Route()). The first plan type is the
/// timer/ad-hoc row type. <see cref="TimerRecordsSkill"/> mirrors whether the
/// source page passes its skill to ActivityTimer.StartSession (Matching,
/// NumberDrill and VideoWatching pass null).
/// </summary>
public sealed record NativeActivityDefinition(
    string Route, ImmutableArray<PlanActivityType> PlanTypes, bool TimerRecordsSkill = true)
{
    public PlanActivityType? TimerType => PlanTypes.IsDefaultOrEmpty ? null : PlanTypes[0];
}

/// <summary>
/// Registry of activities that have a working native screen. To add an
/// activity, append one definition here; plan cards and Choose My Own tiles
/// for every other route/type keep their disabled presentation. The Vocabulary
/// Quiz keeps its dedicated launch path and is not listed here.
/// </summary>
public static class NativeActivityCatalog
{
    public const string VocabularyMatching = "vocab-matching";
    public const string Cloze = "cloze";
    public const string Translation = "translation";
    public const string NumberDrill = "numberdrill";
    public const string Writing = "writing";
    public const string Reading = "reading";
    public const string Shadowing = "shadowing";
    public const string Conversation = "conversation";

    public static ImmutableArray<NativeActivityDefinition> Implemented { get; } =
    [
        new(VocabularyMatching, [PlanActivityType.VocabularyGame], TimerRecordsSkill: false),
        new(Cloze, [PlanActivityType.Cloze]),
        new(Translation, [PlanActivityType.Translation]),
        new(NumberDrill, [PlanActivityType.NumberDrill], TimerRecordsSkill: false),
        new(Writing, [PlanActivityType.Writing]),
        new(Reading, [PlanActivityType.Reading]),
        // No dedicated Listening page: plan type Listening shares the Shadowing UI.
        new(Shadowing, [PlanActivityType.Shadowing, PlanActivityType.Listening]),
        new(Conversation, [PlanActivityType.Conversation]),
    ];

    public static NativeActivityDefinition? ForRoute(string? route) =>
        Implemented.FirstOrDefault(definition =>
            string.Equals(definition.Route, route, StringComparison.Ordinal));

    public static NativeActivityDefinition? ForPlanType(PlanActivityType type) =>
        Implemented.FirstOrDefault(definition => definition.PlanTypes.Contains(type));

    public static bool IsImplemented(string? route) => ForRoute(route) is not null;

    public static bool IsImplemented(PlanActivityType type) => ForPlanType(type) is not null;

    /// <summary>
    /// Source Index.razor:1055-1064 omits the resource for these plan types and
    /// PlanConverter sets DueOnly=true for them (vocabulary-driven).
    /// </summary>
    public static bool IsVocabularyDriven(PlanActivityType type) => type is
        PlanActivityType.VocabularyReview or PlanActivityType.VocabularyGame
        or PlanActivityType.Cloze or PlanActivityType.NumberDrill;

    /// <summary>
    /// Source NavigateToActivity (Index.razor:1273-1320) passes every selected
    /// resource for these tiles and only the first selected resource otherwise.
    /// </summary>
    public static bool TakesResourceList(string route) => route is
        "vocab-quiz" or VocabularyMatching or "word-association";
}

/// <summary>
/// Owner, date, plan item and source launch context travel together
/// (activities.md 1.1). PlanItemId is null for Choose My Own, which records an
/// ad-hoc DailyPlanCompletion row. TimerResourceId is what the owner-validated
/// timer checks against the plan row (or stores on the ad-hoc row); it can
/// differ from ResourceIds because vocabulary-driven plan launches omit the
/// resource for content selection. ActivityType is the plan card's own type
/// (one route can serve several plan types, e.g. Shadowing and Listening); the
/// owner-validated timer must start with it or the plan row is refused.
/// </summary>
public sealed record NativeActivityLaunch(
    string Route,
    string UserId,
    DateOnly PlanDate,
    string? PlanItemId,
    ImmutableArray<string> ResourceIds,
    string? SkillId,
    ImmutableArray<string> FocusVocabularyIds,
    bool DueOnly,
    string? TimerResourceId,
    PlanActivityType? ActivityType = null)
{
    public bool IsPlanItem => !string.IsNullOrWhiteSpace(PlanItemId);
}
