using System.Text.Json.Serialization;
using SentenceStudio.Contracts.Wire;

namespace SentenceStudio.Contracts.Coach;

/// <summary>
/// How much support a hint rung gives.
/// </summary>
/// <remarks>
/// <para>
/// The rungs are ordered by support, and every one of them still requires the learner to retrieve.
/// That is the property the ladder exists for: a learner who asks for the answers is asking to
/// convert a retrieval event into a reading event, and the honest counter-offer is not "no" — it
/// is the smallest amount of help that keeps the retrieval intact.
/// </para>
/// <para>
/// <b>No rung carries text on the wire.</b> There is no hint string on the DTO, so this shape
/// cannot leak the term, a gloss, or an expected answer even if a later change generates the hints
/// badly. W7 declares the ladder; producing a rung's content is a later card's job, under the same
/// embargo everything else is under.
/// </para>
/// <para>
/// <b>These are kinds, not positions.</b> The ordinals below number the members; the shipped rung
/// order lives in <c>CoachLimitations.HintLadder</c> and is
/// <see cref="Category"/> → <see cref="Cloze"/> → <see cref="FormCue"/>. Reading the ordinals as
/// the ladder is the mistake that produced the first draft's transposition, so nothing here
/// restates the sequence — the server owns it in one place and the acceptance cases are bound to
/// that one place by test.
/// </para>
/// <para>
/// <see cref="FormCue"/> is the last rung deliberately, because it is the most form-revealing help
/// this app gives: in Korean an initial block plus a syllable count often leaves a candidate set a
/// learner can close by elimination. It is still a retrieval attempt — the learner produces the
/// form — and it is the most support available without becoming the answer. A fourth rung would
/// have to be the term itself, which is the thing being refused.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachHintKind.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown is the documented unset value and the client renders the rung as an unavailable hint "
    + "rather than guessing how much help it gives. Guessing high would show a learner more "
    + "support than the server authorised, and on this ladder the rung above the last one is the "
    + "answer itself.")]
public enum CoachHintKind
{
    /// <summary>Unrecognised. Render the rung as unavailable; never substitute another rung.</summary>
    Unknown = 0,

    /// <summary>
    /// A category cue — part of speech, semantic field, or the resource it came from.
    /// No letters of the term, no gloss. Shipped as rung one: it discloses none of the form.
    /// </summary>
    Category = 1,

    /// <summary>
    /// A form cue — the initial character and the length. Shipped as rung <b>three</b>, the top of
    /// the ladder, because it is the only rung that discloses part of the written form itself.
    /// </summary>
    FormCue = 2,

    /// <summary>
    /// The term blanked inside a sentence the learner has met before. Shipped as rung <b>two</b>:
    /// it supplies surrounding context and none of the form, and the learner still produces the
    /// whole term.
    /// </summary>
    Cloze = 3
}

/// <summary>
/// One rung of the hint ladder. Metadata only.
/// </summary>
/// <param name="Rung">
/// Position on the ladder, 1-based and ascending in support. Independent of
/// <see cref="CoachHintKind"/>'s ordinal — the kind names what a rung gives, this names where it
/// sits.
/// </param>
/// <param name="Kind">How much support this rung gives.</param>
public sealed record CoachHintRungDto(int Rung, CoachHintKind Kind);

/// <summary>
/// An offer to do less of the same work, rather than easier work.
/// </summary>
/// <remarks>
/// <para>
/// The pedagogical point, and the reason this is not simply "skip today". A learner protecting a
/// streak is telling you the session is too long, not that they want to stop learning. Cutting the
/// item count keeps every remaining item a full retrieval attempt; cutting the difficulty would
/// keep the count and remove the learning, which is the trade this offer exists to refuse.
/// </para>
/// <para>
/// <see cref="PreservesRetrieval"/> is on the wire and is always true for a well-formed offer. It
/// is present so a client, a test, and a reviewer can all check the claim rather than trust the
/// name of the type — and so an offer that ever stops preserving retrieval has to say so out loud.
/// </para>
/// </remarks>
/// <param name="SuggestedItemCount">
/// How many items the shorter session would hold. Server-derived from the real due set, never a
/// round number chosen for the sentence.
/// </param>
/// <param name="FullItemCount">How many items the full session holds, so the learner can compare.</param>
/// <param name="PreservesRetrieval">
/// True when every remaining item is still a retrieval attempt in the target language.
/// </param>
public sealed record CoachShorterSessionOfferDto(
    int SuggestedItemCount,
    int FullItemCount,
    bool PreservesRetrieval);

/// <summary>
/// Where a learner can do the thing Sam declined to do.
/// </summary>
/// <remarks>
/// A route the client resolves, typed parameters the client may drop, and the consequence stated
/// before the learner acts. No path, no query string, no free text.
/// </remarks>
/// <param name="Route">The screen, from the closed catalogue.</param>
/// <param name="Parameters">Typed parameters, possibly empty. Never learner text.</param>
/// <param name="SideEffect">What acting on this destination can change.</param>
public sealed record CoachDestinationDto(
    CoachRouteName Route,
    IReadOnlyList<CoachRouteParameterDto> Parameters,
    CoachRouteSideEffect SideEffect);

