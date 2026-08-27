using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Coach.Persistence.Deletion;

/// <summary>
/// Joins <see cref="ApplicationDbContext"/> to a coach deletion transaction when both contexts
/// address the same physical database, so one commit or one rollback covers every table the
/// erasure touches.
/// </summary>
/// <remarks>
/// <para>
/// Coach state and the legacy activity tables live in the same PostgreSQL database but in separate
/// contexts, and a context brings its own connection. Two connections mean two transactions: the
/// legacy delete commits the moment it saves, so a coach failure afterwards rolls back only the
/// coach half and the learner is told "nothing was removed" over conversations that are already
/// gone. Sharing one <see cref="DbConnection"/> is what makes a single transaction genuinely
/// available — PostgreSQL will not span two connections without two-phase commit, and nothing here
/// needs that.
/// </para>
/// </remarks>
public interface ICoachDeletionEnlistment
{
    /// <summary>
    /// Enlists the application context in <paramref name="transaction"/> when it is safe to do so.
    /// Returns an inactive result — never throws — when the two contexts address different
    /// databases, which is the coordinator's signal to fall back to explicit partial reporting.
    /// </summary>
    Task<CoachDeletionEnlistmentResult> EnlistAsync(
        DbContext coachDb,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of an enlistment attempt, and the lifetime of the context it created.
/// </summary>
public sealed class CoachDeletionEnlistmentResult : IAsyncDisposable
{
    private readonly ApplicationDbContext? _context;

    private CoachDeletionEnlistmentResult(ApplicationDbContext? context) => _context = context;

    /// <summary>The two contexts do not share a database, so nothing was enlisted.</summary>
    public static CoachDeletionEnlistmentResult NotShared { get; } = new(null);

    /// <summary>True when legacy writes can now run on the coordinator's connection and transaction.</summary>
    public bool IsActive => _context is not null;

    internal static CoachDeletionEnlistmentResult Active(ApplicationDbContext context) => new(context);

    /// <summary>
    /// Publishes the enlisted context to the current execution flow. Returns null when there is
    /// nothing to publish.
    /// </summary>
    /// <remarks>
    /// The caller has to invoke this from its own frame. <see cref="AsyncLocal{T}"/> flows to the
    /// methods a frame calls but never back to the frame that called it, so setting the ambient
    /// value inside <see cref="ICoachDeletionEnlistment.EnlistAsync"/> would be discarded the
    /// moment that async method returned — the enlistment would look active and every repository
    /// would still resolve a context of its own.
    /// </remarks>
    public IDisposable? Activate() =>
        _context is null ? null : AmbientApplicationDbContext.Use(_context);

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <inheritdoc />
public sealed class SharedConnectionCoachDeletionEnlistment : ICoachDeletionEnlistment
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SharedConnectionCoachDeletionEnlistment> _logger;

    public SharedConnectionCoachDeletionEnlistment(
        IServiceProvider services,
        ILogger<SharedConnectionCoachDeletionEnlistment> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<CoachDeletionEnlistmentResult> EnlistAsync(
        DbContext coachDb,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coachDb);
        ArgumentNullException.ThrowIfNull(transaction);

        var options = _services.GetService<DbContextOptions<ApplicationDbContext>>();
        if (options is null)
        {
            // A coach-only host. There is no legacy contributor to enlist either, so this is
            // ordinary rather than a problem.
            _logger.LogDebug(
                "[Coach] No application context is registered, so deletion runs on the coach context alone.");
            return CoachDeletionEnlistmentResult.NotShared;
        }

        var coachConnection = coachDb.Database.GetDbConnection();

        ApplicationDbContext? application = null;
        try
        {
            application = new ApplicationDbContext(WithoutRetries(options));

            if (!AddressesSameDatabase(coachConnection, application.Database.GetDbConnection()))
            {
                _logger.LogWarning(
                    "[Coach] The application and coach contexts address different databases, so account "
                    + "erasure cannot run as one transaction. Legacy deletion will be deferred until after "
                    + "the coach commit and reported as partial if it fails.");

                await application.DisposeAsync().ConfigureAwait(false);
                return CoachDeletionEnlistmentResult.NotShared;
            }

            // Swap in the coordinator's connection before the context has ever used one, then
            // enrol in its transaction. Both are the documented EF Core way to share a transaction
            // across contexts: RelationalDatabaseFacadeExtensions.SetDbConnection followed by
            // UseTransactionAsync, with the connection left in the coordinator's ownership.
            application.Database.SetDbConnection(coachConnection, contextOwnsConnection: false);
            await application.Database
                .UseTransactionAsync(transaction.GetDbTransaction(), cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "[Coach] Account erasure enlisted the application context in the coach transaction; "
                + "every table it touches now commits or rolls back together.");

            return CoachDeletionEnlistmentResult.Active(application);
        }
        catch
        {
            if (application is not null)
            {
                await application.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// The same options with retries turned off.
    /// </summary>
    /// <remarks>
    /// The application context is registered through Aspire, which enables
    /// <c>NpgsqlRetryingExecutionStrategy</c> by default. EF refuses to run any operation under a
    /// retrying strategy while a user-initiated transaction is current, because replaying a
    /// statement inside an already-aborted transaction cannot mean anything. Retrying is the
    /// caller's business, not this unit of work's, so the enlisted context gets
    /// <see cref="NonRetryingExecutionStrategy"/>. The service is replaced rather than configured
    /// because <c>ExecutionStrategy</c> is only exposed on the provider-specific options builder,
    /// and this code is deliberately provider-agnostic.
    /// </remarks>
    private static DbContextOptions<ApplicationDbContext> WithoutRetries(
        DbContextOptions<ApplicationDbContext> options) =>
        new DbContextOptionsBuilder<ApplicationDbContext>(options)
            .ReplaceService<IExecutionStrategyFactory, NonRetryingExecutionStrategyFactory>()
            .Options;

    /// <summary>
    /// Whether two connections point at the same database, decided without opening either.
    /// </summary>
    /// <remarks>
    /// Conservative on purpose. A false negative costs atomicity and is reported honestly; a false
    /// positive would run legacy deletes against the wrong database. Both parts must be present and
    /// equal, so a provider that reports nothing before opening is treated as "not shared".
    /// </remarks>
    private static bool AddressesSameDatabase(DbConnection coach, DbConnection application)
    {
        var coachSource = coach.DataSource;
        var coachDatabase = coach.Database;

        if (string.IsNullOrWhiteSpace(coachSource) || string.IsNullOrWhiteSpace(coachDatabase))
        {
            return false;
        }

        return string.Equals(coachSource, application.DataSource, StringComparison.OrdinalIgnoreCase)
            && string.Equals(coachDatabase, application.Database, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NonRetryingExecutionStrategyFactory : IExecutionStrategyFactory
    {
        private readonly ExecutionStrategyDependencies _dependencies;

        public NonRetryingExecutionStrategyFactory(ExecutionStrategyDependencies dependencies) =>
            _dependencies = dependencies;

        public IExecutionStrategy Create() => new NonRetryingExecutionStrategy(_dependencies);
    }
}
