using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// Marks a shape as a result-scope envelope, so the embargo contract judges it under the
/// scope-shape rules rather than the rules of whatever result it happens to hang off.
/// </summary>
/// <remarks>
/// A scope describes <em>how</em> a read answered. It must therefore be incapable of carrying
/// <em>what</em> the read found — no target term, no gloss, no example, no transcript, no learner
/// text, and no echo of the query the model supplied. The attribute is what lets the scanner
/// apply that stricter judgement to a subtree without the scanner having to know the name of any
/// particular coach type.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class CoachScopeShapeAttribute : Attribute;

/// <summary>How much of the learner's data a read looked at.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoachScopeCoverage>))]
public enum CoachScopeCoverage
{
    /// <summary>Never emitted. Present so a default-constructed scope is visibly incomplete.</summary>
    Unspecified = 0,

    /// <summary>Every row the learner owns, after the declared filters.</summary>
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

    /// <summary>
    /// The answer's aggregate figures cover every row the learner owns, and the answer also
    /// carries a breakdown sub-list drawn from a <em>different, smaller</em> population. The
    /// scope's count family — <see cref="CoachResultScope.RequestedCount"/>,
    /// <see cref="CoachResultScope.ReturnedCount"/>, <see cref="CoachResultScope.MatchedCount"/>,
    /// <see cref="CoachResultScope.EligiblePopulationCount"/>,
    /// <see cref="CoachResultScope.WithheldCount"/> and
    /// <see cref="CoachResultScope.Truncated"/> — describes <b>the breakdown</b>. The aggregates
    /// are named fields on the answer body and are not counted here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This member exists because one scope cannot honestly describe two populations with one set
    /// of counts. The due summary is the case: its counts of tracked, due, and never-practised
    /// words cover everything the learner owns, while its category-tag list is a bounded page of
    /// the distinct tags found on the due words. Reporting that as
    /// <see cref="CompleteOwnedSet"/> with <see cref="CoachResultScope.Truncated"/> set says both
    /// "you have all of it" and "you do not", and reporting it as <see cref="PageOfOwnedSet"/>
    /// would understate counts that really are complete.
    /// </para>
    /// <para>
    /// It is used whenever the answer has this shape, not only when the breakdown is actually
    /// paged. A coverage that changed with the data would leave the model unable to tell which
    /// population <see cref="CoachResultScope.MatchedCount"/> was counting on any given call.
    /// </para>
    /// </remarks>
    CompleteAggregateWithBreakdown = 8
}

/// <summary>The order the rows came back in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoachScopeOrder>))]
public enum CoachScopeOrder
{
    /// <summary>Never emitted. Present so a default-constructed scope is visibly incomplete.</summary>
    Unspecified = 0,

    /// <summary>The answer is not a sequence, so order is meaningless.</summary>
    NotApplicable = 1,

    /// <summary>A set with no order the caller may rely on.</summary>
    Unordered = 2,

    /// <summary>
    /// Ordered by how long ago the row was last used, fewest days first — so the most recently
    /// used comes first and never-used rows sort last.
    /// </summary>
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
/// The predicates a read applied. Flags rather than a single value, because a read routinely
/// applies several and a caller that only learns about one of them has been told a half-truth.
/// </summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<CoachScopeFilters>))]
public enum CoachScopeFilters
{
    /// <summary>Never emitted by a real read: every read is at minimum owner-scoped.</summary>
    None = 0,

    /// <summary>Restricted to the rows the trusted learner owns.</summary>
    OwnerScoped = 1 << 0,

    /// <summary>Rows the learner archived were left out.</summary>
    ExcludeArchived = 1 << 1,

    /// <summary>Rows currently due for review were left out.</summary>
    ExcludeDue = 1 << 2,

    /// <summary>Only rows the learner already has a progress record for.</summary>
    ProgressRowExists = 1 << 3,

    /// <summary>A text query narrowed the set.</summary>
    TextQuery = 1 << 4,

    /// <summary>A date window narrowed the set.</summary>
    DateWindow = 1 << 5,

    /// <summary>One identifier named the row.</summary>
    SingleIdentifier = 1 << 6,

    /// <summary>One calendar day narrowed the set.</summary>
    CalendarDay = 1 << 7,

