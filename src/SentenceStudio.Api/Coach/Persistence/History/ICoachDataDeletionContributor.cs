namespace SentenceStudio.Api.Coach.Persistence.History;

/// <summary>
/// A store that owns coach rows for a learner and can delete them on request.
/// </summary>
/// <remarks>
/// <para>
/// Discovery-based, so the deletion coordinator never holds a hand-maintained list of tables.
/// A new coach store registers one contributor and is covered from that moment; the failure mode
/// this avoids is a table added in one lane and forgotten in the deletion lane, which is exactly
/// the shape of a data-subject-request miss.
/// </para>
/// <para>
/// Implementations must be idempotent: a partially completed deletion is retried, and a second
/// run over already-deleted rows must succeed with a count of zero rather than fail.
/// </para>
/// </remarks>
public interface ICoachDataDeletionContributor
{
    /// <summary>A stable, content-free name for logs and progress reporting.</summary>
    string Name { get; }

    /// <summary>
    /// Permanently removes every row this contributor owns for <paramref name="owner"/> and
    /// returns how many were deleted. Returns zero for an empty owner rather than deleting
    /// anything.
    /// </summary>
    Task<int> DeleteAllAsync(CoachOwner owner, CancellationToken cancellationToken = default);
}

/// <summary>
/// A contributor whose rows live outside the coordinator's own <c>CoachDbContext</c>, so its
/// writes only join the coordinator's transaction when an ambient enlistment made that possible.
/// </summary>
/// <remarks>
/// <para>
/// The distinction is not cosmetic. A contributor writing through its own context commits the
/// moment it saves, which means a failure later in the pass rolls back the coach half and leaves
/// this half destroyed — and the endpoint then tells the learner nothing was removed. Declaring
/// itself here lets the coordinator either bring the contributor inside the transaction (the normal
/// case, where both contexts share a database) or defer it until after the coach commit and report
/// partial completion honestly (where they do not).
/// </para>
/// <para>
/// Implementations still have to be idempotent. Deferral only bounds the damage; a retry is what
/// finishes the job.
/// </para>
/// </remarks>
public interface ICoachExternalStoreDeletionContributor : ICoachDataDeletionContributor
{
}
