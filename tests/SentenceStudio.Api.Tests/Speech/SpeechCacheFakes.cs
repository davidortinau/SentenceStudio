using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Speech;
using SentenceStudio.Contracts.Speech;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>
/// Stands in for ElevenLabs. Every call returns distinct bytes ("clip-1:", "clip-2:", ...), so a
/// test can tell a stored clip from a freshly generated one.
/// </summary>
internal sealed class FakeSpeechClipProvider : ISpeechClipProvider
{
    private int _calls;

    public ConcurrentQueue<SpeechClipSpec> Requests { get; } = new();

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>When set, every call throws it (a paid call that failed).</summary>
    public Exception? FailWith { get; set; }

    public bool ReturnEmptyAudio { get; set; }

    public bool ReturnNoTimings { get; set; }

    /// <summary>Optional delay so concurrent requests overlap.</summary>
    public TimeSpan Delay { get; set; }

    public void Reset()
    {
        Interlocked.Exchange(ref _calls, 0);
        Requests.Clear();
        FailWith = null;
        ReturnEmptyAudio = false;
        ReturnNoTimings = false;
        Delay = TimeSpan.Zero;
    }

    public async Task<byte[]> GenerateAsync(SpeechClipSpec spec, CancellationToken cancellationToken)
    {
        var call = await BeginAsync(spec, cancellationToken);
        return ReturnEmptyAudio ? [] : Encoding.UTF8.GetBytes($"clip-{call}:{spec.VoiceId}");
    }

    public async Task<TimestampedSpeechClip> GenerateTimestampedAsync(
        SpeechClipSpec spec,
        CancellationToken cancellationToken)
    {
        var call = await BeginAsync(spec, cancellationToken);
        var audio = ReturnEmptyAudio ? [] : Encoding.UTF8.GetBytes($"timed-{call}:{spec.VoiceId}");
        var characters = ReturnNoTimings
            ? new List<CharacterTimestamp>()
            : spec.Text.Select((c, i) => new CharacterTimestamp
            {
                Char = c.ToString(),
                StartMs = i * 87.5 + call,
                EndMs = (i + 1) * 87.5 + call
            }).ToList();
        var duration = characters.Count == 0 ? 0 : characters[^1].EndMs / 1000.0;
        return new TimestampedSpeechClip(audio, characters, duration);
    }

    private async Task<int> BeginAsync(SpeechClipSpec spec, CancellationToken cancellationToken)
    {
        Requests.Enqueue(spec);
        var call = Interlocked.Increment(ref _calls);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (FailWith is not null)
        {
            throw FailWith;
        }

        return call;
    }
}

/// <summary>Keeps formatted log lines so tests can assert what was (and was not) logged.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception), exception));
}

/// <summary>An in-memory blob container with switchable faults. Write-if-absent like the real store.</summary>
internal sealed class InMemorySpeechClipStore : ISpeechClipStore
{
    private int _reads;
    private int _writes;

    public ConcurrentDictionary<string, StoredBlob> Blobs { get; } = new();

    public int Reads => Volatile.Read(ref _reads);

    public int Writes => Volatile.Read(ref _writes);

    public Exception? ReadFailure { get; set; }

    public Exception? WriteFailure { get; set; }

    /// <summary>Reads never complete until cancelled (an unreachable store).</summary>
    public bool HangReads { get; set; }

    /// <summary>Writes never complete until cancelled (a store that accepts the connection and stalls).</summary>
    public bool HangWrites { get; set; }

    public List<bool> OverwriteFlags { get; } = [];

    public void Reset()
    {
        Blobs.Clear();
        Interlocked.Exchange(ref _reads, 0);
        Interlocked.Exchange(ref _writes, 0);
        ReadFailure = null;
        WriteFailure = null;
        HangReads = false;
        HangWrites = false;
        lock (OverwriteFlags)
        {
            OverwriteFlags.Clear();
        }
    }

    public async Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        if (HangReads)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        return Blobs.TryGetValue(name, out var blob) ? blob.Content.ToArray() : null;
    }

    public async Task<bool> WriteAsync(
        string name,
        byte[] content,
        string contentType,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _writes);
        lock (OverwriteFlags)
        {
            OverwriteFlags.Add(overwrite);
        }

        if (HangWrites)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (WriteFailure is not null)
        {
            throw WriteFailure;
        }

        var blob = new StoredBlob(content.ToArray(), contentType);
        if (overwrite)
        {
            Blobs[name] = blob;
            return true;
        }

        return Blobs.TryAdd(name, blob);
    }

    internal sealed record StoredBlob(byte[] Content, string ContentType);
}
