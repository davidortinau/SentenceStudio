namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// The bounds and windows the opportunity ledger enforces before anything reaches the database.
/// </summary>
/// <remarks>
/// Every bound here guards a column that holds an identifier, a closed-vocabulary code, or a
/// digest. There is no bound on "text", because there is no text column — see
/// <see cref="CoachOpportunity"/>.
/// </remarks>
public static class CoachOpportunityLimits
{
    /// <summary>Maximum length of an opaque identifier column.</summary>
    public const int IdMaxLength = 64;

    /// <summary>Maximum length of the owning user profile id.</summary>
    public const int UserProfileIdMaxLength = 64;

    /// <summary>Maximum length of the forward-compatibility tenant id.</summary>
    public const int TenantIdMaxLength = 64;

    /// <summary>Maximum length of a capability code.</summary>
    public const int CapabilityCodeMaxLength = 64;

    /// <summary>Maximum length of a tool name column.</summary>
    public const int ToolNameMaxLength = 64;

    /// <summary>Maximum length of a content-free failure code.</summary>
    public const int FailureCodeMaxLength = 64;

    /// <summary>Maximum length of the fingerprint column (hex SHA-256 is 64 characters).</summary>
    public const int FingerprintMaxLength = 128;

    /// <summary>Maximum length of a reviewer's linked spec path.</summary>
    public const int LinkedSpecPathMaxLength = 200;

    /// <summary>
    /// The contract version of the row shape. Part of the fingerprint, so a shape change
    /// deliberately produces new fingerprints rather than silently merging with old rows.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// How long a row survives after its last occurrence, when nobody has decided anything
    /// about it.
    /// </summary>
    /// <remarks>
    /// 180 days rather than 90: a quarterly product review needs more than one quarter of
    /// history to see a trend, and the rows are content-free so the privacy cost of keeping them
    /// is bounded by construction. <see cref="CoachOpportunityStatus.Accepted"/> and
    /// <see cref="CoachOpportunityStatus.Deferred"/> rows are exempt — those are decisions, not
    /// observations.
    /// </remarks>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(180);

    /// <summary>How many rows one retention pass may remove, so a sweep stays bounded.</summary>
    public const int RetentionBatchSize = 500;

    /// <summary>Default page size for the operator list surface.</summary>
    public const int OperatorPageSize = 50;

    /// <summary>Maximum page size the operator list surface will honour.</summary>
    public const int OperatorPageSizeMax = 200;

    /// <summary>Maximum rows one rollup or export response returns.</summary>
    public const int OperatorRollupMax = 500;

    /// <summary>
    /// How far back the recorder looks for a related row when chaining a referent loss to the
    /// capability refusal that preceded it.
    /// </summary>
    public static readonly TimeSpan RelatedOpportunityWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The literal a caller must send to reveal encrypted evidence. Not a secret — a speed bump
    /// that makes the reveal an explicit, auditable act rather than a side effect of loading a
    /// page.
    /// </summary>
    public const string EvidenceRevealAcknowledgement = "reveal-learner-content";
}
