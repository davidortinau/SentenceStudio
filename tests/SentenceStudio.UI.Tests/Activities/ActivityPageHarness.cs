using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;
using SentenceStudio.Services.Progress;
using SentenceStudio.Services.Timer;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Services;
using Dispatcher = Microsoft.AspNetCore.Components.Dispatcher;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Hosts a real activity page against real repositories and services over scratch SQLite, with
/// a scripted AI gateway and a switchable active profile, so owner scoping and "no fake results"
/// regressions are checked on the page code the WebApp and MAUI heads both run.
/// </summary>
internal sealed class ActivityPageFixture : IAsyncDisposable
{
    public const string OwnerA = "activity-owner-a";
    public const string OwnerB = "activity-owner-b";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly string _appData = Path.Combine(Path.GetTempPath(), "ss-activity-tests-" + Guid.NewGuid().ToString("N"));

    public SwitchablePreferences Preferences { get; } = new() { ActiveId = OwnerA };
    public ScriptedAiGateway Ai { get; } = new();
    public List<ToastMessage> Toasts { get; } = new();
    public TestNavigationManager Navigation { get; } = new();
    public ServiceProvider Services { get; private set; } = null!;
    public ActivityTestRenderer Renderer { get; private set; } = null!;

    /// <param name="configure">Adds the services a page needs beyond the shared set.</param>
    public static async Task<ActivityPageFixture> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var fixture = new ActivityPageFixture();
        await fixture.InitializeAsync(configure);
        return fixture;
    }

    private async Task InitializeAsync(Action<IServiceCollection>? configure)
    {
        Directory.CreateDirectory(_appData);
        await _connection.OpenAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Settings:OpenAIKey"] = "fixture-only",
                ["AI:OpenAI:Endpoint"] = "https://localhost.invalid/openai/v1"
            })
            .Build();

        var toast = new ToastService();
        toast.OnShow += Toasts.Add;

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddHttpClient();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IPreferencesService>(Preferences);
        services.AddSingleton<IAiGatewayClient>(Ai);
        services.AddSingleton<IConnectivityService>(new OnlineConnectivity());
        services.AddSingleton<IFileSystemService>(new TemplateFiles(_appData));
        services.AddSingleton<ISyncService>(new NoOpSyncService());
        services.AddTieredChatClients(configuration, "fixture-only");
        services.AddSingleton<AiService>();
        services.AddSingleton<LearningResourceRepository>();
        services.AddSingleton<UserActivityRepository>();
        services.AddSingleton<UserProfileRepository>();
        services.AddSingleton<SkillProfileRepository>();
        services.AddSingleton<VocabularyProgressRepository>();
        services.AddSingleton<VocabularyLearningContextRepository>();
        services.AddSingleton<VocabularyProgressService>();
        services.AddSingleton<IVocabularyProgressService>(sp => sp.GetRequiredService<VocabularyProgressService>());
        services.AddSingleton<ProgressCacheService>();
        services.AddSingleton<WordAssociationService>();
        services.AddSingleton<TranslationService>();
        services.AddSingleton<TeacherService>();
        services.AddSingleton<IActivityTimerService>(new ActivityTimerService());
        services.AddSingleton<IProgressService>(ThrowingStub<IProgressService>.Create());
        services.AddSingleton<IJSRuntime>(ThrowingStub<IJSRuntime>.Create());
        services.AddSingleton<IAudioPlayerFactory>(ThrowingStub<IAudioPlayerFactory>.Create());
        services.AddSingleton(new ElevenLabs.ElevenLabsClient("fixture-only",
            httpClient: new HttpClient(new RefusingHandler())));
        services.AddSingleton<ElevenLabsSpeechService>();
        services.AddSingleton(toast);
        services.AddSingleton(new BlazorLocalizationService());
        services.AddSingleton<NavigationManager>(Navigation);
        configure?.Invoke(services);
        Services = services.BuildServiceProvider();

        await using (var scope = Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.UserProfiles.AddRange(
                new UserProfile { Id = OwnerA, Name = OwnerA, NativeLanguage = "English", TargetLanguage = "Korean", CreatedAt = DateTime.UtcNow },
                new UserProfile { Id = OwnerB, Name = OwnerB, NativeLanguage = "English", TargetLanguage = "Korean", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        Renderer = new ActivityTestRenderer(Services, Services.GetRequiredService<ILoggerFactory>());
    }

    /// <summary>Seeds a resource owned by <paramref name="owner"/> holding the given words.</summary>
    public async Task<(LearningResource Resource, List<VocabularyWord> Words)> SeedResourceAsync(
        string owner, params (string Target, string Native)[] words)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resource = new LearningResource
        {
            Id = Guid.NewGuid().ToString(),
            Title = "Fixture resource",
            MediaType = "Vocabulary List",
            Language = "Korean",
            UserProfileId = owner,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.LearningResources.Add(resource);
        var rows = new List<VocabularyWord>();
        foreach (var (target, native) in words)
        {
            var word = new VocabularyWord
            {
                Id = Guid.NewGuid().ToString(),
                TargetLanguageTerm = target,
                NativeLanguageTerm = native,
                Language = "Korean",
                LexicalUnitType = LexicalUnitType.Word,
                UserProfileId = owner,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            rows.Add(word);
            db.VocabularyWords.Add(word);
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = Guid.NewGuid().ToString(), ResourceId = resource.Id, VocabularyWordId = word.Id
            });
        }
        await db.SaveChangesAsync();
        return (resource, rows);
    }

    public async Task<List<T>> RowsAsync<T>() where T : class
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Set<T>().AsNoTracking().ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Renderer.Dispose();
        await Services.DisposeAsync();
        await _connection.DisposeAsync();
        try { Directory.Delete(_appData, recursive: true); } catch (IOException) { }
    }
}

