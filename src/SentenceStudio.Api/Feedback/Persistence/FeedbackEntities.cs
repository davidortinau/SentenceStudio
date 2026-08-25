using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Feedback.Persistence;

/// <summary>
/// One redeemable preview, and whatever became of it.
/// </summary>
/// <remarks>
/// <para>
/// Keyed on the preview token's <c>jti</c>. That is the whole exactly-once mechanism: the claim is
/// an <c>INSERT</c>, and the primary key is what makes two concurrent inserts resolve to one
/// winner and one unique-violation, in the database, across processes and replicas. No lock lives
/// in a process here, because the thing being protected — a public issue — is not a process-local
/// resource.
/// </para>
/// <para>
/// <b>No issue content is stored.</b> Not the title, not the body, not the description the learner
/// typed. The row keeps a digest of what was posted so the preview-to-post binding stays checkable,
/// the issue's public identity once it exists (a number and a URL that are public by definition),
/// and closed codes. A database copy of this table tells an attacker who filed how often and how it
/// went; it does not tell them what anybody wrote.
/// </para>
/// </remarks>
public sealed class FeedbackSubmission
{
    /// <summary>The preview token's nonce. Primary key, and the claim.</summary>
    public string Jti { get; set; } = string.Empty;

    /// <summary>The owning learner. Every read is filtered by this first.</summary>
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>Where the attempt is in its lifecycle.</summary>
    public FeedbackSubmissionStatus Status { get; set; } = FeedbackSubmissionStatus.Claimed;

    /// <summary>
    /// Digest over exactly the bytes posted to GitHub. Binds the preview the learner approved to
    /// the issue that was created, without keeping either one.
    /// </summary>
    public string ContentDigest { get; set; } = string.Empty;

    /// <summary>The public issue number, once one exists.</summary>
    public int? IssueNumber { get; set; }

    /// <summary>The public issue URL, once one exists.</summary>
    public string? IssueUrl { get; set; }

    /// <summary>
    /// The title GitHub reported back. Public by construction — it is the title of a public issue —
    /// and stored so a replay can answer identically to the original response.
    /// </summary>
    public string? IssueTitle { get; set; }

    /// <summary>A closed code from <see cref="FeedbackFailureCodes"/>. Null while nothing failed.</summary>
    public string? FailureCode { get; set; }

    /// <summary>Normalised route category, for aggregate triage. Closed set, never a route.</summary>
    public FeedbackRouteCategory RouteCategory { get; set; } = FeedbackRouteCategory.Unknown;

    /// <summary>Normalised platform. Closed set.</summary>
    public FeedbackPlatform Platform { get; set; } = FeedbackPlatform.Unknown;

    /// <summary>Normalised app version. Shape-validated, bounded.</summary>
    public string AppVersion { get; set; } = FeedbackClientMetadataNormalizer.UnknownVersion;

    /// <summary>When the claim was taken.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the row last changed.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// When the presented token stopped being redeemable. Retention prunes from here rather than
    /// from <see cref="CreatedAtUtc"/> so a row can never be removed while its token is still live.
    /// </summary>
    public DateTime TokenExpiresAtUtc { get; set; }

    /// <summary>
    /// Optimistic concurrency token. The settle is conditional on the value the claimer read, so a
    /// row that moved underneath a settle is reported rather than overwritten.
    /// </summary>
    public int Version { get; set; }
}

/// <summary>
/// One rolling window of recent limited events for one owner and one limit.
/// </summary>
/// <remarks>
/// <para>
/// A single row per (owner, kind) rather than an append-only event table, because the check and
/// the record have to be one atomic step across replicas. With one row that is a compare-and-swap
/// on <see cref="Version"/> — provider-independent, no advisory lock, no isolation-level
/// assumption, and no serialisation-failure retry loop to get subtly wrong. With an event table it
/// would be a count-then-insert, which two replicas can interleave into an over-admission unless
/// the whole thing runs at <c>SERIALIZABLE</c>.
/// </para>
/// <para>
/// The window contents live in <see cref="RecentTicksCsv"/> — the exact instants of the events
/// still inside the window, ascending, pruned on every pass. Exact instants rather than a counter
/// and a window start, because a counter cannot answer "when may I retry?" truthfully: it knows
/// how many happened, not when the oldest one falls out. The list is bounded by the limit itself,
/// which the options validator caps, so the column cannot grow without bound.
/// </para>
/// </remarks>
public sealed class FeedbackRateWindow
{
    /// <summary>The owning learner.</summary>
    public string UserProfileId { get; set; } = string.Empty;

    /// <summary>Which limit this row governs.</summary>
    public FeedbackRateKind Kind { get; set; }

    /// <summary>
    /// UTC ticks of the events still inside the window, ascending, comma-separated. Empty when the
    /// window has drained.
    /// </summary>
    public string RecentTicksCsv { get; set; } = string.Empty;

    /// <summary>When the row last changed. Retention prunes drained rows from here.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Optimistic concurrency token; the compare-and-swap turns on it.</summary>
    public int Version { get; set; }
}
