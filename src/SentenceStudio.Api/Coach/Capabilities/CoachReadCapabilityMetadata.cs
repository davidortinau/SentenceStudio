using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Tools.SamTools;

namespace SentenceStudio.Api.Coach.Capabilities;

/// <summary>Whether a read accepts a date, and in what shape.</summary>
/// <remarks>
/// <b>Member set is derived, not planned.</b> §5.2 line 160 names <c>DateSupport</c> but does not
/// enumerate it. The three members below are the three shapes the frozen reads actually exhibit
/// (<c>CoachScopeFilters.DateWindow</c>, <c>CalendarDay</c>, or neither). Flagged for Simon.
/// </remarks>
public enum CoachReadDateSupport
{
    /// <summary>Takes no date.</summary>
    None = 0,

    /// <summary>Bounded to one calendar day in the learner's zone.</summary>
    CalendarDay,

    /// <summary>Bounded to a date window.</summary>
    Window
}

/// <summary>Whether a read accepts a caller-supplied bound on how much comes back.</summary>
/// <remarks>
/// <b>Member set is derived, not planned</b>, on the same basis as
/// <see cref="CoachReadDateSupport"/>. Flagged for Simon.
/// </remarks>
public enum CoachReadRangeSupport
{
    /// <summary>Takes no bound. The answer is whatever the read defines.</summary>
    None = 0,

    /// <summary>Takes a caller-supplied maximum row count, clamped by the tool.</summary>
    ResultLimit
}

/// <summary>
/// The §5.2 read-capability metadata for one read tool.
/// </summary>
/// <param name="Coverage">What population the answer covers.</param>
/// <param name="SupportedOrders">Every order the read can emit.</param>
/// <param name="SupportedFilters">Every filter the read can apply.</param>
/// <param name="DateSupport">Whether it takes a date, and in what shape.</param>
/// <param name="RangeSupport">Whether it takes a caller bound on size.</param>
/// <param name="MaxPageSize">The tool's own clamp, or null when it does not page.</param>
/// <param name="Source">
/// Where each value was read from. Carried on the record rather than in a comment so the citation
/// travels with the data and a reviewer can check a row without leaving it.
/// </param>
public sealed record CoachReadCapabilityMetadata(
    CoachScopeCoverage Coverage,
    IReadOnlyList<CoachScopeOrder> SupportedOrders,
    CoachScopeFilters SupportedFilters,
    CoachReadDateSupport DateSupport,
    CoachReadRangeSupport RangeSupport,
    int? MaxPageSize,
    string Source);

/// <summary>
/// The one declared source of read-capability metadata, keyed by tool name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a rival manifest.</b> This holds no capability and answers no availability question. It
/// is a lookup the single manifest reads while projecting a registration, in the same way the
/// registration itself is read. Nothing resolves against it.
/// </para>
/// <para>
/// <b>Every value comes from the tool that emits it</b>, not from the tool's name or its
/// description. Each row carries the file and the member it came from. Where a value is genuinely
/// absent from the source — a read that declares no clamp — the row says <see langword="null"/>
/// rather than a plausible number, because a guessed page size is worse than an admitted gap: it
/// would be repeated back to a learner as fact.
/// </para>
/// <para>
/// <b>Ceilings are cited, not transcribed.</b> The five bounded reads each declare their clamp as
/// one constant, and the rows below reference that constant directly rather than restating the
/// number. A hand-copied number is a number that drifts: the first version of this table recorded
/// <see langword="null"/> for <c>GetVocabularyDueSummary</c> and explained the gap with a rationale
/// that was simply untrue — the tool has declared a ceiling of
/// <see cref="VocabularyDueSummaryTool.MaxTagCount"/> the whole time and rejects anything outside
/// it. Citing the constant makes that class of error unrepresentable rather than merely corrected.
/// </para>
/// <para>
/// <see cref="CoachReadCapabilityMetadataValidator"/> asserts at startup that this table and the
/// frozen registry agree in both directions — no read without metadata, no metadata without a read
/// — and that every declared ceiling still matches the tool constant it claims to cite.
/// </para>
/// </remarks>
public static class CoachReadCapabilityMetadataTable
{
    private const CoachScopeFilters Owner = CoachScopeFilters.OwnerScoped;

