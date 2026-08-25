using SentenceStudio.Api.Coach.Operations.Handlers;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Coach.Opportunities;

/// <summary>
/// The closed vocabulary of "what the learner was reaching for".
/// </summary>
/// <remarks>
/// <para>
/// Constants rather than free text, for the same reason <see cref="Operations.CoachWriteFailureCodes"/>
/// is: a capability code can never accidentally carry a phrase built from learner input, a model
/// completion, or an exception. A gap that does not fit one of these gets a new constant here,
/// reviewed like any other schema change.
/// </para>
/// <para>
/// The values are stored in a live table, so they are <b>append-only forever</b>. Renaming a
/// constant's value re-labels every row already written and breaks every fingerprint that
/// included it. <c>CoachOpportunityStoredEnumContractTests</c> pins the whole set.
/// </para>
/// <para>
/// Every value is snake_case, at most <see cref="CoachOpportunityLimits.CapabilityCodeMaxLength"/>
/// characters, and content-free by construction: nothing here is derived from a learner's text.
/// The one family that is computed — <c>preference_setting_{name}</c> — is built only from
/// <see cref="CoachPreferenceChangeHandler.CandidateNames"/>, a server-owned closed set, and
/// anything outside it collapses to <see cref="PreferenceSettingUnknown"/>.
/// </para>
/// </remarks>
public static class CoachOpportunityCapabilityCodes
{
    // ---------------------------------------------------------------- preferences

    /// <summary>Prefix for the preference-setting family. Never used on its own.</summary>
    public const string PreferenceSettingPrefix = "preference_setting_";

    /// <summary>A preference change was asked for by a name outside the server's candidate set.</summary>
    public const string PreferenceSettingUnknown = "preference_setting_unknown";

    // ---------------------------------------------------------------- entities

    /// <summary>The learner named an entity by title and the server could not resolve or own it.</summary>
    public const string EntityLookupByName = "entity_lookup_by_name";

    // ---------------------------------------------------------------- feature gates

    /// <summary>The write tools are switched off for this deployment or learner.</summary>
    public const string WriteToolsDisabled = "write_tools_disabled";

    /// <summary>The read tools are switched off for this deployment or learner.</summary>
    public const string ReadToolsDisabled = "read_tools_disabled";

    /// <summary>The Sam overlay itself is switched off for this deployment or learner.</summary>
    public const string OverlayDisabled = "overlay_disabled";

    // ---------------------------------------------------------------- policy

    /// <summary>A tool reached the model that the deployment allow-list does not permit.</summary>
    public const string ToolAllowListViolation = "tool_allowlist_violation";

    // ---------------------------------------------------------------- conversation

    /// <summary>
    /// A decisive short answer arrived after a coach offer with nothing structured to bind it to.
    /// Entry 1 of <c>docs/sam-future-opportunities.md</c>.
    /// </summary>
    public const string ReferentLostAfterOffer = "referent_lost_after_offer";

    // ---------------------------------------------------------------- validation

    /// <summary>The model's typed intent failed its own shape rules.</summary>
    public const string IntentShapeInvalid = "intent_shape_invalid";

    /// <summary>The model's answer passed intent validation but failed post-projection shape rules.</summary>
    public const string AnswerShapeInvalid = "answer_shape_invalid";

    /// <summary>The model's answer did not deserialize into a turn intent at all.</summary>
    public const string ModelOutputUnreadable = "model_output_unreadable";

    /// <summary>A write proposal's arguments failed validation for the tool's declared shape.</summary>
    public const string WriteArgumentsInvalid = "write_arguments_invalid";

    /// <summary>
    /// The turn was refused because the answer would have leaked material that is due for review.
    /// The <b>rate</b> is the signal here, not any individual row.
    /// </summary>
    public const string AnswerLeakRefusal = "answer_leak_refusal";

    // ---------------------------------------------------------------- tool execution

    /// <summary>The planner could produce no feasible plan for the constraints given.</summary>
    public const string NoFeasiblePlan = "no_feasible_plan";

    /// <summary>A tool's data read failed.</summary>
    /// <remarks>
    /// Recorded by the <b>tool boundary</b> only, where the failing tool is known by its
    /// registration and the failure kind is the tool's own. The turn boundary never uses this
    /// code — see <see cref="TurnToolFailureFallback"/>.
    /// </remarks>
    public const string ToolDataAccess = "tool_data_access";