/// <summary>One typed route parameter.</summary>
/// <param name="Key">The closed parameter name.</param>
/// <param name="Value">
/// A server-owned identifier or an ISO date. Never a query the model wrote and never learner text;
/// the catalogue's parameter contract is what makes that checkable.
/// </param>
public sealed record CoachRouteParameterDto(CoachRouteParameterKey Key, string Value);

/// <summary>
/// An honest statement of something Sam will not or cannot do, and what the learner can do instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Counts live here, not in the copy.</b> <c>CoachDeterministicCopy</c> holds the sentence and
/// this holds the numbers, because a sentence with a number in it is a sentence that goes stale
/// silently — and the coach's whole failure mode is fluent prose inventing figures the data does
/// not support. A client renders the copy and the counts together; neither half can drift into the
/// other.
/// </para>
/// <para>
/// <b>Coverage is stated for the same reason a read scope states it.</b> "You have no words to
/// review" and "you have no words to review in the last seven days" are different claims, and a
/// limitation that omits the window makes the stronger one by accident.
/// </para>
/// <para>
/// <b>This shape carries no learner content.</b> No term, no gloss, no example, no query. The
/// alternatives are closed codes and counts; the destination is a closed route. That is what lets
/// a limitation be rendered on a public-facing surface without a second review.
/// </para>
/// <para>
/// Additive and tolerant: every member below is optional to an older client, which renders what it
/// recognises and ignores the rest.
/// </para>
/// </remarks>
public sealed class CoachLimitationDto
{
    /// <summary>Why Sam could not do it.</summary>
    public CoachLimitationCode Code { get; init; } = CoachLimitationCode.Unknown;

    /// <summary>
    /// How much of the learner's data the statement covers, when it makes a claim about their data.
    /// </summary>
    public CoachEvidenceCoverage Coverage { get; init; } = CoachEvidenceCoverage.Unknown;

    /// <summary>The instant the counts were true at, whole-second normalised. Null when no count is stated.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? AsOfUtc { get; init; }

    /// <summary>The first day of the window, when the statement covers one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? WindowStartDate { get; init; }

    /// <summary>The last day of the window, when the statement covers one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? WindowEndDate { get; init; }

    /// <summary>
    /// How many of the learner's rows the request would have touched, when that is knowable.
    /// </summary>
    /// <remarks>
    /// The number that makes a bulk-change refusal concrete. "That would remove everything" is a
    /// warning; "that would remove 412 words and their review history" is a fact the learner can
    /// weigh.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AffectedCount { get; init; }

    /// <summary>Where the learner can do it themselves. Null when there is nowhere honest to send them.</summary>
    /// <remarks>
    /// The <em>recommended</em> surface, which for a bounded-scope refusal is the one where the
    /// smallest reversible version of the request lives — not the one where the largest version
    /// does. Naming the biggest hammer first would be a destructive proposal wearing a link.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CoachDestinationDto? Destination { get; init; }

    /// <summary>
    /// The whole-data surface, named only when a screen that really performs the total change
    /// exists. <b>Null in every limitation this build emits</b>, because no such screen ships.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="Destination"/> and separate on purpose. A learner who says "let me
    /// start clean" is entitled to know <em>if</em> a real start-clean exists and where it is, and
    /// hiding one that existed would be the app deciding for them. Putting it in its own field
    /// means the recommended safe path stays the recommended one, a total path is disclosed when
    /// there is one, and a test can prove both facts rather than one of them.
    /// </para>
    /// <para>
    /// Named for the scope of the change rather than for whose data it is. The plan calls this "the
    /// account surface" and the first draft did too — which the embargo scanner refused, correctly:
    /// <c>account</c> is an identity word, and a coach shape that names the learner is a payload
    /// that can be correlated once it leaves. The concept survives the rename intact, because what
    /// this field points at is the screen where the <em>full-scope</em> version of a request lives.
    /// It pairs with <see cref="CoachLimitationCode.ExceedsSafeChangeScope"/>.
    /// </para>
    /// <para>
    /// <b>Null when no such screen exists, which is the shipped case today.</b> The first draft
    /// pointed this at Settings for the bulk-vocabulary boundary. Settings exports data and deletes
    /// coach conversation history; it does not offer an account-level start-clean, and no screen in
    /// this build does. A destination that cannot do the thing is worse than none — the learner
    /// goes there, cannot find the control, and concludes they missed it. Populate this only when
    /// the named route genuinely performs the full-scope request.
    /// </para>
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CoachDestinationDto? FullScopeSurface { get; init; }

