using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Telemetry;

namespace SentenceStudio.Api.Coach.Persistence.Deletion;

/// <summary>
/// Deletes every coach row a learner owns. Called by account deletion before the identity user
/// and the user profile are removed.
/// </summary>
/// <remarks>
/// Ordering is the point: once the profile row is gone, <c>UserProfileId</c> no longer resolves
/// to anyone and the coach rows can never be found again by any means the application offers.
/// So coach data is deleted first, and account deletion is refused if that fails.
/// </remarks>
public interface ICoachDataDeletionService
{
    /// <summary>
    /// Permanently removes all coach data for <paramref name="owner"/>.
    /// Never throws for a caller-visible failure; the report says whether it succeeded.
    /// </summary>
    Task<CoachDeletionReport> DeleteAllForOwnerAsync(CoachOwner owner, CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of a coach deletion. Content-free by construction: counts, contributor names, and
/// a failure category — never a learner identifier and never row content.
/// </summary>
/// <param name="Succeeded">
/// True only when every contributor ran and a verification pass found nothing left.
/// </param>
/// <param name="RowsDeleted">How many rows were removed in total.</param>
/// <param name="DeletesByContributor">Per-contributor counts, for operator diagnosis.</param>
/// <param name="FailureCode">A stable, content-free failure category. Null on success.</param>
/// <param name="DataWasRemoved">
/// True when rows are permanently gone, or may be — because the deletion succeeded, because a
/// partial pass committed before the failure, or because a delete outside the transaction had
/// begun and never reported what it did. False means the database is provably exactly as it was,
/// which is the only condition under which a caller may tell the learner nothing was removed.
/// </param>
public sealed record CoachDeletionReport(
    bool Succeeded,
    int RowsDeleted,
    IReadOnlyDictionary<string, int> DeletesByContributor,
    string? FailureCode,
    bool DataWasRemoved = false)
{
    /// <summary>The result for "there was no owner to delete".</summary>
    public static CoachDeletionReport NoOwner { get; } =
        new(false, 0, new Dictionary<string, int>(), "no_owner");

    /// <summary>A successful deletion.</summary>
    public static CoachDeletionReport Success(int rowsDeleted, IReadOnlyDictionary<string, int> byContributor) =>
        new(true, rowsDeleted, byContributor, null, rowsDeleted > 0);
}

/// <summary>
/// Runs every registered <see cref="ICoachDataDeletionContributor"/> in one transaction and
/// verifies the result before committing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Discovery, not a list.</b> The contributors are resolved as an enumerable, so a table added
/// in any lane is covered as soon as its store registers a contributor. A hand-maintained list
/// here would be the thing that goes stale, and the failure mode of a stale deletion list is a
/// learner who was told their data was erased when it was not.
/// </para>
/// <para>
/// <b>One transaction, across both contexts.</b> Coach state and the legacy activity tables share
/// a physical database but not a context, and a context brings its own connection. Two connections
/// mean two transactions: a contributor that writes through its own context commits the instant it
/// saves, so a failure later in the pass rolled back only the coach half while the learner's
/// conversations were already destroyed — and the endpoint then told them nothing was removed. So
/// the coordinator opens the transaction, then asks <see cref="ICoachDeletionEnlistment"/> to put
/// the application context on the same connection and in the same transaction. Every contributor
/// then commits or rolls back together.
/// </para>
/// <para>
/// <b>When one transaction is genuinely unavailable.</b> A host whose coach and application
/// contexts address different databases cannot have this without two-phase commit, which nothing
/// here needs. Rather than pretend, the coordinator defers those external-store contributors until
/// after the coach commit — so a coach failure can never destroy rows the rollback cannot restore —
/// and reports <see cref="CoachDeletionReport.DataWasRemoved"/> so the caller stops claiming
/// nothing was removed when something was. The retry is safe either way, because contributors are
/// idempotent.
/// </para>
/// <para>
/// <b>Fail closed, and verify.</b> A second pass runs after the first and must delete zero rows.
/// That turns the contributors' required idempotency into an actual check: a contributor that
/// silently skipped rows, filtered on the wrong column, or swallowed an error is caught before
/// commit rather than being reported to the learner as a successful erasure. If the verification
/// pass finds anything, the transaction rolls back and the report fails — account deletion then
/// refuses too, so the learner keeps an account that can retry rather than an orphaned data set
/// nobody can reach.
/// </para>
/// </remarks>
public sealed class CoachDataDeletionService : ICoachDataDeletionService
{
    private readonly CoachDbContext _db;
    private readonly IReadOnlyList<ICoachDataDeletionContributor> _contributors;
    private readonly ICoachDeletionEnlistment? _enlistment;
    private readonly ILogger<CoachDataDeletionService> _logger;

    public CoachDataDeletionService(
        CoachDbContext db,
        IEnumerable<ICoachDataDeletionContributor> contributors,
        ILogger<CoachDataDeletionService> logger)
        : this(db, contributors, enlistment: null, logger)
    {
    }

    public CoachDataDeletionService(
        CoachDbContext db,
        IEnumerable<ICoachDataDeletionContributor> contributors,
        ICoachDeletionEnlistment? enlistment,
        ILogger<CoachDataDeletionService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        ArgumentNullException.ThrowIfNull(contributors);
        _contributors = [.. contributors];
        _enlistment = enlistment;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<CoachDeletionReport> DeleteAllForOwnerAsync(
        CoachOwner owner,
        CancellationToken cancellationToken = default)
    {
        if (owner.IsEmpty)
        {
            _logger.LogWarning("[Coach] Deletion was requested with no owner. Nothing was deleted.");
            return CoachDeletionReport.NoOwner;
        }

        if (_contributors.Count == 0)
        {
            // Not "nothing to do" — it means the coordinator was resolved without the
            // registrations that make it correct, and reporting success would be a lie.
            _logger.LogError("[Coach] Deletion found no registered contributors. Refusing to report success.");
            return new CoachDeletionReport(false, 0, new Dictionary<string, int>(), "no_contributors");
        }

        // The relational check keeps this working on providers without transaction support; the
        // in-memory provider used by some tests is the case that would otherwise throw here.
        IDbContextTransaction? transaction = null;
        var ownsTransaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null;

        var enlistment = CoachDeletionEnlistmentResult.NotShared;
        IDisposable? ambient = null;
        var deletes = new Dictionary<string, int>(_contributors.Count, StringComparer.Ordinal);

        // Whether the transaction has already been committed. After that point a failure is a
        // partial erasure rather than a no-op, and there is nothing left to roll back.
        var committedTransaction = false;

        // Whether any contributor has been invoked at a point where no rollback covers its
        // writes. Counted separately from the per-contributor totals because a contributor that
        // commits and then throws never reports one.
        var unrecoverable = new UnrecoverableWork();

        try
        {
            if (ownsTransaction)
            {
                transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

                if (_enlistment is not null
                    && _contributors.OfType<ICoachExternalStoreDeletionContributor>().Any())
                {
                    enlistment = await _enlistment.EnlistAsync(_db, transaction, cancellationToken);

                    // Activated here, in this frame, and never inside the awaited call above:
                    // an AsyncLocal set inside an async method is discarded when it returns.
                    ambient = enlistment.Activate();
                }
            }

            // Contributors that write through another context only belong inside this transaction
            // when the enlistment put them there. Otherwise they run after the commit, where a
            // coach failure can no longer destroy rows this service cannot restore.
            var deferring = transaction is not null && !enlistment.IsActive;

            IReadOnlyList<ICoachDataDeletionContributor> deferred = deferring
                ? [.. _contributors.OfType<ICoachExternalStoreDeletionContributor>()]
                : [];

            IReadOnlyList<ICoachDataDeletionContributor> transactional = deferred.Count == 0
                ? _contributors
                : [.. _contributors.Where(contributor => contributor is not ICoachExternalStoreDeletionContributor)];

            if (deferred.Count > 0)
            {
                _logger.LogWarning(
                    "[Coach] {DeferredCount} contributor(s) write through another database and could not "
                    + "join this transaction. They will run after the coach commit, and a failure there is "
                    + "reported as a partial erasure rather than as an untouched one.",
                    deferred.Count);
            }

            // A transaction this service opened is what makes the first pass recoverable. Without
            // one — a provider with no transaction support, or a caller that brought its own and
            // owns the decision to roll it back — this service cannot promise anything it deletes
            // can be put back, and says so rather than guessing in the learner's disfavour.
            var transactionalPassIsRecoverable = transaction is not null;

            var total = await RunPassAsync(
                transactional, owner, deletes, unrecoverable, transactionalPassIsRecoverable, cancellationToken);

            // Verification pass. Contributors are required to be idempotent, so a correct
            // deletion leaves nothing for a second run to find.
            var remaining = await CountRemainingAsync(
                transactional, owner, unrecoverable, transactionalPassIsRecoverable, cancellationToken);

            if (remaining != 0)
            {
                _logger.LogError(
                    "[Coach] Deletion verification found {RemainingCount} rows still present after the " +
                    "first pass. Rolling back and refusing to report success.",
                    remaining);

                if (transaction is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return new CoachDeletionReport(
                    false,
                    total,
                    deletes,
                    "verification_failed",
                    DataWasRemoved: AnyRowsArePermanent(deletes, transaction, committedTransaction, unrecoverable));
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                committedTransaction = true;
            }

            if (deferred.Count > 0)
            {
                // Past this line a failure is a partial erasure, not a no-op, and the report says so.
                total += await RunPassAsync(
                    deferred, owner, deletes, unrecoverable, coveredByRollback: false, cancellationToken);

                var deferredRemaining = await CountRemainingAsync(
                    deferred, owner, unrecoverable, coveredByRollback: false, cancellationToken);
                if (deferredRemaining != 0)
                {
                    _logger.LogError(
                        "[Coach] Deletion verification found {RemainingCount} rows still present in a "
                        + "deferred store. The coach half is already committed, so this is a partial "
                        + "erasure.",
                        deferredRemaining);

                    return new CoachDeletionReport(
                        false,
                        total,
                        deletes,
                        "verification_failed",
                        DataWasRemoved: AnyRowsArePermanent(deletes, transaction, committedTransaction, unrecoverable));
                }
            }

            _logger.LogInformation(
                "[Coach] Deletion removed {RowCount} rows across {ContributorCount} contributors.",
                total, _contributors.Count);

            return CoachDeletionReport.Success(total, deletes);
        }
        catch (Exception ex)
        {
            if (transaction is not null && !committedTransaction)
            {
                // Best effort. A rollback failure must not replace the original cause.
                try
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                catch (Exception rollbackFailure)
                {
                    var rollbackFacts = CoachExceptionSanitizer.Describe(rollbackFailure);
                    _logger.LogError(
                        "[Coach] Deletion rollback failed. Category={FailureCategory}",
                        rollbackFacts.Category);
                }
            }

            var permanent = AnyRowsArePermanent(deletes, transaction, committedTransaction, unrecoverable);

            // Shape only: a database exception can carry parameter values, and the parameter here
            // is the learner's own identifier.
            var facts = CoachExceptionSanitizer.Describe(ex);
            _logger.LogError(
                "[Coach] Deletion failed. Category={FailureCategory} InnerDepth={InnerDepth} " +
                "DataWasRemoved={DataWasRemoved}",
                facts.Category, facts.InnerDepth, permanent);

            return new CoachDeletionReport(
                false,
                permanent ? deletes.Values.Sum() : 0,
                deletes,
                "deletion_failed",
                DataWasRemoved: permanent);
        }
        finally
        {
            // The enlisted context borrows the coordinator's connection, so it is released only
            // after the commit or rollback has run. The ambient publication goes first, so no
            // repository can resolve a context that is about to be disposed.
            ambient?.Dispose();
            await enlistment.DisposeAsync();

            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Whether any row this pass deleted is now beyond recovery. This is the only input to the
    /// caller's "nothing was removed" claim, so it errs toward saying something <em>was</em>
    /// removed rather than reassuring a learner whose data is gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three cases for the counted rows, and the middle one is the one that is easy to get wrong.
    /// With no transaction — a provider that does not support them — every delete was already
    /// permanent when it ran. With a transaction that has not been committed, a rollback puts
    /// everything back, and the deferred contributors have not started yet by construction. With a
    /// transaction that has committed, the first pass is permanent and so is anything the deferred
    /// pass managed before failing.
    /// </para>
    /// <para>
    /// Counts alone are not enough, which is why <paramref name="unrecoverable"/> exists. A
    /// deferred contributor commits its own delete and only then returns the number it removed, so
    /// anything that goes wrong in between — its own post-delete verification read failing, a
    /// cancellation, a connection dropped after the commit — destroys rows and reports nothing. A
    /// report assembled purely from counts would total zero and tell the learner nothing was
    /// removed. So the risk is recorded when the work starts rather than when it finishes.
    /// </para>
    /// </remarks>
    private static bool AnyRowsArePermanent(
        IReadOnlyDictionary<string, int> deletes,
        IDbContextTransaction? transaction,
        bool committedTransaction,
        UnrecoverableWork unrecoverable) =>
        unrecoverable.Started
        || ((transaction is null || committedTransaction) && deletes.Values.Sum() > 0);

    private static async Task<int> RunPassAsync(
        IReadOnlyList<ICoachDataDeletionContributor> contributors,
        CoachOwner owner,
        IDictionary<string, int> deletes,
        UnrecoverableWork unrecoverable,
        bool coveredByRollback,
        CancellationToken cancellationToken)
    {
        var total = 0;

        foreach (var contributor in contributors)
        {
            if (!coveredByRollback)
            {
                unrecoverable.Mark();
            }

            var deleted = await contributor.DeleteAllAsync(owner, cancellationToken);
            deletes[contributor.Name] = deleted;
            total += deleted;
        }

        return total;
    }

    private static async Task<int> CountRemainingAsync(
        IReadOnlyList<ICoachDataDeletionContributor> contributors,
        CoachOwner owner,
        UnrecoverableWork unrecoverable,
        bool coveredByRollback,
        CancellationToken cancellationToken)
    {
        var remaining = 0;

        foreach (var contributor in contributors)
        {
            // The verification pass is a delete, not a read: anything it finds, it removes. So it
            // carries exactly the same risk as the first pass and is recorded the same way.
            if (!coveredByRollback)
            {
                unrecoverable.Mark();
            }

            remaining += await contributor.DeleteAllAsync(owner, cancellationToken);
        }

        return remaining;
    }

    /// <summary>
    /// Records that a contributor was invoked where no rollback covers what it writes.
    /// </summary>
    /// <remarks>
    /// Deliberately one-way and deliberately set <em>before</em> the call. Once a delete outside a
    /// live transaction has begun, this service can no longer prove that nothing was destroyed —
    /// and "we cannot prove it" and "it did not happen" are not the same claim to make to someone
    /// asking whether their data is still there.
    /// </remarks>
    private sealed class UnrecoverableWork
    {
        public bool Started { get; private set; }

        public void Mark() => Started = true;
    }
}
