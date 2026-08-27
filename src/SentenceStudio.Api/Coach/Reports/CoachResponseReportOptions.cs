namespace SentenceStudio.Api.Coach.Reports;

/// <summary>
/// The switches that govern learner-initiated response reports.
/// </summary>
/// <remarks>
/// <para>
/// Bound from <c>Coach:Reports</c>, and <b>deliberately its own section rather than a child of
/// <c>Coach:Opportunities</c></b>. Automatic capture observes the server refusing itself; a
/// report is a learner spending an action to disagree with a turn the server thought went fine.
/// Those are different things with different risk profiles, so they get different switches — and
/// crucially, turning automatic capture off must never silently discard a report the learner was
/// told had been received. <c>CoachOpportunityRecorder</c> honours that by admitting
/// <c>UserReportedResponse</c> on this switch alone.
/// </para>
/// <para>
/// The switch is nested (<c>Coach:Reports:Enabled</c>) for the same reason every other coach
/// switch is: a flat <c>Coach:Reports=true</c> binds to a value node, the binder finds no
/// <c>:Enabled</c> child, and the feature stays off while the deployment believes it is on.
/// <c>CoachConfigurationKeyValidator</c> turns the flat spelling into a startup failure.
/// </para>
/// </remarks>
public sealed class CoachResponseReportOptions
{
    /// <summary>The configuration section name: <c>Coach:Reports</c>.</summary>
    public const string SectionName = "Coach:Reports";

    /// <summary>
    /// Whether learners may report a response, and whether reports are recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <b>false</b>. Development turns it on in <c>appsettings.Development.json</c>.
    /// <b>Production stays off pending Captain's decision</b> — not because the mechanism is
    /// unsafe (the row is content-free by construction, the endpoint is owner-scoped, and no
    /// model tool can reach it), but because turning it on is a product promise: a learner who
    /// presses Report is told the report goes somewhere a person looks, and that promise should
    /// be made deliberately rather than inherited from a default.
    /// </para>
    /// <para>
    /// Off means the report routes are not mapped at all, so they 404 exactly as an unknown route
    /// does. That is what keeps the client's feature probe honest: the flag control is withheld
    /// rather than shown and then rejected.
    /// </para>
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// How many days a report survives.
    /// </summary>
    /// <remarks>
    /// Matched to the opportunity ledger's own default so a reviewer reading a ledger row can
    /// still find the report that raised it. The rows are content-free, so the privacy cost of
    /// keeping them is bounded by construction; the reason there is a bound at all is that an
    /// unbounded table is a table nobody has decided the retention policy for.
    /// </remarks>
    public int RetentionDays { get; set; } = 180;

    /// <summary>Whether the retention sweep removes expired reports.</summary>
    /// <remarks>
    /// Separate from <see cref="Enabled"/> so a deployment that turns reporting off still ages
    /// out the rows it already wrote, rather than keeping them forever by accident.
    /// </remarks>
    public bool RetentionSweepEnabled { get; set; } = true;

    /// <summary>The retention window as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Retention => TimeSpan.FromDays(RetentionDays);
}
