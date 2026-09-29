using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Native.Tests;

public sealed class OwnerBoundNativeSyncReadinessTests
{
    private const string Owner = "native-owner-a";
    private const string OtherOwner = "native-owner-b";

    [Fact]
    public async Task ConfirmsOnlyAfterAwaitedSyncAndAuthenticatedExactOwnerResponse()
    {
        using var scenario = new Scenario();
        var releaseSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Sync.Run = cancellationToken => releaseSync.Task.WaitAsync(cancellationToken);

        var pending = scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, scenario.Sync.Calls);
        Assert.Equal(0, scenario.Server.Calls);

        releaseSync.SetResult();
        Assert.Equal(NativeSyncOutcome.Confirmed, await pending);
        Assert.Equal(1, scenario.Server.Calls);
        Assert.Equal($"/api/v1/profile/{Owner}", scenario.Server.Path);
        Assert.Equal("Bearer", scenario.Server.AuthorizationScheme);
        Assert.Equal(scenario.Auth.Token, scenario.Server.AuthorizationToken);
    }

    [Fact]
    public async Task PublishedDashboardOwner_GetsTemporaryLeaseForRefreshAndReleasesIt()
    {
        using var scenario = new Scenario();
        scenario.Sync.Run = _ =>
        {
            Assert.True(scenario.Scope.TryGetPublishedReadinessOwner(out var owner));
            Assert.Equal(Owner, owner);
            return Task.CompletedTask;
        };
        scenario.Server.Reply = _ =>
        {
            Assert.True(scenario.Scope.TryGetPublishedReadinessOwner(out var owner));
            Assert.Equal(Owner, owner);
            return Task.FromResult(ProfileResponse(Owner));
        };

        Assert.False(scenario.Scope.TryGetPublishedReadinessOwner(out _));
        Assert.Equal(NativeSyncOutcome.Confirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        Assert.False(scenario.Scope.TryGetPublishedReadinessOwner(out _));
        Assert.True(scenario.Scope.TryGetUserProfileId(out var publishedOwner));
        Assert.Equal(Owner, publishedOwner);
    }

    [Fact]
    public async Task UnpublishedTransition_ConfirmsOnlyWithinAuthenticatedReadinessLease()
    {
        using var scenario = new Scenario();
        scenario.Scope.SetActiveUser(null);
        Assert.True(scenario.Scope.TryBeginAuthentication(null, out var transition));

        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        Assert.Equal(0, scenario.Sync.Calls);

        using (scenario.Scope.TryBeginReadiness(Owner, transition)!)
        {
            Assert.True(scenario.Scope.TryGetLeasedReadinessOwner(out var owner));
            Assert.Equal(Owner, owner);
            Assert.False(scenario.Scope.TryGetPublishedReadinessOwner(out _));
            Assert.Equal(NativeSyncOutcome.Confirmed,
                await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        }

        Assert.Null(scenario.Scope.CaptureStamp().PublishedOwner);
        Assert.False(scenario.Scope.TryGetUserProfileId(out _));
        Assert.Equal(1, scenario.Sync.Calls);
        Assert.Equal(1, scenario.Server.Calls);
    }

    [Fact]
    public async Task OfflineDoesNotStartSyncOrProbe()
    {
        using var scenario = new Scenario();
        scenario.Connectivity.Available = false;

        Assert.Equal(NativeSyncOutcome.Offline,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        Assert.Equal(0, scenario.Sync.Calls);
        Assert.Equal(0, scenario.Server.Calls);
    }

    [Fact]
    public async Task EmptyOwnerFailsClosedWithoutStartingSync()
    {
        using var scenario = new Scenario();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            scenario.Readiness.SynchronizeAsync("", CancellationToken.None));
        Assert.Equal(0, scenario.Sync.Calls);
        Assert.Equal(0, scenario.Server.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OtherOwner)]
    public async Task MissingOrForeignDeviceScopeCannotStartSync(string? activeOwner)
    {
        using var scenario = new Scenario();
        var originalToken = scenario.Auth.Token;
        scenario.Scope.SetActiveUser(activeOwner);

        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        Assert.Equal(0, scenario.Auth.TokenRequests);
        Assert.Equal(0, scenario.Sync.Calls);
        Assert.Equal(0, scenario.Server.Calls);
        Assert.Equal(Owner,
            scenario.Preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty));
        Assert.Equal(originalToken, scenario.Auth.Token);
    }

    [Fact]
    public async Task ForeignLocalOwnerOrJwtClaimCannotStartSync()
    {
        using var scenario = new Scenario();
        scenario.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, OtherOwner);
        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));

        scenario.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, Owner);
        scenario.Auth.Token = TokenFor(OtherOwner);
        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        Assert.Equal(0, scenario.Sync.Calls);
        Assert.Equal(0, scenario.Server.Calls);
    }

    [Fact]
    public async Task MissingExpiredOrUnauthenticatedJwtCannotStartSync()
    {
        using var scenario = new Scenario();
        scenario.Auth.Token = TokenFor(null);
        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));

        scenario.Auth.Token = TokenFor(Owner, DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));

        scenario.Auth.Token = TokenFor(Owner);
        scenario.Auth.SignedIn = false;
        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        Assert.Equal(0, scenario.Sync.Calls);
        Assert.Equal(0, scenario.Server.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task Non200ProfileResponseNeverConfirms(HttpStatusCode status)
    {
        using var scenario = new Scenario();
        scenario.Server.Reply = _ => Task.FromResult(new HttpResponseMessage(status));

        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        Assert.Equal(1, scenario.Sync.Calls);
        Assert.Equal(1, scenario.Server.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":\"native-owner-b\"}")]
    [InlineData("{\"id\":\"\"}")]
    [InlineData("{\"id\":null}")]
    [InlineData("{\"Id\":\"native-owner-a\"}")]
    [InlineData("{broken-json")]
    public async Task UnmatchedOrMissingProfileIdNeverConfirms(string body)
    {
        using var scenario = new Scenario();
        scenario.Server.Reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        Assert.Equal(1, scenario.Sync.Calls);
        Assert.Equal(1, scenario.Server.Calls);
    }

    [Fact]
    public async Task FailedSyncCannotBeMistakenForTheLegacySwallowedSuccess()
    {
        using var scenario = new Scenario();
        scenario.Sync.Run = _ => throw new InvalidOperationException(
            "Synthetic CoreSync failure, not a completion receipt.");

        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        Assert.Equal(1, scenario.Sync.Calls);
        Assert.Equal(0, scenario.Server.Calls);
    }

    [Fact]
    public async Task NetworkFailureAfterSyncDoesNotConfirm()
    {
        using var scenario = new Scenario();
        scenario.Server.Reply = _ => throw new HttpRequestException("Synthetic network failure.");

        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
    }

    [Fact]
    public async Task OwnerSwitchDuringSyncPreventsTheRemoteProbe()
    {
        using var scenario = new Scenario();
        scenario.Sync.Run = _ =>
        {
            scenario.Auth.Token = TokenFor(OtherOwner);
            return Task.CompletedTask;
        };

        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        Assert.Equal(0, scenario.Server.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OtherOwner)]
    public async Task DeviceScopeSwitchDuringAwaitedSyncPreventsProbe(string? activeOwner)
    {
        using var scenario = new Scenario();
        var originalToken = scenario.Auth.Token;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Sync.Run = async cancellationToken =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };

        var pending = scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scenario.Scope.SetActiveUser(activeOwner);
        release.SetResult();

        Assert.Equal(NativeSyncOutcome.Unconfirmed, await pending);
        Assert.Equal(1, scenario.Auth.TokenRequests);
        Assert.Equal(1, scenario.Sync.Calls);
        Assert.Equal(0, scenario.Server.Calls);
        Assert.Equal(Owner,
            scenario.Preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty));
        Assert.Equal(originalToken, scenario.Auth.Token);
    }

    [Fact]
    public async Task SameOwnerJwtRefreshDuringSyncStillUsesTheAuthenticatedNewToken()
    {
        using var scenario = new Scenario();
        scenario.Sync.Run = _ =>
        {
            scenario.Auth.Token = TokenFor(Owner);
            scenario.Server.ExpectedToken = scenario.Auth.Token;
            return Task.CompletedTask;
        };

        Assert.Equal(NativeSyncOutcome.Confirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
        Assert.Equal(scenario.Auth.Token, scenario.Server.AuthorizationToken);
    }

    [Fact]
    public async Task OwnerSwitchDuringRemoteProbePreventsConfirmation()
    {
        using var scenario = new Scenario();
        scenario.Server.Reply = _ =>
        {
            scenario.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, OtherOwner);
            return Task.FromResult(ProfileResponse(Owner));
        };

        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OtherOwner)]
    public async Task DeviceScopeSwitchDuringAwaitedProbeCannotConfirm(string? activeOwner)
    {
        using var scenario = new Scenario();
        var originalToken = scenario.Auth.Token;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Server.Reply = async cancellationToken =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return ProfileResponse(Owner);
        };

        var pending = scenario.Readiness.SynchronizeAsync(Owner, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scenario.Scope.SetActiveUser(activeOwner);
        release.SetResult();

        Assert.Equal(NativeSyncOutcome.Unconfirmed, await pending);
        Assert.Equal(2, scenario.Auth.TokenRequests);
        Assert.Equal(1, scenario.Sync.Calls);
        Assert.Equal(1, scenario.Server.Calls);
        Assert.Equal(Owner,
            scenario.Preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty));
        Assert.Equal(originalToken, scenario.Auth.Token);
    }

    [Fact]
    public async Task CancellationBeforeSyncIsPropagated()
    {
        using var scenario = new Scenario();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scenario.Readiness.SynchronizeAsync(Owner, cancellation.Token));
        Assert.Equal(0, scenario.Sync.Calls);
        Assert.Equal(0, scenario.Server.Calls);
    }

    [Fact]
    public async Task CancellationInsideSyncIsPropagatedWithoutProbing()
    {
        using var scenario = new Scenario();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Sync.Run = async cancellationToken =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        using var cancellation = new CancellationTokenSource();

        var pending = scenario.Readiness.SynchronizeAsync(Owner, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, scenario.Server.Calls);
    }

    [Fact]
    public async Task CancellationInsideProbeIsPropagatedWithoutConfirmation()
    {
        using var scenario = new Scenario();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Server.Reply = async cancellationToken =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ProfileResponse(Owner);
        };
        using var cancellation = new CancellationTokenSource();

        var pending = scenario.Readiness.SynchronizeAsync(Owner, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void LegacySuccessShapedISyncServiceDoesNotSatisfyExplicitReceipt()
    {
        var services = new ServiceCollection();
        var legacy = new LegacySuccessShapedSync();
        services.AddSingleton<ISyncService>(legacy);
        services.AddOwnerBoundNativeSyncReadiness();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<NotSupportedException>(() =>
            provider.GetRequiredService<IRemoteSyncCompletion>());
        Assert.Equal(0, legacy.Calls);
    }

    private static HttpResponseMessage ProfileResponse(string owner) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"id\":\"{owner}\"}}", Encoding.UTF8, "application/json")
        };

    private static string TokenFor(string? owner, DateTime? expiry = null)
    {
        Claim[] claims = owner is null
            ? []
            : [new Claim(AuthClaimTypes.UserProfileId, owner)];
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: claims, expires: expiry ?? DateTime.UtcNow.AddHours(1)));
    }

    private sealed class Scenario : IDisposable
    {
        private readonly HttpClient _client;
        private readonly ServiceProvider _services;

        public Scenario()
        {
            Auth.Token = TokenFor(Owner);
            Preferences.Set(UserProfileRepository.ActiveProfileIdKey, Owner);
            Scope.SetActiveUser(Owner);
            Server.ExpectedToken = Auth.Token;
            _client = new HttpClient(Server)
            {
                BaseAddress = new Uri("https://api.example.invalid/")
            };
            var services = new ServiceCollection();
            services.AddSingleton<IAuthService>(Auth);
            services.AddSingleton<IPreferencesService>(Preferences);
            services.AddSingleton<DeviceUserScopeProvider>(Scope);
            services.AddSingleton<IUserScopeProvider>(provider =>
                provider.GetRequiredService<DeviceUserScopeProvider>());
            services.AddSingleton<IConnectivityService>(Connectivity);
            services.AddSingleton<IRemoteSyncCompletion>(Sync);
            services.AddSingleton<IHttpClientFactory>(new SingleClientFactory(_client));
            services.AddSingleton<ILogger<OwnerBoundNativeSyncReadiness>>(
                NullLogger<OwnerBoundNativeSyncReadiness>.Instance);
            services.AddOwnerBoundNativeSyncReadiness();
            _services = services.BuildServiceProvider(validateScopes: true);
            Assert.Same(Scope, _services.GetRequiredService<IUserScopeProvider>());
            Readiness = _services.GetRequiredService<INativeSyncReadiness>();
        }

        public MutableAuth Auth { get; } = new();
        public MutablePreferences Preferences { get; } = new();
        public DeviceUserScopeProvider Scope { get; } = new();
        public MutableConnectivity Connectivity { get; } = new();
        public FakeRemoteSyncCompletion Sync { get; } = new();
        public ProfileHandler Server { get; } = new();
        public INativeSyncReadiness Readiness { get; }

        public void Dispose()
        {
            _services.Dispose();
            _client.Dispose();
        }
    }

    private sealed class FakeRemoteSyncCompletion : IRemoteSyncCompletion
    {
        public Func<CancellationToken, Task> Run { get; set; } = _ => Task.CompletedTask;
        public int Calls { get; private set; }

        public async Task SynchronizeForReadinessAsync(CancellationToken cancellationToken)
        {
            Calls++;
            await Run(cancellationToken);
        }
    }

    private sealed class ProfileHandler : HttpMessageHandler
    {
        public string? ExpectedToken { get; set; }
        public Func<CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } =
            _ => Task.FromResult(ProfileResponse(Owner));
        public int Calls { get; private set; }
        public string? Path { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Path = request.RequestUri?.AbsolutePath;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationToken = request.Headers.Authorization?.Parameter;
            if (AuthorizationScheme != "Bearer" || AuthorizationToken != ExpectedToken)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

            return Reply(cancellationToken);
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("AuthClient", name);
            return client;
        }
    }

    private sealed class MutableAuth : IAuthService
    {
        public string? Token { get; set; }
        public bool SignedIn { get; set; } = true;
        public bool IsSignedIn => SignedIn;
        public string? UserName => null;
        public int TokenRequests { get; private set; }
        public Task<string?> GetAccessTokenAsync(string[] scopes)
        {
            TokenRequests++;
            return Task.FromResult(Token);
        }
        public Task<bool> HasStoredSessionAsync() => Task.FromResult(SignedIn);
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

    private sealed class MutablePreferences : IPreferencesService
    {
        private readonly Dictionary<string, object> _values = new();

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
        public void Set<T>(string key, T value) => _values[key] = value!;
        public void Remove(string key) => _values.Remove(key);
        public void Clear() => _values.Clear();
    }

    private sealed class MutableConnectivity : IConnectivityService
    {
        public bool Available { get; set; } = true;
        public bool IsInternetAvailable => Available;
    }

    private sealed class LegacySuccessShapedSync : ISyncService
    {
        public int Calls { get; private set; }
        public bool IsInitialSyncInProgress => false;
        public event Action? InitialSyncCompleted { add { } remove { } }
        public Task InitializeDatabaseAsync() => Task.CompletedTask;
        public Task TriggerSyncAsync()
        {
            Calls++;
            return Task.CompletedTask;
        }
        public void BeginInitialSync() { }
    }
}
