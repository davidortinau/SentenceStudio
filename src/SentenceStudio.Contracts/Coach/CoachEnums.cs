using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// Tells the client if the learner can open the coach.
/// The zero value is Disabled. An unset value never opens the coach.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachAvailabilityState.Disabled), WireEnumFallbackKind.SafeZero,
    "An availability state this build cannot name must not open an entry point. Disabled is already "
    + "the documented unset value, so an unreadable state and a missing one behave identically.")]
public enum CoachAvailabilityState
{
    /// <summary>The feature flag is off. Do not show an entry point.</summary>
    Disabled = 0,

    /// <summary>The learner is not in the pilot group. Do not show an entry point.</summary>
    OutsideCohort,

    /// <summary>The learner is at the run limit. Show the limit notice only.</summary>
    LimitReached,

    /// <summary>The learner can start a new session.</summary>
    Available,

    /// <summary>The learner has an active session. Show a resume action.</summary>
    ResumeAvailable
}

/// <summary>
/// The state of one coach session.
/// The zero value is Expired. An unset value never accepts a new turn.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachSessionStatus.Expired), WireEnumFallbackKind.SafeZero,
    "An unreadable session state must not accept a turn. Expired is the documented unset value and "
    + "the client already refuses to submit against it.")]
public enum CoachSessionStatus
{
    /// <summary>The session is past its expiry time. The server rejects new turns.</summary>
    Expired = 0,

    /// <summary>The session accepts a new turn.</summary>
    Active,

    /// <summary>The coach asked a question. The session waits for an answer.</summary>
    AwaitingClarification,

    /// <summary>A suggestion waits for a clear acceptance or a rejection.</summary>
    SuggestionPending,

    /// <summary>A run, cost, or time limit stopped the session.</summary>
    Limited,

    /// <summary>The session stopped with an error.</summary>
    Failed,

    /// <summary>The learner closed the session.</summary>
    Closed
}

/// <summary>
/// The result of one coach turn.
/// The zero value is Failed. An unset value never shows a success state.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachTurnStatus.Failed), WireEnumFallbackKind.SafeZero,
    "An unreadable turn status must never render as success. Failed is the documented unset value.")]
public enum CoachTurnStatus
{
    /// <summary>The turn stopped with an error. Nothing changed.</summary>
    Failed = 0,

    /// <summary>The turn finished. The response holds the full result.</summary>
    Completed,

    /// <summary>A limit stopped the turn before the end. Nothing changed.</summary>
    Incomplete,

    /// <summary>The server refused the input. Nothing changed.</summary>
    Rejected
}

/// <summary>
/// The reason the coach stopped work on a turn.
/// The zero value is Failed. An unset value never shows a success state.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachStopReason.Failed), WireEnumFallbackKind.SafeZero,
    "An unreadable stop reason must not claim the turn finished as planned. Failed is the documented "
    + "unset value. Note this collapse is client-side only: the stored ordinal is untouched, so "
    + "history still reads back as whatever the server actually recorded.")]
public enum CoachStopReason
{
    /// <summary>An unexpected error stopped the turn.</summary>
    Failed = 0,

    /// <summary>The turn finished as planned.</summary>
    Completed,

    /// <summary>The coach asked one question and stopped.</summary>
    ClarificationRequested,

    /// <summary>The input was too long, empty, or not allowed.</summary>
    InputRejected,

    /// <summary>A plan or constraint check failed. The server did not write.</summary>
    ValidationFailed,

    /// <summary>A read-only tool failed.</summary>
    ToolFailure,

    /// <summary>The turn hit the model and tool iteration limit.</summary>
    IterationLimit,

    /// <summary>The turn hit the output token limit.</summary>
    OutputTokenLimit,

    /// <summary>The turn hit the time limit.</summary>
    Timeout,

    /// <summary>The learner hit the daily or weekly run limit.</summary>
    RateLimit,

    /// <summary>Another run for the same learner is in progress.</summary>
    ConcurrencyLimit,

    /// <summary>The learner or the client stopped the turn.</summary>
    Cancelled,

    /// <summary>The session expired before the turn started.</summary>
    SessionExpired
}

/// <summary>
/// What the learner sent in one turn.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachTurnInputKind.Text), WireEnumFallbackKind.DeliberateNeutral,
    "Request-side only: the client authors this and the server never sends it back, so a fallback is "
    + "reached only by a client parsing its own echo. Text is the least-privileged member \u2014 it "
    + "carries no chip identity and no constraint action, so it can trigger neither a control nor a write.")]
public enum CoachTurnInputKind
{
    /// <summary>Free text from the composer.</summary>
    Text = 0,

    /// <summary>A tap on a suggested chip.</summary>
    Chip,

    /// <summary>A structured constraint change from a control.</summary>
    ConstraintAction
}

/// <summary>
/// Who wrote a coach message.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachMessageRole.Coach), WireEnumFallbackKind.DeliberateNeutral,
    "A message whose author this build cannot name came from the server, so attributing it to the "
    + "learner would put words in their mouth and place their own transcript on the wrong side of the "
    + "thread. Coach is the truthful attribution for anything the server sent, and it is the side that "
    + "gets no composer affordances.")]
