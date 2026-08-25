namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// The switches that govern the opportunity ledger.
/// </summary>
/// <remarks>
/// <para>
/// Bound from <c>Coach:Opportunities</c>. Both switches are <b>nested</b>
/// (<c>Coach:Opportunities:Enabled</c>, <c>Coach:Opportunities:OperatorSurface:Enabled</c>) for
/// the same reason every other coach feature switch is: a flat <c>Coach:Opportunities=true</c>
/// binds to a value node, the binder finds no <c>:Enabled</c> child, and the feature stays off
/// while the deployment believes it is on. <c>CoachConfigurationKeyValidator</c> turns the flat
/// spelling into a startup failure rather than a silent no-op.
/// </para>
/// </remarks>
public sealed class CoachOpportunityOptions
{
    /// <summary>The configuration section name: <c>Coach:Opportunities</c>.</summary>
    public const string SectionName = "Coach:Opportunities";

    /// <summary>
    /// Whether the recorder writes rows at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <b>false</b>. Development turns it on in <c>appsettings.Development.json</c>.
    /// Production stays off until the end-to-end suite has been reviewed and Captain approves the
    /// flip — capture is provably response-neutral (the recorder runs after the response is
    /// computed, inside <c>try/catch</c>, and its output is never read by any learner-facing
    /// path), but "provably" means "after the proof has been reviewed", not "because the design
    /// says so".
    /// </para>
    /// <para>
    /// Off means no-op, not throw: a disabled recorder still satisfies its interface and still
    /// cannot alter a turn.
    /// </para>
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>The operator review surface. Development-only.</summary>
    public CoachOpportunityOperatorSurfaceOptions OperatorSurface { get; set; } = new();

    /// <summary>Whether the retention sweep removes expired rows.</summary>
    /// <remarks>
    /// Separate from <see cref="Enabled"/> so a deployment that turns capture off still ages out
    /// the rows it already wrote, rather than keeping them forever by accident.
    /// </remarks>
    public bool RetentionSweepEnabled { get; set; } = true;

    /// <summary>
    /// How many days a row survives past its last occurrence when nobody has decided anything
    /// about it.
    /// </summary>
    public int RetentionDays { get; set; } = (int)CoachOpportunityLimits.Retention.TotalDays;

    /// <summary>The retention window as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Retention => TimeSpan.FromDays(RetentionDays);
}

/// <summary>
/// The Development-only operator review surface.
/// </summary>
/// <remarks>
/// <para>
/// Four independent gates protect this surface, and they fail closed in that order:
/// </para>
/// <list type="number">
/// <item>The routes are <b>not mapped at all</b> outside Development, so they 404 rather than
/// 403 — the coach never confirms that something exists but is off-limits.</item>
/// <item><see cref="Enabled"/> must be true.</item>
/// <item><c>CoachOpportunityOptionsValidator</c> fails host startup if <see cref="Enabled"/> is
/// true outside Development. The second gate matters because configuration reload does not
/// re-run <c>ValidateOnStart</c>.</item>
/// <item>The caller must be authenticated <b>and</b> their <c>user_profile_id</c> must be in
/// <c>Coach:AllowedUserProfileIds</c>. The <c>__dev_all__</c> sentinel is <b>not</b> honoured
/// here.</item>
/// </list>
/// <para>
/// This is fail-closed rather than a role check because the codebase has no admin authorization
/// primitive. Inventing one under time pressure to ship a backlog-review screen would be the
/// wrong trade; when an admin primitive exists, gates 1 and 3 become a policy and gates 2 and 4
/// stay.
/// </para>
/// </remarks>
public sealed class CoachOpportunityOperatorSurfaceOptions
{
    /// <summary>The configuration section name: <c>Coach:Opportunities:OperatorSurface</c>.</summary>
    public const string SectionName = "Coach:Opportunities:OperatorSurface";

    /// <summary>Whether the operator routes and page are available. Development-only.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether an operator may reveal evidence on a row they do not own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <b>false</b> and stays false. This is the one control in the design that
    /// crosses the boundary the cross-tenant write tests were built to defend, so it is off, it
    /// is Development-only by inheritance from the surface gates, and every reveal it permits is
    /// counted on the row and logged.
    /// </para>
    /// </remarks>
    public bool AllowCrossOwnerEvidence { get; set; }
}
