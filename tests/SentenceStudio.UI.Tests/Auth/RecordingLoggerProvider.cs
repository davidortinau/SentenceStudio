using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.UI.Tests.Auth;

/// <summary>
/// Records every surface a structured logging sink would ship: the rendered message, the
/// structured state as key/value pairs, the enclosing scopes, and the exception.
/// </summary>
/// <remarks>
/// The existing <c>CapturingLoggerProvider</c> in the unit-test project returns a no-op scope and
/// keeps only the rendered text. That is enough for the tests it was written for, and not enough
/// here: a leak in this area is most likely to be a template that renders a masked value while
/// passing the raw one as its argument, and the rendered string is exactly the surface that would
/// look clean while the state dictionary carried the address to storage.
/// </remarks>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<RecordedLog> _entries = new();
    private readonly AsyncLocal<Scope?> _current = new();

    public IReadOnlyList<RecordedLog> Entries => _entries.ToArray();

    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

    public void Dispose() { }

    private IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        var scope = new Scope(this, state, _current.Value);
        _current.Value = scope;
        return scope;
    }

    private IReadOnlyList<string> CurrentScopes()
    {
        var scopes = new List<string>();
        for (var scope = _current.Value; scope is not null; scope = scope.Parent)
        {
            scopes.Add(Describe(scope.State));
        }

        scopes.Reverse();
        return scopes;
    }

    /// <summary>
    /// Renders a state object the way a structured sink would: every key and value of a
    /// key/value sequence, not just <c>ToString</c>, because <c>ToString</c> on
    /// <c>FormattedLogValues</c> returns the formatted message and hides the arguments.
    /// </summary>
    private static string Describe(object? state)
    {
        if (state is null)
        {
            return string.Empty;
        }

        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            var builder = new StringBuilder();
            foreach (var pair in pairs)
            {
                builder.Append(pair.Key).Append('=').Append(pair.Value).Append("; ");
            }

            return builder.ToString();
        }

        return state.ToString() ?? string.Empty;
    }

    private sealed class Scope : IDisposable
    {
        private readonly RecordingLoggerProvider _owner;

        public Scope(RecordingLoggerProvider owner, object state, Scope? parent)
        {
            _owner = owner;
            State = state;
            Parent = parent;
        }

        public object State { get; }

        public Scope? Parent { get; }

        public void Dispose() => _owner._current.Value = Parent;
    }

    private sealed class Recorder(RecordingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => owner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            owner._entries.Enqueue(new RecordedLog(
                logLevel,
                category,
                formatter(state, exception),
                Describe(state),
                owner.CurrentScopes(),
                exception?.ToString() ?? string.Empty));
        }
    }
}

/// <summary>
/// One log record, split into the surfaces a sink persists separately.
/// </summary>
internal sealed record RecordedLog(
    LogLevel Level,
    string Category,
    string Rendered,
    string State,
    IReadOnlyList<string> Scopes,
    string Exception)
{
    /// <summary>
    /// Every persisted surface, named, so an assertion failure says which one leaked rather than
    /// only that something did.
    /// </summary>
    public IEnumerable<(string Surface, string Text)> Surfaces()
    {
        yield return ("rendered message", Rendered);
        yield return ("structured state", State);
        yield return ("exception", Exception);
        yield return ("category", Category);

        for (var i = 0; i < Scopes.Count; i++)
        {
            yield return ($"scope[{i}]", Scopes[i]);
        }
    }
}
