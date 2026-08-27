using SentenceStudio.Api.Coach.Persistence.History;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// An external store whose delete commits on its own connection and which then fails before it
/// can report how much it removed.
/// </summary>
/// <remarks>
/// <para>
/// This is the ordering the real contributors have. <c>LegacyConversationDeletionContributor</c>
/// calls <c>DeleteOwnedAsync</c>, which commits, and only then reads the rows back to confirm the
/// delete actually happened — because the underlying service reports a database failure as a
/// zero-row result rather than an exception. If that read throws, or the request is cancelled
/// between the commit and the return, the rows are gone and the coordinator is never told a count.
/// </para>
/// <para>
/// A coordinator that decides "was anything removed?" from counts alone therefore totals zero and
/// tells the learner nothing was removed, over data that no longer exists. That is what this fake
/// exists to reproduce.
/// </para>
/// </remarks>
internal sealed class CommittedThenFailingExternalContributor : ICoachExternalStoreDeletionContributor
{
    private readonly Func<Exception> _failure;
    private readonly int _rowsPerCall;

    public CommittedThenFailingExternalContributor(Func<Exception>? failure = null, int rowsPerCall = 3)
    {
        _failure = failure ?? (() => new InvalidOperationException("post-delete verification read failed"));
        _rowsPerCall = rowsPerCall;
    }

    public string Name => "ExternalStore";

    /// <summary>Rows this contributor has destroyed, whatever it managed to report.</summary>
    public int RowsCommitted { get; private set; }

    public int Invocations { get; private set; }

    public Task<int> DeleteAllAsync(CoachOwner owner, CancellationToken cancellationToken = default)
    {
        Invocations++;

        // The delete lands and commits here — on this store's own connection, so no rollback the
        // coordinator can perform will bring these rows back.
        RowsCommitted += _rowsPerCall;

        // And the confirmation read fails before the count can be returned.
        throw _failure();
    }
}
