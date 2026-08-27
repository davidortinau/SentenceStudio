using System.ComponentModel;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// The learner settings the coach can read.
/// This shape holds no name, no email, no key, and no identifier of the learner.
/// </summary>
public sealed record LearnerProfileSummary(
    [property: Description("The language the learner studies.")] string TargetLanguage,
    [property: Description("Every language the learner studies.")] IReadOnlyList<string> TargetLanguages,
    [property: Description("The first language of the learner.")] string NativeLanguage,
    [property: Description("The language of the interface.")] string? DisplayLanguage,
    [property: Description("The session length the learner prefers, in minutes.")] int PreferredSessionMinutes,
    [property: Description("The level the learner works toward, for example B1. Null if the learner set no level.")] string? TargetLevel,
    [property: Description("The days since the learner started to use the app.")] int DaysSinceStart,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

/// <summary>How one activity type contributes to the balance of input and output.</summary>
public enum CoachPracticeChannel
{
    /// <summary>Comprehension work: reading, listening, and video.</summary>
    Input = 0,

    /// <summary>Production work: writing, speaking, translation, and conversation.</summary>
    Output,

    /// <summary>Recognition and retrieval work that mixes both channels.</summary>
    Mixed
}

/// <summary>The minutes and counts for one activity type in the window.</summary>
public sealed record PracticeActivityTotal(
    [property: Description("The activity type.")] string ActivityType,
    [property: Description("The channel of the activity type.")] CoachPracticeChannel Channel,
    [property: Description("The minutes the learner spent on this activity type.")] int Minutes,
    [property: Description("The plan items of this type the learner completed.")] int CompletedCount);

/// <summary>
/// The balance of input work and output work over a stated window.
/// Every value is an aggregate. This shape holds no learner content.
/// </summary>
public sealed record PracticeBalanceSummary(
    [property: Description("The length of the window in days.")] int WindowDays,
    [property: Description("The first day of the window.")] DateOnly WindowStartDate,
    [property: Description("The last day of the window.")] DateOnly WindowEndDate,
    [property: Description("The comprehension minutes in the window.")] int InputMinutes,
    [property: Description("The production minutes in the window.")] int OutputMinutes,
    [property: Description("The mixed minutes in the window.")] int MixedMinutes,
    [property: Description("All minutes in the window.")] int TotalMinutes,
    [property: Description("The days in the window with at least one minute of work.")] int ActiveDayCount,
    [property: Description("The graded attempts in the window.")] int AttemptCount,
    [property: Description("The minutes and counts for each activity type.")] IReadOnlyList<PracticeActivityTotal> ByActivityType,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

/// <summary>The number of tracked words in one mastery band.</summary>
public sealed record VocabularyBandCount(
    [property: Description("The mastery band.")] string Band,
    [property: Description("The number of words in the band.")] int Count);

/// <summary>The number of due words that carry one category tag.</summary>
public sealed record VocabularyTagCount(
    [property: Description("The category tag.")] string Tag,
    [property: Description("The number of due words with this tag.")] int DueCount);

/// <summary>
/// The counts and bands for due vocabulary work.
/// This shape never holds a target-language term, a translation, an example, or a memory aid.
/// </summary>
public sealed record VocabularyDueSummary(
    [property: Description("The words that are due for review now.")] int DueNowCount,
    [property: Description("The words that become due in the next seven days.")] int DueThisWeekCount,
    [property: Description("The words the learner never practiced.")] int NeverPracticedCount,
    [property: Description("All words the app tracks for the learner.")] int TrackedWordCount,
    [property: Description("The number of words in each mastery band.")] IReadOnlyList<VocabularyBandCount> Bands,
    [property: Description("The share of graded attempts that were wrong. The range is 0 to 1.")] double LapseRate,
    [property: Description("The mean mastery score of the tracked words. The range is 0 to 1.")] double AverageMasteryScore,
    [property: Description("The most common category tags on the due words.")] IReadOnlyList<VocabularyTagCount> CategoryTags,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

/// <summary>
/// One owned resource, as metadata only.
/// This shape never holds a transcript, a translation, or diary text.
/// </summary>
public sealed record ResourceCatalogEntry(
    [property: Description("The resource identifier.")] string ResourceId,
    [property: Description("The resource title.")] string Title,
    [property: Description("The media type, for example Podcast or Vocabulary List.")] string? MediaType,
    [property: Description("The language of the resource.")] string? Language,
    [property: Description("The number of vocabulary words in the resource.")] int VocabularyCount,
    [property: Description("True if the resource has audio.")] bool HasAudio,
    [property: Description("True if the resource has a transcript. The text stays on the server.")] bool HasTranscript,
    [property: Description("True if the resource has a video.")] bool HasVideo,
    [property: Description("True if the app created the resource.")] bool IsSystemGenerated,
    [property: Description("The tags on the resource.")] IReadOnlyList<string> Tags,
    [property: Description("The days since the learner last used the resource. Null if never used.")] int? DaysSinceLastUse);

/// <summary>The owned resources the planner can use.</summary>
public sealed record ResourceCatalogSummary(
    [property: Description("The number of resources the learner owns.")] int TotalCount,
    [property: Description("The number of resources in this answer.")] int ReturnedCount,
    [property: Description("The resources, newest use first.")] IReadOnlyList<ResourceCatalogEntry> Resources,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

/// <summary>One activity in a plan preview.</summary>
public sealed record PlanPreviewItem(
    [property: Description("The activity type.")] string ActivityType,
    [property: Description("The planned minutes.")] int EstimatedMinutes,
    [property: Description("The order of the activity. A lower number comes first.")] int Priority,
    [property: Description("The resource for the activity. Null if the activity uses no resource.")] string? ResourceId,
    [property: Description("The title of the resource. Null if the activity uses no resource.")] string? ResourceTitle,
    [property: Description("The skill for the activity. Null if the activity uses no skill.")] string? SkillId,
    [property: Description("The number of words the activity reviews.")] int FocusWordCount);

/// <summary>
/// A read-only plan preview. The preview performs no write.
/// This shape holds counts and metadata only. It never holds a word or a translation.
/// </summary>
public sealed record PlanPreviewSummary(
    [property: Description("A stable identifier for this preview.")] string PreviewId,
    [property: Description("The planned minutes for the full preview.")] int TotalMinutes,
    [property: Description("The activities in the preview.")] IReadOnlyList<PlanPreviewItem> Items,
    [property: Description("The words the preview reviews.")] int VocabularyReviewWordCount,
    [property: Description("All words that are due now.")] int TotalDueCount,
    [property: Description("The title of the main resource. Null if the preview uses no resource.")] string? PrimaryResourceTitle,
    [property: Description("The identifier of the main resource. Null if the preview uses no resource.")] string? PrimaryResourceId,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

/// <summary>
/// The learner's most recent recorded practice. This shape holds no vocabulary terms,
/// answers, transcript text, or other learner content.
/// </summary>
public sealed record PracticeHistorySummary(
    [property: Description("The learner-local date of the most recent recorded practice, or null when the learner has never practised.")]
    DateOnly? LastPracticeDate,
    [property: Description("Whole days between the last practice date and today in the learner's timezone, or null when no practice is recorded.")]
    int? DaysSincePractice,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")]
    CoachResultScope Scope) : ICoachScopedResult;
