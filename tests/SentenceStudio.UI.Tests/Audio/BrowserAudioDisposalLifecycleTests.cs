using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;
using SentenceStudio.Services.Timer;
using SentenceStudio.WebUI.Pages;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Audio;

public class BrowserAudioDisposalLifecycleTests
{
    public enum AudioPageKind
    {
        Reading,
        Shadowing,
        MinimalPairSession,
        NumberDrill,
        VocabularyWordEdit,
        Conversation,
        VocabQuiz
    }

    public enum StopFailureMode
    {
        SynchronousThrow,
        AsyncRejection,
        AsyncDisconnect,
        AsyncCancellation,
        UnexpectedThrow
    }

    public static IEnumerable<object[]> StopFailureCases()
    {
        foreach (var page in Enum.GetValues<AudioPageKind>())
        foreach (var failure in Enum.GetValues<StopFailureMode>())
            yield return [page, failure];
    }

    public static IEnumerable<object[]> Pages()
    {
        foreach (var page in Enum.GetValues<AudioPageKind>())
            yield return [page];
    }

    [Theory]
    [MemberData(nameof(StopFailureCases))]
    public async Task DisposeAsync_WhenBrowserStopFails_ReleasesEveryOwnedResourceOnce(
        AudioPageKind page, StopFailureMode failure)
    {
        using var fixture = new AudioPageFixture(page, failure);

        await fixture.Component.DisposeAsync();
        await fixture.Component.DisposeAsync();

        fixture.AssertReleased();
        var stopWarning = fixture.StopWarning();
        if (failure == StopFailureMode.AsyncCancellation)
            stopWarning.Error.Should().BeAssignableTo<OperationCanceledException>();
        else
            stopWarning.Error.Should().BeSameAs(fixture.Js.StopError);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task DisposeAsync_WhenCancellationCallbackThrows_StillStopsBrowserAndReleasesResources(
        AudioPageKind page)
    {
        using var fixture = new AudioPageFixture(page, StopFailureMode.SynchronousThrow);
        using var registration = fixture.PrimaryLifetime.Token.Register(
            () => throw new InvalidOperationException("Cancellation callback failed"));

        Func<Task> dispose = () => fixture.Component.DisposeAsync().AsTask();

        await dispose.Should().ThrowAsync<AggregateException>();
        await fixture.Component.DisposeAsync();
        fixture.AssertReleased();
        fixture.StopWarning().Error.Should().BeSameAs(fixture.Js.StopError);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task DisposeAsync_WhenNativeCleanupThrows_StillReleasesLaterResources(
        AudioPageKind page)
    {
        using var fixture = new AudioPageFixture(page, StopFailureMode.AsyncDisconnect);
        if (page == AudioPageKind.Reading)
            fixture.Player.ThrowOnDispose = true;
        else
            fixture.Player.ThrowOnStop = true;

        Func<Task> dispose = () => fixture.Component.DisposeAsync().AsTask();

        if (page == AudioPageKind.VocabQuiz)
            await dispose();
        else
            await dispose.Should().ThrowAsync<InvalidOperationException>();

        await fixture.Component.DisposeAsync();
        fixture.AssertReleased();
        fixture.StopWarning().Error.Should().BeSameAs(fixture.Js.StopError);
        if (page == AudioPageKind.VocabQuiz)
            fixture.Logs.Should().Contain(entry =>
                entry.Level == LogLevel.Warning && entry.Message.Contains("native quiz audio"));
    }

    [Fact]
    public async Task NumberDrill_ModuleDisconnectsAfterFailedStop_LifetimeStillDisposed()
    {
        using var fixture = new AudioPageFixture(
            AudioPageKind.NumberDrill, StopFailureMode.AsyncRejection);
        var moduleFailure = new JSDisconnectedException("Module circuit disconnected");
        fixture.Modules.Single().DisposeError = moduleFailure;
        fixture.Modules.Single().RejectDisposeAsync = true;

        await fixture.Component.DisposeAsync();

        fixture.AssertReleased();
        fixture.Logs.Should().Contain(entry =>
            entry.Level == LogLevel.Warning
            && ReferenceEquals(entry.Error, moduleFailure)
            && entry.Message.Contains("number-drill browser module"));
    }

    [Fact]
    public async Task VocabQuiz_GestureModuleDisconnects_StillDisposesModalAndCancellations()
    {
        using var fixture = new AudioPageFixture(
            AudioPageKind.VocabQuiz, StopFailureMode.AsyncCancellation);
        var moduleFailure = new JSDisconnectedException("Gesture module circuit disconnected");
        fixture.Modules[0].DisposeError = moduleFailure;
        fixture.Modules[0].RejectDisposeAsync = true;

        await fixture.Component.DisposeAsync();

        fixture.AssertReleased();
        fixture.Logs.Should().Contain(entry =>
            entry.Level == LogLevel.Warning
            && ReferenceEquals(entry.Error, moduleFailure)
            && entry.Message.Contains("vocabulary quiz gesture module"));
    }

    [Fact]
    public async Task VocabQuiz_NonCleanupFailureStillSurfaces_AfterResourcesAreReleased()
    {
        using var fixture = new AudioPageFixture(
            AudioPageKind.VocabQuiz, StopFailureMode.SynchronousThrow);
        var preferences = new ThrowingPreferences();
        SetMember(fixture.Component, "Preferences", preferences);
        SetMember(fixture.Component, "activeUserId", "test-user");

        Func<Task> dispose = () => fixture.Component.DisposeAsync().AsTask();

        (await dispose.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should().BeSameAs(preferences.Error);
        fixture.AssertReleased();
        fixture.StopWarning().Error.Should().BeSameAs(fixture.Js.StopError);
    }

    [Fact]
    public async Task VocabQuiz_TimerStartCancellationThrows_StillDisposesItsTokenAndLaterResources()
    {
        using var fixture = new AudioPageFixture(
            AudioPageKind.VocabQuiz, StopFailureMode.AsyncRejection);
        var timerStart = fixture.TimerStartLifetime!;
        using var registration = timerStart.Token.Register(
            () => throw new InvalidOperationException("Timer-start cancellation callback failed"));

        Func<Task> dispose = () => fixture.Component.DisposeAsync().AsTask();

        await dispose.Should().ThrowAsync<AggregateException>();
        fixture.AssertReleased();
        fixture.StopWarning().Error.Should().BeSameAs(fixture.Js.StopError);
    }

    [Fact]
    public async Task VocabQuiz_AudioCancellationThrows_StillCancelsInFlightSentenceHints()
    {
        using var fixture = new AudioPageFixture(
            AudioPageKind.VocabQuiz, StopFailureMode.AsyncDisconnect);
        using var hintRequest = new CancellationTokenSource();
        SetMember(fixture.Component, "sentenceHintLoadCts", hintRequest);
        using var registration = fixture.PrimaryLifetime.Token.Register(
            () => throw new InvalidOperationException("Audio cancellation callback failed"));

        Func<Task> dispose = () => fixture.Component.DisposeAsync().AsTask();

        await dispose.Should().ThrowAsync<AggregateException>();
        hintRequest.IsCancellationRequested.Should().BeTrue();
        GetField<CancellationTokenSource?>(fixture.Component, "sentenceHintLoadCts")
            .Should().BeNull();
        fixture.AssertReleased();
    }

    [Theory]
    [InlineData(AudioPageKind.Reading)]
    [InlineData(AudioPageKind.VocabQuiz)]
    public async Task DisposeAsync_AfterNavigationInvalidatesAudio_StillReleasesResources(
        AudioPageKind page)
    {
        using var fixture = new AudioPageFixture(page, StopFailureMode.AsyncDisconnect);
        SetMember(fixture.Component,
            page == AudioPageKind.Reading ? "_disposed" : "_audioDisposed", true);

        await fixture.Component.DisposeAsync();

        fixture.AssertReleased();
        fixture.StopWarning().Error.Should().BeSameAs(fixture.Js.StopError);
    }

    private sealed class AudioPageFixture : IDisposable
    {
        private readonly ActivityTimerService? _timer;
        private readonly List<CancellationTokenSource> _lifetimes = new();
        private readonly Action? _assertObjectReferenceDisposed;
        private readonly System.Threading.Timer? _playbackTimer;
        private readonly MemoryStream? _nativeStream;

        public AudioPageFixture(AudioPageKind page, StopFailureMode failure)
        {
            Component = page switch
            {
                AudioPageKind.Reading => new Reading(),
                AudioPageKind.Shadowing => new Shadowing(),
                AudioPageKind.MinimalPairSession => new MinimalPairSession(),
                AudioPageKind.NumberDrill => new NumberDrill(),
                AudioPageKind.VocabularyWordEdit => new VocabularyWordEdit(),
                AudioPageKind.Conversation => new Conversation(),
                AudioPageKind.VocabQuiz => new VocabQuiz(),
                _ => throw new ArgumentOutOfRangeException(nameof(page))
            };

            Js = new FailingStopJsRuntime(failure);
            SetMember(Component, "Localize", new BlazorLocalizationService());
            SetMember(Component, "JS", Js);
            SetMember(Component, "Logger", page switch
            {
                AudioPageKind.Reading => new RecordingLogger<Reading>(Logs),
                AudioPageKind.Shadowing => new RecordingLogger<Shadowing>(Logs),
                AudioPageKind.MinimalPairSession => new RecordingLogger<MinimalPairSession>(Logs),
                AudioPageKind.NumberDrill => new RecordingLogger<NumberDrill>(Logs),
                AudioPageKind.VocabularyWordEdit => new RecordingLogger<VocabularyWordEdit>(Logs),
                AudioPageKind.Conversation => new RecordingLogger<Conversation>(Logs),
                AudioPageKind.VocabQuiz => new RecordingLogger<VocabQuiz>(Logs),
                _ => throw new ArgumentOutOfRangeException(nameof(page))
            });

            if (page is AudioPageKind.Reading or AudioPageKind.Shadowing
                or AudioPageKind.NumberDrill or AudioPageKind.Conversation
                or AudioPageKind.VocabQuiz)
            {
                _timer = new ActivityTimerService();
                SetMember(Component, "ActivityTimer", _timer);
            }

            if (page == AudioPageKind.Conversation)
            {
                var request = new CancellationTokenSource();
                _lifetimes.Add(request);
                SetMember(Component, "_audioRequestCts", request);
                SetMember(Component, "_browserAudioLoaded", true);
                var reference = DotNetObjectReference.Create((Conversation)Component);
                SetMember(Component, "_audioJsRef", reference);
                _assertObjectReferenceDisposed = () =>
                {
                    Action readValue = () => { _ = reference.Value; };
                    readValue.Should().Throw<ObjectDisposedException>();
                };
            }
            else
            {
                _lifetimes.Add(GetField<CancellationTokenSource>(Component, "_audioLifetime"));
                SetMember(Component, "_browserAudioAttempted", true);
            }

            if (page == AudioPageKind.Reading)
            {
                var manager = new TimestampedAudioManager(
                    new SentenceTimingCalculator(NullLogger<SentenceTimingCalculator>.Instance),
                    new FakeAudioFactory(Player),
                    NullLogger<TimestampedAudioManager>.Instance);
                SetMember(manager, "_player", Player);
                SetMember(Component, "_audioManager", manager);
                var reference = DotNetObjectReference.Create((Reading)Component);
                SetMember(Component, "_dotNetRef", reference);
                _assertObjectReferenceDisposed = () =>
                {
                    Action readValue = () => { _ = reference.Value; };
                    readValue.Should().Throw<ObjectDisposedException>();
                };
            }
            else
            {
                _nativeStream = new MemoryStream([0x49, 0x44, 0x33]);
                GetField<UiAudioPlayerSession>(Component, "_nativeAudio")
                    .CreatePlayer(new FakeAudioFactory(Player), _nativeStream);
            }

            if (page == AudioPageKind.Shadowing)
            {
                _playbackTimer = new System.Threading.Timer(
                    _ => { }, null, Timeout.Infinite, Timeout.Infinite);
                _playbackTimer.Change(Timeout.Infinite, Timeout.Infinite).Should().BeTrue();
                SetMember(Component, "_playbackTimer", _playbackTimer);
            }

            if (page == AudioPageKind.NumberDrill)
            {
                var module = new RecordingJsModule();
                Modules.Add(module);
                SetMember(Component, "jsModule", module);
            }

            if (page == AudioPageKind.VocabQuiz)
            {
                var autoAdvance = new CancellationTokenSource();
                var timerStart = new CancellationTokenSource();
                _lifetimes.AddRange([autoAdvance, timerStart]);
                SetMember(Component, "autoAdvanceCts", autoAdvance);
                SetMember(Component, "activityTimerStartCts", timerStart);
                var gesture = new RecordingJsModule();
                var modal = new RecordingJsModule();
                Modules.AddRange([gesture, modal]);
                SetMember(Component, "gestureModule", gesture);
                SetMember(Component, "modalModule", modal);
            }
        }

        public IAsyncDisposable Component { get; }
        public FailingStopJsRuntime Js { get; }
        public TrackingAudioPlayer Player { get; } = new();
        public List<LogEntry> Logs { get; } = new();
        public List<RecordingJsModule> Modules { get; } = new();
        public CancellationTokenSource PrimaryLifetime => _lifetimes[0];
        public CancellationTokenSource? TimerStartLifetime =>
            _lifetimes.Count > 2 ? _lifetimes[2] : null;

        public LogEntry StopWarning() => Logs.Should().ContainSingle(entry =>
                entry.Level == LogLevel.Warning
                && entry.Message.Contains("stop", StringComparison.OrdinalIgnoreCase)
                && entry.Message.Contains("browser", StringComparison.OrdinalIgnoreCase))
            .Which;

        public void AssertReleased()
        {
            Js.StopCalls.Should().Be(1);
            Player.DisposeCalls.Should().Be(1);
            if (_nativeStream is not null)
                _nativeStream.CanRead.Should().BeFalse();
            foreach (var lifetime in _lifetimes)
            {
                lifetime.IsCancellationRequested.Should().BeTrue();
                Action readToken = () => { _ = lifetime.Token; };
                readToken.Should().Throw<ObjectDisposedException>();
            }

            _assertObjectReferenceDisposed?.Invoke();
            foreach (var module in Modules)
                module.DisposeCalls.Should().Be(1);
            if (_playbackTimer is not null)
            {
                GetField<System.Threading.Timer?>(Component, "_playbackTimer")
                    .Should().BeNull();
                _playbackTimer.Change(Timeout.Infinite, Timeout.Infinite)
                    .Should().BeFalse("Shadowing must dispose its playback timer");
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _nativeStream?.Dispose();
            _playbackTimer?.Dispose();
        }
    }

    private static void SetMember(object owner, string name, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic
            | BindingFlags.Public | BindingFlags.DeclaredOnly;
        for (var type = owner.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetProperty(name, flags) is { } property)
            {
                property.SetValue(owner, value);
                return;
            }
            if (type.GetField(name, flags) is { } field)
            {
                field.SetValue(owner, value);
                return;
            }
        }
        throw new InvalidOperationException($"Missing component member: {name}");
    }

    private static T GetField<T>(object owner, string name)
    {
        for (var type = owner.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly);
            if (field is not null)
                return (T)field.GetValue(owner)!;
        }
        throw new InvalidOperationException($"Missing component field: {name}");
    }

    private sealed record LogEntry(LogLevel Level, Exception? Error, string Message);

    private sealed class RecordingLogger<T>(List<LogEntry> entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
    }

    private sealed class FailingStopJsRuntime(StopFailureMode failure) : IJSRuntime
    {
        public Exception StopError { get; } = failure switch
        {
            StopFailureMode.AsyncDisconnect => new JSDisconnectedException("Browser disconnected"),
            StopFailureMode.UnexpectedThrow => new InvalidOperationException("Unexpected JS stop failure"),
            StopFailureMode.AsyncCancellation => new TaskCanceledException("Browser stop canceled"),
            _ => new JSException("Browser rejected audioInterop.stop")
        };
        public int StopCalls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "audioInterop.stop")
                throw new InvalidOperationException($"Unexpected JS call: {identifier}");
            StopCalls++;
            return failure switch
            {
                StopFailureMode.SynchronousThrow or StopFailureMode.UnexpectedThrow => throw StopError,
                StopFailureMode.AsyncRejection or StopFailureMode.AsyncDisconnect =>
                    new ValueTask<TValue>(Task.FromException<TValue>(StopError)),
                StopFailureMode.AsyncCancellation =>
                    new ValueTask<TValue>(Task.FromCanceled<TValue>(new CancellationToken(true))),
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            };
        }
    }

