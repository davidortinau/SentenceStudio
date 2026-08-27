using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Coach.Persistence;

/// <summary>
/// The resolved persistence settings used by the coach stores and the cleanup service.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is not bound to configuration.</b> The public configuration contract is
/// <see cref="CoachOptions"/> (<c>Coach:*</c>), and
/// <see cref="CoachPersistenceOptionsSetup"/> projects the operator-owned values onto this
/// type. There is deliberately no <c>Coach:Persistence:*</c> section: two environment keys
/// claiming control over the same knob is how an operator change silently does nothing.
/// </para>
/// <para>
/// The remaining members are implementation details with no operator surface. Changing them
/// is a code change, reviewed like any other.
/// </para>
/// <para>
/// Values resolve once through <c>IOptions</c>, so a change to <c>Coach:SessionExpiryHours</c>,
/// <c>Coach:RevisionRetentionDays</c>, or <c>Coach:AgentConfigVersion</c> takes effect on the
/// next host start. (<c>Coach:Enabled</c> and the cohort stay hot-reloadable — those are read
/// through <c>IOptionsMonitor</c> by the availability policy.)
/// </para>
/// </remarks>
public sealed class CoachPersistenceOptions
{
    /// <summary>
    /// The serialized-agent-session schema version this build can rehydrate. Bump it in code
    /// whenever the stored agent state format changes; stored sessions on any other value are
    /// rejected on load. This is not an operator knob — an operator cannot make an
    /// unrehydratable payload readable by editing configuration.
    /// </summary>
    public const int CurrentSessionSchemaVersion = 1;

    /// <summary>
    /// Sliding session lifetime, projected from <see cref="CoachOptions.SessionExpiryHours"/>.
    /// Reads and writes push a session's expiry forward by this amount; a read past the expiry
    /// is rejected.
    /// </summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long the normalized revision audit is kept, projected from
    /// <see cref="CoachOptions.RevisionRetentionDays"/>. Removing an audit row does not undo the
    /// plan change it recorded.
    /// </summary>
    public TimeSpan RevisionRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// The current coach configuration version, projected from
    /// <see cref="CoachOptions.AgentConfigVersion"/>. A stored session stamped with a different
    /// value is rejected on load, because the instructions, tools, or policy it was created
    /// under no longer exist.
    /// </summary>
    public string AgentConfigVersion { get; set; } = "1";

    /// <summary>
    /// The serialized-agent-session schema version. Defaults to
    /// <see cref="CurrentSessionSchemaVersion"/>; tests override it to prove the rejection path.
    /// </summary>
    public int SessionSchemaVersion { get; set; } = CurrentSessionSchemaVersion;

    /// <summary>
    /// How long daily usage counters are kept. Implementation detail: counters are only read
    /// for the current day and ISO week, so this window exists for cost forensics, not policy.
    /// </summary>
    public TimeSpan UsageRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Maximum rows a single cleanup pass deletes per entity set.</summary>
    public int CleanupBatchSize { get; set; } = 500;
}