public enum CoachMessageRole
{
    /// <summary>The coach wrote the message.</summary>
    Coach = 0,

    /// <summary>The learner wrote the message.</summary>
    Learner
}

/// <summary>
/// The display role of a coach message.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachMessageKind.Unrecognized), WireEnumFallbackKind.AppendedSentinel,
    "The one enum where the client must be able to TELL a value is unknown: an unreadable message kind "
    + "renders an unavailable placeholder with no controls, and every other member would have it render "
    + "as real content. Text \u2014 the zero value \u2014 would print whatever text arrived as though "
    + "this build understood it. The sentinel is APPENDED: CoachMessage.Kind is stored as an ordinal, so "
    + "an Unknown = 0 would silently relabel every stored Text row.")]
public enum CoachMessageKind
{
    /// <summary>Normal text.</summary>
    Text = 0,

    /// <summary>A question that waits for an answer.</summary>
    Clarification,

    /// <summary>A suggestion with accept and reject actions.</summary>
    Suggestion,

    /// <summary>A receipt for an applied plan change.</summary>
    Receipt,

    /// <summary>A status notice, for example a limit or an error.</summary>
    Notice,

    /// <summary>An answer to a language-learning question. Appended, never inserted.</summary>
    PedagogicalAnswer,

    /// <summary>
    /// A message whose kind this build does not recognise. Render an unavailable placeholder with
    /// no controls; never print the text as though it were understood.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The server never sends this.</b> It is produced only by the client's tolerant wire
    /// converter, when a newer server names a kind this build has no case for. It exists because
    /// the alternative — collapsing onto <see cref="Text"/> — would render a suggestion, a consent
    /// prompt, or an action card as ordinary prose, stripped of the controls that made it safe.
    /// A learner reading "I'll delete those five words" as a plain sentence has no way to tell it
    /// was a proposal awaiting their answer.
    /// </para>
    /// <para>
    /// <b>Appended, never inserted.</b> <c>CoachMessage.Kind</c> is stored with
    /// <c>HasConversion&lt;int&gt;()</c>, so the ordinals are a persistence contract: an
    /// <c>Unknown = 0</c> would have silently relabelled every stored <see cref="Text"/> row.
    /// </para>
    /// </remarks>
    Unrecognized
}

/// <summary>
/// One study constraint field. Use these names in receipts and telemetry.
/// The set is closed. The coach cannot add a new constraint field.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachConstraintField.AvailableMinutes), WireEnumFallbackKind.DeliberateNeutral,
    "No member is neutral, and appending a sentinel here would reach into the server\u2019s telemetry tag "
    + "map and deterministic receipt copy \u2014 a blast radius this foundation change does not need. Safe "
    + "to collapse because the client never renders this field name on its own: receipts carry "
    + "server-localized summary lines, and this enum drives telemetry and ordering. The primary defence "
    + "for a new constraint field is the client-version gate; this is only the fail-safe that keeps the "
    + "conversation up.")]
public enum CoachConstraintField
{
    /// <summary>The minutes the learner has for this session.</summary>
    AvailableMinutes = 0,

    /// <summary>Audio playback is allowed.</summary>
    AudioAllowed,

    /// <summary>Speech input is allowed.</summary>
    SpeechAllowed,

    /// <summary>Typed input is allowed.</summary>
    TypingAllowed,

    /// <summary>The skill to emphasize.</summary>
    SkillEmphasis,

    /// <summary>The goal tag.</summary>
    GoalTag,

    /// <summary>The goal horizon in days.</summary>
    GoalHorizonDays,

    /// <summary>The energy level.</summary>
    EnergyLevel,

    /// <summary>
    /// The vocabulary focus set. Appended: this enum is persisted by ordinal inside the
    /// normalized delta JSON, so a new member goes on the end and never in the middle.
    /// </summary>
    VocabularyFocus
}

/// <summary>
/// The skill to emphasize. Emphasis changes weight only.
/// Emphasis cannot remove all due review work.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachSkillEmphasis.Listening), WireEnumFallbackKind.DeliberateNeutral,
    "No member is neutral. Safe to collapse because emphasis is advisory display state on the client: it "
    + "changes no plan, triggers no write, and every plan item the learner sees is titled from the "
    + "server\u2019s own localized copy. The version gate is what stops a new emphasis reaching a build "
    + "that cannot name it.")]
public enum CoachSkillEmphasis
{
    Listening = 0,
    Speaking,
    Reading,
    Writing,
    Vocabulary
}

/// <summary>
/// The energy level for this session.
/// A low level can shorten the session or change the mode.
/// A low level cannot lower the difficulty floor.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachEnergyLevel.Normal), WireEnumFallbackKind.DeliberateNeutral,
    "Normal is the no-op reading: Low can shorten a session or change its mode, so an unreadable level "
    + "must land on the member that changes nothing rather than on the one that does.")]
public enum CoachEnergyLevel
{
    Normal = 0,
    Low
}

