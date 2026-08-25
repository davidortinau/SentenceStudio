namespace SentenceStudio.Contracts.Feedback;

/// <summary>
/// The closed discriminators a feedback failure response carries in its problem details.
/// </summary>
/// <remarks>
/// <para>
/// These live in the contracts assembly because they are the wire, not an implementation detail:
/// the server writes them into the <c>code</c> extension and the client branches on them. The
/// alternative — inferring meaning from the status code alone — cannot work here, because the two
/// most important outcomes share a status. A submission that provably filed nothing and one whose
/// outcome is unknown are both 409, and telling a learner the wrong one is either a false alarm
/// ("check GitHub before writing it again", when nothing was filed) or a duplicate waiting to
/// happen.
/// </para>
/// <para>
/// The set is closed and the values are stable strings. A client that meets a code it does not
/// recognise must fall back to the most cautious interpretation of the status, never to the most
/// convenient one.
/// </para>
/// </remarks>
public static class FeedbackProblemCodes
{
    /// <summary>The header the server writes and the client reads.</summary>
    public const string ExtensionName = "code";

    /// <summary>
    /// The submission closed without creating an issue, and that is known rather than assumed.
    /// The preview is spent; the learner writes the report again.
    /// </summary>
    public const string SubmissionClosed = "submission_closed";

    /// <summary>
    /// A submission is in flight, or its outcome was never recorded. Whether an issue exists is
    /// unknown, so the request must not be repeated.
    /// </summary>
    public const string SubmissionInDoubt = "submission_in_doubt";

    /// <summary>A per-owner limit refused the request; a truthful Retry-After accompanies it.</summary>
    public const string RateLimited = "rate_limited";
}
