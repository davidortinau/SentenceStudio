using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Pages;
using SentenceStudio.WebUI.Services;
using Conversation = SentenceStudio.WebUI.Pages.Conversation;

namespace SentenceStudio.UI.Tests.Audio;

public class AudioFailureLifecycleTests
{
    [Fact]
    public async Task ConversationPlaybackFailure_JsStopAlsoThrows_ShowsOriginalError()
    {
        var events = new List<string>();
        var js = new ThrowingStopJsRuntime(events);
        var toast = new ToastService();
        var toasts = new List<ToastMessage>();
        toast.OnShow += toasts.Add;
        var logger = new RecordingLogger<Conversation>();
        var conversation = new Conversation();
        SetMember(conversation, "JS", js);
        SetMember(conversation, "Toast", toast);
        SetMember(conversation, "Logger", logger);
        SetMember(conversation, "_browserAudioLoaded", true);
        SetMember(conversation, "isPlayingAudio", true);
        var playbackError = new InvalidOperationException("The browser rejected playback");

        var handler = typeof(Conversation).GetMethod(
            "HandleAudioPlaybackErrorAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)handler.Invoke(conversation, [playbackError])!;

        js.StopCalls.Should().Be(1);
        toasts.Should().ContainSingle()
            .Which.Should().Be(new ToastMessage("Audio playback failed.", ToastType.Error, 5000));
        GetField<bool>(conversation, "isPlayingAudio").Should().BeFalse();
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error && ReferenceEquals(entry.Error, playbackError));
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Error is JSException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationStop_JsStopFails_CancelsAndLogsWithoutFaulting(bool disconnected)
    {
        Exception stopError = disconnected
            ? new JSDisconnectedException("The browser circuit disconnected")
            : new JSException("The browser rejected audioInterop.stop");
        var js = new ThrowingStopJsRuntime(new List<string>(), stopError);
        var logger = new RecordingLogger<Conversation>();
        var toast = new ToastService();
        var toasts = new List<ToastMessage>();
        toast.OnShow += toasts.Add;
        var conversation = new Conversation();
        SetMember(conversation, "JS", js);
        SetMember(conversation, "Logger", logger);
        SetMember(conversation, "Toast", toast);
        var cancellation = PrepareBrowserAudio(conversation);

        await InvokeConversationHandlerAsync(conversation, "StopAudioAsync");

        js.StopCalls.Should().Be(1);
        cancellation.IsCancellationRequested.Should().BeTrue();
        GetField<CancellationTokenSource?>(conversation, "_audioRequestCts").Should().BeNull();
        GetField<bool>(conversation, "_browserAudioLoaded").Should().BeFalse();
        GetField<bool>(conversation, "isPlayingAudio").Should().BeFalse();
        var warning = logger.Entries.Should().ContainSingle().Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Error.Should().BeSameAs(stopError);
        warning.Message.Should().Contain("stop was not confirmed");
        if (disconnected)
            warning.Message.Should().Contain("JS circuit disconnected");
        toasts.Should().BeEmpty("a stop failure must not be reported as successful playback");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConversationSwitch_JsStopFails_StillStartsSelectedConversation(
        bool changeScenario, bool disconnected)
    {
        Exception stopError = disconnected
            ? new JSDisconnectedException("The browser circuit disconnected")
            : new JSException("The browser rejected audioInterop.stop");
        var js = new ThrowingStopJsRuntime(new List<string>(), stopError);
        var logger = new RecordingLogger<Conversation>();
        var toast = new ToastService();
        var toasts = new List<ToastMessage>();
        toast.OnShow += toasts.Add;
        var gateway = new RecordingAiGateway();
        using var services = CreateConversationServices(gateway);
        var conversation = new Conversation();
        var original = new ConversationScenario { Id = 1, Name = "First", PersonaName = "First guide" };
        var selected = new ConversationScenario { Id = 2, Name = "Second", PersonaName = "Second guide" };
        SetMember(conversation, "ConversationSvc", services.GetRequiredService<ConversationService>());
        SetMember(conversation, "JS", js);
        SetMember(conversation, "Logger", logger);
        SetMember(conversation, "Toast", toast);
        SetMember(conversation, "VoicePrefs",
            new SpeechVoicePreferences(NullLogger<SpeechVoicePreferences>.Instance, new EmptyPreferences()));
        SetMember(conversation, "scenarios", new List<ConversationScenario> { original, selected });
        SetMember(conversation, "activeScenario", original);
        SetMember(conversation, "chunks", new List<ConversationChunk> { new() { Text = "Old opening" } });
        var cancellation = PrepareBrowserAudio(conversation);

        if (changeScenario)
            await InvokeConversationHandlerAsync(conversation, "OnScenarioChanged",
                new ChangeEventArgs { Value = selected.Id.ToString() });
        else
            await InvokeConversationHandlerAsync(conversation, "ChangeLanguage", "Spanish");

        var expectedScenario = changeScenario ? selected : original;
        var expectedLanguage = changeScenario ? "Korean" : "Spanish";
        var prompt = gateway.Prompts.Should().ContainSingle().Which;
        prompt.Should().Contain(expectedScenario.PersonaName);
        prompt.Should().Contain(expectedLanguage);
        GetField<List<ConversationChunk>>(conversation, "chunks").Should().ContainSingle()
            .Which.Text.Should().Be("New opening");
        GetField<bool>(conversation, "isBusy").Should().BeFalse();
        GetField<bool>(conversation, "isPlayingAudio").Should().BeFalse();
        GetField<bool>(conversation, "_browserAudioLoaded").Should().BeFalse();
        GetField<CancellationTokenSource?>(conversation, "_audioRequestCts").Should().BeNull();
        cancellation.IsCancellationRequested.Should().BeTrue();
        js.StopCalls.Should().Be(1);
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Warning
            && ReferenceEquals(entry.Error, stopError)
            && entry.Message.Contains("stop was not confirmed"));
        toasts.Should().BeEmpty("restarting successfully must not claim playback succeeded or failed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationSwitch_DelayedJsStop_PreventsPreviousListenFromStarting(
        bool changeScenario)
    {
        var js = new DelayedStopJsRuntime();
        var gateway = new RecordingAiGateway();
        using var services = CreateConversationServices(gateway);
        var logger = new RecordingLogger<Conversation>();
        var toast = new ToastService();
        var toasts = new List<ToastMessage>();
        toast.OnShow += toasts.Add;
        var conversation = new Conversation();
        var original = new ConversationScenario { Id = 1, Name = "First", PersonaName = "First guide" };
        var selected = new ConversationScenario { Id = 2, Name = "Second", PersonaName = "Second guide" };
        SetMember(conversation, "ConversationSvc", services.GetRequiredService<ConversationService>());
        SetMember(conversation, "JS", js);
        SetMember(conversation, "Logger", logger);
        SetMember(conversation, "Toast", toast);
        SetMember(conversation, "VoicePrefs",
            new SpeechVoicePreferences(NullLogger<SpeechVoicePreferences>.Instance, new EmptyPreferences()));
        SetMember(conversation, "scenarios", new List<ConversationScenario> { original, selected });
        SetMember(conversation, "activeScenario", original);
        SetMember(conversation, "chunks", new List<ConversationChunk> { new() { Text = "Old opening" } });
        var previousToken = PrepareBrowserAudio(conversation);

        var staleListen = InvokeConversationHandlerAsync(
            conversation, "PlayChunkAudio", new ConversationChunk { Text = "Previous conversation audio" });
        await js.StopStarted.WaitAsync(TimeSpan.FromSeconds(10));
        staleListen.IsCompleted.Should().BeFalse("the JS stop is still blocked");

        if (changeScenario)
            await InvokeConversationHandlerAsync(conversation, "OnScenarioChanged",
                new ChangeEventArgs { Value = selected.Id.ToString() });
        else
            await InvokeConversationHandlerAsync(conversation, "ChangeLanguage", "Spanish");

        var expectedScenario = changeScenario ? selected : original;
        var expectedLanguage = changeScenario ? "Korean" : "Spanish";
        var prompt = gateway.Prompts.Should().ContainSingle().Which;
        prompt.Should().Contain(expectedScenario.PersonaName);
        prompt.Should().Contain(expectedLanguage);
        previousToken.IsCancellationRequested.Should().BeTrue();
        GetField<long>(conversation, "_audioGeneration").Should().Be(2);
        GetField<CancellationTokenSource?>(conversation, "_audioRequestCts").Should().BeNull();
        staleListen.IsCompleted.Should().BeFalse("the previous Listen has not resumed yet");

        js.ReleaseStop();
        await staleListen.WaitAsync(TimeSpan.FromSeconds(10));

        js.Calls.Should().Equal("audioInterop.stop");
        GetField<List<ConversationChunk>>(conversation, "chunks").Should().ContainSingle()
            .Which.Text.Should().Be("New opening");
        GetField<bool>(conversation, "isPlayingAudio").Should().BeFalse();
        GetField<bool>(conversation, "_browserAudioLoaded").Should().BeFalse();
        GetField<CancellationTokenSource?>(conversation, "_audioRequestCts").Should().BeNull();
        logger.Entries.Should().BeEmpty("the superseded Listen must not start TTS or report playback failure");
        toasts.Should().BeEmpty();
    }

    [Fact]
    public async Task ConversationStop_NonInteropFailure_PropagatesAfterLocalCleanup()
    {
        var stopError = new InvalidOperationException("Unexpected browser error");
        var js = new ThrowingStopJsRuntime(new List<string>(), stopError);
        var logger = new RecordingLogger<Conversation>();
        var conversation = new Conversation();
        SetMember(conversation, "JS", js);
        SetMember(conversation, "Logger", logger);
        var cancellation = PrepareBrowserAudio(conversation);

        Func<Task> stop = () => InvokeConversationHandlerAsync(conversation, "StopAudioAsync");

        (await stop.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(stopError);
        js.StopCalls.Should().Be(1);
        cancellation.IsCancellationRequested.Should().BeTrue();
        GetField<CancellationTokenSource?>(conversation, "_audioRequestCts").Should().BeNull();
        GetField<bool>(conversation, "isPlayingAudio").Should().BeFalse();
        GetField<bool>(conversation, "_browserAudioLoaded").Should().BeFalse();
        logger.Entries.Should().BeEmpty("only expected JS interop failures may be handled");
    }

    private static CancellationToken PrepareBrowserAudio(Conversation conversation)
    {
        var request = new CancellationTokenSource();
        SetMember(conversation, "_audioRequestCts", request);
        SetMember(conversation, "_browserAudioLoaded", true);
        SetMember(conversation, "isPlayingAudio", true);
        return request.Token;
    }

    private static Task InvokeConversationHandlerAsync(
        Conversation conversation, string method, params object?[] args) =>
        (Task)typeof(Conversation).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(conversation, args)!;

    private static void SetMember(object component, string name, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            | BindingFlags.DeclaredOnly;
        for (var type = component.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(name, flags);
            if (property is not null)
            {
                property.SetValue(component, value);
                return;
            }

            var field = type.GetField(name, flags);
            if (field is not null)
            {
                field.SetValue(component, value);
                return;
            }
        }
        throw new InvalidOperationException($"Missing component member: {name}");
    }

    private static T GetField<T>(object component, string name)
    {
        for (var type = component.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly);
            if (field is not null)
                return (T)field.GetValue(component)!;
        }
        throw new InvalidOperationException($"Missing component field: {name}");
    }

    private static ServiceProvider CreateConversationServices(RecordingAiGateway gateway)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Settings:OpenAIKey"] = "fixture-only",
                ["AI:OpenAI:Endpoint"] = "https://localhost.invalid/openai/v1"
            })
            .Build();
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddHttpClient();
        registrations.AddSingleton<IConfiguration>(configuration);
        registrations.AddSingleton<IAiGatewayClient>(gateway);
        registrations.AddSingleton<IConnectivityService, OnlineConnectivity>();
        registrations.AddSingleton<IFileSystemService, ConversationPromptFiles>();
        registrations.AddTieredChatClients(configuration, "fixture-only");
        registrations.AddSingleton<AiService>();
        registrations.AddSingleton<ConversationService>();
        return registrations.BuildServiceProvider();
    }

    private sealed class RecordingAiGateway : IAiGatewayClient
    {
        public List<string> Prompts { get; } = new();

        public Task<T?> SendPromptAsync<T>(
            string prompt, AiTier tier = AiTier.Fast,
            string? reasoningEffort = null, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult<T?>((T)(object)"New opening");
        }

        public Task<T?> SendMessagesAsync<T>(
            List<(string role, string content)> messages,
            string? instructions = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string> AnalyzeImageAsync(
            string prompt, string imageBase64, string mediaType = "image/jpeg",
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class OnlineConnectivity : IConnectivityService
    {
        public bool IsInternetAvailable => true;
    }

    private sealed class ConversationPromptFiles : IFileSystemService
    {
        public string AppDataDirectory => throw new NotSupportedException();

        public Task<Stream> OpenAppPackageFileAsync(string filename)
        {
            if (filename is not ("Conversation.scenario.scriban-txt"
                or "StartConversation.scenario.scriban-txt"))
                throw new InvalidOperationException($"Unexpected template: {filename}");

            return Task.FromResult<Stream>(new MemoryStream(
                Encoding.UTF8.GetBytes("{{ target_language }}|{{ scenario.persona_name }}")));
        }
    }

    private sealed class EmptyPreferences : IPreferencesService
    {
        public T Get<T>(string key, T defaultValue) => defaultValue;
        public void Set<T>(string key, T value) { }
        public void Remove(string key) { }
        public void Clear() { }
    }

    private sealed class DelayedStopJsRuntime : IJSRuntime
    {
        private readonly TaskCompletionSource<bool> _stopStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseStop =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StopStarted => _stopStarted.Task;
        public List<string> Calls { get; } = new();
        public void ReleaseStop() => _releaseStop.TrySetResult(true);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add(identifier);
            if (identifier != "audioInterop.stop")
                throw new InvalidOperationException($"Superseded Listen called JS: {identifier}");

            _stopStarted.TrySetResult(true);
            return new ValueTask<TValue>(CompleteStopAsync<TValue>());
        }

        private async Task<TValue> CompleteStopAsync<TValue>()
        {
            await _releaseStop.Task;
            return default!;
        }
    }

    private sealed class ThrowingStopJsRuntime(List<string> events, Exception? stopError = null) : IJSRuntime
    {
        private readonly Exception _stopError =
            stopError ?? new JSException("The browser audio interop is unavailable");

        public int StopCalls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "audioInterop.stop")
                throw new InvalidOperationException($"Unexpected JS call: {identifier}");

            StopCalls++;
            events.Add(identifier);
            throw _stopError;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Error, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}
