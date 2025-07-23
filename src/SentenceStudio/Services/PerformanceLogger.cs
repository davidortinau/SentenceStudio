using System.Diagnostics;

namespace SentenceStudio.Services;

/// <summary>
/// Performance logger specifically for diagnosing UI rendering performance
/// in the Reading Page sentence highlighting system
/// </summary>
public class PerformanceLogger
{
    private static readonly Dictionary<string, Stopwatch> _activeTimers = new();
    private static readonly Dictionary<string, List<double>> _measurements = new();
    
    /// <summary>
    /// Start timing an operation
    /// </summary>
    public static void StartTimer(string operationName)
    {
        if (_activeTimers.ContainsKey(operationName))
        {
            _activeTimers[operationName].Restart();
        }
        else
        {
            _activeTimers[operationName] = Stopwatch.StartNew();
        }
    }
    
    /// <summary>
    /// End timing and log the result
    /// </summary>
    public static void EndTimer(string operationName, double warningThresholdMs = 10.0)
    {
        if (!_activeTimers.TryGetValue(operationName, out var stopwatch))
        {
            Debug.WriteLine($"⚠️ PERF: No active timer for '{operationName}'");
            return;
        }
        
        stopwatch.Stop();
        var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
        
        // Store measurement
        if (!_measurements.ContainsKey(operationName))
        {
            _measurements[operationName] = new List<double>();
        }
        _measurements[operationName].Add(elapsedMs);
        
        // Log result with appropriate emoji based on performance
        var emoji = elapsedMs <= warningThresholdMs ? "✅" : "⚠️";
        var perfLevel = elapsedMs switch
        {
            <= 1.0 => "EXCELLENT",
            <= 5.0 => "GOOD",
            <= 10.0 => "OK",
            <= 50.0 => "SLOW",
            _ => "VERY SLOW"
        };
        
        Debug.WriteLine($"{emoji} PERF [{perfLevel}]: {operationName} = {elapsedMs:F1}ms");
        
        // Show statistics every 10 measurements
        var measurements = _measurements[operationName];
        if (measurements.Count % 10 == 0)
        {
            var avg = measurements.Average();
            var min = measurements.Min();
            var max = measurements.Max();
            Debug.WriteLine($"📊 PERF STATS: {operationName} (last 10) - Avg: {avg:F1}ms, Min: {min:F1}ms, Max: {max:F1}ms");
        }
    }
    
    /// <summary>
    /// Time a synchronous operation
    /// </summary>
    public static T Time<T>(string operationName, Func<T> operation, double warningThresholdMs = 10.0)
    {
        StartTimer(operationName);
        try
        {
            return operation();
        }
        finally
        {
            EndTimer(operationName, warningThresholdMs);
        }
    }
    
    /// <summary>
    /// Time a synchronous void operation
    /// </summary>
    public static void Time(string operationName, Action operation, double warningThresholdMs = 10.0)
    {
        StartTimer(operationName);
        try
        {
            operation();
        }
        finally
        {
            EndTimer(operationName, warningThresholdMs);
        }
    }
    
    /// <summary>
    /// Get performance summary for all operations
    /// </summary>
    public static string GetPerformanceSummary()
    {
        if (!_measurements.Any())
        {
            return "No performance measurements recorded.";
        }
        
        var summary = new System.Text.StringBuilder();
        summary.AppendLine("🏴‍☠️ Performance Summary:");
        summary.AppendLine("======================");
        
        foreach (var (operation, measurements) in _measurements.OrderBy(x => x.Key))
        {
            if (measurements.Any())
            {
                var avg = measurements.Average();
                var min = measurements.Min();
                var max = measurements.Max();
                var count = measurements.Count;
                
                var rating = avg switch
                {
                    <= 1.0 => "🚀 EXCELLENT",
                    <= 5.0 => "✅ GOOD",
                    <= 10.0 => "⚠️ OK",
                    <= 50.0 => "🐌 SLOW",
                    _ => "🔥 VERY SLOW"
                };
                
                summary.AppendLine($"{rating} {operation}:");
                summary.AppendLine($"  Count: {count}, Avg: {avg:F1}ms, Min: {min:F1}ms, Max: {max:F1}ms");
            }
        }
        
        return summary.ToString();
    }
    
    /// <summary>
    /// Reset all measurements
    /// </summary>
    public static void Reset()
    {
        _activeTimers.Clear();
        _measurements.Clear();
        Debug.WriteLine("🔄 PERF: All measurements reset");
    }
}