    private sealed class RecordingJsModule : IJSObjectReference
    {
        public Exception? DisposeError { get; set; }
        public bool RejectDisposeAsync { get; set; }
        public int DisposeCalls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (DisposeError is not null)
            {
                if (RejectDisposeAsync)
                    return new ValueTask(Task.FromException(DisposeError));
                throw DisposeError;
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAudioFactory(TrackingAudioPlayer player) : IAudioPlayerFactory
    {
        public bool CanPlayLocally => true;
        public IUiAudioPlayer? CreatePlayer(Stream audioStream) => player;
    }

    private sealed class TrackingAudioPlayer : IUiAudioPlayer
    {
        public event EventHandler? PlaybackEnded;
        public bool IsPlaying => false;
        public double CurrentPosition => 0;
        public double Duration => 1;
        public int DisposeCalls { get; private set; }
        public bool ThrowOnStop { get; set; }
        public bool ThrowOnDispose { get; set; }

        public void Play() { }
        public void Pause() { }
        public void Seek(double seconds) { }
        public void End() => PlaybackEnded?.Invoke(this, EventArgs.Empty);
        public void Stop()
        {
            if (ThrowOnStop)
                throw new InvalidOperationException("Native player stop failed");
        }
        public void Dispose()
        {
            DisposeCalls++;
            if (ThrowOnDispose)
                throw new InvalidOperationException("Native player disposal failed");
        }
    }

    private sealed class ThrowingPreferences : IPreferencesService
    {
        public InvalidOperationException Error { get; } = new("Preference read failed");
        public T Get<T>(string key, T defaultValue) => throw Error;
        public void Set<T>(string key, T value) { }
        public void Remove(string key) { }
        public void Clear() { }
    }
}