    /// <summary>
    /// Rows that did not clear the read's evidence bar were left out of the answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bar itself is named by <see cref="CoachResultScope.MinimumEvidence"/>, which is held
    /// back from the model until the client adoption gate opens. This flag is the model-visible
    /// half: it says a bar was applied at all, which is what turns an unexplained gap between
    /// <see cref="CoachResultScope.MatchedCount"/> and
    /// <see cref="CoachResultScope.ReturnedCount"/> into a stated one.
    /// </para>
    /// <para>
    /// Present whenever the read applies a bar, including when the bar happened to drop nothing —
    /// the same convention every other filter follows. The count of what it dropped rides on
    /// <see cref="CoachResultScope.WithheldCount"/> with
    /// <see cref="CoachScopeWithheldReason.BelowMinimumEvidence"/>.
    /// </para>
    /// </remarks>
    MinimumEvidence = 1 << 8
}

/// <summary>Why a read declined to return something it found.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoachScopeWithheldReason>))]
public enum CoachScopeWithheldReason
{
    /// <summary>Nothing was withheld.</summary>
    None = 0,

    /// <summary>
    /// Matches were withheld because they are due for review. Their terms are the answers to a
    /// review the learner has not taken, so the count crosses and the words do not.
    /// </summary>
    DueReviewEmbargo = 1,

    /// <summary>Matches were withheld because the caller's result limit was reached.</summary>
    ResultLimit = 2,

    /// <summary>Rows were withheld because the learner archived them.</summary>
    ArchivedExcluded = 3,

    /// <summary>
    /// Rows matched the read's population but did not clear its evidence bar, so they were left
    /// out of the answer.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ResultLimit"/>: nothing was paged away and asking for more would
    /// not produce these rows. They exist, they belong to the population the read named, and they
    /// have nothing to show — a planned activity the learner never started, for instance. Saying
    /// so is what stops the model reading the gap as a paging boundary and offering to fetch the
    /// rest.
    /// </remarks>
    BelowMinimumEvidence = 4
}

/// <summary>
/// Which definition of the population a read used, so two reads that disagree can be told apart
/// from two reads that were asking different questions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoachScopeDefinition>))]
public enum CoachScopeDefinition
{
    /// <summary>Never emitted. Present so a default-constructed scope is visibly incomplete.</summary>
    Unspecified = 0,

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
    DeterministicPlanPreview = 13
}

/// <summary>What a row had to show before it was counted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoachScopeMinimumEvidence>))]
public enum CoachScopeMinimumEvidence
{
    /// <summary>Never emitted. Present so a default-constructed scope is visibly incomplete.</summary>
    Unspecified = 0,

    /// <summary>No threshold: every owned row qualified.</summary>
    None = 1,

    /// <summary>The learner had to have a progress record for the row.</summary>
    ProgressRowRequired = 2,

    /// <summary>The row had to carry at least one logged minute.</summary>
    LoggedMinutesRequired = 3,

    /// <summary>The row had to carry at least one graded attempt.</summary>
    GradedAttemptRequired = 4,

    /// <summary>
    /// The row had to carry at least one logged minute or one completed item. A planned activity
    /// the learner never touched is not evidence of practice, and counting it would let an
    /// untouched plan read as a balanced week.
    /// </summary>
    LoggedWorkRequired = 5
}

/// <summary>How ties were broken in an ordered answer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoachScopeTieBreak>))]
public enum CoachScopeTieBreak
{
    /// <summary>Never emitted. Present so a default-constructed scope is visibly incomplete.</summary>
    Unspecified = 0,

    /// <summary>The answer is not a sequence.</summary>
    NotApplicable = 1,

    /// <summary>No tiebreak was applied, so equal rows have no guaranteed relative order.</summary>
    None = 2,

    /// <summary>Ordinal comparison of the title.</summary>
    TitleOrdinal = 3,

    /// <summary>Ordinal comparison of the activity type.</summary>
    ActivityTypeOrdinal = 4,

    /// <summary>Ordinal comparison of the tag.</summary>
    TagOrdinal = 5,

    /// <summary>Ordinal comparison of the band label.</summary>
    BandOrdinal = 6
}

/// <summary>Which clock decided what "now" meant.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoachScopeClockBasis>))]
public enum CoachScopeClockBasis
{
    /// <summary>Never emitted. Present so a default-constructed scope is visibly incomplete.</summary>
    Unspecified = 0,

    /// <summary>The answer does not depend on a clock.</summary>
    NotApplicable = 1,

    /// <summary>A UTC instant on the server.</summary>
    ServerUtcInstant = 2,

    /// <summary>The learner's own local calendar day.</summary>
    LearnerLocalDay = 3
}

/// <summary>What the time reference on the scope refers to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoachScopeReferenceMode>))]
public enum CoachScopeReferenceMode
{
    /// <summary>Never emitted. Present so a default-constructed scope is visibly incomplete.</summary>
    Unspecified = 0,