    /// <summary>
    /// A turn stopped with <c>ToolFailure</c> and the turn boundary knows nothing more than that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A separate code from <see cref="ToolDataAccess"/> because the turn boundary genuinely does
    /// not know which tool failed or why: <c>CoachStopReason.ToolFailure</c> is a single
    /// turn-level verdict, and <see cref="ObservedCoachFunction"/> — which does know both — has
    /// already recorded the detailed row. Labelling the turn-level fallback as a data-access
    /// failure asserted a cause nobody established, and it made the rollup read as though every
    /// tool failure were a read failure.
    /// </para>
    /// <para>
    /// Kept as its own aggregate code rather than dropped, so the two counts stay comparable: a
    /// turn-level count materially higher than the tool-boundary count means failures are
    /// reaching the turn from somewhere the tool observer does not wrap, which is a real gap
    /// worth seeing. It is aggregate-only, so it is a number, never a dossier.
    /// </para>
    /// </remarks>
    public const string TurnToolFailureFallback = "turn_tool_failure_unattributed";

    /// <summary>A tool needed a learner profile that does not exist.</summary>
    public const string ToolProfileMissing = "tool_profile_missing";

    /// <summary>A write executed and failed, or its receipt could not be recorded.</summary>
    public const string WriteExecutionFailed = "write_execution_failed";

    // ---------------------------------------------------------------- lifecycle

    /// <summary>The approval or confirmation window elapsed before the learner answered.</summary>
    public const string ApprovalWindowElapsed = "approval_window_elapsed";

    /// <summary>Undo was asked for and was not available.</summary>
    public const string UndoUnavailable = "undo_unavailable";

    /// <summary>An approval arrived against a state that does not accept it.</summary>
    public const string ApprovalProtocolError = "approval_protocol_error";

    /// <summary>
    /// An approval named an operation, conversation, or identity that does not resolve.
    /// <b>Always recorded with no conversation id, no turn id, and no pointers</b> — this is the
    /// shape a cross-tenant probe takes, and an inspectable row would be an existence oracle.
    /// </summary>
    public const string ApprovalTargetUnresolved = "approval_target_unresolved";

    // ---------------------------------------------------------------- capacity

    /// <summary>The turn asked for a second write proposal. The surface carries exactly one.</summary>
    public const string OneProposalPerTurn = "one_proposal_per_turn";

    /// <summary>The turn spent its tool-call budget.</summary>
    public const string ToolCallBudgetExhausted = "tool_call_budget_exhausted";

    /// <summary>The learner reached the daily or weekly run limit.</summary>
    public const string DailyRunLimit = "daily_run_limit";

    /// <summary>The turn hit the wall-clock limit.</summary>
    public const string TurnTimeout = "turn_timeout";

    /// <summary>The turn hit the output-token cap.</summary>
    public const string OutputTokenLimit = "output_token_limit";

    /// <summary>The turn hit the model and tool iteration limit.</summary>
    public const string IterationLimit = "iteration_limit";

    // ---------------------------------------------------------------- scope / safety

    /// <summary>The learner asked about something the coach does not cover.</summary>
    public const string OffTopic = "off_topic";

    /// <summary>The learner asked for a destructive action the coach must refuse.</summary>
    public const string DestructiveRequestRefused = "destructive_request_refused";

    // ---------------------------------------------------------------- learner reports

    /// <summary>
    /// The umbrella name for the learner-report family. Never stored on a row on its own.
    /// </summary>
    /// <remarks>
    /// Kept as a named constant because it is the name the family is discussed by, and because a
    /// reviewer reading <see cref="All"/> should be able to see that the five codes below are one
    /// thing rather than five unrelated additions.
    /// </remarks>
    public const string LearnerReportedResponse = "learner_reported_unsatisfactory_response";

    /// <summary>The learner reported a response that did not answer what they asked.</summary>
    public const string LearnerReportedDidNotAnswer = "learner_reported_did_not_answer";

    /// <summary>The learner reported a response as wrong or misleading.</summary>
    public const string LearnerReportedIncorrect = "learner_reported_incorrect_or_misleading";

    /// <summary>The learner expected the app to act and the response only talked about acting.</summary>
    public const string LearnerReportedExpectedAppAction = "learner_reported_expected_app_action";

    /// <summary>The learner reported a response as hard to follow.</summary>
    public const string LearnerReportedConfusing = "learner_reported_confusing";

    /// <summary>The learner reported a response for a reason outside the other four.</summary>
    public const string LearnerReportedOther = "learner_reported_other";