/// <summary>A renderer that hosts a page as a root component and can dispatch its events.</summary>
internal sealed class ActivityTestRenderer : Renderer
{
    public ActivityTestRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : base(services, loggerFactory)
    {
    }

    public List<Exception> Unhandled { get; } = new();

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    protected override void HandleException(Exception exception) => Unhandled.Add(exception);

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

    public async Task<(int Id, TComponent Component)> RenderAsync<TComponent>(ParameterView parameters)
        where TComponent : IComponent
    {
        TComponent? instance = default;
        var id = await Dispatcher.InvokeAsync(async () =>
        {
            var component = InstantiateComponent(typeof(TComponent));
            instance = (TComponent)component;
            var componentId = AssignRootComponentId(component);
            await RenderRootComponentAsync(componentId, parameters);
            return componentId;
        });
        return (id, instance!);
    }

    public Task<(int Id, TComponent Component)> RenderAsync<TComponent>() where TComponent : IComponent =>
        RenderAsync<TComponent>(ParameterView.Empty);

    /// <summary>Invokes a private page method on the renderer's dispatcher, as an event handler would.</summary>
    public Task InvokeAsync(object component, string method, params object?[] args) =>
        Dispatcher.InvokeAsync(() =>
        {
            var info = component.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new InvalidOperationException($"Missing method {method}");
            return info.Invoke(component, args) as Task ?? Task.CompletedTask;
        });

    /// <summary>Clicks the element carrying <paramref name="attribute"/>=<paramref name="value"/>.</summary>
    public Task ClickElementAsync(int componentId, string attribute, string value) =>
        Dispatcher.InvokeAsync(async () =>
        {
            var handler = FindClickHandler(componentId, attribute, value)
                ?? throw new InvalidOperationException($"No clickable element with {attribute}='{value}'.");
            await DispatchEventAsync(handler, fieldInfo: null, new MouseEventArgs());
        });

    /// <summary>All text a component and its children rendered.</summary>
    public string RenderedText(int componentId)
    {
        var builder = new StringBuilder();
        CollectText(componentId, builder);
        return builder.ToString();
    }

