using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Api.Feedback.Persistence;

/// <summary>The outcome of erasing one learner's feedback rows.</summary>
/// <param name="Succeeded">False when anything was left behind or the pass threw.</param>
/// <param name="RowsDeleted">How many rows were removed across both tables.</param>
/// <param name="FailureCode">A closed, content-free category. Null on success.</param>
public readonly record struct FeedbackDeletionReport(bool Succeeded, int RowsDeleted, string? FailureCode)
{
    /// <summary>The result for "there was no owner to delete".</summary>
    public static FeedbackDeletionReport NoOwner { get; } = new(false, 0, "no_owner");
}

/// <summary>Erases every feedback row a learner owns.</summary>
public interface IFeedbackDataDeletionService
{
    /// <summary>
    /// Permanently removes all feedback rows for <paramref name="userProfileId"/>. Never throws for
    /// a caller-visible failure; the report says whether it succeeded.
    /// </summary>
    Task<FeedbackDeletionReport> DeleteAllForOwnerAsync(
        string userProfileId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Both tables are user-scoped, so both are in scope for erasure. The rate window is included
/// deliberately even though deleting it hands a departing account a clean allowance: the account
/// is being destroyed, so there is no allowance to hand back to, and keeping a row keyed on a
/// profile id after that profile is gone is exactly the orphan an erasure request exists to
/// prevent.
/// </para>
/// <para>
/// What survives erasure is the GitHub issue, and that is not a gap this service can close. The
/// issue is public, was created at the learner's explicit request with an on-screen notice saying
/// so, and cannot be deleted by the app's credentials. The ledger row that links this learner to
/// that issue <em>is</em> removed here, so after erasure nothing in our database associates them.
/// </para>
/// </remarks>
public sealed class FeedbackDataDeletionService : IFeedbackDataDeletionService
{
    private readonly FeedbackDbContext _db;
    private readonly ILogger<FeedbackDataDeletionService> _logger;

    public FeedbackDataDeletionService(
        FeedbackDbContext db,
        ILogger<FeedbackDataDeletionService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<FeedbackDeletionReport> DeleteAllForOwnerAsync(
        string userProfileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userProfileId))
        {
            // An empty scope would make the filter vacuous and take every learner's rows.
            _logger.LogWarning("[Feedback] Erasure called with no owner — deleting nothing.");
            return FeedbackDeletionReport.NoOwner;
        }

        try
        {
            var submissions = await _db.FeedbackSubmissions
                .Where(s => s.UserProfileId == userProfileId)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            var windows = await _db.FeedbackRateWindows
                .Where(w => w.UserProfileId == userProfileId)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            // Verify rather than assume. A delete that reported rows but left some behind is the
            // failure an erasure guarantee has to catch, and the count alone cannot see it.
            var remaining = await _db.FeedbackSubmissions
                .CountAsync(s => s.UserProfileId == userProfileId, cancellationToken)
                .ConfigureAwait(false);

            remaining += await _db.FeedbackRateWindows
                .CountAsync(w => w.UserProfileId == userProfileId, cancellationToken)
                .ConfigureAwait(false);

            if (remaining > 0)
            {
                _logger.LogError(
                    "[Feedback] Erasure left {RemainingCount} row(s) behind.", remaining);
                return new FeedbackDeletionReport(false, submissions + windows, "verification_failed");
            }

            _logger.LogInformation(
                "[Feedback] Erasure removed {SubmissionCount} submission row(s) and "
                + "{RateWindowCount} rate window row(s).",
                submissions,
                windows);

            return new FeedbackDeletionReport(true, submissions + windows, null);
        }
        catch (Exception ex)
        {
            // Content-free. The exception type is operationally useful; the owner id is not ours
            // to write into a log at the moment they asked to be forgotten.
            _logger.LogError(
                "[Feedback] Erasure failed with {ExceptionType}.", ex.GetType().Name);
            return new FeedbackDeletionReport(false, 0, "delete_failed");
        }
    }
}
