using System.ComponentModel;

namespace SentenceStudio.Api.Coach.Tools;

// ──────────────────────────────────────────────────────
// Vocabulary search / detail
// ──────────────────────────────────────────────────────

/// <summary>
/// One vocabulary match: the term, its gloss, the dictionary form, tags, and the learner's
/// progress on it.
/// </summary>
/// <remarks>
/// There is no mnemonic, example sentence, or audio member on this record. That is worth stating
/// positively rather than as a list of things "never exposed": nothing is withheld at runtime,
/// those members simply do not exist, and describing absent fields as suppressed invites a reader
/// — or a tool description copied from here — to believe they could be returned.
/// </remarks>
public sealed record VocabularySearchEntry(
    [property: Description("The word identifier.")] string WordId,
    [property: Description("The word in the target language.")] string TargetTerm,
    [property: Description("The word in the native language.")] string NativeTerm,
    [property: Description("The dictionary form.")] string? Lemma,
    [property: Description("The language.")] string? Language,
    [property: Description("Comma-separated tags.")] IReadOnlyList<string> Tags,
    [property: Description("The mastery score, 0 to 1. Null if never practiced.")] double? MasteryScore,
    [property: Description("Days since last practice. Null if never practiced.")] int? DaysSinceLastPractice);

/// <summary>Bounded vocabulary search result. Words currently due for review are excluded.</summary>
public sealed record VocabularySearchResult(
    [property: Description("Total words matching the query, excluding words currently due for review.")] int TotalMatchCount,
    [property: Description("Words returned in this page.")] int ReturnedCount,
    [property: Description("The matches.")] IReadOnlyList<VocabularySearchEntry> Words,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

/// <summary>
/// Detail for one vocabulary word the caller named: the term, its gloss, the dictionary form,
/// tags, mastery, days since last practice, and attempt counts.
/// </summary>
/// <remarks>
/// <b>It carries no example sentences.</b> The previous summary said it did, and no such member
/// has ever been on this record — the mistake was harmless while it lived only here, and stopped
/// being harmless when it was transcribed into the model-facing tool description, where it would
/// have had the coach offer a learner examples the tool cannot return. The members are listed
/// below; there is no example, mnemonic, or audio among them.
/// </remarks>
public sealed record VocabularyWordDetail(
    [property: Description("The word identifier.")] string WordId,
    [property: Description("The word in the target language.")] string TargetTerm,
    [property: Description("The word in the native language.")] string NativeTerm,
    [property: Description("The dictionary form.")] string? Lemma,
    [property: Description("The language.")] string? Language,
    [property: Description("Comma-separated tags.")] IReadOnlyList<string> Tags,
    [property: Description("The mastery score, 0 to 1. Null if never practiced.")] double? MasteryScore,
    [property: Description("Days since last practice. Null if never practiced.")] int? DaysSinceLastPractice,
    [property: Description("Total practice attempts.")] int TotalAttempts,
    [property: Description("Correct attempts.")] int CorrectAttempts,
    [property: Description("True if the learner set this word's status themselves (marked it Known, or chose Practice again or Start over).")] bool IsLearnerAdded,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

/// <summary>One example sentence on the learner's word.</summary>
public sealed record VocabularyExampleSentenceEntry(
    [property: Description("The example sentence identifier, for the propose_ example sentence edit and removal tools.")] string SentenceId,
    [property: Description("The sentence in the target language.")] string TargetSentence,
    [property: Description("Its meaning in the learner's own language. Null when the learner saved none.")] string? NativeSentence,
    [property: Description("True for the word's main example, the one shown first.")] bool IsCore);

/// <summary>
/// The example sentences on one of the learner's words, or only their count when the word is due
/// for review.
/// </summary>
/// <remarks>
/// A due word's example sentences are part of the answer to the review the learner has not taken
/// yet, and the coach's leak check embargoes them; so for a due word the sentences are counted and
/// reported as withheld, never returned (the query does not even select them).
/// </remarks>
public sealed record VocabularyExampleSentencesResult(
    [property: Description("The word identifier.")] string WordId,
    [property: Description("The word in the target language. Null when the word is due for review.")] string? TargetTerm,
    [property: Description("True when the word is due for review. Its example sentences are then hidden until the review is done; say so and never guess them.")] bool IsDueForReview,
    [property: Description("How many example sentences the learner has on this word, including hidden ones.")] int TotalCount,
    [property: Description("The example sentences, main example first. Empty when the word is due for review.")] IReadOnlyList<VocabularyExampleSentenceEntry> Sentences,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

// ──────────────────────────────────────────────────────
// Skill list / detail
// ──────────────────────────────────────────────────────

/// <summary>One skill profile summary.</summary>
public sealed record SkillListEntry(
    [property: Description("The skill identifier.")] string SkillId,
    [property: Description("The skill title.")] string Title,
    [property: Description("The skill description.")] string? SkillDescription,
    [property: Description("The language.")] string Language);

/// <summary>Bounded skill list result.</summary>
public sealed record SkillListResult(
    [property: Description("Total skill profiles.")] int TotalCount,
    [property: Description("Returned in this page.")] int ReturnedCount,
    [property: Description("The skills.")] IReadOnlyList<SkillListEntry> Skills,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

/// <summary>Detail for one skill profile.</summary>
public sealed record SkillDetailResult(
    [property: Description("The skill identifier.")] string SkillId,
    [property: Description("The skill title.")] string Title,
    [property: Description("The skill description.")] string? SkillDescription,
    [property: Description("The language.")] string Language,
    [property: Description("Days since the skill was created.")] int DaysSinceCreated,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

// ──────────────────────────────────────────────────────
// Learning resource list / detail
// ──────────────────────────────────────────────────────

/// <summary>One learning resource. Never exposes transcript text.</summary>
public sealed record LearningResourceListEntry(
    [property: Description("The resource identifier.")] string ResourceId,
    [property: Description("The resource title.")] string Title,
    [property: Description("The media type.")] string? MediaType,
    [property: Description("The language.")] string? Language,
    [property: Description("Number of vocabulary words.")] int VocabularyCount,
    [property: Description("True if the resource has a transcript.")] bool HasTranscript,
    [property: Description("Tags on the resource.")] IReadOnlyList<string> Tags);

/// <summary>Bounded learning resource list.</summary>
public sealed record LearningResourceListResult(
    [property: Description("Total learning resources.")] int TotalCount,
    [property: Description("Returned in this page.")] int ReturnedCount,
    [property: Description("The resources.")] IReadOnlyList<LearningResourceListEntry> Resources,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

/// <summary>Detail for one learning resource. Never exposes transcript text.</summary>
public sealed record LearningResourceDetailResult(
    [property: Description("The resource identifier.")] string ResourceId,
    [property: Description("The resource title.")] string Title,
    [property: Description("The media type.")] string? MediaType,
    [property: Description("The language.")] string? Language,
    [property: Description("Number of vocabulary words.")] int VocabularyCount,
    [property: Description("True if the resource has audio.")] bool HasAudio,
    [property: Description("True if the resource has a transcript.")] bool HasTranscript,
    [property: Description("True if the resource has video.")] bool HasVideo,
    [property: Description("True if the app created it.")] bool IsSystemGenerated,
    [property: Description("Tags.")] IReadOnlyList<string> Tags,
    [property: Description("Days since last use. Null if never used.")] int? DaysSinceLastUse,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

// ──────────────────────────────────────────────────────
// Profile / settings / plan summaries
// ──────────────────────────────────────────────────────

/// <summary>Extended profile summary. Never exposes name, email, or API key.</summary>
public sealed record CurrentProfileSummary(
    [property: Description("The target language.")] string TargetLanguage,
    [property: Description("All target languages.")] IReadOnlyList<string> TargetLanguages,
    [property: Description("The native language.")] string NativeLanguage,
    [property: Description("The display language.")] string? DisplayLanguage,
    [property: Description("Preferred session minutes.")] int PreferredSessionMinutes,
    [property: Description("Target CEFR level.")] string? TargetLevel,
    [property: Description("Days since the learner started.")] int DaysSinceStart,
    [property: Description("Words that have a practice record. Not the learner's word total: use TotalWordCount for how many words they have.")] int TrackedWordCount,
    [property: Description("Total words, the same number the dashboard shows: the learner's words that are on at least one of their resources, practised or not. Use this when they ask how many words they have.")] int TotalWordCount,
    [property: Description("Dashboard New: words on a resource never practised.")] int NewWordCount,
    [property: Description("Dashboard Learning: words being learned.")] int LearningWordCount,
    [property: Description("Dashboard Familiar: words the learner marked familiar, not yet confirmed by practice.")] int FamiliarWordCount,
    [property: Description("Dashboard Review: words due for review now. A count only; never name them.")] int ReviewWordCount,
    [property: Description("Dashboard Known: words the learner knows.")] int KnownWordCount,
    [property: Description("Every word the learner owns, on a resource or not: the total on the Vocabulary screen. Larger than TotalWordCount when some words are on no resource.")] int VocabularyScreenWordCount,
    [property: Description("Total skill profiles.")] int SkillCount,
    [property: Description("Total learning resources.")] int ResourceCount,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

/// <summary>Learner settings summary. Never exposes credentials.</summary>
public sealed record LearnerSettingsSummary(
    [property: Description("The target language.")] string TargetLanguage,
    [property: Description("The native language.")] string NativeLanguage,
    [property: Description("The display language.")] string? DisplayLanguage,
    [property: Description("Preferred session minutes.")] int PreferredSessionMinutes,
    [property: Description("Target CEFR level.")] string? TargetLevel,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

/// <summary>One item in today's plan summary.</summary>
public sealed record PlanItemSummary(
    [property: Description("The activity type.")] string ActivityType,
    [property: Description("True if completed.")] bool IsCompleted,
    [property: Description("Estimated minutes.")] int MinutesPlanned,
    [property: Description("Minutes spent so far.")] int MinutesSpent);

/// <summary>Today's plan summary.</summary>
public sealed record CurrentPlanSummary(
    [property: Description("Whether a plan exists for today.")] bool HasPlan,
    [property: Description("The plan date (yyyy-MM-dd).")] string PlanDate,
    [property: Description("The plan strategy.")] string? Strategy,
    [property: Description("Plan items.")] IReadOnlyList<PlanItemSummary> Items,
    [property: Description("Overall completion percentage, 0 to 100.")] double OverallCompletionPct,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;

// ──────────────────────────────────────────────────────
// Current screen
// ──────────────────────────────────────────────────────

/// <summary>What kind of thing an on-screen item is, and so which tool takes its identifier.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<CurrentScreenItemKind>))]
public enum CurrentScreenItemKind
{
    /// <summary>A kind this server does not know; its identifier fits no tool.</summary>
    Other = 0,

    /// <summary>A vocabulary word; its identifier is a word id.</summary>
    VocabularyWord,

    /// <summary>An example sentence; its identifier is a sentence id.</summary>
    ExampleSentence,

    /// <summary>A learning resource; its identifier is a resource id.</summary>
    LearningResource,

    /// <summary>A skill; its identifier is a skill id.</summary>
    Skill,

    /// <summary>A minimal pair.</summary>
    MinimalPair,

    /// <summary>A practice activity; its identifier is the activity type.</summary>
    Activity,

    /// <summary>An item of today's plan.</summary>
    PlanItem,

    /// <summary>A YouTube channel.</summary>
    YouTubeChannel,

    /// <summary>Text the learner is reading or writing, with no identifier.</summary>
    Text
}

/// <summary>One thing the screen shows.</summary>
public sealed record CurrentScreenItem(
    [property: Description("What kind of thing it is. Its identifier goes to the tool for that kind: a word id to the vocabulary tools, a resource id to the resource tools, a skill id to the skill tools.")] CurrentScreenItemKind Kind,
    [property: Description("Its identifier, or null when it has none yet (a new word still being typed).")] string? Id,
    [property: Description("What the learner sees for it: the word, the resource title, the activity name.")] string? Label,
    [property: Description("Secondary text on screen, such as the meaning once the quiz has shown it.")] string? Detail,
    [property: Description("True for the main thing on screen: what 'this', 'it' or 'here' refers to.")] bool Focus);

/// <summary>One short piece of screen state, such as the open tab or the quiz phase.</summary>
public sealed record CurrentScreenFact(
    [property: Description("What the fact is about.")] string Name,
    [property: Description("Its value.")] string? Value);

/// <summary>
/// What the learner's app was showing when they sent this message. Reported by the app, never
/// read from stored data.
/// </summary>
public sealed record CurrentScreenResult(
    [property: Description("False when the app sent no screen with this message; then you do not know what the learner is looking at.")] bool Available,
    [property: Description("The screen, for example vocabulary-quiz, resources.detail or vocabulary.word-form.")] string? Screen,
    [property: Description("The app: ios, android, web, maui-ios and so on.")] string? Platform,
    [property: Description("True while the screen keeps an answer from the learner, such as a quiz question not yet answered. Do not reveal the answer, its meaning or a sentence that gives it away until they have answered.")] bool AnswerHidden,
    [property: Description("What the screen shows, the main thing first.")] IReadOnlyList<CurrentScreenItem> Items,
    [property: Description("Short screen state: tab, open sheet, activity mode, quiz phase.")] IReadOnlyList<CurrentScreenFact> Facts,
    [property: Description("What this answer covers, how it is ordered, and what it withheld.")] CoachResultScope Scope) : ICoachScopedResult;