    private void CollectText(int componentId, StringBuilder builder)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        for (var i = 0; i < frames.Count; i++)
        {
            ref readonly var frame = ref frames.Array[i];
            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Text:
                    builder.Append(frame.TextContent).Append(' ');
                    break;
                case RenderTreeFrameType.Markup:
                    builder.Append(frame.MarkupContent).Append(' ');
                    break;
                case RenderTreeFrameType.Component:
                    CollectText(frame.ComponentId, builder);
                    break;
            }
        }
    }

    private ulong? FindClickHandler(int componentId, string attribute, string value)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        for (var i = 0; i < frames.Count; i++)
        {
            ref readonly var frame = ref frames.Array[i];
            if (frame.FrameType == RenderTreeFrameType.Component)
            {
                var nested = FindClickHandler(frame.ComponentId, attribute, value);
                if (nested is not null)
                    return nested;
                continue;
            }
            if (frame.FrameType != RenderTreeFrameType.Element)
                continue;

            var matches = false;
            ulong handler = 0;
            for (var j = i + 1; j < frames.Count && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
            {
                ref readonly var attr = ref frames.Array[j];
                if (attr.AttributeName == attribute && Convert.ToString(attr.AttributeValue) == value)
                    matches = true;
                if (attr.AttributeName == "onclick" && attr.AttributeEventHandlerId != 0)
                    handler = attr.AttributeEventHandlerId;
            }
            if (matches && handler != 0)
                return handler;
        }
        return null;
    }
}

internal static class Members
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static T Get<T>(object component, string name)
    {
        var field = component.GetType().GetField(name, Flags);
        if (field is not null)
            return (T)field.GetValue(component)!;
        var property = component.GetType().GetProperty(name, Flags)
            ?? throw new InvalidOperationException($"Missing member {name}");
        return (T)property.GetValue(component)!;
    }

    public static void Set(object component, string name, object? value)
    {
        var field = component.GetType().GetField(name, Flags);
        if (field is not null)
        {
            field.SetValue(component, value);
            return;
        }
        var property = component.GetType().GetProperty(name, Flags)
            ?? throw new InvalidOperationException($"Missing member {name}");
        property.SetValue(component, value);
    }
}

internal sealed class SwitchablePreferences : IPreferencesService
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public string ActiveId { get; set; } = string.Empty;

    public T Get<T>(string key, T defaultValue)
    {
        if (key == "active_profile_id")
            return (T)(object)ActiveId;
        return _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
    }

    public void Set<T>(string key, T value)
    {
        if (key != "active_profile_id")
            _values[key] = value;
    }

    public void Remove(string key) => _values.Remove(key);

    public void Clear() => _values.Clear();
}

/// <summary>
/// AI gateway whose answer each test scripts. <see cref="Hold"/> keeps the next calls in flight
/// until <see cref="Release"/>, so a test can act while a grade or lookup is pending.
/// </summary>
internal sealed class ScriptedAiGateway : IAiGatewayClient
{
    private TaskCompletionSource? _hold;
    private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<string> Prompts { get; } = new();
    public Func<Type, object?> Respond { get; set; } = _ => null;

    /// <summary>Completes when a held call has reached the gateway.</summary>
    public Task Started => _started.Task;

    public void Hold()
    {
        _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Release() => _hold?.TrySetResult();

    public async Task<T?> SendPromptAsync<T>(string prompt, AiTier tier = AiTier.Fast,
        string? reasoningEffort = null, CancellationToken cancellationToken = default)
    {
        lock (Prompts)
            Prompts.Add(prompt);
        if (_hold is { } hold)
        {
            _started.TrySetResult();
            await hold.Task;
        }
        return (T?)Respond(typeof(T));
    }

    public Task<T?> SendMessagesAsync<T>(List<(string role, string content)> messages,
        string? instructions = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<string> AnalyzeImageAsync(string prompt, string imageBase64, string mediaType = "image/jpeg",
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class TestNavigationManager : NavigationManager
{
    public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");

    public List<string> Visited { get; } = new();

    protected override void NavigateToCore(string uri, bool forceLoad) => Visited.Add(uri);
}

/// <summary>A stand-in for a service the tested path must never call.</summary>
public class ThrowingStub<T> : DispatchProxy where T : class
{
    public static T Create() => DispatchProxy.Create<T, ThrowingStub<T>>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        throw new NotSupportedException($"{typeof(T).Name}.{targetMethod?.Name} is not expected in this test.");
}

internal sealed class OnlineConnectivity : IConnectivityService
{
    public bool IsInternetAvailable => true;
}

/// <summary>Every prompt template renders the learner's input, which the gateway records.</summary>
internal sealed class TemplateFiles(string appData) : IFileSystemService
{
    public string AppDataDirectory => appData;

    public Task<Stream> OpenAppPackageFileAsync(string filename) =>
        Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(filename + ": {{ user_input }}")));
}

/// <summary>ElevenLabs requests fail as if the service were unreachable.</summary>
internal sealed class RefusingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("Speech service unreachable in this test.");
}
