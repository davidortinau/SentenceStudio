using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Api.Tests.Auth;

/// <summary>
/// Records every surface a log entry can leak through: the rendered message, the structured
/// state that ships to the sink as individual attributes, the enclosing scopes, and the
/// exception. Asserting only on the rendered message would miss a raw address that was passed
/// as a structured argument, which is the specific defect this suite exists to prevent.
/// </summary>
public sealed class AuthLogRecorder : ILoggerProvider
{
    private readonly ConcurrentQueue<RecordedAuthLog> _entries = new();
    private readonly AsyncLocal<ScopeNode?> _scope = new();

    public IReadOnlyList<RecordedAuthLog> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

    public void Dispose() { }

    private sealed class ScopeNode
    {
        public ScopeNode? Parent { get; init; }
        public object? State { get; init; }
    }

    private sealed class Recorder(AuthLogRecorder owner, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var parent = owner._scope.Value;
            owner._scope.Value = new ScopeNode { Parent = parent, State = state };
            return new Pop(owner, parent);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<string>();
            for (var node = owner._scope.Value; node is not null; node = node.Parent)
                scopes.Add(Describe(node.State));

            owner._entries.Enqueue(new RecordedAuthLog(
                category,
                logLevel,
                formatter(state, exception),
                Describe(state),
                scopes,
                exception?.ToString()));
        }

        private sealed class Pop(AuthLogRecorder owner, ScopeNode? previous) : IDisposable
        {
            public void Dispose() => owner._scope.Value = previous;
        }
    }

    /// <summary>
    /// Expands structured state into its individual key/value pairs rather than relying on
    /// <c>ToString</c>, which for <c>FormattedLogValues</c> renders the template and would hide
    /// an argument whose placeholder never made it into the message.
    /// </summary>
    private static string Describe(object? state) => state switch
    {
        null => string.Empty,
        IEnumerable<KeyValuePair<string, object?>> pairs =>
            string.Join(" | ", pairs.Select(p => $"{p.Key}={p.Value}")),
        _ => state.ToString() ?? string.Empty
    };
}

public sealed record RecordedAuthLog(
    string Category,
    LogLevel Level,
    string Message,
    string State,
    IReadOnlyList<string> Scopes,
    string? Exception)
{
    /// <summary>Every named text surface, so an assertion can name which one leaked.</summary>
    public IEnumerable<(string Name, string Text)> Surfaces()
    {
        yield return ("rendered message", Message);
        yield return ("structured state", State);
        yield return ("exception", Exception ?? string.Empty);
        for (var i = 0; i < Scopes.Count; i++)
            yield return ($"scope[{i}]", Scopes[i]);
    }
}
