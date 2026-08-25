using System.Text.RegularExpressions;

namespace SentenceStudio.Api.Coach.Opportunities.Endpoints;

/// <summary>
/// One content-free ledger row, as the operator surface sees it.
/// </summary>
/// <remarks>
/// A projection of the entity with the same guarantee: identifiers, enum names, closed-vocabulary
/// codes, counts, and timestamps. The evidence pointers are reported as booleans rather than as
/// identifiers, because an operator listing rows has no use for a message id and a response that
/// carried one would be a place for it to be copied somewhere less careful.
/// </remarks>
public sealed record CoachOpportunityRowDto(
    string Id,
    string Kind,
    string Disposition,
    string Surface,
    string CapabilityCode,
    string? ToolName,
    string? RiskClass,
    string? FailureCode,
    string? StopReason,
    string OfferLink,
    string Fingerprint,
    DateOnly DedupBucketDate,
    int OccurrenceCount,
    DateTime FirstObservedAtUtc,
    DateTime LastObservedAtUtc,
    string Status,
    DateTime? ReviewedAtUtc,
    string? ReviewerNoteCode,
    string? LinkedSpecPath,
    bool HasEvidence,
    int EvidenceRevealCount,
    DateTime? EvidenceLastRevealedAtUtc,
    int SchemaVersion,
    CoachOpportunityReportFactsDto? Report = null,
    CoachOpportunityReportRollupDto? ReportRollup = null);

/// <summary>
/// How many learner reports rolled into one ledger row, and under which reasons.
/// </summary>
/// <remarks>
/// <para>
/// A ledger row's identity is <em>(learner, problem, UTC day)</em>, so several reports — of
/// several different responses — can land on the same row. <see cref="CoachOpportunityReportFactsDto"/>
/// describes exactly one of them, and describing "one of them" without saying how many there were
/// is how a reviewer reads a single turn's tool list as if it were the row's.
/// </para>
/// <para>
/// This shape is therefore attached whenever the row is a learner report, including when the
/// per-response facts beside it are null. Counts and closed reason codes only; there is no
/// identifier here and nowhere to put one.
/// </para>
/// </remarks>
/// <param name="ReportCount">How many reports link to this row.</param>
/// <param name="ReportedResponseCount">How many distinct responses those reports name.</param>
/// <param name="Reasons">The reason breakdown, most frequent first.</param>
/// <param name="FirstReportedAtUtc">The earliest report on this row.</param>
/// <param name="LastReportedAtUtc">The most recent report on this row.</param>
/// <param name="FactsAreForTheReportedResponse">
/// True when the facts beside this rollup belong to the response this row's evidence points at.
/// False when no surviving report names that response — retention prunes reports at 180 days
/// while a decided row is kept forever — in which case the facts are deliberately absent rather
/// than borrowed from whichever other report is still there.
/// </param>
public sealed record CoachOpportunityReportRollupDto(
    int ReportCount,
    int ReportedResponseCount,
    IReadOnlyList<CoachOpportunityReportReasonCountDto> Reasons,
    DateTime FirstReportedAtUtc,
    DateTime LastReportedAtUtc,
    bool FactsAreForTheReportedResponse,

    // Counts by closed rule code across the reports on this row. Additive and defaulted, so a
    // pre-W9 caller and a deployment that never ran the ladder both read an empty list — which is
    // the honest rendering of "nothing was measured" rather than "nothing fired".
    IReadOnlyList<CoachOpportunityGroundingRuleCountDto>? GroundingRules = null);

/// <summary>How many reports on one ledger row carried one reason.</summary>
/// <param name="Reason">The closed reason enum name the learner chose.</param>
/// <param name="ReportCount">How many reports carried it.</param>
public sealed record CoachOpportunityReportReasonCountDto(string Reason, int ReportCount);

