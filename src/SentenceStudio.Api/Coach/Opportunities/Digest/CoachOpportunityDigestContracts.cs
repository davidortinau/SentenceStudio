namespace SentenceStudio.Api.Coach.Opportunities.Digest;

/// <summary>
/// One problem in the operational digest: what it was, how often, how many learners.
/// </summary>
/// <remarks>
/// <para>
/// The same aggregate the Development-only operator rollup renders, minus everything that could
/// address a row. There is no identifier of any kind on this shape — no owner, no conversation,
/// no message, no turn, no write operation, and no ledger row id — because the digest is read
/// <b>outside</b> the request pipeline that enforces owner scope, and a shape that could name a
/// row would be a shape somebody could join back to a learner.
/// </para>
/// <para>
/// <see cref="Fingerprint"/> is the one identity that survives, and it is already the ledger's own
/// content-free problem digest: safe to log, safe to paste into
/// <c>docs/sam-future-opportunities.md</c>, and not resolvable to a person.
/// </para>
/// </remarks>
/// <param name="Fingerprint">The ledger's content-free problem identity.</param>
/// <param name="Kind">What kind of gap this is, by enum name.</param>
/// <param name="Disposition">Whether the underlying rows are individually reviewable, by enum name.</param>
/// <param name="CapabilityCode">What the learner was reaching for, from the closed vocabulary.</param>
/// <param name="ToolName">The registered tool, when one was involved.</param>
/// <param name="FailureCode">The closed refusal code, when the server refused.</param>
/// <param name="OfferLink">What the learner's message was answering, by enum name.</param>
/// <param name="TotalOccurrences">How many times the problem happened across every bucket in the window.</param>
/// <param name="DistinctLearners">How many learners hit it. A count, never a list.</param>
/// <param name="RowCount">How many (learner, day) buckets carry it.</param>
/// <param name="FirstObservedAtUtc">The earliest occurrence in the window.</param>
/// <param name="LastObservedAtUtc">The most recent occurrence in the window.</param>
/// <param name="Statuses">The distinct review statuses across those buckets, ordinally sorted.</param>
public sealed record CoachOpportunityDigestLine(
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

/// <summary>
/// How many learner reports arrived under one reason in the window.
/// </summary>
/// <remarks>
/// Reported separately from <see cref="CoachOpportunityDigestLine"/> because the two answer
/// different questions. A ledger line answers "how often does this problem occur"; this answers
/// "how many times did a person spend an action to disagree with us, and about what". Reports are
/// the only source in the ledger that arrives with a human's deliberate intent behind it, so
/// losing their shape inside the automatic counts would bury the signal the digest exists to
/// surface.
/// </remarks>
/// <param name="Reason">The closed reason enum name the learner chose.</param>
/// <param name="ReportCount">How many reports carried it.</param>
/// <param name="DistinctLearners">How many learners filed them. A count, never a list.</param>
/// <param name="FirstReportedAtUtc">The earliest report in the window.</param>
/// <param name="LastReportedAtUtc">The most recent report in the window.</param>
public sealed record CoachOpportunityDigestReasonLine(
    string Reason,
    int ReportCount,
    int DistinctLearners,
    DateTime FirstReportedAtUtc,
    DateTime LastReportedAtUtc);

/// <summary>
/// The whole operational digest for one window.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>production reviewer path</b>. It exists so <c>Coach:Reports:Enabled</c> can ship
/// true in Production without the evidence-decrypting operator UI going with it: a learner who
/// presses Report is told a person looks at it, and this is the artifact that makes the sentence
/// true. It carries counts, closed codes, statuses, timestamps, and fingerprints — and nothing
/// else, by construction rather than by redaction.
/// </para>
/// <para>
/// <see cref="Truncated"/> is reported rather than silently applied. A digest that quietly dropped
/// the tail would let a reviewer read "these are the problems" when it meant "these are the first
/// five hundred", which is the shape a missed signal takes.
/// </para>
/// </remarks>
/// <param name="GeneratedAtUtc">When the digest was produced.</param>
/// <param name="WindowStartUtc">The lower bound applied, or null for "everything retained".</param>
/// <param name="WindowEndUtc">The upper bound, which is always the generation instant.</param>
/// <param name="Lines">One line per problem, most frequent first.</param>
/// <param name="ReportReasons">One line per learner-report reason, most frequent first.</param>
/// <param name="TotalReports">How many learner reports fall in the window.</param>
/// <param name="TotalOpportunityRows">How many ledger buckets fall in the window.</param>
/// <param name="Truncated">True when more problems exist than the digest returned.</param>
public sealed record CoachOpportunityDigest(
    DateTime GeneratedAtUtc,
    DateTime? WindowStartUtc,
    DateTime WindowEndUtc,
    IReadOnlyList<CoachOpportunityDigestLine> Lines,
    IReadOnlyList<CoachOpportunityDigestReasonLine> ReportReasons,
    int TotalReports,
    int TotalOpportunityRows,
    bool Truncated);
