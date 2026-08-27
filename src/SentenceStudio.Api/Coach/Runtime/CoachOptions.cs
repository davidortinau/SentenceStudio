using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Runtime;

/// <summary>
/// Server configuration for the Learning Coach, bound from the <c>Coach:*</c> configuration
/// section (environment variables use the <c>Coach__*</c> form).
/// </summary>
/// <remarks>
/// <para>
/// Every default here is chosen to fail closed: the feature is off, the arm is the plain
/// baseline agent, and the cohort is empty. A deployment must opt in explicitly, per user,
/// before any coach route or model call becomes reachable.
/// </para>
/// <para>
/// Bounds are enforced by <see cref="CoachOptionsValidator"/> at startup, not at call sites.
/// A configuration mistake stops the host with a readable message instead of producing a
/// runaway budget, an unbounded run, or a session that never expires.
/// </para>
/// </remarks>
public sealed class CoachOptions
{
    /// <summary>The configuration section name: <c>Coach</c>.</summary>
    public const string SectionName = "Coach";

    /// <summary>
    /// Master feature flag. When false the coach routes return 404, no entry point is shown,
    /// and no model call is made. Disabling is non-destructive: stored sessions and revisions
    /// remain until their normal expiry or deletion.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Which coach arm serves runs. Defaults to <see cref="CoachImplementation.Baseline"/> and
    /// stays there until the trajectory evaluation supports a harness decision.
    /// </summary>
    public CoachImplementation Implementation { get; set; } = CoachImplementation.Baseline;

    /// <summary>
    /// Turns on durable, encrypted conversation history: the
    /// <c>/api/v1/coach/conversations</c> surface, the canonical message ledger, and durable
    /// turn operations. Off by default. Canonical key: <c>Coach:DurableHistory:Enabled</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="Enabled"/> on purpose. <see cref="Enabled"/> answers "may this
    /// learner talk to the coach at all"; this answers "does what they say survive the 24-hour
    /// checkpoint". Turning it off is non-destructive — the ledger stays encrypted at rest and
    /// the conversation routes answer 404 — so a rollback loses the surface, never the rows.
    /// </para>
    /// <para>
    /// While this is false the coach behaves exactly as it did before durable history existed:
    /// a session is the only state, <c>Messages</c> comes back empty on a read, and turn
    /// idempotency is the process-local store that a restart forgets.
    /// </para>
    /// <para>
    /// This is a nested switch, not a flat boolean, and that shape is load-bearing. The
    /// Data Protection guard in <c>Security/DataProtection</c> decides whether Production may
    /// boot without a durable key ring, and it keys off the same
    /// <c>Coach:DurableHistory:Enabled</c> path. When this was a flat <c>Coach:DurableHistory</c>
    /// boolean the two disagreed: the flat key turned the ledger on while the guard stayed off,
    /// so a host could write durable history against an ephemeral key ring and lose the ability
    /// to read it after a restart. One shape, one key, both readers.
    /// </para>
    /// </remarks>
    public CoachFeatureSwitch DurableHistory { get; set; } = new();

    /// <summary>
    /// Long-term learner memory. Off by default. Canonical key: <c>Coach:Memory:Enabled</c>.
    /// </summary>
    /// <remarks>
    /// This binds the same key as <c>CoachMemoryOptions.Enabled</c> and exists so the durable
    /// content gate can read history and memory from one options instance instead of issuing its
    /// own raw configuration reads. <c>CoachMemoryOptions</c> remains the owner of every other
    /// memory setting; this switch reads nothing but the master flag.
    /// </remarks>
    public CoachFeatureSwitch Memory { get; set; } = new();

    /// <summary>True when durable conversation history is switched on.</summary>
    /// <remarks>
    /// Prefer this over reaching through <see cref="DurableHistory"/> at a call site. It is the
    /// single effective answer that <c>CoachAvailabilityResponse.IsDurableHistoryAvailable</c>
    /// and the Data Protection guard both resolve to.
    /// </remarks>
    public bool IsDurableHistoryEnabled => DurableHistory.Enabled;