/// <summary>
/// The turn facts behind one learner report, as the operator surface sees them.
/// </summary>
/// <remarks>
/// <para>
/// <b>These describe exactly one reported response</b>, never a day's worth of them. A ledger row
/// can carry several reports (its identity is <em>learner, problem, UTC day</em>), so this block
/// is attached only when one of them can be tied to the row — see
/// <see cref="CoachOpportunityReportRollupDto"/>, which is always attached and says how many
/// there were.
/// </para>
/// <para>
/// <b>Closed codes only.</b> Every member is an enum name, a closed-vocabulary code, a count, or
/// a comma-separated list of registered tool names. There is no free-text member and no place to
/// add one, which is what keeps the reviewer's view of a report the same shape as the ledger row
/// beside it.
/// </para>
/// <para>
/// Attached to the detail response only. The list is a triage surface, and a per-row join would
/// have made loading it proportional to the reports rather than to the page.
/// </para>
/// </remarks>
/// <param name="Reason">Why the learner reported the response, by name.</param>
/// <param name="ResponseKind">How the reported response rendered: an answer, a clarification.</param>
/// <param name="TurnStatus">The durable turn operation's state when the report was filed.</param>
/// <param name="TurnAttemptCount">How many workers claimed the turn. A retried turn is worth knowing.</param>
/// <param name="TurnErrorCode">The operation's closed, content-free failure code, when it failed.</param>
/// <param name="InvokedToolNames">The registered tools the turn ran, ordinally sorted.</param>
/// <param name="WriteStatus">The write proposal's state, when the turn produced one.</param>
/// <param name="WriteFailureCode">The write ledger's closed refusal code, when it refused.</param>
/// <param name="ReportedAtUtc">When the learner filed the report.</param>
public sealed record CoachOpportunityReportFactsDto(
    string Reason,
    string ResponseKind,
    string? TurnStatus,
    int? TurnAttemptCount,
    string? TurnErrorCode,
    string? InvokedToolNames,
    string? WriteStatus,
    string? WriteFailureCode,
    DateTime ReportedAtUtc,

    // Grounding evidence, all nullable and all closed codes or counts. Null is the reading for a
    // rung of Off, for a report filed before these columns existed, and for a stored outcome that
    // could not be read — three situations that are all "no evidence" and none of which is a zero.
    //
    // Names rather than ordinals on the two the reviewer reads directly, because a card outlives
    // the build that wrote the row. Deliberately no trace and no grounding summary: this is the
    // report's own evidence, not a second channel onto the turn.
    string? GroundingStage = null,
    bool? GroundingRefused = null,
    bool? GroundingAltered = null,
    bool? GroundingRepairSuppressed = null,
    int? GroundingFindingCount = null,
    string? GroundingRuleCodes = null,
    string? GroundingLimitationCode = null);

/// <summary>One closed grounding rule code and how many reports carried it.</summary>
public sealed record CoachOpportunityGroundingRuleCountDto(string Rule, int ReportCount);

/// <summary>One page of ledger rows.</summary>
public sealed record CoachOpportunityPageDto(
    IReadOnlyList<CoachOpportunityRowDto> Items,
    int Total,
    int Skip,
    int Take);

/// <summary>
/// One problem, aggregated across every learner who hit it.
/// </summary>
/// <remarks>
/// <b><see cref="DistinctLearners"/> is a count and never a list.</b> That is the whole design of
/// this shape: the cross-learner view a reviewer needs is "how many people", and any response
/// that answered "which people" would have turned a product rollup into a cross-tenant read.
/// </remarks>
public sealed record CoachOpportunityRollupDto(
    string Fingerprint,
    string Kind,
    string Disposition,
    string CapabilityCode,
    string? ToolName,
    string? FailureCode,
    string OfferLink,
    int TotalOccurrences,
    int DistinctLearners,
    int RowCount,
    DateTime FirstObservedAtUtc,
    DateTime LastObservedAtUtc,
    IReadOnlyList<string> Statuses);

