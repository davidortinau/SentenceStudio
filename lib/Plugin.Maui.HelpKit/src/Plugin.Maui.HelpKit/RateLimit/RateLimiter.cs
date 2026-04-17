using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plugin.Maui.HelpKit.Diagnostics;

namespace Plugin.Maui.HelpKit.RateLimit;

/// <summary>
/// Simple in-memory sliding-window rate limiter keyed by user id. Windows
/// are a rolling 60 seconds; overflow returns <c>false</c> and increments
/// the <c>helpkit.rate_limit.rejected</c> counter.
/// </summary>
/// <remarks>
/// Not shared across processes — Alpha ships a single-process app
/// experience. A distributed limiter would be a Beta concern.
/// </remarks>
internal sealed class RateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly HelpKitOptions _options;
    private readonly ILogger<RateLimiter> _logger;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _buckets = new(StringComparer.Ordinal);

    public RateLimiter(IOptions<HelpKitOptions> options, ILogger<RateLimiter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to charge the bucket for <paramref name="userKey"/>. Returns
    /// <c>false</c> when the caller has already sent
    /// <see cref="HelpKitOptions.MaxQuestionsPerMinute"/> questions in the
    /// past 60 seconds.
    /// </summary>
    public Task<bool> TryAcquireAsync(string? userKey, CancellationToken ct = default)
    {
        var key = string.IsNullOrWhiteSpace(userKey) ? "_anon" : userKey!;
        var limit = _options.MaxQuestionsPerMinute;
        if (limit <= 0) return Task.FromResult(true); // disabled

        var now = DateTime.UtcNow;
        var bucket = _buckets.GetOrAdd(key, _ => new Queue<DateTime>());

        lock (bucket)
        {
            // Evict timestamps outside the window.
            while (bucket.Count > 0 && (now - bucket.Peek()) > Window)
                bucket.Dequeue();

            if (bucket.Count >= limit)
            {
                HelpKitMetrics.Increment(HelpKitMetrics.RateLimitRejected);
                _logger.LogInformation(
                    "Rate limit hit for user '{UserKey}' ({Count}/{Limit} within 60s).",
                    key, bucket.Count, limit);
                return Task.FromResult(false);
            }

            bucket.Enqueue(now);
            return Task.FromResult(true);
        }
    }
}
