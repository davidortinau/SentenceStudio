using Microsoft.Extensions.Options;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// A minimal <see cref="IOptionsMonitor{TOptions}"/> whose current value can be swapped, so tests
/// can exercise a live configuration change (for example flipping the coach kill switch) without a
/// configuration provider.
/// </summary>
internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = new();
    private T _current;

    public TestOptionsMonitor(T current) => _current = current;

    public T CurrentValue => _current;

    public T Get(string? name) => _current;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new Subscription(() => _listeners.Remove(listener));
    }

    /// <summary>Replaces the current value and notifies listeners.</summary>
    public void Set(T value)
    {
        _current = value;
        foreach (var listener in _listeners.ToArray())
        {
            listener(value, Options.DefaultName);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _dispose;
        public Subscription(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }
}
