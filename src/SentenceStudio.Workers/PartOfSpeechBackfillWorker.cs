using Microsoft.Extensions.Options;
using SentenceStudio.Services.Vocabulary;

namespace SentenceStudio.Workers;

/// <summary>
/// Runs the part-of-speech backfill exactly once per process start, then stops.
/// </summary>
/// <remarks>
/// <para>
/// One-shot on purpose. The backfill is a migration-shaped chore, not a schedule: it converges on
/// zero remaining rows, and a loop would keep re-querying and re-billing a finished job. When the
/// pass ends, the worker logs a completion marker and returns; the host keeps running so the other
/// workers are unaffected.
/// </para>
/// <para>
/// It is safe to leave registered: with the feature disabled — the default — the service returns
/// before issuing any query, and this worker just logs that it had nothing to do.
/// </para>
/// </remarks>
public sealed class PartOfSpeechBackfillWorker : BackgroundService
{
    /// <summary>
    /// Delay before the pass, matching the existing worker's startup grace so migrations and
    /// dependencies are settled before the first query.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<VocabularyPartOfSpeechBackfillOptions> _options;
    private readonly ILogger<PartOfSpeechBackfillWorker> _logger;

    public PartOfSpeechBackfillWorker(
        IServiceProvider serviceProvider,
        IOptions<VocabularyPartOfSpeechBackfillOptions> options,
        ILogger<PartOfSpeechBackfillWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
    }

    /// <summary>True once the single pass has finished, whatever its outcome.</summary>
    public bool HasCompleted { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.CanRun())
        {
            // Disabled, or enabled with an empty allowlist. Either way this is a no-op and the
            // service itself is never given the chance to query.
            _logger.LogInformation(
                "Part-of-speech backfill worker: not configured to run. Skipping.");
            HasCompleted = true;
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);

            using var scope = _serviceProvider.CreateScope();
            var backfill = scope.ServiceProvider.GetRequiredService<VocabularyPartOfSpeechBackfillService>();

            var report = await backfill.RunAsync(stoppingToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Part-of-speech backfill worker complete. Outcome={Outcome} Attempted={Attempted} Updated={Updated} " +
                "Committed={Committed} Rejected={Rejected} Failed={Failed} InputTokens={InputTokens} OutputTokens={OutputTokens}",
                report.Outcome, report.WordsAttempted, report.WordsUpdated, report.BatchesCommitted,
                report.BatchesRejected, report.BatchesFailed, report.InputTokens, report.OutputTokens);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Part-of-speech backfill worker cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            // A failed chore must not take the host down with it; the other workers keep running
            // and the next process start retries from where this left off.
            _logger.LogError(ex, "Part-of-speech backfill worker failed.");
        }
        finally
        {
            HasCompleted = true;
        }
    }
}