    /// <summary>The answer carries no time reference.</summary>
    NotApplicable = 1,

    /// <summary>A single instant the answer was true at.</summary>
    AsOfInstant = 2,

    /// <summary>One calendar day.</summary>
    CalendarDay = 3,

    /// <summary>A closed range of calendar days.</summary>
    DateWindow = 4
}

/// <summary>
/// What a read actually looked at, and what it declined to hand back.
/// </summary>
/// <remarks>
/// <para>
/// A tool answer says what was found. It has never said what was <em>looked for</em>, and the
/// difference is where a coach becomes untruthful without anyone writing an untruth. A list of
/// twenty resources is a complete shelf or the first page of eighty; a vocabulary search that
/// returns ten words silently dropped the four that were due. The model cannot tell those apart
/// from the rows alone, so it fills the gap with the most fluent available assumption — which is
/// how "here is your vocabulary" comes to describe a set the learner does not have.
/// </para>
/// <para>
/// This record closes the gap in the answer rather than in the prompt. Every field is a closed
/// enum, a bounded count, a flag, or a date: there is no member on this shape that a term, a
/// gloss, an example, or an echo of the model's own query could travel in, and
/// <see cref="CoachScopeShapeAttribute"/> is what makes the embargo contract enforce that rather
/// than trust it.
/// </para>
/// <para>
/// The shape is complete; the projection is not. Members the model can act on today are
/// serialized, and the foundation members below them are held server-side until the client wire
/// tolerance gate permits new enum values to reach a client. Adding a field to the model's view
/// costs tokens on every one of a turn's tool calls, so the split is deliberate rather than
/// incidental.
/// </para>
/// <para>
/// <b>The count contract.</b> Every count on this shape describes <em>one</em> population, and
/// <see cref="Coverage"/> is what names which one. For most reads it is the rows the answer
/// carries. For a read whose answer mixes complete aggregates with a smaller breakdown list —
/// <see cref="CoachScopeCoverage.CompleteAggregateWithBreakdown"/> — it is the breakdown, and the
/// aggregates live as named fields on the answer body instead. Overloading one
/// <see cref="MatchedCount"/> across two populations is the specific defect this rule exists to
/// prevent: it produces a scope where every field is individually true and the sentence they form
/// is not.
/// </para>
/// <para>
/// Within that one population, four relationships always hold and are swept over every registered
/// read:
/// </para>
/// <list type="number">
///   <item><see cref="ReturnedCount"/> &lt;= <see cref="EligiblePopulationCount"/> &lt;= <see cref="MatchedCount"/>.</item>
///   <item><see cref="MatchedCount"/> == <see cref="ReturnedCount"/> + <see cref="WithheldCount"/> when nothing was paged.</item>
///   <item><see cref="Truncated"/> is true exactly when paging dropped eligible rows, and never when <see cref="Coverage"/> claims a complete set.</item>
///   <item><see cref="WithheldCount"/>, <see cref="WithheldReason"/> and the matching <see cref="Filters"/> flag agree.</item>
/// </list>
/// </remarks>
[CoachScopeShape]
public sealed record CoachResultScope
{
    /// <summary>The largest count this shape will carry. A larger value is a defect, not a page.</summary>
    public const int MaxCount = 1_000_000;

    private readonly int? _requestedCount;
    private readonly int _returnedCount;
    private readonly int? _matchedCount;
    private readonly int _withheldCount;
    private readonly int? _eligiblePopulationCount;
    private readonly DateTime _asOfUtc;

    // ---------------------------------------------------------------------
    // Shipped: the model can change what it says on the strength of these.
    // ---------------------------------------------------------------------

    /// <summary>How much of the learner's data this read covered.</summary>
    [Description("How much of the learner's data this answer covers.")]
    public required CoachScopeCoverage Coverage { get; init; }

    /// <summary>The order the rows are in.</summary>
    [Description("The order the rows are in.")]
    public required CoachScopeOrder Order { get; init; }

    /// <summary>
    /// False when the requested order could not be applied, so the sequence must not be described
    /// as ranked.
    /// </summary>
    [Description("False when the stated order could not be applied to this answer.")]
    public required bool OrderHonored { get; init; }

    /// <summary>The predicates this read applied.</summary>
    [Description("The filters this answer was produced under.")]
    public required CoachScopeFilters Filters { get; init; }