/// <summary>What a reviewer decided.</summary>
/// <param name="Status">The new lifecycle status.</param>
/// <param name="ReviewerNoteCode">
/// The decision, from the closed enum. There is deliberately no prose field.
/// </param>
/// <param name="LinkedSpecPath">
/// A repository-relative path to the spec or backlog entry that owns this now. Validated against
/// <see cref="CoachOpportunityReviewRequest.LinkedSpecPathPattern"/>.
/// </param>
public sealed partial record CoachOpportunityReviewRequest(
    CoachOpportunityStatus Status,
    CoachOpportunityReviewerNoteCode? ReviewerNoteCode = null,
    string? LinkedSpecPath = null)
{
    /// <summary>
    /// The only shapes a linked path may take.
    /// </summary>
    /// <remarks>
    /// Anchored, character-class bounded, and with no directory traversal, so this field is a
    /// reference into this repository's own documentation and cannot become a second free-text
    /// column or a path that escapes it.
    /// </remarks>
    public const string LinkedSpecPathPattern =
        @"^docs/(specs/[A-Za-z0-9._-]+\.md|sam-future-opportunities\.md)$";

    /// <summary>True when the linked path is absent or matches the allowed shape.</summary>
    public bool IsLinkedSpecPathValid =>
        string.IsNullOrWhiteSpace(LinkedSpecPath)
        || LinkedSpecPathRegex().IsMatch(LinkedSpecPath);

    [GeneratedRegex(LinkedSpecPathPattern, RegexOptions.CultureInvariant)]
    private static partial Regex LinkedSpecPathRegex();
}

/// <summary>
/// The result of a review, including a paste-ready markdown block.
/// </summary>
/// <remarks>
/// The block is rendered from content-free fields only and is <b>returned, never written</b>. A
/// bot committing to <c>docs/sam-future-opportunities.md</c> would bypass the Zoe-triage and
/// Captain-approval gates that log exists to enforce, so a human pastes it and commits it.
/// </remarks>
public sealed record CoachOpportunityReviewResponse(
    CoachOpportunityRowDto Row,
    string MarkdownBlock);

/// <summary>The body that authorises an evidence reveal.</summary>
/// <param name="Acknowledgement">
/// Must equal <see cref="CoachOpportunityLimits.EvidenceRevealAcknowledgement"/>. Not a secret —
/// a speed bump that makes the reveal an explicit act rather than a side effect of loading a page.
/// </param>
public sealed record CoachOpportunityEvidenceRequest(string? Acknowledgement);

/// <summary>Why an evidence reveal returned what it did.</summary>
/// <remarks>
/// Serialized <b>by name</b>. This is a response member the operator client models as a string;
/// a numeric enum would make a successful, correctly authorized reveal read as a client-side
/// failure.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(
    typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum CoachOpportunityEvidenceState
{
    /// <summary>Both messages resolved and were decrypted.</summary>
    Available = 0,

    /// <summary>
    /// The row carries pointers but the messages no longer resolve — usually because the learner
    /// deleted the conversation. Reported as a state, never as an error: the ledger row is still
    /// a valid product signal without its evidence.
    /// </summary>
    Unavailable = 1,

    /// <summary>The row carries no pointers, because it is an aggregate-only row.</summary>
    NotApplicable = 2
}

/// <summary>The decrypted evidence for one Product row.</summary>
/// <remarks>
/// The only response on this surface that carries learner text. It requires all four surface
/// gates, a Product row, non-null pointers, the literal acknowledgement, a matching owner, and a
/// durable key ring — and it increments a counter on the row it read.
/// </remarks>
public sealed record CoachOpportunityEvidenceResponse(
    string OpportunityId,
    CoachOpportunityEvidenceState EvidenceState,
    string? LearnerMessageText,
    string? PriorCoachMessageText,
    bool CrossOwner,
    int EvidenceRevealCount);
