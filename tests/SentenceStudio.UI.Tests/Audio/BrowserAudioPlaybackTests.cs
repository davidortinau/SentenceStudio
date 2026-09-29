using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Audio;

public class BrowserAudioPlaybackTests
{
    private static readonly byte[] AudioBytes = { 0x49, 0x44, 0x33 };
    private static readonly ILogger Logger = NullLogger.Instance;

    [Fact]
    public async Task PlayAsync_UsesAtomicBrowserStartup()
    {
        var js = new RecordingJsRuntime();

        await BrowserAudioPlayback.PlayAsync(js, AudioBytes, logger: Logger);

        js.Calls.Select(call => call.Name).Should().Equal(
            "audioInterop.loadFromBase64", "audioInterop.startPlayback");
        js.Calls[0].Arguments.Should().Equal(
            Convert.ToBase64String(AudioBytes), "audio/mpeg");
        js.Calls[1].Arguments.Should().Equal(null, 1000);
    }

    [Fact]
    public async Task PlayAsync_LoadFailureDoesNotPretendAudioPlayed()
    {
        var js = new RecordingJsRuntime { Duration = 0 };

        Func<Task> play = () => BrowserAudioPlayback.PlayAsync(js, AudioBytes, logger: Logger);

        await play.Should().ThrowAsync<InvalidOperationException>();
        js.Calls.Select(call => call.Name).Should().Equal(
            "audioInterop.loadFromBase64", "audioInterop.stop");
    }

    [Fact]
    public async Task PlayAsync_EmptyAudioFailsWithoutStartingBrowserPlayback()
    {
        var js = new RecordingJsRuntime();

        Func<Task> play = () => BrowserAudioPlayback.PlayAsync(js, Array.Empty<byte>(), logger: Logger);

        await play.Should().ThrowAsync<InvalidOperationException>();
        js.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PlayAsync_RejectedPlaybackDoesNotPretendAudioPlayed()
    {
        var js = new RecordingJsRuntime { Started = false };

        Func<Task> play = () => BrowserAudioPlayback.PlayAsync(js, AudioBytes, logger: Logger);

        await play.Should().ThrowAsync<InvalidOperationException>();
        js.Calls.Select(call => call.Name).Should().Equal(
            "audioInterop.loadFromBase64", "audioInterop.startPlayback", "audioInterop.stop");
    }

    [Fact]
    public async Task PlayAsync_PropagatesJsFailuresAfterStoppingTheBrowserPlayer()
    {
        var js = new RecordingJsRuntime { ThrowOn = "audioInterop.startPlayback" };

        Func<Task> play = () => BrowserAudioPlayback.PlayAsync(js, AudioBytes, logger: Logger);

        await play.Should().ThrowAsync<JSException>();
        js.Calls.Select(call => call.Name).Should().Equal(
            "audioInterop.loadFromBase64", "audioInterop.startPlayback", "audioInterop.stop");
    }

    [Fact]
    public async Task PlayAndTrackAsync_RegistersEndCallbackOnTheBrowserPlayer()
    {
        var js = new RecordingJsRuntime();
        using var callbacks = DotNetObjectReference.Create(new AudioCallbacks());

        await BrowserAudioPlayback.PlayAndTrackAsync(js, AudioBytes, callbacks, logger: Logger);

        js.Calls.Select(call => call.Name).Should().Equal(
            "audioInterop.loadFromBase64", "audioInterop.startPlayback");
        js.Calls[1].Arguments[0].Should().BeSameAs(callbacks);
        js.Calls[1].Arguments[1].Should().Be(1000);
    }

    [Fact]
    public async Task PlayAndTrackAsync_ShortClipEndsBeforeStartupReturns_StillSucceeds()
    {
        var listener = new AudioCallbacks();
        using var callbacks = DotNetObjectReference.Create(listener);
        var js = new RecordingJsRuntime
        {
            OnInvoke = name =>
            {
                if (name == "audioInterop.startPlayback")
                    listener.OnJsPlaybackEnded();
            }
        };

        await BrowserAudioPlayback.PlayAndTrackAsync(js, AudioBytes, callbacks, logger: Logger);

        listener.EndedCalls.Should().Be(1);
        js.Calls.Select(call => call.Name).Should().Equal(
            "audioInterop.loadFromBase64", "audioInterop.startPlayback");
    }

    [Fact]
    public async Task FailedPlaybackAndCleanup_LogsCleanupButPreservesOriginalFailure()
    {
        var js = new RecordingJsRuntime { ThrowOn = "audioInterop.startPlayback", FailStop = true };
        var logger = new RecordingLogger();

        Func<Task> play = () => BrowserAudioPlayback.PlayAsync(js, AudioBytes, logger: logger);

        await play.Should().ThrowAsync<JSException>().WithMessage("*startPlayback*");
        logger.Warnings.Should().ContainSingle()
            .Which.Message.Should().Contain("audioInterop.stop");
        js.Calls.Select(call => call.Name).Should().Equal(
            "audioInterop.loadFromBase64", "audioInterop.startPlayback", "audioInterop.stop");
    }

    [Fact]
    public async Task LegacyCaller_WithoutLogger_ExposesBothPlaybackAndCleanupFailures()
    {
        var js = new RecordingJsRuntime { ThrowOn = "audioInterop.startPlayback", FailStop = true };

        Func<Task> play = () => BrowserAudioPlayback.PlayAsync(js, AudioBytes);

        var failure = await play.Should().ThrowAsync<AggregateException>();
        failure.Which.InnerExceptions.Select(error => error.Message).Should().Equal(
            "Interop failed: audioInterop.startPlayback", "Interop failed: audioInterop.stop");
    }

    [Fact]
    public async Task CanceledLoad_DoesNotStartAudioOrStopANewerRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var js = new RecordingJsRuntime
        {
            OnInvoke = name =>
            {
                if (name == "audioInterop.loadFromBase64")
                    cancellation.Cancel();
            }
        };

        Func<Task> play = () => BrowserAudioPlayback.PlayAsync(js, AudioBytes, cancellation.Token, Logger);

        await play.Should().ThrowAsync<OperationCanceledException>();
        js.Calls.Select(call => call.Name).Should().Equal("audioInterop.loadFromBase64");
    }

    private sealed class AudioCallbacks
    {
        public int EndedCalls { get; private set; }

        [JSInvokable]
        public void OnJsPlaybackEnded() => EndedCalls++;
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public double Duration { get; init; } = 2.5;
        public bool Started { get; init; } = true;
        public string? ThrowOn { get; init; }
        public bool FailStop { get; init; }
        public Action<string>? OnInvoke { get; init; }
        public List<(string Name, object?[] Arguments)> Calls { get; } = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add((identifier, args ?? Array.Empty<object?>()));
            OnInvoke?.Invoke(identifier);
            if (identifier == ThrowOn || (FailStop && identifier == "audioInterop.stop"))
                throw new JSException($"Interop failed: {identifier}");
            if (identifier == "audioInterop.loadFromBase64")
                return ValueTask.FromResult((TValue)(object)Duration);
            if (identifier == "audioInterop.startPlayback")
                return ValueTask.FromResult((TValue)(object)Started);

            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add((logLevel, exception?.Message ?? formatter(state, exception)));
        }
    }
}