    /// <summary>The instant the answer was true at, to the second.</summary>
    /// <remarks>
    /// <para>
    /// Normalised on the way in by <see cref="NormalizeAsOf"/>, so every read states this the same
    /// way whatever clock it was handed. Two things made that necessary.
    /// </para>
    /// <para>
    /// The first is cost. <c>IPlanDateContext.UtcNow</c> is <see cref="DateTime.UtcNow"/>, which
    /// carries sub-second ticks, and <c>System.Text.Json</c> renders those in full:
    /// <c>"2026-08-14T12:00:00Z"</c> becomes <c>"2026-08-14T12:00:00.4821593Z"</c>. That is eight
    /// characters, on every scope, on every one of a turn's twenty tool calls — roughly a hundred
    /// and sixty characters of the model's context spent on a precision no read is accurate to and
    /// no model can act on. A scope is metadata about an answer, and sub-second metadata is the
    /// clearest case of paying for something nobody reads.
    /// </para>
    /// <para>
    /// The second is that it made the token budget untestable. A fixture clock built from whole
    /// seconds produced a shorter string than production ever would, so the ceiling was being
    /// measured against a shape that only existed in tests. Normalising here means the fixture and
    /// the deployment produce the same bytes, which is the only condition under which measuring the
    /// one says anything about the other.
    /// </para>
    /// </remarks>
    [Description("The UTC instant this answer was true at.")]
    public required DateTime AsOfUtc
    {
        get => _asOfUtc;
        init => _asOfUtc = NormalizeAsOf(value);
    }

