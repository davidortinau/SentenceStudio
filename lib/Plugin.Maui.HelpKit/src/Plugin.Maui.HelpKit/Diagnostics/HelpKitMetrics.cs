using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Plugin.Maui.HelpKit.Diagnostics;

/// <summary>
/// Static entry point for HelpKit telemetry. Built on <see cref="Meter"/>
/// so any <c>System.Diagnostics.Metrics</c>-aware OpenTelemetry or
/// <c>MeterListener</c> wiring can subscribe without coupling to the
/// library.
/// </summary>
/// <remarks>
/// Counters are created lazily on first use so unknown counter names never
/// cause a hard failure.
/// </remarks>
public static class HelpKitMetrics
{
    /// <summary>Canonical meter name. Hosts can subscribe to this.</summary>
    public const string MeterName = "Plugin.Maui.HelpKit";

    public const string IngestChunks = "helpkit.ingest.chunks";
    public const string IngestErrors = "helpkit.ingest.errors";
    public const string RetrievalQueries = "helpkit.retrieval.queries";
    public const string RetrievalThresholdRefusals = "helpkit.retrieval.threshold_refusals";
    public const string LlmTokensIn = "helpkit.llm.tokens_in";
    public const string LlmTokensOut = "helpkit.llm.tokens_out";
    public const string AnswerCacheHits = "helpkit.answer_cache.hits";
    public const string AnswerCacheMisses = "helpkit.answer_cache.misses";
    public const string RateLimitRejected = "helpkit.rate_limit.rejected";

    private static readonly Meter s_meter = new(MeterName, "0.1.0-alpha");

    private static readonly ConcurrentDictionary<string, Counter<long>> s_counters =
        new(StringComparer.Ordinal);

    /// <summary>Adds <paramref name="value"/> to the named counter (lazy-created on first use).</summary>
    public static void Increment(string counter, long value = 1, params KeyValuePair<string, object?>[] tags)
    {
        if (string.IsNullOrWhiteSpace(counter)) return;
        var instrument = s_counters.GetOrAdd(counter, name => s_meter.CreateCounter<long>(name));
        if (tags is null || tags.Length == 0)
            instrument.Add(value);
        else
            instrument.Add(value, tags);
    }
}