    private static readonly Dictionary<string, CoachReadCapabilityMetadata> _byToolName = new(StringComparer.Ordinal)
    {
        [CoachToolNames.GetLearnerProfileSummary] = new(
            CoachScopeCoverage.SettingsSnapshot,
            [CoachScopeOrder.NotApplicable],
            Owner,
            CoachReadDateSupport.None,
            CoachReadRangeSupport.None,
            MaxPageSize: null,
            Source: "LearnerProfileSummaryTool.cs scope block"),

        [CoachToolNames.GetPracticeBalance] = new(
            CoachScopeCoverage.WindowBounded,
            [CoachScopeOrder.MinutesDescending],
            Owner | CoachScopeFilters.DateWindow | CoachScopeFilters.MinimumEvidence,
            CoachReadDateSupport.Window,
            CoachReadRangeSupport.None,
            MaxPageSize: null,
            Source: "PracticeBalanceTool.cs scope block"),

        [CoachToolNames.GetVocabularyDueSummary] = new(
            CoachScopeCoverage.CompleteAggregateWithBreakdown,
            [CoachScopeOrder.FrequencyDescending],
            Owner | CoachScopeFilters.ProgressRowExists,
            CoachReadDateSupport.None,
            CoachReadRangeSupport.ResultLimit,
            MaxPageSize: VocabularyDueSummaryTool.MaxTagCount,
            Source: "VocabularyDueSummaryTool.cs MaxTagCount; RequestedCount = maxCategoryTags, "
                + "which the tool rejects outside 1..MaxTagCount"),

        [CoachToolNames.GetResourceCatalog] = new(
            CoachScopeCoverage.PageOfOwnedSet,
            [CoachScopeOrder.LastUsedAscending],
            Owner,
            CoachReadDateSupport.None,
            CoachReadRangeSupport.ResultLimit,
            MaxPageSize: ResourceCatalogTool.MaxResults,
            Source: "ResourceCatalogTool.cs MaxResults; coverage is Page or Complete by count"),

        [CoachToolNames.PreviewPracticePlan] = new(
            CoachScopeCoverage.DerivedProjection,
            [CoachScopeOrder.PriorityAscending],
            Owner,
            CoachReadDateSupport.None,
            CoachReadRangeSupport.None,
            MaxPageSize: null,
            Source: "PreviewPracticePlanTool.cs scope block"),

        [CoachToolNames.ListUserVocabularies] = new(
            CoachScopeCoverage.PageOfOwnedSet,
            [CoachScopeOrder.MasteryDescending],
            Owner | CoachScopeFilters.ProgressRowExists | CoachScopeFilters.ExcludeDue
                | CoachScopeFilters.TextQuery,
            CoachReadDateSupport.None,
            CoachReadRangeSupport.ResultLimit,
            MaxPageSize: VocabularySearchTool.MaxResults,
            Source: "SamTools/VocabularySearchTool.cs MaxResults; TextQuery applied only when a query is given"),

        [CoachToolNames.GetVocabularyWordDetail] = new(
            CoachScopeCoverage.SingleItem,
            [CoachScopeOrder.NotApplicable],
            Owner | CoachScopeFilters.ProgressRowExists | CoachScopeFilters.SingleIdentifier,
            CoachReadDateSupport.None,
            CoachReadRangeSupport.None,
            MaxPageSize: null,
            Source: "VocabularyWordDetailTool.cs scope block"),

        [CoachToolNames.GetSkillList] = new(
            CoachScopeCoverage.PageOfOwnedSet,
            [CoachScopeOrder.UpdatedDescending],
            Owner | CoachScopeFilters.ExcludeArchived,
            CoachReadDateSupport.None,
            CoachReadRangeSupport.ResultLimit,
            MaxPageSize: SkillListTool.MaxResults,
            Source: "SamTools/SkillTools.cs SkillListTool.MaxResults"),

        [CoachToolNames.GetSkillDetail] = new(
            CoachScopeCoverage.SingleItem,
            [CoachScopeOrder.NotApplicable],
            Owner,
            CoachReadDateSupport.None,
            CoachReadRangeSupport.None,
            MaxPageSize: null,
            Source: "SamTools/SkillTools.cs detail scope block"),

        [CoachToolNames.GetLearningResourceList] = new(
            CoachScopeCoverage.PageOfOwnedSet,
            [CoachScopeOrder.UpdatedDescending],
            Owner,
            CoachReadDateSupport.None,
            CoachReadRangeSupport.ResultLimit,
            MaxPageSize: LearningResourceListTool.MaxResults,
            Source: "SamTools/LearningResourceTools.cs LearningResourceListTool.MaxResults"),

        [CoachToolNames.GetLearningResourceDetail] = new(
            CoachScopeCoverage.SingleItem,
            [CoachScopeOrder.NotApplicable],
            Owner | CoachScopeFilters.SingleIdentifier,
            CoachReadDateSupport.None,
            CoachReadRangeSupport.None,
            MaxPageSize: null,
            Source: "SamTools/LearningResourceTools.cs detail scope block"),

        [CoachToolNames.GetCurrentProfileSummary] = new(
            CoachScopeCoverage.SettingsSnapshot,
            [CoachScopeOrder.NotApplicable],
            Owner,
            CoachReadDateSupport.None,
            CoachReadRangeSupport.None,
            MaxPageSize: null,
            Source: "SamTools/ProfileTools.cs scope block"),

        [CoachToolNames.GetLearnerSettingsSummary] = new(
            CoachScopeCoverage.SettingsSnapshot,
            [CoachScopeOrder.NotApplicable],
            Owner,
            CoachReadDateSupport.None,
            CoachReadRangeSupport.None,
            MaxPageSize: null,
            Source: "SamTools/ProfileTools.cs settings scope block"),

        [CoachToolNames.GetCurrentPlanSummary] = new(
            CoachScopeCoverage.SingleDay,
            [CoachScopeOrder.Unordered],
            Owner | CoachScopeFilters.CalendarDay,
            CoachReadDateSupport.CalendarDay,
            CoachReadRangeSupport.None,
            MaxPageSize: null,
            Source: "SamTools/CurrentPlanSummaryTool.cs scope block"),

        [CoachToolNames.GetPracticeHistorySummary] = new(
            CoachScopeCoverage.DerivedProjection,
            [CoachScopeOrder.NotApplicable],
            Owner,
            CoachReadDateSupport.None,
            CoachReadRangeSupport.None,
            MaxPageSize: null,
            Source: "PracticeHistorySummaryTool.cs scope block")
    };

    /// <summary>Every declared row, for census assertions.</summary>
    public static IReadOnlyDictionary<string, CoachReadCapabilityMetadata> All => _byToolName;

    /// <summary>The metadata for <paramref name="toolName"/>, or null when none is declared.</summary>
    public static CoachReadCapabilityMetadata? Find(string toolName) =>
        toolName is not null && _byToolName.TryGetValue(toolName, out var found) ? found : null;
}

/// <summary>
/// The table startup validation reads.
/// </summary>
/// <remarks>
/// Production registers nothing and startup falls back to
/// <see cref="CoachReadCapabilityMetadataTable.All"/>. The seam exists so a fixture can hand the
/// host a doctored table and prove the host refuses to start — the same reason
/// <see cref="CoachCapabilityManifest"/> accepts extra declarations. A startup check nobody can
/// watch fail is the thing this whole file is a correction for.
/// </remarks>
public interface ICoachReadCapabilityMetadataSource
{
    /// <summary>The rows to validate.</summary>
    IReadOnlyDictionary<string, CoachReadCapabilityMetadata> All { get; }
}
