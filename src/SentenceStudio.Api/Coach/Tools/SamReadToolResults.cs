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
    [property: Description("True if the learner added this word manually.")] bool IsLearnerAdded,
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
    [property: Description("Total vocabulary words tracked.")] int TrackedWordCount,
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
