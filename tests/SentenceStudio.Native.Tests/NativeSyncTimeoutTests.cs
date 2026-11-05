using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using CoreSync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// A server that accepts connections but never answers (an Aspire proxy with the API stopped,
/// captive Wi-Fi) held sign-in readiness and pre-activity syncs for the 10-minute CoreSync
/// HttpClient timeout, and every quiz question for 30 seconds of prompt audio.
/// </summary>
public sealed class NativeSyncTimeoutTests
{
    private const string Owner = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private static readonly NativeSyncTimeouts Short =
        new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600));

    [Fact]
    public async Task Readiness_AgainstUnansweringServer_GivesUpQuickly_AndKeepsWritesPending()
    {
        await using var scenario = await Scenario.CreateAsync();
        await scenario.AddPendingLocalWriteAsync();
        var pendingBefore = await scenario.PendingUploadCountAsync();
        Assert.True(pendingBefore > 0);

        var timer = Stopwatch.StartNew();
        var outcome = await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None);
        timer.Stop();

        Assert.NotEqual(NativeSyncOutcome.Confirmed, outcome);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"Readiness waited {timer.Elapsed}.");
        Assert.Equal(1, scenario.Server.Calls);
        Assert.Equal("/api/sync-agent/store-id", scenario.Server.LastPath);
        Assert.Equal(pendingBefore, await scenario.PendingUploadCountAsync());
        Assert.Equal(0, await scenario.SavedAnchorCountAsync());

        // The run released the sync gate: a background trigger and another readiness
        // attempt are bounded the same way instead of queueing behind a hung request.
        timer.Restart();
        await scenario.Services.GetRequiredService<ISyncService>().TriggerSyncAsync();
        Assert.NotEqual(NativeSyncOutcome.Confirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"Retries waited {timer.Elapsed}.");
    }

    [Fact]
    public async Task Readiness_CallerCancellation_IsHonoredAsCancellation()
    {
        await using var scenario = await Scenario.CreateAsync(
            new NativeSyncTimeouts(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scenario.Readiness.SynchronizeAsync(Owner, cancel.Token));
    }

    [Theory]
    [InlineData("GET", "https://api.example.invalid/api/sync-agent/store-id", true)]
    [InlineData("POST", "https://api.example.invalid/api/sync-agent/store-id", false)]
    [InlineData("GET", "https://api.example.invalid/api/sync-agent/changes-bulk/abc", false)]
    [InlineData("POST", "https://api.example.invalid/api/sync-agent/changes-bulk-complete/abc", false)]
    public void OnlyTheStoreIdRead_UsesTheShortProbeBudget(string method, string uri, bool probe) =>
        Assert.Equal(probe, NativeSyncResponseTimeoutHandler.IsProbe(
            new HttpRequestMessage(new HttpMethod(method), uri)));

    [Fact]
    public async Task ChangeSetRequest_GetsTheLongerBudget_ThenFailsAsAnHttpError()
    {
        var server = new HangingServer();
        using var client = new HttpClient(new NativeSyncResponseTimeoutHandler(Short)
        {
            InnerHandler = server
        }) { BaseAddress = new Uri("https://api.example.invalid/") };

        var timer = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(
            "api/sync-agent/changes-bulk-complete/abc", new StringContent("{}")));
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(550), $"Gave up after {timer.Elapsed}.");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AnsweringServer_IsUnaffected_AndBodyIsReadAfterTheDeadline()
    {
        using var client = new HttpClient(new NativeSyncResponseTimeoutHandler(Short)
        {
            InnerHandler = new SlowBodyServer(TimeSpan.FromMilliseconds(900))
        }) { BaseAddress = new Uri("https://api.example.invalid/") };

        using var response = await client.GetAsync(
            "api/sync-agent/store-id", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"store\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PromptAudio_AgainstUnansweringServer_FallsBackWithinItsBudget()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), NativeQuizSpeechSource.DefaultTimeout);
        var scope = new DeviceUserScopeProvider();
        Assert.True(scope.TryBeginAuthentication(null, out var transition));
        Assert.True(scope.TryPublishAuthenticatedUser(
            Owner, transition, CancellationToken.None, out _));
        var preferences = new Preferences();
        preferences.Set(UserProfileRepository.ActiveProfileIdKey, Owner);
        var speech = new NativeQuizSpeechSource(
            new FakeAuth(), preferences, scope, new HangingHandlers(),
            NullLogger<SpeechGatewayClient>.Instance,
            new Uri("https://api.example.invalid/"), TimeSpan.FromMilliseconds(300));

        var timer = Stopwatch.StartNew();
        // The quiz presenter treats a non-caller cancellation as "audio unavailable".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            speech.SynthesizeAsync(Owner, "반찬", "voice", CancellationToken.None));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"Audio waited {timer.Elapsed}.");
    }

    [Fact]
    public async Task AfterATimeout_NextBlockingReadinessFailsFast_WritesStayPending_AndBackgroundRetries()
    {
        var reachability = new NativeServerReachability(TimeProvider.System, TimeSpan.FromMinutes(5));
        await using var scenario = await Scenario.CreateAsync(reachability: reachability);
        await scenario.AddPendingLocalWriteAsync();
        var pendingBefore = await scenario.PendingUploadCountAsync();

        // Sign-in restore: waits its probe budget once and remembers the failure.
        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        Assert.True(reachability.IsRecentlyUnreachable);
        Assert.Equal(1, scenario.Server.Calls);

        // Dashboard readiness right after: the same honest outcome without another wait.
        var timer = Stopwatch.StartNew();
        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        timer.Stop();
        Assert.Equal(1, scenario.Server.Calls);
        Assert.True(timer.Elapsed < Short.Probe, $"Readiness waited {timer.Elapsed}.");
        Assert.Equal(pendingBefore, await scenario.PendingUploadCountAsync());
        Assert.Equal(0, await scenario.SavedAnchorCountAsync());

        // An awaited source trigger (ProgressService) returns at once but still retries.
        var trigger = scenario.Services.GetRequiredService<ISyncService>().TriggerSyncAsync();
        Assert.True(trigger.IsCompleted);
        var deadline = Stopwatch.StartNew();
        while (Volatile.Read(ref scenario.Server.Calls) < 2 && deadline.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20);
        Assert.Equal(2, Volatile.Read(ref scenario.Server.Calls));
        Assert.Equal(pendingBefore, await scenario.PendingUploadCountAsync());
    }

    [Fact]
    public async Task WithoutARecentFailure_ReadinessAndTriggerStillWaitForTheServer()
    {
        var reachability = new NativeServerReachability(
            TimeProvider.System, TimeSpan.FromMilliseconds(1));
        await using var scenario = await Scenario.CreateAsync(reachability: reachability);
        await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None);
        await Task.Delay(20);
        Assert.False(reachability.IsRecentlyUnreachable);

        // The window ended: the next readiness tries the server again.
        await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None);
        Assert.Equal(2, scenario.Server.Calls);
        await Task.Delay(20);
        var timer = Stopwatch.StartNew();
        await scenario.Services.GetRequiredService<ISyncService>().TriggerSyncAsync();
        Assert.True(timer.Elapsed >= Short.Probe - TimeSpan.FromMilliseconds(50),
            $"Trigger returned after {timer.Elapsed}.");
        Assert.Equal(3, scenario.Server.Calls);
    }

    [Fact]
    public async Task AnyServerResponse_ClearsTheRecentFailure()
    {
        var reachability = new NativeServerReachability();
        reachability.RecordFailure();
        using var sync = new HttpClient(new NativeSyncResponseTimeoutHandler(Short, reachability)
        {
            InnerHandler = new StatusServer(HttpStatusCode.Unauthorized)
        }) { BaseAddress = new Uri("https://api.example.invalid/") };
        using (await sync.GetAsync("api/sync-agent/store-id"))
            Assert.False(reachability.IsRecentlyUnreachable);

        reachability.RecordFailure();
        using var auth = new HttpClient(new NativeServerReachabilityHandler(reachability)
        {
            InnerHandler = new StatusServer(HttpStatusCode.OK)
        }) { BaseAddress = new Uri("https://api.example.invalid/") };
        using (await auth.GetAsync("api/v1/profile/x"))
            Assert.False(reachability.IsRecentlyUnreachable);

        // A refused connection opens the window just like a timeout.
        using var refused = new HttpClient(new NativeServerReachabilityHandler(reachability)
        {
            InnerHandler = new RefusingServer()
        }) { BaseAddress = new Uri("https://api.example.invalid/") };
        await Assert.ThrowsAsync<HttpRequestException>(() => refused.GetAsync("api/v1/x"));
        Assert.True(reachability.IsRecentlyUnreachable);
    }

    [Fact]
    public async Task PromptAudio_AfterARecentFailure_FallsBackAtOnce_WithoutCallingTheServer()
    {
        var reachability = new NativeServerReachability();
        var (speech, handlers) = SpeechFor(reachability, TimeSpan.FromSeconds(30));
        reachability.RecordFailure();

        var timer = Stopwatch.StartNew();
        // HttpRequestException is the quiz's existing "audio unavailable" path.
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            speech.SynthesizeAsync(Owner, "반찬", "voice", CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            speech.GetVoicesAsync(Owner, "Korean", CancellationToken.None));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Audio waited {timer.Elapsed}.");
        Assert.Equal(0, handlers.Created);
    }

    [Fact]
    public async Task PromptAudio_Timeout_OpensTheWindow_ButOwnerChecksStillComeFirst()
    {
        var reachability = new NativeServerReachability();
        var (speech, _) = SpeechFor(reachability, TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            speech.SynthesizeAsync(Owner, "반찬", "voice", CancellationToken.None));
        Assert.True(reachability.IsRecentlyUnreachable);

        // A foreign owner is refused as before, not reported as a connection failure.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            speech.SynthesizeAsync("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "반찬", "voice",
                CancellationToken.None));
    }

    private static (NativeQuizSpeechSource Speech, HangingHandlers Handlers) SpeechFor(
        NativeServerReachability reachability, TimeSpan timeout)
    {
        var scope = new DeviceUserScopeProvider();
        Assert.True(scope.TryBeginAuthentication(null, out var transition));
        Assert.True(scope.TryPublishAuthenticatedUser(
            Owner, transition, CancellationToken.None, out _));
        var preferences = new Preferences();
        preferences.Set(UserProfileRepository.ActiveProfileIdKey, Owner);
        var handlers = new HangingHandlers();
        return (new NativeQuizSpeechSource(
            new FakeAuth(), preferences, scope, handlers,
            NullLogger<SpeechGatewayClient>.Instance,
            new Uri("https://api.example.invalid/"), timeout, reachability), handlers);
    }

    private sealed class StatusServer(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class RefusingServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused.");
    }

    private static string TokenFor(string owner) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim(AuthClaimTypes.UserProfileId, owner)],
            expires: DateTime.UtcNow.AddHours(1)));

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly string _database;

        private Scenario(ServiceProvider services, string database, HangingServer server)
        {
            _services = services;
            _database = database;
            Server = server;
        }

        public HangingServer Server { get; }
        public IServiceProvider Services => _services;
        public INativeSyncReadiness Readiness => _services.GetRequiredService<INativeSyncReadiness>();

        public static async Task<Scenario> CreateAsync(
            NativeSyncTimeouts? timeouts = null, NativeServerReachability? reachability = null)
        {
            var database = Path.Combine(Path.GetTempPath(), $"native-timeout-{Guid.NewGuid():N}.sqlite");
            await using (var db = new ApplicationDbContext(
                DeviceInterpreterQueryGuard.Guard(new DbContextOptionsBuilder<ApplicationDbContext>())
                    .UseSqlite($"Data Source={database};Pooling=False").Options))
                await db.Database.EnsureCreatedAsync();

            var scope = new DeviceUserScopeProvider();
            Assert.True(scope.TryBeginAuthentication(null, out var transition));
            Assert.True(scope.TryPublishAuthenticatedUser(
                Owner, transition, CancellationToken.None, out _));
            var preferences = new Preferences();
            preferences.Set(UserProfileRepository.ActiveProfileIdKey, Owner);
            var server = new HangingServer();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IAuthService>(new FakeAuth());
            services.AddSingleton<IPreferencesService>(preferences);
            services.AddSingleton(scope);
            services.AddSingleton<IUserScopeProvider>(_ => scope);
            services.AddSingleton<IConnectivityService>(new OnlineConnectivity());
            services.AddSingleton(timeouts ?? Short);
            if (reachability is not null)
                services.AddSingleton(reachability);
            services.AddSyncServices($"{database};Pooling=False",
                new Uri("https://api.example.invalid/"), downloadOnlyProfile: true);
            services.AddSingleton<ISyncService, SyncService>();
            services.AddOwnerBoundNativeSyncReadiness();
            services.AddHttpClient("HttpClientToServer")
                .ConfigurePrimaryHttpMessageHandler(() => server);
            services.AddHttpClient("AuthClient")
                .ConfigurePrimaryHttpMessageHandler(() => server);
            var provider = services.BuildServiceProvider(validateScopes: true);
            await provider.GetRequiredService<ISyncProvider>().ApplyProvisionAsync();
            return new Scenario(provider, database, server);
        }

        public async Task AddPendingLocalWriteAsync()
        {
            await using var db = Open();
            db.LearningResources.Add(new LearningResource
            {
                Id = Guid.NewGuid().ToString(), UserProfileId = Owner, Title = "Offline"
            });
            await db.SaveChangesAsync();
        }

        public async Task<int> PendingUploadCountAsync()
        {
            var local = _services.GetRequiredService<ISyncProvider>();
            var changes = await local.GetChangesAsync(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                syncDirection: SyncDirection.UploadOnly);
            return changes.Items.Count;
        }

        /// <summary>No anchor saved: nothing local was acknowledged as uploaded.</summary>
        public async Task<long> SavedAnchorCountAsync()
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={_database};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM __CORE_SYNC_REMOTE_ANCHOR";
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        private ApplicationDbContext Open() => new(
            DeviceInterpreterQueryGuard.Guard(new DbContextOptionsBuilder<ApplicationDbContext>())
                .UseSqlite($"Data Source={_database};Pooling=False").Options);

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            Server.Release();
            try
            {
                File.Delete(_database);
            }
            catch (IOException)
            {
                // Task-owned scratch; a lingering handle only leaves a temp file behind.
            }
        }
    }

    /// <summary>Accepts the request and never answers until released or cancelled.</summary>
    private sealed class HangingServer : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public string? LastPath { get; private set; }

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            LastPath = request.RequestUri?.AbsolutePath;
            await _release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    /// <summary>Answers at once but streams its body after the response deadline.</summary>
    private sealed class SlowBodyServer(TimeSpan bodyDelay) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new SlowContent(bodyDelay)
            });

        private sealed class SlowContent(TimeSpan delay) : HttpContent
        {
            protected override async Task SerializeToStreamAsync(
                Stream stream, TransportContext? context)
            {
                await Task.Delay(delay);
                await stream.WriteAsync("\"store\""u8.ToArray());
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }
    }

    private sealed class HangingHandlers : IHttpMessageHandlerFactory
    {
        public int Created;
        public HttpMessageHandler CreateHandler(string name)
        {
            Interlocked.Increment(ref Created);
            return new HangingServer();
        }
    }

    private sealed class FakeAuth : IAuthService
    {
        public bool IsSignedIn => true;
        public string? UserName => null;
        public Task<string?> GetAccessTokenAsync(string[] scopes) =>
            Task.FromResult<string?>(TokenFor(Owner));
        public Task<bool> HasStoredSessionAsync() => Task.FromResult(true);
        public Task<AuthResult?> SignInAsync() => throw new NotSupportedException();
        public Task<AuthResult?> SignInAsync(string email, string password) =>
            throw new NotSupportedException();
        public Task<AuthResult?> RegisterAsync(string email, string password, string displayName) =>
            throw new NotSupportedException();
        public Task SignOutAsync() => throw new NotSupportedException();
        public Task<bool> DeleteAccountAsync() => throw new NotSupportedException();
        public Task<bool> ChangePasswordAsync(string currentPassword, string newPassword) =>
            throw new NotSupportedException();
    }

    private sealed class Preferences : IPreferencesService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
        public void Set<T>(string key, T value) => _values[key] = value!;
        public void Remove(string key) => _values.Remove(key);
        public void Clear() => _values.Clear();
    }

    private sealed class OnlineConnectivity : IConnectivityService
    {
        public bool IsInternetAvailable => true;
    }
}
