using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Speech;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Timer;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Renders one real activity page through <see cref="HtmlRenderer"/> with real
/// owner-scoped repositories on in-memory SQLite and fakes only for the browser,
/// audio device, network speech, clock and app-data directory. Handlers run
/// through <see cref="IHandleEvent"/>, the same path a click takes.
/// </summary>
public sealed class ActivityPageHarness : IAsyncDisposable
{
    public const string OwnerA = "page-owner-a";
    public const string OwnerB = "page-owner-b";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ServiceProvider _services = null!;
    private HtmlRenderer _renderer = null!;
    private HtmlRootComponent? _root;
    private IComponent? _page;

    public DirectoryInfo AppData { get; } =
        Directory.CreateTempSubdirectory("ss-activity-page-");
    public MemoryPreferences Preferences { get; } = new();
    public FakeConnectivity Connectivity { get; } = new();
    public RecordingJsRuntime Js { get; } = new();
    public FakeAudioFactory Audio { get; } = new();
    public FakeSpeechHandler Speech { get; } = new();
    public List<ToastMessage> Toasts { get; } = new();
    public ActivityTimerService Timer { get; } = new();
    public FixedLearnerClock Clock { get; } = new();
    public ListLogger Logs { get; } = new();

    public IServiceProvider Services => _services;

    public static async Task<ActivityPageHarness> CreateAsync(
        Action<IServiceCollection>? configure = null, string targetLanguage = "Korean")
    {
        var harness = new ActivityPageHarness();
        await harness._connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(new SingleLoggerFactory(harness.Logs));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(harness._connection));
        services.AddSingleton<IPreferencesService>(harness.Preferences);
        services.AddSingleton<DeviceUserScopeProvider>();
        services.AddSingleton<IUserScopeProvider>(sp => sp.GetRequiredService<DeviceUserScopeProvider>());
        services.AddSingleton<IConnectivityService>(harness.Connectivity);
        services.AddSingleton<IFileSystemService>(new TempFileSystem(harness.AppData.FullName));
        services.AddSingleton<IJSRuntime>(harness.Js);
        services.AddSingleton<IAudioPlayerFactory>(harness.Audio);
        services.AddSingleton<NavigationManager>(new StubNavigationManager());
        services.AddSingleton<IActivityTimerService>(harness.Timer);
        services.AddSingleton<IPlanDateContext>(harness.Clock);
        services.AddScoped<BlazorLocalizationService>();
        // Every page's header carries Sam's entry; with no overlay host attached it draws nothing.
        services.AddScoped<SentenceStudio.WebUI.Shared.Sam.SamEntryState>();
        services.AddScoped<CoachPersona>();
        var toast = new ToastService();
        toast.OnShow += harness.Toasts.Add;
        services.AddSingleton(toast);
        services.AddSingleton<StreamHistoryRepository>();
        services.AddSingleton<UserProfileRepository>();
        services.AddSingleton<SpeechVoicePreferences>();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        // Speech goes through the API (/api/v1/speech/*); the fake below answers as the API does.
        services.AddSingleton<ISpeechApiClient>(new SpeechApiClient(harness.Speech.CreateClient()));
        services.AddSingleton<ITimestampedSpeechApiClient>(
            new TimestampedSpeechApiClient(harness.Speech.CreateClient()));
        services.AddSingleton<ElevenLabsSpeechService>();
        configure?.Invoke(services);
        harness._services = services.BuildServiceProvider();

