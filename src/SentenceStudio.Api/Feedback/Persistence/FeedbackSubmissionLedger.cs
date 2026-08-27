using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Feedback.Persistence;

/// <summary>What a claim attempt found.</summary>
public enum FeedbackClaimOutcome
{
    /// <summary>This caller inserted the row and is the only one permitted to call GitHub.</summary>
    Won = 0,

    /// <summary>An issue already exists for this token; the stored receipt is authoritative.</summary>
    AlreadySettled = 1,

    /// <summary>
    /// Another caller holds the claim, or held it and never recorded an outcome. Whether an issue
    /// exists is unknown, so this caller must not call GitHub.
    /// </summary>
    InDoubt = 2,

    /// <summary>The token's attempt already closed without creating an issue. Terminal.</summary>
    ClosedWithoutIssue = 3
}

/// <summary>The outcome of a claim or lookup, with the row when the caller is allowed to see it.</summary>
public readonly record struct FeedbackClaimResult(FeedbackClaimOutcome Outcome, FeedbackSubmission? Row);

/// <summary>Everything the ledger needs to open a claim.</summary>
public sealed record FeedbackClaimRequest(
    string Jti,
    string UserProfileId,
    string ContentDigest,
    FeedbackRouteCategory RouteCategory,
    FeedbackPlatform Platform,
    string AppVersion,
    DateTimeOffset TokenExpiresAt);