/// <summary>
/// How one plan item changed in a difference view.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachPlanItemChangeKind.Unchanged), WireEnumFallbackKind.SafeZero,
    "Unchanged renders no change marker at all, which is the honest answer for a change this build "
    + "cannot name. Every other member puts a specific claim \u2014 New, Removed, Adjusted \u2014 next to "
    + "a plan row.")]
public enum CoachPlanItemChangeKind
{
    /// <summary>The item is the same in both plans.</summary>
    Unchanged = 0,

    /// <summary>The item is new in the second plan.</summary>
    Added,

    /// <summary>The item is not in the second plan.</summary>
    Removed,

    /// <summary>The item stays, but its minutes or its order changed.</summary>
    Adjusted,

    /// <summary>The learner completed the item. The server kept it without a change.</summary>
    PreservedCompleted,

    /// <summary>The learner started the item. The server kept it and kept its minutes.</summary>
    PreservedInProgress
}

/// <summary>
/// What caused a plan revision.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachRevisionSource.DirectRequest), WireEnumFallbackKind.DeliberateNeutral,
    "No member is neutral and the ordinal is persisted, so a sentinel would have to be appended through "
    + "the revision store. Safe to collapse because the client uses this only to caption a history row "
    + "whose text the server already localized; it gates no control and reverses nothing. DirectRequest "
    + "is the least presumptuous of the three: it claims the learner asked, which is true of every "
    + "revision the learner can see, whereas AcceptedSuggestion and Undo each assert a specific prior act.")]
public enum CoachRevisionSource
{
    /// <summary>The learner asked for the change directly.</summary>
    DirectRequest = 0,

    /// <summary>The learner accepted a coach suggestion.</summary>
    AcceptedSuggestion,

    /// <summary>The learner undid the last revision.</summary>
    Undo
}

/// <summary>
/// The kind of read-only evidence behind a coach statement.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachEvidenceKind.Unrecognized), WireEnumFallbackKind.AppendedSentinel,
    "The old rationale said the learner reads server-localized Label and Summary and never this enum, "
    + "so collapsing onto PracticeBalance was harmless. That was never true \u2014 Label was server "
    + "English, not localized \u2014 and it is now false by design: the client localizes the heading FROM "
    + "this member, so an unreadable kind collapsing onto PracticeBalance would print \u2018Practice "
    + "balance\u2019 over a card about something else. Unrecognized is APPENDED, never inserted: the "
    + "members are a grouping key a client may already hold, and renumbering would relabel stored cards.")]
public enum CoachEvidenceKind
{
    /// <summary>Input and output minutes over a stated window.</summary>
    PracticeBalance = 0,

    /// <summary>Counts, bands, and lapse rates for due work. No terms.</summary>
    VocabularyDue,

    /// <summary>Owned resource counts and capabilities. No full text.</summary>
    ResourceCatalog,

    /// <summary>Learner settings and goals. No identity data.</summary>
    LearnerProfile,

    /// <summary>The deterministic plan preview.</summary>
    PlanPreview,

    /// <summary>
    /// A kind this build cannot name. Produced only by the tolerant converter when a newer server
    /// sends a member this client has never heard of.
    /// </summary>
    /// <remarks>
    /// The client has no heading for it, so it falls back to the server's <c>Label</c> prose and,
    /// failing that, prints no heading at all. It never borrows another kind's heading: a wrong
    /// heading over real numbers is worse than a missing one, because the reader cannot see that
    /// anything is missing.
    /// </remarks>
    Unrecognized
}

/// <summary>
/// The unit for one evidence value.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachEvidenceUnit.Count), WireEnumFallbackKind.NeutralMember,
    "Count is the unitless member: it asserts a quantity without asserting what of. Minutes \u2014 the "
    + "zero value \u2014 would print \u201c5 minutes\u201d next to a number that might be attempts or "
    + "days. The old rationale added that the label beside it is \u2018server-localized\u2019, which was "
    + "never true \u2014 the server writes it in English \u2014 and the label is now localized by the "
    + "client from CoachEvidenceValueCode instead. That corrects the reason, not the choice: the label "
    + "still names what is being counted, so the unit is never the only thing carrying meaning.")]
public enum CoachEvidenceUnit
{
    Minutes = 0,
    Attempts,
    Items,
    Days,
    Percent,
    Count
}

/// <summary>
/// The activity type of a coach plan item.
/// The names stay the same as the server plan activity names.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachPlanActivityType.VocabularyReview), WireEnumFallbackKind.DeliberateNeutral,
    "No member is neutral, and a sentinel cannot be appended without breaking the parity contract that "
    + "these names match Today\u2019s Plan activity names exactly. Safe to collapse because a coach plan "
    + "item renders from the server\u2019s own localized title and minutes; this enum picks an icon and a "
    + "route, and an unknown activity therefore looks like a review item rather than a broken row. A new "
    + "activity type is exactly what the client-version gate exists to hold back.")]
public enum CoachPlanActivityType
{
    VocabularyReview = 0,
    Reading,
    Listening,
    VideoWatching,
    Shadowing,
    Cloze,
    Translation,
    Writing,
    SceneDescription,
    Conversation,
    VocabularyGame,
    NumberDrill
}
