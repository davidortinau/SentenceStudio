using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.UnitTests.Logging;

/// <summary>One captured log record: level, rendered message, and structured state.</summary>
public sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    string Message,
    IReadOnlyList<KeyValuePair<string, string?>> State,
    string? Exception)
{
    /// <summary>Every string a log sink could render or index for this entry.</summary>
    public IEnumerable<string> AllText()
    {
        yield return Message;
        foreach (var pair in State)
        {
            yield return pair.Key;
            if (pair.Value is not null)
            {
                yield return pair.Value;
            }
        }
        if (Exception is not null)
        {
            yield return Exception;
        }
    }
}

/// <summary>
/// An <see cref="ILoggerProvider"/> that records rendered messages AND the
/// structured state of every log call, so privacy tests can assert an
/// identifier is absent from both the message-template output and the
/// key/value pairs a structured sink (Aspire, OpenTelemetry) would export.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<CapturedLogEntry> _entries;

        public CapturingLogger(string category, ConcurrentQueue<CapturedLogEntry> entries)
        {
            _category = category;
            _entries = entries;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var pairs = new List<KeyValuePair<string, string?>>();
            if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
            {
                foreach (var pair in structured)
                {
                    pairs.Add(new KeyValuePair<string, string?>(pair.Key, pair.Value?.ToString()));
                }
            }
            else if (state is not null)
            {
                pairs.Add(new KeyValuePair<string, string?>("{State}", state.ToString()));
            }

            _entries.Enqueue(new CapturedLogEntry(
                _category,
                logLevel,
                formatter(state, exception),
                pairs,
                exception?.ToString()));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