/// <summary>
/// The durable, cross-process arbiter of "has this preview already been filed?".
/// </summary>
public interface IFeedbackSubmissionLedger
{
    /// <summary>Reads the owner's row for <paramref name="jti"/> without creating one.</summary>
    Task<FeedbackClaimResult> LookupAsync(
        string jti, string userProfileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the claim row, or reports what the winner left behind. Exactly one concurrent caller
    /// can receive <see cref="FeedbackClaimOutcome.Won"/>.
    /// </summary>
    Task<FeedbackClaimResult> TryClaimAsync(
        FeedbackClaimRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits, briefly and boundedly, for a claim held by somebody else to settle, then reports it.
    /// </summary>
    Task<FeedbackClaimResult> WaitForSettlementAsync(
        string jti, string userProfileId, CancellationToken cancellationToken = default);

    /// <summary>Records the created issue against the claim. Returns false if the row moved.</summary>
    Task<bool> SettleSubmittedAsync(
        string jti,
        string userProfileId,
        int readVersion,
        int issueNumber,
        string issueUrl,
        string issueTitle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a claim that provably created no issue. Only ever called for outcomes that prove it.
    /// </summary>
    Task MarkFailedAsync(
        string jti,
        string userProfileId,
        int readVersion,
        string failureCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that an issue was created and its identity could not be stored. Best effort.
    /// </summary>
    Task MarkCommittedAsync(
        string jti,
        string userProfileId,
        int readVersion,
        string failureCode,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class FeedbackSubmissionLedger : IFeedbackSubmissionLedger
{
    private readonly FeedbackDbContext _db;
    private readonly TimeProvider _time;
    private readonly FeedbackOptions _options;
    private readonly ILogger<FeedbackSubmissionLedger> _logger;

    public FeedbackSubmissionLedger(
        FeedbackDbContext db,
        TimeProvider time,
        IOptions<FeedbackOptions> options,
        ILogger<FeedbackSubmissionLedger> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<FeedbackClaimResult> LookupAsync(
        string jti, string userProfileId, CancellationToken cancellationToken = default)
    {
        var row = await ReadAsync(jti, userProfileId, cancellationToken).ConfigureAwait(false);
        return row is null
            ? new FeedbackClaimResult(FeedbackClaimOutcome.Won, null)
            : Classify(row);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The claim is the <c>INSERT</c> itself, not a read followed by an insert. The primary key on
    /// <see cref="FeedbackSubmission.Jti"/> is what arbitrates: two replicas inserting the same
    /// nonce produce one committed row and one unique violation, decided inside the database, with
    /// no lock that lives in either process. A check-then-insert would leave a window between the
    /// two statements in which both callers see nothing and both proceed to call GitHub — which is
    /// two public issues, and public issues cannot be un-filed.
    /// </para>
    /// <para>
    /// The loser does not treat the violation as an error. It re-reads and answers from whatever
    /// the winner left, which is the only way a duplicate submit can end with the learner being
    /// told the truth about their issue rather than an error about a race they did not cause.
    /// </para>
    /// </remarks>
    public async Task<FeedbackClaimResult> TryClaimAsync(
        FeedbackClaimRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(request.UserProfileId) || string.IsNullOrEmpty(request.Jti))
        {
            // No owner means no claim. Inserting an unowned row would create a ledger entry nobody
            // can ever look up, and a token nobody can ever replay.
            _logger.LogWarning("[Feedback] Claim attempted with no owner or no token id — refusing.");
            return new FeedbackClaimResult(FeedbackClaimOutcome.InDoubt, null);
        }

        var existing = await ReadAsync(request.Jti, request.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return Classify(existing);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var row = new FeedbackSubmission
        {
            Jti = request.Jti,
            UserProfileId = request.UserProfileId,
            Status = FeedbackSubmissionStatus.Claimed,
            ContentDigest = request.ContentDigest,
            RouteCategory = request.RouteCategory,
            Platform = request.Platform,
            AppVersion = request.AppVersion,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            TokenExpiresAtUtc = request.TokenExpiresAt.UtcDateTime,
            Version = 1
        };

        _db.FeedbackSubmissions.Add(row);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new FeedbackClaimResult(FeedbackClaimOutcome.Won, row);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();

            var winner = await ReadAsync(request.Jti, request.UserProfileId, cancellationToken)
                .ConfigureAwait(false);

            if (winner is null)
            {
                // The key is taken but not by a row this owner can see. For a 128-bit nonce inside
                // a signed payload that should be unreachable, and "unreachable" is exactly when a
                // system must not improvise: refusing costs one submission, guessing costs a
                // duplicate public issue or a cross-owner disclosure.
                _logger.LogWarning(
                    "[Feedback] Claim insert collided with a row this owner cannot read — refusing.");
                return new FeedbackClaimResult(FeedbackClaimOutcome.InDoubt, null);
            }

            return Classify(winner);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Bounded on purpose. Waiting forever would hold a request open for as long as GitHub is slow,
    /// and waiting not at all would answer "in doubt" to a caller whose sibling request is about to
    /// succeed — which is the common case for a double-click, and which would tell the learner
    /// their report failed while it was in fact being filed.
    /// </para>
    /// <para>
    /// If the wait elapses with the row still claimed, the answer stays <c>InDoubt</c>. That is
    /// honest: at that moment nobody knows whether an issue exists.
    /// </para>
    /// </remarks>
    public async Task<FeedbackClaimResult> WaitForSettlementAsync(
        string jti, string userProfileId, CancellationToken cancellationToken = default)
    {
        var deadline = _time.GetUtcNow() + _options.ReplayWait;

        while (true)
        {
            var row = await ReadAsync(jti, userProfileId, cancellationToken).ConfigureAwait(false);

            if (row is null)
            {
                return new FeedbackClaimResult(FeedbackClaimOutcome.InDoubt, null);
            }

            if (row.Status != FeedbackSubmissionStatus.Claimed)
            {
                return Classify(row);
            }

            if (_time.GetUtcNow() >= deadline || cancellationToken.IsCancellationRequested)
            {
                return new FeedbackClaimResult(FeedbackClaimOutcome.InDoubt, row);
            }

            await Task.Delay(_options.ReplayPollInterval, _time, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<bool> SettleSubmittedAsync(
        string jti,
        string userProfileId,
        int readVersion,
        int issueNumber,
        string issueUrl,
        string issueTitle,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        // Conditional on the claimed status and the version this caller holds, so a settle can only
        // ever move the row it claimed, from the state it claimed it in.
        var rows = await _db.FeedbackSubmissions
            .Where(s => s.Jti == jti
                        && s.UserProfileId == userProfileId
                        && s.Status == FeedbackSubmissionStatus.Claimed
                        && s.Version == readVersion)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(s => s.Status, FeedbackSubmissionStatus.Submitted)
                    .SetProperty(s => s.IssueNumber, issueNumber)
                    .SetProperty(s => s.IssueUrl, issueUrl)
                    .SetProperty(s => s.IssueTitle, issueTitle)
                    .SetProperty(s => s.UpdatedAtUtc, now)
                    .SetProperty(s => s.Version, readVersion + 1),
                cancellationToken)
            .ConfigureAwait(false);

        if (rows == 1)
        {
            return true;
        }

        _logger.LogError(
            "[Feedback] Settle matched no claimed row; the issue exists but its receipt was not "
            + "recorded. Code={FailureCode}",
            FeedbackFailureCodes.ClaimContended);

        return false;
    }

    /// <inheritdoc />
    public Task MarkFailedAsync(
        string jti,
        string userProfileId,
        int readVersion,
        string failureCode,
        CancellationToken cancellationToken = default) =>
        CloseAsync(jti, userProfileId, readVersion, FeedbackSubmissionStatus.Failed, failureCode, cancellationToken);

    /// <inheritdoc />
    public Task MarkCommittedAsync(
        string jti,
        string userProfileId,
        int readVersion,
        string failureCode,
        CancellationToken cancellationToken = default) =>
        CloseAsync(jti, userProfileId, readVersion, FeedbackSubmissionStatus.Committed, failureCode, cancellationToken);

    /// <summary>
    /// Moves a claimed row to a terminal state, best effort.
    /// </summary>
    /// <remarks>
    /// Never throws. If this write fails the row stays <c>Claimed</c>, which every later submission
    /// already refuses — so failing to record the outcome degrades the operator's information and
    /// nothing else. Letting the exception out would replace the failure the caller is already
    /// handling with a less informative one.
    /// </remarks>
    private async Task CloseAsync(
        string jti,
        string userProfileId,
        int readVersion,
        FeedbackSubmissionStatus status,
        string failureCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;

            var rows = await _db.FeedbackSubmissions
                .Where(s => s.Jti == jti
                            && s.UserProfileId == userProfileId
                            && s.Status == FeedbackSubmissionStatus.Claimed
                            && s.Version == readVersion)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(s => s.Status, status)
                        .SetProperty(s => s.FailureCode, failureCode)
                        .SetProperty(s => s.UpdatedAtUtc, now)
                        .SetProperty(s => s.Version, readVersion + 1),
                    cancellationToken)
                .ConfigureAwait(false);

            if (rows == 0)
            {
                _logger.LogWarning(
                    "[Feedback] Could not close a claim as {Status}; it had already moved. "
                    + "Code={FailureCode}",
                    status,
                    failureCode);
            }
        }
        catch (Exception ex)
        {
            // Content-free: the exception type and the closed code, never the row's owner.
            _db.ChangeTracker.Clear();
            _logger.LogError(
                "[Feedback] Closing a claim as {Status} failed with {ExceptionType}. The row stays "
                + "in doubt and refuses every retry. Code={FailureCode}",
                status,
                ex.GetType().Name,
                failureCode);
        }
    }

    private async Task<FeedbackSubmission?> ReadAsync(
        string jti, string userProfileId, CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();

        return await _db.FeedbackSubmissions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                s => s.Jti == jti && s.UserProfileId == userProfileId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a stored row to what the caller may do about it.
    /// </summary>
    /// <remarks>
    /// The default arm is <see cref="FeedbackClaimOutcome.InDoubt"/>, so a status added without
    /// being classified refuses the call rather than permitting it. Failing closed is the only
    /// acceptable default when the failure mode is an irreversible public disclosure.
    /// </remarks>
    private static FeedbackClaimResult Classify(FeedbackSubmission row) => row.Status switch
    {
        FeedbackSubmissionStatus.Submitted => new(FeedbackClaimOutcome.AlreadySettled, row),
        FeedbackSubmissionStatus.Failed => new(FeedbackClaimOutcome.ClosedWithoutIssue, row),
        FeedbackSubmissionStatus.Claimed => new(FeedbackClaimOutcome.InDoubt, row),
        FeedbackSubmissionStatus.Committed => new(FeedbackClaimOutcome.InDoubt, row),
        _ => new(FeedbackClaimOutcome.InDoubt, row)
    };
}