    /// <summary>True when learner memory is switched on.</summary>
    public bool IsMemoryEnabled => Memory.Enabled;

    /// <summary>
    /// Persistent overlay UX for Sam. Off by default. Canonical key: <c>Coach:SamOverlay:Enabled</c>.
    /// Requires <see cref="DurableHistory"/> (overlay conversations need a durable ledger).
    /// </summary>
    public CoachFeatureSwitch SamOverlay { get; set; } = new();

    /// <summary>
    /// Extended read tools beyond the original 5. Off by default.
    /// Requires <see cref="SamOverlay"/> (read tool results surface in the overlay).
    /// </summary>
    public CoachFeatureSwitch SamReadTools { get; set; } = new();

    /// <summary>
    /// Write-capable proposal tools (<c>propose_*</c>). Off by default.
    /// Requires <see cref="SamReadTools"/>.
    /// </summary>
    public CoachFeatureSwitch SamWriteTools { get; set; } = new();

    /// <summary>
    /// The grounding tier. Canonical key: <c>Coach:Grounding:Stage</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nested rather than flat, for the reason <see cref="CoachFeatureSwitch"/> spells out: a flat
    /// <c>Coach:Grounding=Observe</c> binds to nothing and leaves the ladder at
    /// <see cref="CoachGroundingStage.Off"/> while the deployment believes grounding is on.
    /// <c>CoachConfigurationKeyValidator</c> refuses that spelling at startup.
    /// </para>
    /// <para>
    /// <b>The default is Off and W6 ships it that way.</b> Plan §10.2 promotes the stage in its own
    /// step, after the code that reads it is in production and quiet. A workstream that shipped
    /// itself already promoted would be deciding a rollout question that belongs to the operator.
    /// </para>
    /// </remarks>
    public CoachGroundingOptions Grounding { get; set; } = new();

    /// <summary>
    /// Correction and dispute state. Canonical key: <c>Coach:CorrectionState:Enabled</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Default off, and W8 ships it that way.</b> Promotion is the operator's decision and it
    /// belongs to the rollout step, not to the workstream that wrote the code. A workstream that
    /// shipped itself already on would be answering a question nobody asked it.
    /// </para>
    /// <para>
    /// <b>Off is a total bypass.</b> No dispute is classified, none is persisted, and
    /// <c>RepeatedDisputedClaim</c> has nothing to fire on because the rule context carries no
    /// dispute. There is no second flag inside the pipeline that could get out of step with this
    /// one.
    /// </para>
    /// <para>
    /// Nested rather than flat, for the reason <see cref="CoachFeatureSwitch"/> spells out: a flat
    /// <c>Coach:CorrectionState=true</c> binds to nothing and leaves the feature off while the
    /// deployment believes it is on. <c>CoachConfigurationKeyValidator</c> refuses that spelling at
    /// startup.
    /// </para>
    /// </remarks>
    public CoachFeatureSwitch CorrectionState { get; set; } = new();

    /// <summary>True when correction and dispute state is switched on.</summary>
    public bool IsCorrectionStateEnabled => CorrectionState.Enabled;

    /// <summary>True when the Sam overlay UX is switched on.</summary>
    public bool IsSamOverlayEnabled => SamOverlay.Enabled;

    /// <summary>True when extended read tools are switched on.</summary>
    public bool IsSamReadToolsEnabled => SamReadTools.Enabled;

    /// <summary>True when write tools (propose_*) are switched on.</summary>
    public bool IsSamWriteToolsEnabled => SamWriteTools.Enabled;