        await using var scope = harness._services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.UserProfiles.AddRange(
            new UserProfile { Id = OwnerA, TargetLanguage = targetLanguage, CreatedAt = DateTime.UtcNow.AddDays(-3) },
            new UserProfile { Id = OwnerB, TargetLanguage = "Korean", CreatedAt = DateTime.UtcNow.AddDays(-3) });
        await db.SaveChangesAsync();
        harness.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, OwnerA);
        harness._renderer = new HtmlRenderer(harness._services,
            harness._services.GetRequiredService<ILoggerFactory>());
        return harness;
    }

    public async Task<TPage> RenderAsync<TPage>(IDictionary<string, object?>? parameters = null)
        where TPage : IComponent
    {
        TPage? captured = default;
        _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<Capture<TPage>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Capture<TPage>.OnCapture)] = (Action<TPage>)(page => captured = page),
                [nameof(Capture<TPage>.PageParameters)] = parameters,
            })));
        await _root.Value.QuiescenceTask;
        _page = captured ?? throw new InvalidOperationException("The page was not rendered.");
        return captured;
    }

    public string Html() => _renderer.Dispatcher.InvokeAsync(
        () => WebUtility.HtmlDecode(_root!.Value.ToHtmlString())).GetAwaiter().GetResult();

    /// <summary>Runs a page handler the way a click does: render after its first await and after it completes.</summary>
    public Task DispatchAsync(string method, params object?[] args)
    {
        var page = _page ?? throw new InvalidOperationException("Render the page first.");
        var handler = FindMethod(page.GetType(), method);
        Func<Task> work = () => handler.Invoke(page, args) as Task ?? Task.CompletedTask;
        return _renderer.Dispatcher.InvokeAsync(
            () => ((IHandleEvent)page).HandleEventAsync(new EventCallbackWorkItem(work), null));
    }

    public T Get<T>(string member) =>
        _renderer.Dispatcher.InvokeAsync(() => (T)Member(_page!, member)!).GetAwaiter().GetResult();

    public void Set(string member, object? value) =>
        _renderer.Dispatcher.InvokeAsync(() => SetMember(_page!, member, value)).GetAwaiter().GetResult();

    public async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await _renderer.Dispatcher.InvokeAsync(condition))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The page did not reach the expected state.");
            await Task.Delay(10);
        }
    }

    public async Task<TResult> WithDbAsync<TResult>(Func<ApplicationDbContext, Task<TResult>> query)
    {
        await using var scope = _services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    public string AudioCacheDirectory
    {
        get
        {
            var path = Path.Combine(AppData.FullName, UiSpeechClipCache.DirectoryName);
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Disposing the renderer disposes the page, as leaving it does.
        await _renderer.DisposeAsync();
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
        try
        {
            AppData.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static MethodInfo FindMethod(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var method = current.GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (method is not null)
                return method;
        }
        throw new MissingMethodException(type.Name, name);
    }

    private static object? Member(object target, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = target.GetType();
        return type.GetField(name, flags)?.GetValue(target)
            ?? type.GetProperty(name, flags)?.GetValue(target)
            ?? (type.GetField(name, flags) is null && type.GetProperty(name, flags) is null
                ? throw new MissingMemberException(type.Name, name) : null);
    }

    private static void SetMember(object target, string name, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = target.GetType();
        if (type.GetField(name, flags) is { } field)
            field.SetValue(target, value);
        else if (type.GetProperty(name, flags) is { } property)
            property.SetValue(target, value);
        else
            throw new MissingMemberException(type.Name, name);
    }

    /// <summary>Hosts the page and captures its instance.</summary>
    public sealed class Capture<TPage> : ComponentBase where TPage : IComponent
    {
        [Parameter] public Action<TPage>? OnCapture { get; set; }
        [Parameter] public IDictionary<string, object?>? PageParameters { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<TPage>(0);
            if (PageParameters is not null)
                builder.AddMultipleAttributes(1, PageParameters!);
            builder.AddComponentReferenceCapture(2, page => OnCapture?.Invoke((TPage)page));
            builder.CloseComponent();
        }
    }

    /// <summary>A service the page injects but this test never reaches.</summary>
    public static T Unreached<T>() where T : class =>
        typeof(T).IsInterface
            ? DispatchProxy.Create<T, UnreachedProxy>()
            : (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    public class UnreachedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"{targetMethod?.Name} was not expected in this test.");
    }

    public sealed class MemoryPreferences : IPreferencesService
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value;
        public void Remove(string key) => _values.Remove(key);
        public void Clear() => _values.Clear();
    }

    public sealed class FakeConnectivity : IConnectivityService
    {
        public bool IsInternetAvailable { get; set; } = true;
    }

    private sealed class TempFileSystem(string root) : IFileSystemService
    {
        public string AppDataDirectory => root;
        public Task<Stream> OpenAppPackageFileAsync(string filename) =>
            throw new FileNotFoundException(filename);
    }

    public sealed class FixedLearnerClock : IPlanDateContext
    {
        public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;
        public DateTime UtcNow => DateTime.UtcNow;
        public DateOnly UserLocalDate => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, TimeZone));
        public DateOnly ToUserLocal(DateTime utc) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone));
        public DateTime ToUtcMidnight(DateOnly userLocal) =>
            TimeZoneInfo.ConvertTimeToUtc(userLocal.ToDateTime(TimeOnly.MinValue), TimeZone);
    }

    private sealed class StubNavigationManager : NavigationManager
    {
        public StubNavigationManager() => Initialize("https://sentencestudio.test/", "https://sentencestudio.test/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    /// <summary>
    /// Records browser calls; audio loads with a <see cref="Duration"/>-second clip. Like
    /// JSRuntime, a call whose token is cancelled completes as a TaskCanceledException at once
    /// while the browser keeps running it, and a call with a token already cancelled is not
    /// sent. Like audioInterop, the browser has one player: loadFromBase64 replaces it, stop
    /// clears it, and a clip plays once startPlayback's play() resolves unless a stop or another
    /// load came first (<see cref="Played"/>).
    /// </summary>
    public sealed class RecordingJsRuntime : IJSRuntime
    {
        public const double Duration = 30.0;
        public List<(string Identifier, object?[] Args)> Calls { get; } = new();
        /// <summary>The clips (their base64 data) the browser started playing, in order.</summary>
        public List<string> Played { get; } = new();
        /// <summary>
        /// The next call with this identifier (<c>audioInterop.loadFromBase64</c> or
        /// <c>audioInterop.startPlayback</c>) stays pending in the browser until
        /// <see cref="Release"/>, as a clip still loading or a play() not yet resolved.
        /// </summary>
        public string? Hold { get; set; }
        /// <summary>Completes when the held call arrives.</summary>
        public TaskCompletionSource HoldReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<int, string> _clips = new();
        private int _player;
        private int _lastClip;
        private Action? _release;

        /// <summary>The browser finishes the held call: the load resolves, or play() resolves or is rejected.</summary>
        public void Release() => Interlocked.Exchange(ref _release, null)?.Invoke();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<TValue>(cancellationToken);
            Calls.Add((identifier, args ?? []));
            object? result = identifier switch
            {
                "audioInterop.loadFromBase64" or "audioInterop.getDuration" => Duration,
                "audioInterop.startPlayback" or "audioInterop.setPlaybackRate" => true,
                "import" => new NoOpModule(),
                _ => null,
            };
            var starting = 0;
            switch (identifier)
            {
                case "audioInterop.loadFromBase64":
                    _player = ++_lastClip;
                    _clips[_player] = args?.FirstOrDefault() as string ?? string.Empty;
                    break;
                case "audioInterop.stop":
                    _player = 0;
                    break;
                case "audioInterop.startPlayback":
                    starting = _player;
                    break;
            }
            var value = result is TValue typed ? typed : default(TValue)!;
            if (identifier != Hold)
            {
                if (starting != 0)
                    Played.Add(_clips[starting]);
                return ValueTask.FromResult(value);
            }

            Hold = null;
            var browser = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = () =>
            {
                if (starting == 0)
                {
                    browser.TrySetResult(value);
                }
                else if (_player == starting)
                {
                    Played.Add(_clips[starting]);
                    browser.TrySetResult(value);
                }
                else
                {
                    browser.TrySetException(
                        new JSException("The play() request was interrupted by a call to pause()."));
                }
            };
            HoldReached.TrySetResult();
            return new ValueTask<TValue>(browser.Task.WaitAsync(cancellationToken));
        }

        private sealed class NoOpModule : IJSObjectReference
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
                ValueTask.FromResult(default(TValue)!);
            public ValueTask<TValue> InvokeAsync<TValue>(
                string identifier, CancellationToken cancellationToken, object?[]? args) =>
                ValueTask.FromResult(default(TValue)!);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    public sealed class FakeAudioFactory : IAudioPlayerFactory
    {
        public bool CanPlayLocally { get; set; } = true;
        public List<FakePlayer> Players { get; } = new();

        public IUiAudioPlayer? CreatePlayer(Stream audioStream)
        {
            using var buffer = new MemoryStream();
            audioStream.CopyTo(buffer);
            var player = new FakePlayer(buffer.ToArray());
            Players.Add(player);
            return player;
        }
    }

    public sealed class FakePlayer(byte[] audio) : IUiAudioPlayer
    {
        public event EventHandler? PlaybackEnded { add { } remove { } }
        public byte[] Audio { get; } = audio;
        public bool IsPlaying { get; private set; }
        public double CurrentPosition { get; private set; }
        public double Duration => 2.0;
        public double? Rate { get; private set; }
        public List<string> Events { get; } = new();

        public void Play() { IsPlaying = true; Events.Add(FormattableString.Invariant($"play@{Rate ?? 1.0}")); }
        public void Pause() { IsPlaying = false; Events.Add("pause"); }
        public void Stop() { IsPlaying = false; Events.Add("stop"); }
        public void Seek(double seconds) { CurrentPosition = seconds; Events.Add(FormattableString.Invariant($"seek:{seconds}")); }
        public bool TrySetPlaybackRate(double rate) { Rate = rate; Events.Add(FormattableString.Invariant($"rate:{rate}")); return true; }
        public void Dispose() { }
    }

    /// <summary>
    /// The API's speech endpoint (<c>POST /api/v1/speech/synthesize</c>): answers each request with
    /// <see cref="Audio"/> as a <c>data:audio/mpeg;base64,</c> URL, or with <see cref="Status"/>.
    /// </summary>
    public sealed class FakeSpeechHandler : HttpMessageHandler
    {
        public const string SynthesizePath = "/api/v1/speech/synthesize";

        public byte[] Audio { get; set; } = [0x49, 0x44, 0x33, 0x04, 0x00];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        /// <summary>Answer without VoiceId and VoiceSubstituted, as servers older than 2026-11-04 do.</summary>
        public bool AnswerAsOlderServer { get; set; }
        public List<string> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        /// <summary>When set, each request waits for it (a clip still loading).</summary>
        public TaskCompletionSource? Hold { get; set; }
        /// <summary>Completes when the first request arrives.</summary>
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpClient CreateClient() =>
            new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.test") };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            if (Hold is { } hold)
                await hold.Task.WaitAsync(cancellationToken);
            Requests.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            Bodies.Add(request.Content is null
                ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            if (Status != HttpStatusCode.OK)
                return new HttpResponseMessage(Status) { RequestMessage = request };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                // Answers like the current API: the requested voice was used, unless set otherwise.
                Content = JsonContent.Create(new SynthesizeResponse
                {
                    AudioUrl = "data:audio/mpeg;base64," + Convert.ToBase64String(Audio),
                    VoiceId = AnswerAsOlderServer ? null : RequestedVoice(Bodies[^1]),
                    VoiceSubstituted = AnswerAsOlderServer ? null : false
                }),
                RequestMessage = request,
            };
        }

        public int SynthesisCount => Requests.Count(path => path == SynthesizePath);

        private static string? RequestedVoice(string body) =>
            string.IsNullOrEmpty(body) ? null
                : JsonSerializer.Deserialize<SynthesizeRequest>(body, JsonSerializerOptions.Web)?.VoiceId;

        /// <summary>The synthesis requests as the API received them.</summary>
        public List<SynthesizeRequest> SynthesisRequests => Requests
            .Select((path, i) => (path, body: Bodies[i]))
            .Where(r => r.path == SynthesizePath)
            .Select(r => JsonSerializer.Deserialize<SynthesizeRequest>(r.body, JsonSerializerOptions.Web)!)
            .ToList();
    }

    public sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class SingleLoggerFactory(ListLogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }
}
