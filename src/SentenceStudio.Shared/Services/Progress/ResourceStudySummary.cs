namespace SentenceStudio.Services.Progress;

/// <summary>
/// How much, how recently and how well the active learner has studied one Learning Resource
/// (issue #219). Built by <see cref="ResourceStudySummaryService"/> for one owner; every client
/// (web, MAUI through the shared Razor UI, the native heads) shows the same facts.
/// </summary>
/// <param name="ResourceId">The resource the facts describe.</param>
/// <param name="SessionCount">Activity sessions on this resource that were started (minutes
/// logged) or completed; plan items that were never begun are not sessions.</param>
/// <param name="TotalMinutes">Minutes logged across those sessions.</param>
/// <param name="Activities">Sessions and minutes per activity, most minutes first.</param>
/// <param name="LastStudiedDate">The plan date of the most recent session, or null when the
/// resource has never been studied. A calendar day, not an instant: show it without converting
/// time zones.</param>
/// <param name="LastActivity">The activity of that session; null for an activity this build
/// does not know.</param>
/// <param name="WordCount">The owner's words attached to the resource.</param>
/// <param name="UnknownCount">Words never practiced, or practiced without any mastery yet.</param>
/// <param name="LearningCount">Words with some mastery that are not Known yet.</param>
/// <param name="KnownCount">Words that are Known by the app's one rule
/// (<see cref="SentenceStudio.Services.VocabularyKnownRule"/>) in the learner's Learning mode.</param>
/// <param name="AverageMastery">Mean mastery (0 to 1) over every word of the resource; a word
/// never practiced counts as 0.</param>
public sealed record ResourceStudySummary(
    string ResourceId,
    int SessionCount,
    int TotalMinutes,
    IReadOnlyList<ResourceActivityStudy> Activities,
    DateTime? LastStudiedDate,
    PlanActivityType? LastActivity,
    int WordCount,
    int UnknownCount,
    int LearningCount,
    int KnownCount,
    double AverageMastery)
{
    /// <summary>Whether any session on this resource was started or completed.</summary>
    public bool HasStudy => SessionCount > 0;
}

/// <summary>Sessions and minutes for one activity on a resource.</summary>
/// <param name="ActivityType">The activity; null groups activity types this build does not
/// know.</param>
/// <param name="Sessions">Sessions of this activity.</param>
/// <param name="Minutes">Minutes logged in them.</param>
public sealed record ResourceActivityStudy(
    PlanActivityType? ActivityType, int Sessions, int Minutes);
