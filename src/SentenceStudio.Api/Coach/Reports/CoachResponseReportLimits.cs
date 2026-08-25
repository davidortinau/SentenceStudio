namespace SentenceStudio.Api.Coach.Reports;

/// <summary>
/// The bounds the learner-report table enforces before anything reaches the database.
/// </summary>
/// <remarks>
/// Every bound here guards a column holding an identifier or a closed-vocabulary code. There is
/// no bound on "text", because there is no text column — see <see cref="CoachResponseReport"/>.
/// </remarks>
public static class CoachResponseReportLimits
{
    /// <summary>Maximum length of an opaque identifier column.</summary>
    public const int IdMaxLength = 64;

    /// <summary>Maximum length of the owning user profile id.</summary>
    public const int UserProfileIdMaxLength = 64;

    /// <summary>Maximum length of the forward-compatibility tenant id.</summary>
    public const int TenantIdMaxLength = 64;

    /// <summary>Maximum length of a content-free failure code.</summary>
    public const int FailureCodeMaxLength = 64;

    /// <summary>
    /// Maximum length of the comma-separated registered tool names column.
    /// </summary>
    /// <remarks>
    /// Sized for the whole registry rather than for a guess at a typical turn, and enforced by
    /// dropping whole names rather than truncating mid-name: a half-written tool name is not a
    /// registered tool name, and a column that can hold a fragment of one is a column that can
    /// hold a fragment of something else.
    /// </remarks>
    public const int InvokedToolNamesMaxLength = 512;

    /// <summary>
    /// How many distinct tool names one row will record.
    /// </summary>
    public const int InvokedToolNamesMaxCount = 12;

    /// <summary>
    /// How many reported response ids one conversation read returns.
    /// </summary>
    /// <remarks>
    /// A learner who has reported more responses than this in one conversation has told us
    /// something the flag control cannot usefully render anyway. The bound exists so a hostile or
    /// broken client cannot turn a page load into an unbounded read.
    /// </remarks>
    public const int ReportedResponsePageSize = 500;

    /// <summary>
    /// The contract version of the row shape.
    /// </summary>
    /// <summary>
    /// Maximum length of the ordinal-sorted rule-code list.
    /// </summary>
    /// <remarks>
    /// Nine closed rule names, comma-joined, is comfortably under this. The bound exists so a
    /// column cannot grow without a decision, and it is enforced by dropping the whole value rather
    /// than truncating: half a name is a name that decodes to nothing, and a reader would have no
    /// way to tell a truncated list from a short one.
    /// </remarks>
    public const int GroundingRuleCodesMaxLength = 256;

    /// <summary>
    /// The row-shape contract version.
    /// </summary>
    /// <remarks>
    /// Bumped 1 to 2 for the eight nullable grounding columns. A version-1 row reads back with all
    /// eight null, which is the same shape a version-2 row has when the grounding ladder was Off —
    /// and that is deliberate: a report from a deployment that never ran the ladder and a report
    /// from before the columns existed are the same absence of evidence, and neither should read
    /// as a finding of zero.
    /// </remarks>
    public const int SchemaVersion = 2;
}
