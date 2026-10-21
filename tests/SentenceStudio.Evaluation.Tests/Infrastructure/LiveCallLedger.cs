using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentenceStudio.Evaluation.Tests.Infrastructure;

/// <summary>
/// Retail list prices (USD per 1M tokens) used for spend estimates. These are public retail
/// estimates, not an invoice: Azure billing can differ by region, agreement, and cached-input
/// discounts (ignored here, which makes every estimate conservative).
/// Reasoning tokens are billed as output tokens and are already included in OutputTokenCount.
/// </summary>
public static class RetailPricing
{
    private static readonly Dictionary<string, (decimal InputPerMillion, decimal OutputPerMillion)> Rates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5-mini"] = (0.25m, 2.00m),
            ["gpt-5"] = (1.25m, 10.00m),
        };

    public static (decimal InputPerMillion, decimal OutputPerMillion) For(string deployment) =>
        Rates.TryGetValue(deployment, out var rate)
            ? rate
            : throw new InvalidOperationException(
                $"No retail rate recorded for deployment '{deployment}'. Add one to RetailPricing before running it, so the budget guard can bound spend.");

    public static decimal Cost(string deployment, long inputTokens, long outputTokens)
    {
        var (inRate, outRate) = For(deployment);
        return (inputTokens * inRate + outputTokens * outRate) / 1_000_000m;
    }
}

/// <summary>One live (uncached) model call.</summary>
public sealed record LiveCallRecord(
    DateTimeOffset StartedUtc,
    string ExecutionName,
    string Scenario,
    string Role,
    string Deployment,
    long InputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long CachedInputTokens,
    double LatencySeconds,
    decimal EstimatedCostUsd,
    decimal WorstCaseCostUsd,
    string? FinishReason,
    string? Error);

/// <summary>
/// Append-only ledger of every live call, persisted as JSON lines under the storage root so the
/// budget cap holds across reruns, not just within one process. Cached responses never reach the
/// ledger because the meter sits below the response cache.
/// </summary>
public sealed class LiveCallLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<LiveCallRecord> _records = new();
    private decimal _reservedUsd;

    public LiveCallLedger(string storageRoot, decimal budgetCapUsd)
    {
        BudgetCapUsd = budgetCapUsd;
        _path = Path.Combine(storageRoot, "usage", "live-calls.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path))
        {
            foreach (var line in File.ReadAllLines(_path))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _records.Add(JsonSerializer.Deserialize<LiveCallRecord>(line, JsonOptions)!);
                }
            }
        }
    }

    public decimal BudgetCapUsd { get; }

    public string LedgerPath => _path;

    public IReadOnlyList<LiveCallRecord> Records
    {
        get { lock (_gate) { return _records.ToList(); } }
    }

    public decimal SpentUsd
    {
        get { lock (_gate) { return _records.Sum(r => r.EstimatedCostUsd); } }
    }

    /// <summary>
    /// Reserves the worst-case cost of a call before it is sent. Throws, without calling the model,
    /// when recorded spend plus in-flight reservations plus this worst case would exceed the cap.
    /// </summary>
    public void Reserve(decimal worstCaseUsd, string description)
    {
        lock (_gate)
        {
            var committed = _records.Sum(r => r.EstimatedCostUsd) + _reservedUsd;
            if (committed + worstCaseUsd > BudgetCapUsd)
            {
                throw new BudgetExceededException(
                    $"Budget guard refused a live call ({description}): spent+reserved ${committed:F4} + worst case ${worstCaseUsd:F4} > cap ${BudgetCapUsd:F2}. No request was sent.");
            }

            _reservedUsd += worstCaseUsd;
        }
    }

    public void Complete(decimal reservedUsd, LiveCallRecord record)
    {
        lock (_gate)
        {
            _reservedUsd -= reservedUsd;
            _records.Add(record);
            File.AppendAllText(_path, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
        }
    }
}

public sealed class BudgetExceededException(string message) : Exception(message);
