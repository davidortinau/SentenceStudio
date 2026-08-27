using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.Coach;

// ─────────────────────────────────────────────────────────────────────────────
// Mirrors of the server-side scope vocabulary.
//
// The server's CoachScope* enums live in SentenceStudio.Api, and Contracts cannot
// reference Api — it is a compile constraint, not a preference. So the wire carries
// its own copies, and a total mapper on the server converts between them.
//
// MIRRORED, NOT MOVED. Moving the server enums into Contracts would touch every
// coach tool and every scope test, in files three workstreams are currently inside.
// The cost of the mirror is one mapper and one census test that fails the moment the
// two vocabularies disagree; the cost of the move was a batch-wide merge conflict.
//
// Ordinals are held equal to the server enum wherever the server's zero member is
// already "unspecified", so a reviewer can check the mirror by reading two lists
// side by side. CoachWithheldReason is the one exception and says why in place.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// How much of the learner's data an evidence item was drawn from.
/// </summary>
/// <remarks>
/// This is the field that separates "here is your resource shelf" from "here are the first
/// twenty of eighty". Without it a learner reading a coach claim has no way to tell a complete
/// answer from a page, and neither does the coach.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachEvidenceCoverage.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown is the documented unset value and the client renders it as no coverage claim at all. "
    + "Every other member asserts something specific about how much of the learner's data was "
    + "examined, and asserting the wrong one is worse than asserting nothing: a page described as a "
    + "complete set is the exact over-claim this field exists to prevent.")]
public enum CoachEvidenceCoverage
{
    /// <summary>
    /// The server stated no coverage, or this build cannot name the one it stated. Render no
    /// coverage claim.
    /// </summary>
    Unknown = 0,

    /// <summary>Every row the learner owns, after the stated filters.</summary>
    CompleteOwnedSet = 1,

    /// <summary>A bounded page drawn from the owned set.</summary>
    PageOfOwnedSet = 2,

    /// <summary>Only rows whose date falls inside the stated window.</summary>
    WindowBounded = 3,

    /// <summary>One row, named by the caller.</summary>
    SingleItem = 4,

    /// <summary>The rows belonging to one calendar day.</summary>
    SingleDay = 5,

    /// <summary>The learner's current settings, which are a snapshot rather than a row set.</summary>
    SettingsSnapshot = 6,

    /// <summary>A value computed from the learner's data rather than a set of their rows.</summary>
    DerivedProjection = 7,

    /// <summary>An aggregate over the complete set, with a bounded breakdown alongside it.</summary>
    CompleteAggregateWithBreakdown = 8
}

/// <summary>
/// The order the rows behind an evidence item came back in.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachEvidenceOrder.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown renders no ordering claim. Collapsing onto a named order would tell the learner their "
    + "rows are ranked by something this build invented, and a wrong ranking claim reads as a "
    + "recommendation.")]
public enum CoachEvidenceOrder
{
    /// <summary>
    /// The server stated no order, or this build cannot name the one it stated. Render no
    /// ordering claim.
    /// </summary>
    Unknown = 0,

    /// <summary>The answer is not a sequence, so order is meaningless.</summary>
    NotApplicable = 1,

    /// <summary>A set with no order the reader may rely on.</summary>
    Unordered = 2,

    /// <summary>Fewest days since last use first; never-used rows last.</summary>
    LastUsedAscending = 3,

    /// <summary>Most recently updated first.</summary>
    UpdatedDescending = 4,

    /// <summary>Highest mastery first.</summary>
    MasteryDescending = 5,

    /// <summary>Most minutes first.</summary>
    MinutesDescending = 6,

    /// <summary>Lowest priority number first.</summary>
    PriorityAscending = 7,

    /// <summary>Most frequent first.</summary>
    FrequencyDescending = 8,

    /// <summary>Ordered by the band label itself, not by any measure of the learner.</summary>
    BandLabelAscending = 9
}

/// <summary>
/// Which definition of the population an evidence item used.
/// </summary>
/// <remarks>
/// Two evidence items that disagree are either a bug or two different questions, and this is what
/// tells them apart. It is a grouping and diagnosis key; the learner reads the server's localized
/// label, never this member's name.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachDefinitionCode.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown is the documented unset value. This code names which population was counted, so "
    + "collapsing onto a real definition would label one question's answer with another question's "
    + "name — which is exactly how two honest numbers come to look like a contradiction.")]
public enum CoachDefinitionCode
{
    /// <summary>
    /// The server stated no definition, or this build cannot name the one it stated.
    /// </summary>
    Unknown = 0,

    /// <summary>Owned resources ranked by how recently they were practised.</summary>
    OwnedResourceCatalog = 1,

