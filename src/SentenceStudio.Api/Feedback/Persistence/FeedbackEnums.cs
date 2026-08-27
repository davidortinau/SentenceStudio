namespace SentenceStudio.Api.Feedback.Persistence;

/// <summary>
/// The lifecycle of one attempt to turn a signed preview into a public GitHub issue.
/// </summary>
/// <remarks>
/// <para>
/// Stored as an ordinal. Members may only be appended — inserting one silently re-labels every row
/// already written, and these rows are the only record of whether a public issue exists.
/// </para>
/// <para>
/// The shape of this enum is dictated by one fact: <b>creating a GitHub issue is not
/// transactional with our database and cannot be undone.</b> Deleting an issue requires admin
/// rights the app does not hold, and even with them the issue has already been emailed to
/// watchers, indexed, and syndicated. So every state below is really an answer to "given what we
/// know, is it safe to call GitHub again for this token?", and the default answer when we do not
/// know is no.
/// </para>
/// </remarks>
public enum FeedbackSubmissionStatus
{
    /// <summary>
    /// Exactly one caller inserted this row and holds the claim. The call to GitHub may be in
    /// flight, may have completed, or may never have been made — the row does not say, and cannot.
    /// </summary>
    /// <remarks>
    /// <b>In doubt, not retryable.</b> A process that died between claiming and settling leaves a
    /// row here, and there is no way to distinguish that from a call still running. Retrying would
    /// risk a second public issue; discarding the row would let the next request retry, which is
    /// the same risk with an extra step. So this state refuses every later submission of the token,
    /// permanently, and an operator reconciles by looking at the repository.
    /// </remarks>
    Claimed = 0,

    /// <summary>
    /// The issue exists and its identity is recorded here. This row is the authoritative answer to
    /// every later submission of the token.
    /// </summary>
    Submitted = 1,

    /// <summary>
    /// The attempt closed without creating an issue, and that is <em>known</em> rather than
    /// assumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only reachable from outcomes that prove no issue was created: GitHub answered with a
    /// non-success status (its issue creation is atomic per request — a 4xx or 5xx response means
    /// no issue), or the request was refused by our own rate limiter before any call was made.
    /// </para>
    /// <para>
    /// A transport failure is deliberately <em>not</em> routed here. A timeout after the bytes went
    /// out is exactly the case where an issue may exist, and calling that "failed" is how a
    /// duplicate gets filed.
    /// </para>
    /// <para>
    /// Terminal even so. The token is spent; the learner previews again. Reopening a closed
    /// attempt would mean this row no longer answers for the request, and the whole design rests
    /// on it always answering.
    /// </para>
    /// </remarks>
    Failed = 2,

    /// <summary>
    /// GitHub created the issue and we could not record which one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The worst honest state, and the reason it has its own member rather than being folded into
    /// <see cref="Claimed"/>. It says something <see cref="Claimed"/> cannot: an issue definitely
    /// exists. That distinction is what an operator needs — <see cref="Claimed"/> means "look and
    /// see", this means "it is there, find it" — and it is what lets the learner be told the truth
    /// ("it was filed, we lost the link") instead of a shrug.
    /// </para>
    /// <para>
    /// Reaching this state is itself best-effort: it is written after the settle that failed, so if
    /// the database is the thing that is broken, this write fails too and the row stays
    /// <see cref="Claimed"/>. That degradation is safe by construction, because both states refuse
    /// every retry. There is no ordering of failures that produces a re-postable row.
    /// </para>
    /// </remarks>
    Committed = 3
}

/// <summary>
/// Which of a stored status's jobs it is still doing.
/// </summary>
/// <remarks>
/// The predicates are written as explicit switches rather than as each other's negation, so a
/// member added later falls out of both and the classification tests fail rather than silently
/// picking a behaviour. Defining one as <c>!</c> the other is how a new state gets classified by
/// accident, and here an accidental classification is a duplicate public issue.
/// </remarks>
public static class FeedbackSubmissionStates
{
    /// <summary>The issue exists and this row can answer for it.</summary>
    public static bool HasReceipt(FeedbackSubmissionStatus status) =>
        status == FeedbackSubmissionStatus.Submitted;

    /// <summary>
    /// Whether an issue exists is unknown or known-but-unrecorded. Either way, calling GitHub
    /// again for this token is forbidden.
    /// </summary>
    public static bool IsInDoubt(FeedbackSubmissionStatus status) => status switch
    {
        FeedbackSubmissionStatus.Claimed => true,
        FeedbackSubmissionStatus.Committed => true,
        _ => false
    };

    /// <summary>The attempt closed and no issue was created.</summary>
    public static bool IsClosedWithoutIssue(FeedbackSubmissionStatus status) =>
        status == FeedbackSubmissionStatus.Failed;

    /// <summary>
    /// The one predicate the endpoint actually branches on: may this request call GitHub?
    /// </summary>
    /// <remarks>
    /// Never true for any stored row. It exists as a named concept so the answer is impossible to
    /// get wrong by adding a status: every declared member is covered above, and a new one that
    /// nobody classified reaches none of the branches that permit a call.
    /// </remarks>
    public static bool PermitsExternalCall(FeedbackSubmissionStatus status) => false;
}

/// <summary>Which per-owner limit a rate window row governs.</summary>
/// <remarks>Stored as an ordinal; append only.</remarks>
public enum FeedbackRateKind
{
    /// <summary>Requests that generate a preview, which cost an AI call.</summary>
    Preview = 0,

    /// <summary>Requests that claim a submission, which cost a public GitHub issue.</summary>
    Submit = 1
}
