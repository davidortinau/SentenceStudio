namespace SentenceStudio.Data;

/// <summary>
/// The <see cref="ApplicationDbContext"/> an owner-scoped repository must run on for the current
/// logical operation, when a caller has already opened a unit of work the repository has to join.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="ConversationRepository"/> is a singleton that creates its own
/// DI scope for every call, so the context it resolves sits on its own connection and its
/// <c>SaveChangesAsync</c> commits on its own. That is right for ordinary use and wrong for account
/// erasure: the coach deletion coordinator runs its contributors inside a transaction on a
/// different context, so a repository that commits independently makes the erasure non-atomic. A
/// failure after that point rolls the coach work back and leaves the learner's conversations
/// destroyed, while the endpoint tells them nothing was removed. Joining the caller's unit of work
/// is what makes the rollback cover both.
/// </para>
/// <para>
/// <b>Why ambient rather than a parameter.</b> The scope the repository creates comes from the root
/// provider, so a DI-scoped hand-off cannot reach it; and threading a context through
/// <see cref="IConversationOwnerDataService"/> would push EF plumbing into a contract whose whole
/// job is to keep the ownership rules in one place. <see cref="AsyncLocal{T}"/> flows across the DI
/// scope boundary and is bounded by the <see cref="IDisposable"/> that <see cref="Use"/> returns —
/// the same mechanism <c>TransactionScope</c> uses, for the same reason. Being per-flow rather than
/// static state, it also cannot leak between tests running in parallel.
/// </para>
/// <para>
/// <b>This never changes what "owned" means.</b> The ambient context only decides which connection
/// and transaction an owner-scoped query runs on. Every predicate still comes from the repository,
/// and an unresolved owner still means "no data".
/// </para>
/// </remarks>
public static class AmbientApplicationDbContext
{
    private static readonly AsyncLocal<ApplicationDbContext?> Ambient = new();

    /// <summary>The context to join, or null when the caller has no unit of work of its own.</summary>
    public static ApplicationDbContext? Current => Ambient.Value;

    /// <summary>
    /// Publishes <paramref name="context"/> to this execution flow until the returned handle is
    /// disposed. Nesting restores the previous value rather than clearing it, so an inner scope
    /// can never silently detach an outer one.
    /// </summary>
    public static IDisposable Use(ApplicationDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var previous = Ambient.Value;
        Ambient.Value = context;
        return new Restoration(previous);
    }

    private sealed class Restoration : IDisposable
    {
        private readonly ApplicationDbContext? _previous;
        private bool _disposed;

        public Restoration(ApplicationDbContext? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = _previous;
        }
    }
}