    /// <summary>
    /// The pilot cohort, as <c>user_profile_id</c> values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This list fails closed. An empty list means <em>nobody</em> is in the cohort, even when
    /// <see cref="Enabled"/> is true. Stage 1 is an internal dogfood, so an operator must name
    /// each participating learner rather than flipping one boolean and exposing everyone.
    /// </para>
    /// <para>
    /// The sentinel value <see cref="DevAllSentinel"/> (<c>__dev_all__</c>) admits any
    /// authenticated user. It exists so a local Aspire run does not require hardcoding a
    /// database-specific profile GUID that changes with every fresh Postgres volume.
    /// It is honoured <b>only</b> in the Development environment, and only when a caller opts in
    /// explicitly: <see cref="CoachOptionsValidator"/> fails startup if it appears outside
    /// Development, and <see cref="IsInCohort(string?)"/> ignores it unless the caller passes
    /// <c>allowDevelopmentSentinel: true</c>. Two independent gates, because a validator runs
    /// once at boot and configuration can be reloaded afterwards.
    /// </para>
    /// </remarks>
    public IList<string> AllowedUserProfileIds { get; set; } = new List<string>();

    /// <summary>
    /// Sentinel value that, when present in <see cref="AllowedUserProfileIds"/>, admits every
    /// authenticated user into the cohort. Development only.
    /// </summary>
    /// <remarks>
    /// It is never honoured by <see cref="IsInCohort(string?)"/>. A caller must ask for it by
    /// passing <c>allowDevelopmentSentinel: true</c> to
    /// <see cref="IsInCohort(string?, bool)"/>, and the only caller that does is
    /// <c>CoachAvailabilityPolicy</c> when <see cref="IHostEnvironment.IsDevelopment"/> is true.
    /// </remarks>
    public const string DevAllSentinel = "__dev_all__";

