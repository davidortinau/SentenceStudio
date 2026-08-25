using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SentenceStudio.Api.Feedback.Persistence;

/// <summary>What one retention pass removed.</summary>
public readonly record struct FeedbackRetentionResult(int SubmissionsRemoved, int RateWindowsRemoved);

/// <summary>
/// Ages out feedback rows that no longer answer for anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why removing a ledger row is safe.</b> The row's job is to answer "has this token already
/// been filed?", and a token stops being presentable when it expires. Retention therefore prunes
/// on <see cref="FeedbackSubmission.TokenExpiresAtUtc"/> plus the retention window — never on the
/// created time — so a row can only be removed long after the only token that could reach it
/// stopped verifying. Pruning on creation time would open a window where a live token's ledger
/// entry is gone and the exactly-once guarantee quietly lapses.
/// </para>
/// <para>
/// <b>Why in-doubt rows are kept.</b> A row in <c>Claimed</c> or <c>Committed</c> is the only
/// record that an issue may exist without a link, which is precisely the thing an operator needs
/// to reconcile. Those are retained on the same schedule as the rest — the point is that the
/// schedule is long enough to reconcile within, not that they are exempt — and the sweep reports
/// how many it removed so a deployment losing unreconciled rows can see it.
/// </para>
/// <para>
/// <b>No lease.</b> Both deletes are idempotent range deletes: two replicas sweeping at once
/// delete the same rows and the second finds none. A distributed lock here would add a failure
/// mode to protect a pass that cannot be harmed by concurrency.
/// </para>
/// </remarks>
public sealed class FeedbackRetentionSweep
{
    private readonly FeedbackDbContext _db;
    private readonly TimeProvider _time;
    private readonly FeedbackOptions _options;
    private readonly ILogger<FeedbackRetentionSweep> _logger;

    public FeedbackRetentionSweep(
        FeedbackDbContext db,
        TimeProvider time,
        IOptions<FeedbackOptions> options,
        ILogger<FeedbackRetentionSweep> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Runs one pass and reports the counts.</summary>
    public async Task<FeedbackRetentionResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var submissionCutoff = now - TimeSpan.FromDays(_options.RetentionDays);

        var submissions = await _db.FeedbackSubmissions
            .Where(s => s.TokenExpiresAtUtc < submissionCutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        // A rate window is disposable once every stamp it could hold has fallen out of the widest
        // window this deployment enforces. Doubling that is slack for clock skew between replicas;
        // deleting one early would hand its owner a fresh allowance.
        var widestWindow = _options.PreviewWindow > _options.SubmitWindow
            ? _options.PreviewWindow
            : _options.SubmitWindow;
        var windowCutoff = now - (widestWindow + widestWindow);

        var windows = await _db.FeedbackRateWindows
            .Where(w => w.UpdatedAtUtc < windowCutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (submissions > 0 || windows > 0)
        {
            _logger.LogInformation(
                "[Feedback] Retention removed {SubmissionCount} submission row(s) and "
                + "{RateWindowCount} rate window row(s).",
                submissions,
                windows);
        }

        return new FeedbackRetentionResult(submissions, windows);
    }
}

/// <summary>Schedules <see cref="FeedbackRetentionSweep"/> on an interval.</summary>
public sealed class FeedbackRetentionBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FeedbackOptions _options;
    private readonly ILogger<FeedbackRetentionBackgroundService> _logger;

    public FeedbackRetentionBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<FeedbackOptions> options,
        ILogger<FeedbackRetentionBackgroundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RetentionSweepEnabled)
        {
            _logger.LogInformation("[Feedback] Retention sweep is disabled on this deployment.");
            return;
        }

        using var timer = new PeriodicTimer(_options.RetentionSweepInterval);

        // Waits before the first pass, deliberately. A sweep at startup competes with migrations
        // and warm-up for connections on every replica simultaneously, and buys nothing: the rows
        // it would remove have been removable for hours. It also keeps short-lived hosts — a test
        // host, a health probe, a container that restarts — from opening a database connection
        // they were never going to need.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sweep = scope.ServiceProvider.GetRequiredService<FeedbackRetentionSweep>();
                await sweep.RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed sweep must not take the host down, and must not stop the next one.
                _logger.LogError(
                    "[Feedback] Retention sweep failed with {ExceptionType}; the next pass will retry.",
                    ex.GetType().Name);
            }
        }
    }
}