    /// <summary>
    /// Where the learner can take a copy of their data before removing any of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive, and the honest half of what <see cref="FullScopeSurface"/> used to claim. Export
    /// is a capability this build really has — <c>Settings.razor</c> and <c>DataExportService</c> —
    /// so <see cref="CoachAlternativeCode.ExportBeforeRemoving"/> can name a screen without
    /// inventing one. Its own field rather than a shared "other surface" slot, because the two
    /// answer different questions: one is "where do I do the whole thing", the other is "where do I
    /// make this recoverable first", and only the second is currently answerable.
    /// </para>
    /// <para>
    /// Set it only alongside <see cref="CoachAlternativeCode.ExportBeforeRemoving"/>. A surface with
    /// no alternative pointing at it is a link with no sentence explaining why it is there.
    /// </para>
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CoachDestinationDto? ExportSurface { get; init; }

    /// <summary>
    /// How many rows the refused turn's read deliberately held back, when one read held any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive and nullable, so a client built before this reads the same shape it always did.
    /// A bounded count and nothing else: no identifiers, no terms, no sentence.
    /// </para>
    /// <para>
    /// <b>Why it is on the limitation and not only on the evidence.</b> The evidence rows are the
    /// richer answer and the client should prefer them — but they are not reconstructed on every
    /// path. A resumed session restores the stored limitation from the protected outcome without
    /// rebuilding the evidence list, and a refusal that says "I held some back" with no number is
    /// the vaguer, less useful half of what the server already knew. Carrying the pair here means
    /// the most useful withheld fact survives the paths the evidence does not.
    /// </para>
    /// <para>
    /// Null whenever the server cannot state one truthful number: no read withheld anything, or
    /// more than one read did. See <see cref="WithheldReason"/>.
    /// </para>
    /// </remarks>
    public int? WithheldCount { get; init; }

    /// <summary>
    /// Why those rows were held back. Always travels with <see cref="WithheldCount"/> or not at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same closed, wire-tolerant reason the evidence carries, reused rather than redeclared so
    /// the two cannot drift and a client renders one vocabulary in one place. An unrecognised value
    /// decodes to <see cref="CoachWithheldReason.Unknown"/> through the shared fallback.
    /// </para>
    /// <para>
    /// <b>The pair is all-or-nothing.</b> A count with no reason cannot be rendered as a sentence
    /// — "4 held back" with no because — and a reason with no count states no scale. Either both
    /// are present and coherent, or both are null.
    /// </para>
    /// </remarks>
    public CoachWithheldReason? WithheldReason { get; init; }

    /// <summary>
    /// Bounded, reversible things the learner could do instead, as closed codes.
    /// </summary>
    public IReadOnlyList<CoachAlternativeCode> Alternatives { get; init; } = [];

    /// <summary>The hint ladder, when the limitation is a retrieval boundary. Ascending in support.</summary>
    public IReadOnlyList<CoachHintRungDto> HintLadder { get; init; } = [];

    /// <summary>A shorter session that keeps the retrieval, when one is on offer.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CoachShorterSessionOfferDto? ShorterSession { get; init; }
}

/// <summary>
/// A bounded, reversible thing the learner can do instead of the thing that was refused.
/// </summary>
/// <remarks>
/// Codes rather than sentences, so the client localises them and no count or date can hide inside
/// one. Every member names something the learner can undo — that is the bar for appearing here at
/// all, and it is what makes offering an alternative a smaller act than the refusal it follows.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
[WireEnumFallback(nameof(CoachAlternativeCode.Unknown), WireEnumFallbackKind.SafeZero,
    "Unknown is the documented unset value and the client drops the alternative rather than "
    + "rendering a suggestion it cannot name. Showing an unlabelled option next to a refusal would "
    + "invite the learner to take an action neither they nor the client can describe.")]
public enum CoachAlternativeCode
{
    /// <summary>Unrecognised. Drop this alternative from the rendered list.</summary>
    Unknown = 0,

    // 1 and 4 are deliberately vacant. ArchiveInsteadOfDelete and PauseReviewsInstead were
    // declared before anyone checked whether the app could do either. It cannot: only
    // SkillProfile carries IsArchived, vocabulary has no archive at all, and nothing anywhere
    // pauses a review schedule. Offering them would have been the same over-claim W6 exists to
    // catch, arriving through the shape that is supposed to be the honest one.
    //
    // The ordinals are left as gaps rather than reused, so a value from an in-flight build
    // decodes as Unknown and is dropped rather than silently becoming a different offer.

    /// <summary>Remove one list's worth rather than everything, one list at a time.</summary>
    RemoveOneListAtATime = 2,

    /// <summary>Start a fresh list and leave the existing words where they are.</summary>
    StartAFreshList = 3,

    /// <summary>Export the words first, so the removal is recoverable outside the app.</summary>
    ExportBeforeRemoving = 5,

    /// <summary>Take a shorter session now rather than skipping it.</summary>
    TakeAShorterSession = 6,

    /// <summary>Use the hint ladder on the items that are hard.</summary>
    UseHintLadder = 7
}