    /// <summary>Owned resources ranked by when they were last edited.</summary>
    OwnedResourceList = 2,

    /// <summary>One owned resource.</summary>
    OwnedResourceDetail = 3,

    /// <summary>Unarchived skills.</summary>
    ActiveSkillList = 4,

    /// <summary>One unarchived skill.</summary>
    ActiveSkillDetail = 5,

    /// <summary>Every tracked word, banded and counted by review schedule.</summary>
    TrackedVocabularyDueSummary = 6,

    /// <summary>Tracked words that are not currently due.</summary>
    UndueVocabularySearch = 7,

    /// <summary>One tracked word, named by the learner.</summary>
    TrackedVocabularyDetail = 8,

    /// <summary>The learner's stated preferences.</summary>
    LearnerSettingsSnapshot = 9,

    /// <summary>The learner's preferences plus the sizes of the sets they own.</summary>
    LearnerOverviewSummary = 10,

    /// <summary>The plan generated for one calendar day, with its logged items.</summary>
    PlanDaySummary = 11,

    /// <summary>Logged practice aggregated over a date window.</summary>
    PracticeWindowBalance = 12,

    /// <summary>A plan the planner produced without writing anything.</summary>
    DeterministicPlanPreview = 13,

    /// <summary>The learner's most recent practice date and days-since count.</summary>
    LatestPracticeSummary = 14
}

/// <summary>
/// Why an evidence item declined to return something it found.
/// </summary>
/// <remarks>
/// <para>
/// This is a reason code and a count, never content. <see cref="DueReviewEmbargo"/> discloses that
/// the learner has matching words the coach may not name — because naming them would hand over the
/// answers to reviews they have not taken. The count crosses the boundary; the words do not.
/// </para>
/// <para>
/// <b>Ordinals deliberately differ from the server enum.</b> The server's zero member is
/// <c>None</c> — "nothing was withheld" — which is a real claim, and the wire needs its zero to
/// mean "no claim readable". Collapsing an unreadable reason onto <c>None</c> would tell the
/// learner nothing was held back at the exact moment the client had lost track of whether anything
/// was. So <see cref="Unknown"/> takes zero and every server member shifts up one. That is free
/// here and only here: this enum is new, additive, wire-only, and stored nowhere by ordinal. The
/// server enum keeps its own numbering, and <c>CoachEvidenceScopeProjection</c> is the single place
/// the two are reconciled.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachWithheldReason.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown renders as 'some rows were not shown' with no reason attached, which is the honest "
    + "reading. Collapsing onto None would claim nothing was withheld while a non-zero WithheldCount "
    + "sat next to it, and collapsing onto DueReviewEmbargo would invent a pedagogical reason for a "
    + "value the build could not read.")]
public enum CoachWithheldReason
{
    /// <summary>
    /// The server stated no reason, or this build cannot name the one it stated. Any accompanying
    /// count still stands; only the explanation is missing.
    /// </summary>
    Unknown = 0,

    /// <summary>Nothing was withheld.</summary>
    None = 1,

    /// <summary>
    /// Matching rows were withheld because they are due for review. Disclosed as a count and this
    /// reason only — never as the words themselves.
    /// </summary>
    DueReviewEmbargo = 2,

    /// <summary>Matching rows were withheld because the result limit was reached.</summary>
    ResultLimit = 3,

    /// <summary>Rows were withheld because the learner archived them.</summary>
    ArchivedExcluded = 4,

    /// <summary>Rows were withheld because they did not meet the evidence threshold.</summary>
    BelowMinimumEvidence = 5
}

/// <summary>
/// Which aggregate a <see cref="CoachEvidenceValueDto"/> holds, so the client can localize its
/// label instead of printing the server's English.
/// </summary>
/// <remarks>
/// A closed code rather than a free label, for the same reason every other member of this family
/// is one: a string the server writes is a string the server has to know the learner's language to
/// write, and it does not.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachEvidenceValueCode.Unknown), WireEnumFallbackKind.SafeZero,
    "The zero member already means 'the server did not name this value'. A code this build cannot "
    + "read lands there and the client falls back to the server's own prose label, which is exactly "
    + "what a client with no code support does — so an unreadable code degrades to the old "
    + "behaviour rather than to a wrong label.")]
public enum CoachEvidenceValueCode
{
    /// <summary>Unnamed. The client falls back to the server's prose label.</summary>
    Unknown = 0,

    /// <summary>How many rows the fact was computed from.</summary>
    RowsRead = 1,

    /// <summary>How many rows matched before paging or withholding.</summary>
    RowsMatched = 2,

    /// <summary>How many matching rows were deliberately left out.</summary>
    RowsWithheld = 3
}
