using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Feedback;

/// <summary>
/// Stable, content-free codes for every way a feedback request can be refused or fail.
/// </summary>
/// <remarks>
/// <para>
/// These are the only things the feedback lane is allowed to log about a failure, and the only
/// discriminators it returns. The rule they exist to enforce is narrow and absolute: a refusal
/// must never carry the identity of the caller, the identity of the token's owner, the preview
/// body, the issue title, or anything GitHub echoed back — all three of the last are learner text,
/// and the first two turn an operator log into a record of who tried to submit what.
/// </para>
/// <para>
/// The owner-mismatch case is the sharpest example. The obvious log line names both profile ids so
/// an operator can "see who did it"; what it actually produces is a durable, searchable record
/// linking two accounts, written on a path an unauthenticated attacker can trigger at will by
/// replaying somebody else's token. The code below says the same operational thing — a token was
/// presented by someone who does not own it — and says nothing about whom.
/// </para>
/// </remarks>
public static class FeedbackFailureCodes
{
    /// <summary>The token was absent, malformed, or its signature did not verify.</summary>
    public const string TokenInvalid = "token_invalid";

    /// <summary>The token verified but its lifetime had elapsed.</summary>
    public const string TokenExpired = "token_expired";

    /// <summary>The token verified but the caller is not the owner it was issued to.</summary>
    public const string TokenOwnerMismatch = "token_owner_mismatch";

    /// <summary>The token's payload verified but carried content the server will not post.</summary>
    public const string TokenPayloadRejected = "token_payload_rejected";

    /// <summary>A per-owner limit refused the request. Always paired with a truthful Retry-After.</summary>
    /// <remarks>
    /// Aliases the wire constant so the value an operator greps for in a log and the value a client
    /// branches on cannot drift apart. <c>FeedbackProblemCodeParityTests</c> pins the equality.
    /// </remarks>
    public const string RateLimited = FeedbackProblemCodes.RateLimited;

    /// <summary>GitHub is not configured on this deployment, so nothing can be filed.</summary>
    public const string GitHubUnconfigured = "github_unconfigured";

    /// <summary>GitHub rejected the request. No issue was created.</summary>
    public const string GitHubRejected = "github_rejected";

    /// <summary>GitHub refused our credentials. No issue was created.</summary>
    public const string GitHubUnauthorized = "github_unauthorized";

    /// <summary>GitHub's own rate limit refused the request. No issue was created.</summary>
    public const string GitHubRateLimited = "github_rate_limited";

    /// <summary>The call to GitHub did not complete, so whether an issue exists is unknown.</summary>
    public const string GitHubUnreachable = "github_unreachable";

    /// <summary>
    /// The issue was created but its receipt could not be recorded. The submission is closed and
    /// can never be retried.
    /// </summary>
    public const string SettlementFailed = "settlement_failed";

    /// <summary>
    /// A submission for this token is already under way, or its outcome was never recorded, so
    /// this request refuses rather than risk a second public issue.
    /// </summary>
    /// <remarks>Aliases the wire constant; see <see cref="RateLimited"/>.</remarks>
    public const string SubmissionInDoubt = FeedbackProblemCodes.SubmissionInDoubt;

    /// <summary>This token's submission already closed without creating an issue.</summary>
    /// <remarks>Aliases the wire constant; see <see cref="RateLimited"/>.</remarks>
    public const string SubmissionClosed = FeedbackProblemCodes.SubmissionClosed;

    /// <summary>The claim could not be settled because the ledger row moved underneath us.</summary>
    public const string ClaimContended = "claim_contended";
}