    /// <summary>The first day of the window, when the read was window-bounded.</summary>
    [Description("The first day of the window, when the answer covers one.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? WindowStartDate { get; init; }

    /// <summary>The last day of the window, when the read was window-bounded.</summary>
    [Description("The last day of the window, when the answer covers one.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? WindowEndDate { get; init; }

    /// <summary>How many rows the caller asked for, when it asked for a bounded number.</summary>
    [Description("How many rows were asked for, when a limit was supplied.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RequestedCount
    {
        get => _requestedCount;
        init => _requestedCount = Bound(value, nameof(RequestedCount));
    }

    /// <summary>How many rows this answer carries.</summary>
    [Description("How many rows this answer carries.")]
    public required int ReturnedCount
    {
        get => _returnedCount;
        init => _returnedCount = Bound(value, nameof(ReturnedCount));
    }

    /// <summary>
    /// How many rows matched before any withholding or paging. Null when the read is not a
    /// search over a population.
    /// </summary>
    /// <remarks>
    /// Counts the population <see cref="Coverage"/> names — for a
    /// <see cref="CoachScopeCoverage.CompleteAggregateWithBreakdown"/> answer that is the
    /// breakdown, not the aggregates. It is the widest of the three counts:
    /// <see cref="ReturnedCount"/> &lt;= <see cref="EligiblePopulationCount"/> &lt;= this.
    /// </remarks>
    [Description("How many rows matched before anything was withheld or paged away.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MatchedCount
    {
        get => _matchedCount;
        init => _matchedCount = Bound(value, nameof(MatchedCount));
    }

    /// <summary>How many matching rows were deliberately not returned.</summary>
    /// <remarks>
    /// Omitted from the model's view when it is zero, which is not ambiguous:
    /// <see cref="Filters"/> is always emitted, so a read that could withhold says so by carrying
    /// the corresponding filter — <see cref="CoachScopeFilters.ExcludeDue"/>, for instance. Filter
    /// present and no count means none were withheld; filter absent means withholding was never a
    /// concept for this read.
    /// </remarks>
    [Description("How many matching rows were deliberately not returned.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int WithheldCount
    {
        get => _withheldCount;
        init => _withheldCount = Bound(value, nameof(WithheldCount));
    }

    /// <summary>Why rows were withheld.</summary>
    [Description("Why rows were withheld.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CoachScopeWithheldReason WithheldReason { get; init; }

    /// <summary>True when more rows matched than this answer carries.</summary>
    /// <remarks>
    /// Specifically: true when <em>paging</em> dropped rows that had already cleared every filter
    /// and every evidence bar. A row left out by a bar is withheld, not truncated, and the two are
    /// reported separately because only one of them can be fetched by asking for more. The
    /// population is whichever one <see cref="Coverage"/> names.
    /// </remarks>
    [Description("True when more rows matched than this answer carries.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; init; }

    // ---------------------------------------------------------------------
    // Foundation: complete in the shape, withheld from the wire until the
    // client wire tolerance gate permits new enum values to reach a client.
    //
    // Not `required`, because System.Text.Json reads a required member as a
    // required JSON property and refuses a type whose required member is also
    // ignored. Completeness is enforced instead by the Unspecified sentinel
    // every one of these enums carries plus a contract test that refuses it on
    // any shipped scope, which is a check a serializer cannot argue with.
    // ---------------------------------------------------------------------

    /// <summary>Which definition of the population this read used.</summary>
    [JsonIgnore]
    public CoachScopeDefinition DefinitionCode { get; init; }

    /// <summary>
    /// How many rows were eligible under the read's own definition, after withholding and before
    /// paging. Distinct from <see cref="MatchedCount"/>, which counts matches before withholding
    /// as well.
    /// </summary>
    /// <remarks>
    /// The middle of the three counts, and the one that makes <see cref="Truncated"/> checkable:
    /// paging dropped rows exactly when this exceeds <see cref="ReturnedCount"/>. Counts the same
    /// population as <see cref="MatchedCount"/> — a count of some other set of rows here is the
    /// defect that lets a scope report more eligible rows than it ever matched.
    /// </remarks>
    [JsonIgnore]
    public int? EligiblePopulationCount
    {
        get => _eligiblePopulationCount;
        init => _eligiblePopulationCount = Bound(value, nameof(EligiblePopulationCount));
    }

    /// <summary>What a row had to show before it counted.</summary>
    [JsonIgnore]
    public CoachScopeMinimumEvidence MinimumEvidence { get; init; }

    /// <summary>How ties were broken.</summary>
    [JsonIgnore]
    public CoachScopeTieBreak TieBreak { get; init; }

    /// <summary>Which clock decided what "now" meant.</summary>
    [JsonIgnore]
    public CoachScopeClockBasis ClockBasis { get; init; }

    /// <summary>What the time reference refers to.</summary>
    [JsonIgnore]
    public CoachScopeReferenceMode ReferenceMode { get; init; }

    private static int Bound(int value, string member) =>
        value is >= 0 and <= MaxCount
            ? value
            : throw new ArgumentOutOfRangeException(
                member,
                value,
                $"A coach result scope count must be from 0 to {MaxCount}. A value outside that range " +
                "is an arithmetic defect, and reporting it to the model would describe the learner's " +
                "account with a number that cannot be true.");

    private static int? Bound(int? value, string member) =>
        value is null ? null : Bound(value.Value, member);

    /// <summary>
    /// The canonical form of an "as of" instant: UTC, truncated to the whole second.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single normalizer every scope passes through. It lives on the
    /// <see cref="AsOfUtc"/> <c>init</c> accessor rather than in a factory method deliberately: all
    /// fourteen registered reads build their scope with an object initializer, and a factory is a
    /// convention the fifteenth can decline to follow without anything failing. An accessor is not
    /// optional — there is no construction path that reaches the backing field around it, including
    /// <c>with</c> expressions, deserialization, and any tool written next year.
    /// </para>
    /// <para>
    /// <b>Truncated, never rounded.</b> Rounding a 12:00:00.7 read to 12:00:01 would place the
    /// answer's stated instant in the future relative to the data it was computed from. "As of"
    /// is a claim that the answer was true at that moment; the only safe direction to move it is
    /// backwards, by less than a second.
    /// </para>
    /// <para>
    /// <b>Kind is pinned too.</b> A <see cref="DateTimeKind.Local"/> value renders with an offset
    /// instead of <c>Z</c>, which is both a different number of characters and a different claim
    /// from the one this member's name makes. Local is converted — preserving the instant — and
    /// <see cref="DateTimeKind.Unspecified"/> is read as UTC, which is the assumption the member
    /// name already encodes.
    /// </para>
    /// <para>
    /// No meaning is lost. No read in the coach surface is accurate to the second, let alone to a
    /// hundred nanoseconds; the reads are computed from calendar days, review dates and completion
    /// rows. The sub-second component was never information, only the residue of the clock that
    /// happened to be consulted.
    /// </para>
    /// </remarks>
    public static DateTime NormalizeAsOf(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTime(
            utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond),
            DateTimeKind.Utc);
    }
}

/// <summary>
/// A read result that states the terms it was produced under.
/// </summary>
/// <remarks>
/// Implemented by every registered read envelope, and enforced at startup rather than by
/// convention: <c>CoachOutputContract</c> refuses to hand out a tool set when a registered read
/// returns a shape that cannot say what it looked at. An unscoped answer is the one the model
/// will confidently over-claim from.
/// </remarks>
public interface ICoachScopedResult
{
    /// <summary>What this read looked at, and what it declined to hand back.</summary>
    CoachResultScope Scope { get; }
}