    private static readonly string[] FixedCodes =
    [
        PreferenceSettingUnknown,
        EntityLookupByName,
        WriteToolsDisabled,
        ReadToolsDisabled,
        OverlayDisabled,
        ToolAllowListViolation,
        ReferentLostAfterOffer,
        IntentShapeInvalid,
        AnswerShapeInvalid,
        ModelOutputUnreadable,
        WriteArgumentsInvalid,
        AnswerLeakRefusal,
        NoFeasiblePlan,
        ToolDataAccess,
        TurnToolFailureFallback,
        ToolProfileMissing,
        WriteExecutionFailed,
        ApprovalWindowElapsed,
        UndoUnavailable,
        ApprovalProtocolError,
        ApprovalTargetUnresolved,
        OneProposalPerTurn,
        ToolCallBudgetExhausted,
        DailyRunLimit,
        TurnTimeout,
        OutputTokenLimit,
        IterationLimit,
        OffTopic,
        DestructiveRequestRefused,
        LearnerReportedDidNotAnswer,
        LearnerReportedIncorrect,
        LearnerReportedExpectedAppAction,
        LearnerReportedConfusing,
        LearnerReportedOther
    ];

    /// <summary>
    /// The complete closed set, including the generated <c>preference_setting_*</c> family.
    /// </summary>
    /// <remarks>
    /// The preference family is derived from
    /// <see cref="CoachPreferenceChangeHandler.CandidateNames"/> rather than typed out again, so
    /// approving a new candidate setting cannot leave the ledger unable to name it.
    /// </remarks>
    public static IReadOnlyList<string> All { get; } = BuildAll();

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    private static string[] BuildAll()
    {
        var codes = new List<string>(FixedCodes);

        foreach (var candidate in CoachPreferenceChangeHandler.CandidateNames)
        {
            var code = ForPreferenceSetting(candidate);
            if (!codes.Contains(code, StringComparer.Ordinal))
            {
                codes.Add(code);
            }
        }

        codes.Sort(StringComparer.Ordinal);
        return [.. codes];
    }

    /// <summary>True when <paramref name="code"/> is a member of the closed set.</summary>
    /// <remarks>
    /// The recorder's gate. A signal carrying anything else is dropped with a content-free
    /// warning rather than written, because an unbounded code column is a free-text column
    /// wearing a different name.
    /// </remarks>
    public static bool IsKnown(string? code) =>
        !string.IsNullOrEmpty(code) && Known.Contains(code);

    /// <summary>
    /// The capability code for a preference-setting request, or
    /// <see cref="PreferenceSettingUnknown"/> when the name is not a server-owned candidate.
    /// </summary>
    /// <remarks>
    /// <b>This is the only computed code, and it is computed from a closed server-owned set.</b>
    /// A setting name the model invented never reaches the column: it collapses to the unknown
    /// bucket, so the cardinality of this family is bounded by
    /// <see cref="CoachPreferenceChangeHandler.CandidateNames"/> plus one.
    /// </remarks>
    public static string ForPreferenceSetting(string? settingName)
    {
        if (string.IsNullOrWhiteSpace(settingName))
        {
            return PreferenceSettingUnknown;
        }

        var normalized = settingName.Trim().ToLowerInvariant();

        foreach (var candidate in CoachPreferenceChangeHandler.CandidateNames)
        {
            if (string.Equals(candidate, normalized, StringComparison.Ordinal))
            {
                return PreferenceSettingPrefix + candidate;
            }
        }

        return PreferenceSettingUnknown;
    }

    /// <summary>
    /// The capability code for one learner-report reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason is carried in the <b>capability</b> column rather than in the failure column,
    /// and that is the deliberate part. This column answers "what was the learner reaching for",
    /// and for a report the reason is exactly that answer — "I expected an app action" is a
    /// learner naming a capability they thought existed. <c>FailureCode</c> answers "why did the
    /// server say no", and on a reported turn the server usually said nothing of the kind: it
    /// considered the turn a success. Putting a report reason there would pollute a vocabulary
    /// the write ledger and the turn telemetry are joined on.
    /// </para>
    /// <para>
    /// Because the capability code is a fingerprint input, one code per reason is also what makes
    /// the daily rollup answer the useful question — "how many learners reported responses as
    /// incorrect or misleading, and how often" — instead of flattening every report into one
    /// undifferentiated total.
    /// </para>
    /// <para>
    /// An unrecognized reason collapses to <see cref="LearnerReportedOther"/> rather than
    /// throwing: the ledger is an observer, and an observer must not be the thing that fails a
    /// learner's action.
    /// </para>
    /// </remarks>
    public static string ForReportReason(CoachResponseReportReason reason) => reason switch
    {
        CoachResponseReportReason.DidNotAnswer => LearnerReportedDidNotAnswer,
        CoachResponseReportReason.IncorrectOrMisleading => LearnerReportedIncorrect,
        CoachResponseReportReason.ExpectedAppAction => LearnerReportedExpectedAppAction,
        CoachResponseReportReason.Confusing => LearnerReportedConfusing,
        _ => LearnerReportedOther
    };
}
