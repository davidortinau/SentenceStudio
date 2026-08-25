namespace SentenceStudio.Api.Coach.Persistence.Cleanup;

/// <summary>
/// Decides which expired sessions a cleanup pass is allowed to delete.
/// </summary>
/// <remarks>
/// <para>
/// Session expiry and conversation retention are two different clocks, and conflating them is
/// how a retention job quietly destroys a learner's history. A <c>CoachSession</c> is a
/// <b>checkpoint</b>: it holds the resumable agent state and it is meant to age out. Durable
/// conversation history is a separate, learner-visible record with its own retention rules.
/// </para>
/// <para>
/// This hook keeps the two apart. The default implementation deletes every expired checkpoint,
/// which is exactly right while checkpoints are all that exist. When the history lane lands its
/// ledger, it registers a filter that holds back any session a ledger entry still depends on —
/// without that lane having to modify the cleanup service, and without this lane having to know
/// what a ledger is.
/// </para>
/// </remarks>
public interface ICoachExpiredSessionFilter
{
    /// <summary>
    /// Returns the subset of <paramref name="expiredCandidates"/> that may be deleted now.
    /// Returning fewer rows is always safe; the skipped rows are simply reconsidered next pass.
    /// </summary>
    Task<IReadOnlyList<CoachSession>> SelectDeletableAsync(
        IReadOnlyList<CoachSession> expiredCandidates,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The pre-history default: every expired checkpoint is deletable.
/// </summary>
/// <remarks>
/// Registered with <c>TryAdd</c>, so a lane that owns ledger-aware rows can replace it. If this
/// implementation is still active once durable history exists, expired checkpoints are removed
/// and history rows are untouched — history is deleted by its own retention path, never by this
/// one.
/// </remarks>
public sealed class CheckpointOnlyExpiredSessionFilter : ICoachExpiredSessionFilter
{
    /// <inheritdoc />
    public Task<IReadOnlyList<CoachSession>> SelectDeletableAsync(
        IReadOnlyList<CoachSession> expiredCandidates,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(expiredCandidates);
}
