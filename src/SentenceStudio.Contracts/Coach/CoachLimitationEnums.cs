using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.Coach;

// ─────────────────────────────────────────────────────────────────────────────
// The action space: what Sam can honestly point a learner at when it cannot do
// the thing itself.
//
// No destination code existed before this. The temptation was to carry a route
// string, because every screen already has one and a string costs nothing to
// add. A string is what makes a limitation answer unfalsifiable: nothing stops
// "/vocabulary?delete=all", nothing stops a route the app does not have, and
// nothing stops the model's own words arriving in a field the client will
// render as a link. A closed enum plus typed parameters cannot express any of
// those, which is the entire reason it is an enum.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A screen the learner owns and Sam may name.
/// </summary>
/// <remarks>
/// <para>
/// Closed, and deliberately short. A member here is a promise that the screen exists, that the
/// learner can reach it, and that it does what the catalogue says — so adding one is a decision
/// with a test attached, not a convenience.
/// </para>
/// <para>
/// The route <em>path</em> is not on the wire. The client maps a member to its own navigation, so
/// the server never ships a URL, never ships a query string, and cannot ship one by accident.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachRouteName.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown is the documented unset value and the client renders no destination at all. A client "
    + "that guessed a route from an unrecognised member would navigate the learner somewhere the "
    + "server never named, which is worse than offering no link: the learner acted on Sam's "
    + "suggestion and arrived somewhere Sam did not choose.")]
public enum CoachRouteName
{
    /// <summary>No destination, or one this build cannot name. Render no link.</summary>
    Unknown = 0,

    /// <summary>The learner's practice history.</summary>
    ActivityLog = 1,

    /// <summary>The learner's vocabulary, where words can be reviewed, edited and removed.</summary>
    Vocabulary = 2,

    /// <summary>App and learning settings.</summary>
    Settings = 3,

    /// <summary>The learner's skill profiles.</summary>
    Skills = 4,

    /// <summary>The writing activity.</summary>
    Writing = 5,

    /// <summary>The feedback form, which files a public issue.</summary>
    Feedback = 6
}

/// <summary>
/// What happens to the learner's data or account if they act on a destination.
/// </summary>
/// <remarks>
/// <para>
/// Declared per route so a limitation answer can disclose the consequence <em>before</em> the
/// learner goes there. Sam pointing somebody at a screen where they can delete their whole
/// vocabulary is fine; Sam pointing them there without saying so is not, and the difference is a
/// field rather than a habit.
/// </para>
/// <para>
/// This describes the destination's <em>capability</em>, not what the learner will do. A screen is
/// labelled by the most consequential thing it permits, because a disclosure that describes the
/// gentlest available action is not a disclosure.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachRouteSideEffect.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown is the documented unset value and the client renders a neutral 'consequences not "
    + "stated' note rather than silence. Defaulting to None would be the dangerous direction: an "
    + "unrecognised member would read as a harmless read-only screen, and the one case this field "
    + "exists for is the screen that is not.")]
public enum CoachRouteSideEffect
{
    /// <summary>Not stated, or a member this build cannot name. Render a neutral note, never "safe".</summary>
    Unknown = 0,

    /// <summary>Reading only. Nothing the learner does there changes stored data.</summary>
    None = 1,

    /// <summary>The learner can change or delete their own learning data there.</summary>
    EditsLearnerData = 2,

    /// <summary>The learner can change settings that affect future sessions.</summary>
    ChangesSettings = 3,

    /// <summary>The learner can start a practice activity there.</summary>
    StartsActivity = 4,

    /// <summary>
    /// What the learner submits there becomes publicly visible outside the app, and cannot be
    /// withdrawn by the app.
    /// </summary>
    PublishesPublicly = 5
}

/// <summary>
/// A typed parameter a destination accepts.
/// </summary>
/// <remarks>
/// Closed for the same reason the route is. A free-form parameter bag is a route string with extra
/// steps: it would let a model-authored value reach a link, and it would let a caller attach
/// something the destination never reads. Every member here names a server-owned identifier or a
/// closed code, never learner text and never a search phrase.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachRouteParameterKey.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown is the documented unset value and the client drops the parameter rather than passing "
    + "a key it cannot interpret. Dropping a parameter degrades a deep link to a screen link, "
    + "which is a smaller harm than navigating with a key whose meaning this build does not know.")]
public enum CoachRouteParameterKey
{
    /// <summary>Unrecognised. The client drops this parameter and navigates without it.</summary>
    Unknown = 0,