    /// <summary>
    /// True when <see cref="AllowedUserProfileIds"/> contains <see cref="DevAllSentinel"/>.
    /// </summary>
    /// <remarks>
    /// Whitespace-tolerant, because a value arriving from an environment variable or a JSON
    /// manifest can carry padding, and a padded sentinel must be caught by the validator rather
    /// than sliding through as an ordinary (never-matching) cohort id.
    /// </remarks>
    public bool ContainsDevelopmentSentinel
    {
        get
        {
            for (var i = 0; i < AllowedUserProfileIds.Count; i++)
            {
                var allowed = AllowedUserProfileIds[i];
                if (!string.IsNullOrWhiteSpace(allowed)
                    && string.Equals(allowed.Trim(), DevAllSentinel, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The version stamp for the agent instructions, tool surface, and policy. Persisted on each
    /// coach session so a resumed session can be rejected when the server-side agent contract moved.
    /// </summary>
    /// <remarks>
    /// Bumped to 2 for the dual-purpose coach: the turn-intent schema gained a pedagogical
    /// answer and the instructions gained a second job, so a session serialized under version 1
    /// describes a conversation the current agent would not have had. Sessions on an older
    /// version are refused on read rather than resumed.
    /// </remarks>
    public string AgentConfigVersion { get; set; } = "2";

    /// <summary>Maximum coach runs per learner per user-local day.</summary>
    /// <remarks>Pilot-conservative default. Raise deliberately once cost per run is measured.</remarks>
    public int MaxRunsPerDay { get; set; } = 10;

    /// <summary>Maximum coach runs per learner per user-local ISO week.</summary>
    public int MaxRunsPerWeek { get; set; } = 40;

    /// <summary>Sliding session expiry, in hours. A session past this age rejects new turns.</summary>
    public int SessionExpiryHours { get; set; } = 24;

    /// <summary>How long normalized plan-revision audit rows are retained, in days.</summary>
    public int RevisionRetentionDays { get; set; } = 30;

    /// <summary>Wall-clock limit for one coach run, in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 45;

    /// <summary>Maximum combined model and tool iterations inside one run.</summary>
    public int MaxIterationsPerRequest { get; set; } = 6;

    /// <summary>
    /// Maximum clarification questions the coach may ask in one session.
    /// Must not exceed <see cref="CoachConstraintLimits.MaxClarificationsPerSession"/>, which the
    /// client also enforces.
    /// </summary>
    public int MaxClarificationsPerSession { get; set; } = CoachConstraintLimits.MaxClarificationsPerSession;

    /// <summary>
    /// Maximum model output tokens for one response. This maps to the agent's per-response
    /// <c>ChatOptions.MaxOutputTokens</c>, never to a model-capability property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On a reasoning model this is a <b>total generation</b> budget, not a visible-answer
    /// budget: Microsoft Learn states that <c>max_completion_tokens</c> covers "reasoning
    /// tokens, visible output tokens, and formatting tokens", and that exhausting it "can
    /// occur before the model produces any visible output".
    /// (<c>https://learn.microsoft.com/azure/foundry/openai/how-to/reasoning</c>)
    /// </para>
    /// <para>
    /// The original 1,200 was sized for the typed intent alone — the schema caps the coach
    /// message at 400 characters and the clarifying question at 200, so the visible answer is
    /// only a few hundred tokens. A live gpt-5-mini session then spent the entire 1,200 on
    /// hidden reasoning during a tool-using suggestion turn and returned nothing.
    /// </para>
    /// <para>
    /// 16,000 is a measured ceiling, not an open budget: roughly forty times the largest
    /// visible answer the schema permits, which leaves room for reasoning plus several tool-call
    /// round trips inside one response while still bounding a runaway. Learn suggests reserving
    /// at least 25,000 tokens "while you're getting a feel for a workload"; pairing a smaller
    /// reserve with <see cref="ReasoningEffort"/> of <c>minimal</c> is the deliberate trade.
    /// Tokens are billed on what is generated, not on the cap, so headroom costs nothing on a
    /// normal turn. Tune it down once run telemetry shows real reasoning-token usage.
    /// </para>
    /// </remarks>
    public int MaxOutputTokens { get; set; } = 16_000;

    /// <summary>
    /// Reasoning effort the coach requests, as one of <c>minimal</c>, <c>low</c>, <c>medium</c>,
    /// or <c>high</c>. Empty means "do not send the parameter".
    /// </summary>
    /// <remarks>
    /// A coach turn is bounded classification and extraction against a closed schema, so it
    /// wants the least reasoning the model offers; GPT-5 reasoning models accept <c>minimal</c>.
    /// Parallel tool calls are unavailable at <c>minimal</c>, so the registered read-only tools are
    /// called in sequence. Raise this to <c>low</c> if the trajectory evaluation shows the model
    /// under-using its tools.
    /// </remarks>
    public string ReasoningEffort { get; set; } = "minimal";

    /// <summary>
    /// Maximum learner input characters accepted in one turn. Must not exceed
    /// <see cref="CoachConstraintLimits.MaxTurnTextLength"/>.
    /// </summary>
    public int MaxTurnTextLength { get; set; } = CoachConstraintLimits.MaxTurnTextLength;

    /// <summary>The run timeout as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(RequestTimeoutSeconds);

    /// <summary>The session sliding expiry as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan SessionExpiry => TimeSpan.FromHours(SessionExpiryHours);

    /// <summary>The revision retention window as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan RevisionRetention => TimeSpan.FromDays(RevisionRetentionDays);

    /// <summary>
    /// True when <paramref name="userProfileId"/> is named explicitly in
    /// <see cref="AllowedUserProfileIds"/>. The development sentinel is <b>not</b> honoured.
    /// </summary>
    /// <remarks>
    /// This overload fails closed on purpose: a call site that has not thought about the
    /// environment gets the strict answer. Honouring <see cref="DevAllSentinel"/> requires
    /// asking for it through <see cref="IsInCohort(string?, bool)"/>.
    /// </remarks>
    public bool IsInCohort(string? userProfileId) =>
        IsInCohort(userProfileId, allowDevelopmentSentinel: false);

    /// <summary>
    /// True when <paramref name="userProfileId"/> is named in <see cref="AllowedUserProfileIds"/>.
    /// Comparison is ordinal and case-sensitive because profile ids are opaque server values.
    /// </summary>
    /// <param name="userProfileId">The authenticated learner's profile id.</param>
    /// <param name="allowDevelopmentSentinel">
    /// Whether <see cref="DevAllSentinel"/> may admit any authenticated user. Only the
    /// Development environment may pass true. Outside Development the sentinel is treated as an
    /// ordinary cohort entry, which never matches a real profile id, so a configuration that
    /// somehow reached a Production host after startup validation still admits nobody.
    /// </param>
    public bool IsInCohort(string? userProfileId, bool allowDevelopmentSentinel)
    {
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            return false;
        }

        var trimmed = userProfileId.Trim();
        for (var i = 0; i < AllowedUserProfileIds.Count; i++)
        {
            var allowed = AllowedUserProfileIds[i];
            if (string.IsNullOrWhiteSpace(allowed))
            {
                continue;
            }

            var allowedTrimmed = allowed.Trim();

            // Dev-only wildcard: admits any authenticated user without hardcoding a GUID.
            // Outside Development it falls through to the ordinary comparison below, which
            // cannot match because a real profile id is never the literal sentinel.
            if (string.Equals(allowedTrimmed, DevAllSentinel, StringComparison.Ordinal))
            {
                if (allowDevelopmentSentinel)
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(allowedTrimmed, trimmed, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A nested feature switch bound from a <c>...:Enabled</c> configuration key.
/// </summary>
/// <remarks>
/// <para>
/// The nesting is the point. A flat boolean and a nested object are indistinguishable to a
/// human reading a deployment manifest but are entirely different to
/// <see cref="IConfiguration"/>: <c>Coach:DurableHistory=true</c> produces a value node with no
/// children, so anything binding <c>Coach:DurableHistory:Enabled</c> reads nothing and silently
/// stays off.
/// </para>
/// <para>
/// That is not a hypothetical. Durable history shipped as a flat <c>Coach:DurableHistory</c>
/// while the Data Protection guard read the nested <c>Coach:DurableHistory:Enabled</c>, so a
/// host configured with the flat key wrote encrypted history rows while the guard believed no
/// durable content existed and allowed an ephemeral key ring. The rows became unreadable on the
/// next restart. Modelling the switch as an object makes the canonical key the only key that
/// binds, and <c>CoachConfigurationKeyValidator</c> turns the flat spelling into a startup
/// failure rather than a silent off.
/// </para>
/// </remarks>
public sealed class CoachFeatureSwitch
{
    /// <summary>Whether the feature is on. Default false.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// The grounding ladder, bound from <c>Coach:Grounding</c>.
/// </summary>
/// <remarks>
/// <para>
/// One ordered stage rather than a switch, per plan B9. The four rungs answer the same question at
/// four depths — does the honesty layer look, does it record, does it fix, does it block — and an
/// operator promotes one rung at a time with the previous rung's metrics in hand.
/// </para>
/// <para>
/// <b>A malformed value is a startup failure, not a default.</b> <c>Coach:Grounding:Stage=Repare</c>
/// binds to <see cref="CoachGroundingStage.Off"/> silently, and a deployment that believes it is
/// repairing while it is doing nothing is worse than one that never turned grounding on: the
/// metrics look calm because nothing is being measured. <c>CoachConfigurationKeyValidator</c> reads
/// the raw string and stops the host.
/// </para>
/// </remarks>
public sealed class CoachGroundingOptions
{
    /// <summary>
    /// How far the honesty layer may act. Default <see cref="CoachGroundingStage.Off"/>.
    /// </summary>
    public CoachGroundingStage Stage { get; set; } = CoachGroundingStage.Off;
}
