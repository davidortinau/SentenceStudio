using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Telemetry;

namespace SentenceStudio.Api.Coach.Persistence.Cleanup;

/// <summary>The outcome of one scheduled cleanup attempt.</summary>
/// <param name="Ran">False when another replica held the lease; the attempt was skipped, not failed.</param>
/// <param name="Result">What the pass removed, when it ran.</param>
public readonly record struct CoachCleanupAttempt(bool Ran, CoachCleanupResult? Result);

/// <summary>
/// Runs one cleanup pass under the lease, filtered by <see cref="ICoachExpiredSessionFilter"/>.
/// </summary>
/// <remarks>
/// Split out from the hosted service so the work is testable without a timer: the hosted service
/// owns <i>when</i>, this owns <i>what</i>.
/// </remarks>
public sealed class CoachCleanupRunner
{
    private readonly ICoachCleanupLease _lease;
    private readonly CoachExpiryCleanupService _cleanup;
    private readonly ILogger<CoachCleanupRunner> _logger;

    public CoachCleanupRunner(
        ICoachCleanupLease lease,
        CoachExpiryCleanupService cleanup,
        ILogger<CoachCleanupRunner> logger)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Attempts one pass. Returns a skipped attempt when another replica holds the lease.
    /// Exceptions propagate so the caller decides the retry policy.
    /// </summary>
    public async Task<CoachCleanupAttempt> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var handle = await _lease.TryAcquireAsync(cancellationToken);

        if (handle is null)
        {
            _logger.LogDebug("[Coach] Cleanup skipped: the lease is held elsewhere.");
            return new CoachCleanupAttempt(Ran: false, Result: null);
        }

        var result = await _cleanup.RunAsync(cancellationToken);

        // Only commit once the pass has finished. An exception above leaves the handle
        // uncompleted, so disposal rolls the deletes back and releases the lock.
        await handle.CompleteAsync(cancellationToken);

        return new CoachCleanupAttempt(Ran: true, Result: result);
    }
}

/// <summary>
/// Schedules <see cref="CoachCleanupRunner"/> on an interval.
/// </summary>
/// <remarks>
/// <para>
/// Hosted in the API rather than in a separate worker because <see cref="CoachDbContext"/>,
/// its migrations, and its options all live here. A second process would need a duplicate of that
/// wiring, and the duplicate is what drifts.
/// </para>
/// <para>
/// Running in every replica is safe because the pass takes a lease first. Correctness comes from
/// the lease, not from arranging for only one replica to have the job.
/// </para>
/// </remarks>
public sealed class CoachCleanupBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<CoachCleanupOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CoachCleanupBackgroundService> _logger;

    public CoachCleanupBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<CoachCleanupOptions> options,
        TimeProvider timeProvider,
        ILogger<CoachCleanupBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startup = _options.CurrentValue;

        if (!startup.Enabled)
        {
            _logger.LogInformation("[Coach] Cleanup scheduling is disabled by configuration.");
            return;
        }

        _logger.LogInformation(
            "[Coach] Cleanup scheduled every {IntervalMinutes} minutes, first pass in {DelayMinutes} minutes.",
            startup.Interval.TotalMinutes, startup.InitialDelay.TotalMinutes);

        try
        {
            await Task.Delay(startup.InitialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var options = _options.CurrentValue;
                var delay = await RunPassAsync(options, stoppingToken);
                await Task.Delay(delay, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("[Coach] Cleanup scheduling stopped.");
    }

    /// <summary>Runs one pass and returns how long to wait before the next attempt.</summary>
    private async Task<TimeSpan> RunPassAsync(CoachCleanupOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<CoachCleanupRunner>();

            await runner.RunOnceAsync(cancellationToken);

            return options.Interval;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A retention failure must never stop the host or end the loop. Shape only: a
            // database exception can carry parameter values, and the parameters here are row
            // identifiers.
            var facts = CoachExceptionSanitizer.Describe(ex);
            _logger.LogError(
                "[Coach] Cleanup pass failed; retrying. Category={FailureCategory} InnerDepth={InnerDepth}",
                facts.Category, facts.InnerDepth);

            return options.RetryDelay;
        }
    }
}
