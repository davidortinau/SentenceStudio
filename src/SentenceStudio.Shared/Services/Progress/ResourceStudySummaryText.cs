using System.Globalization;

namespace SentenceStudio.Services.Progress;

/// <summary>One labelled value of a resource's study history.</summary>
/// <param name="Id">A stable suffix for automation IDs (each client adds its own prefix).</param>
public sealed record ResourceStudyLine(string Id, string Label, string Value);

/// <summary>
/// The text of a resource's study history, ready to show: the study group (how much, how
/// recently) and the word mastery group (how well). Each client renders these lines in its own
/// controls, so the wording, order and formats are the same on every client.
/// </summary>
public sealed record ResourceStudyText(
    string StudyTitle,
    string? StudyEmpty,
    IReadOnlyList<ResourceStudyLine> Study,
    string MasteryTitle,
    string? MasteryEmpty,
    IReadOnlyList<ResourceStudyLine> Mastery)
{
    /// <summary>
    /// Projects <paramref name="summary"/> through the app's text resources.
    /// </summary>
    /// <param name="text">Looks up an AppResources key in the current language.</param>
    /// <param name="culture">Formats numbers, dates and percentages.</param>
    public static ResourceStudyText From(
        ResourceStudySummary summary, Func<string, string> text, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(culture);
        string Format(string key, params object[] values) =>
            string.Format(culture, text(key), values);
        string Pair(string first, string second) =>
            Format("ResourceEdit_StudyPair", first, second);
        string Sessions(int count) => count == 1
            ? Format("ResourceEdit_StudySessionOne", count)
            : Format("ResourceEdit_StudySessionMany", count);
        string Minutes(int minutes) => Format("ResourceEdit_StudyMinutes", minutes);

        var study = new List<ResourceStudyLine>();
        if (summary.HasStudy)
        {
            var lastDate = summary.LastStudiedDate is { } date
                ? date.ToString("d", culture) : string.Empty;
            study.Add(new("study.last", text("ResourceEdit_StudyLastStudied"),
                Pair(lastDate, text(ActivityNameKey(summary.LastActivity)))));
            study.Add(new("study.sessions", text("ResourceEdit_StudySessions"),
                summary.SessionCount.ToString("N0", culture)));
            study.Add(new("study.minutes", text("ResourceEdit_StudyTime"),
                Minutes(summary.TotalMinutes)));
            foreach (var activity in summary.Activities)
                study.Add(new($"study.activity.{ActivityId(activity.ActivityType)}",
                    text(ActivityNameKey(activity.ActivityType)),
                    Pair(Sessions(activity.Sessions), Minutes(activity.Minutes))));
        }

        var mastery = new List<ResourceStudyLine>();
        if (summary.WordCount > 0)
        {
            mastery.Add(new("mastery.average", text("ResourceEdit_AverageMastery"),
                summary.AverageMastery.ToString("P0", culture)));
            string Of(int count) => Format("ResourceEdit_StudyWordCount",
                count.ToString("N0", culture), summary.WordCount.ToString("N0", culture));
            // The app's word-status names, in the order the Vocabulary page uses.
            mastery.Add(new("mastery.unknown", text("Vocabulary_Unknown"), Of(summary.UnknownCount)));
            mastery.Add(new("mastery.learning", text("Vocabulary_Learning"), Of(summary.LearningCount)));
            mastery.Add(new("mastery.familiar", text("Vocabulary_Familiar"), Of(summary.FamiliarCount)));
            mastery.Add(new("mastery.known", text("Vocabulary_Known"), Of(summary.KnownCount)));
        }

        return new(
            text("ResourceEdit_StudyHistory"),
            summary.HasStudy ? null : text("ResourceEdit_StudyNotYet"),
            study,
            text("ResourceEdit_WordMastery"),
            summary.WordCount > 0 ? null : text("ResourceEdit_MasteryNoWords"),
            mastery);
    }

    /// <summary>The AppResources key of an activity's name (as the Activity Log shows it).</summary>
    public static string ActivityNameKey(PlanActivityType? activity) =>
        activity switch
        {
            PlanActivityType.VocabularyReview => "Activity_VocabularyReview",
            PlanActivityType.Reading => "Activity_Reading",
            PlanActivityType.Listening => "Activity_Listening",
            PlanActivityType.Shadowing => "Activity_Shadowing",
            PlanActivityType.Cloze => "Activity_ClozeExercise",
            PlanActivityType.Translation => "Activity_Translate",
            PlanActivityType.Writing => "Activity_Writing",
            PlanActivityType.SceneDescription => "Activity_DescribeScene",
            PlanActivityType.Conversation => "Activity_Conversation",
            PlanActivityType.VocabularyGame => "Activity_VocabMatching",
            PlanActivityType.VideoWatching => "Activity_VideoWatching",
            PlanActivityType.NumberDrill => "Activity_NumberDrill",
            _ => "Activity_Generic",
        };

    private static string ActivityId(PlanActivityType? activity) =>
        activity?.ToString().ToLowerInvariant() ?? "other";
}