    /// <summary>A vocabulary word the learner owns, by server identifier.</summary>
    VocabularyWordId = 1,

    /// <summary>A skill profile the learner owns, by server identifier.</summary>
    SkillId = 2,

    /// <summary>A learning resource the learner owns, by server identifier.</summary>
    ResourceId = 3,

    /// <summary>A learner-local calendar date, as an ISO date with no time component.</summary>
    PlanDate = 4
}

/// <summary>
/// Why Sam could not do what was asked.
/// </summary>
/// <remarks>
/// <para>
/// Closed, so a limitation is a code the client can render, a metric can count, and a test can
/// assert on — rather than a sentence whose meaning has to be re-read every release.
/// </para>
/// <para>
/// The distinction between <see cref="NotBuilt"/> and <see cref="RefusedByDesign"/> is the one
/// that matters most and the one most easily blurred. "We have not built it" invites the learner
/// to wait for it; "we will not do it" invites them to do it themselves. Answering the second with
/// the first is a false promise, and answering the first with the second is a false refusal.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachLimitationCode.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown is the documented unset value and the client renders the limitation's own text with "
    + "no code-specific framing. Guessing a code would put a reason in the learner's mouth that "
    + "the server never gave, and the two codes most likely to be guessed between — not built, and "
    + "refused by design — imply opposite next actions.")]
public enum CoachLimitationCode
{
    /// <summary>Not stated, or a code this build cannot name. Render the text without framing.</summary>
    Unknown = 0,

    /// <summary>The capability does not exist anywhere in the app yet.</summary>
    NotBuilt = 1,

    /// <summary>
    /// The capability exists, but on a screen rather than through Sam. The destination names it.
    /// </summary>
    AvailableOnAnotherSurface = 2,

    /// <summary>
    /// Sam will not do this, by design, and the reason is not a missing feature. A destination is
    /// offered when the learner can still do it themselves.
    /// </summary>
    RefusedByDesign = 3,

    /// <summary>
    /// Doing it would remove a retrieval opportunity the learner is practising for. An alternative
    /// that preserves the retrieval is offered instead.
    /// </summary>
    WouldRemoveLearningValue = 4,

    /// <summary>
    /// The request covers more data than Sam will change in one step. Bounded, reversible
    /// alternatives are offered.
    /// </summary>
    ExceedsSafeChangeScope = 5,

    /// <summary>
    /// Sam had an answer and could not stand behind it, so it withheld the answer rather than the
    /// capability.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Appended, because none of the five above says this.</b> <c>NotBuilt</c> and
    /// <c>AvailableOnAnotherSurface</c> are statements about the app's capabilities and this is not
    /// one — the app can do the thing, and did. <c>RefusedByDesign</c> would tell the learner Sam
    /// declines this kind of request, which is false and would stop them asking again.
    /// <c>WouldRemoveLearningValue</c> claims a pedagogical reason Sam does not have here.
    /// <c>ExceedsSafeChangeScope</c> is about the size of a write. Reusing any of them would have
    /// been a confident false statement in the shape whose whole job is to be the honest one.
    /// </para>
    /// <para>
    /// The honest content is narrow: the answer made a claim the turn's own reads do not support,
    /// and the grounding layer could not repair it. The evidence panel beside this code is what
    /// makes the refusal useful — it says what <em>was</em> looked at — so this member carries the
    /// reason and the evidence carries the facts.
    /// </para>
    /// <para>
    /// Appended rather than inserted, so every existing ordinal is unchanged and a client built
    /// before W9 decodes it through the wire-tolerance fallback as <see cref="Unknown"/> and
    /// renders the text without framing. That is the correct degradation: an old client shows a
    /// refusal it cannot categorise rather than mislabelling it as one of the five it knows.
    /// </para>
    /// </remarks>
    UnverifiedClaimWithheld = 6,

    /// <summary>
    /// The model produced an answer that could not be rendered against the shipped schema. The turn
    /// wrote nothing; the learner may retry.
    /// </summary>
    /// <remarks>
    /// Appended after <see cref="UnverifiedClaimWithheld"/> so every existing ordinal is unchanged.
    /// A client built before this member decodes it through the wire-tolerance fallback as
    /// <see cref="Unknown"/> and renders the limitation's own text without framing. That is the
    /// correct degradation: an old client shows a refusal it cannot categorise rather than
    /// mislabelling it as one of the six it knows.
    /// </remarks>
    AnswerShapeInvalid = 7
}
