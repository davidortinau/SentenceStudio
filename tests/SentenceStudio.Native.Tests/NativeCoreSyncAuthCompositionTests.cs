using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Native.Tests;

public sealed class NativeCoreSyncAuthCompositionTests
{
    private const string Owner = "native-owner-a";
    private const string OtherOwner = "native-owner-b";

    [Fact]
    public async Task NormalCoreSyncRegistration_ResolvesRealReadinessAndSendsOwnerBearer()
    {
        using var scenario = new Scenario();
        Assert.IsType<OwnerBoundNativeSyncReadiness>(
            scenario.Services.GetRequiredService<INativeSyncReadiness>());
        Assert.Same(scenario.Services.GetRequiredService<ISyncService>(),
            scenario.Services.GetRequiredService<IRemoteSyncCompletion>());
        Assert.IsType<SyncService>(
            scenario.Services.GetRequiredService<IRemoteSyncCompletion>());

        using (scenario.EnterReadiness())
            await scenario.SendAsync();

        Assert.Equal(1, scenario.Server.Calls);
        Assert.Equal("Bearer", scenario.Server.AuthorizationScheme);
        Assert.Equal(scenario.Auth.Token, scenario.Server.AuthorizationToken);
    }

    [Fact]
    public async Task UnpublishedTransition_CanSendOwnerBearerOnlyInsideReadinessLease()
    {
        using var scenario = new Scenario(publishOwner: false);
        Assert.Null(scenario.Scope.CaptureStamp().PublishedOwner);
        Assert.False(scenario.Scope.TryGetUserProfileId(out _));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(scenario.SendAsync);
        using (scenario.EnterReadiness())
        {
            Assert.True(scenario.Scope.TryGetLeasedReadinessOwner(out var owner));
            Assert.Equal(Owner, owner);
            Assert.False(scenario.Scope.TryGetPublishedReadinessOwner(out _));
            await scenario.SendAsync();
        }

        Assert.Equal(1, scenario.Server.Calls);
        Assert.Equal(scenario.Auth.Token, scenario.Server.AuthorizationToken);
        Assert.Null(scenario.Scope.CaptureStamp().PublishedOwner);
        Assert.False(scenario.Scope.TryGetUserProfileId(out _));
    }

    [Fact]
    public async Task PublishedOwnerWithoutReadinessLease_CannotSendCoreSync()
    {
        using var scenario = new Scenario();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(scenario.SendAsync);
        Assert.Equal(0, scenario.Server.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TokenFailure_CannotUseExistingHandlersAnonymousFallback(bool publishOwner)
    {
        using var scenario = new Scenario(publishOwner);
        scenario.Auth.Acquire = () =>
            throw new InvalidOperationException("Synthetic token acquisition failure.");

        using (scenario.EnterReadiness())
            await Assert.ThrowsAsync<InvalidOperationException>(scenario.SendAsync);
        Assert.Equal(0, scenario.Server.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(OtherOwner)]
    public async Task MissingOrForeignJwtOwner_CannotSendCoreSync(string? tokenOwner)
    {
        using var scenario = new Scenario();
        scenario.Auth.Token = TokenFor(tokenOwner);

        using (scenario.EnterReadiness())
            await Assert.ThrowsAsync<UnauthorizedAccessException>(scenario.SendAsync);
        Assert.Equal(0, scenario.Server.Calls);
    }

    [Fact]
    public async Task MismatchedProfilePointer_CannotSendCoreSync()
    {
        using var scenario = new Scenario();
        scenario.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, OtherOwner);

        using (scenario.EnterReadiness())
            await Assert.ThrowsAsync<UnauthorizedAccessException>(scenario.SendAsync);
        Assert.Equal(0, scenario.Server.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnerSwitchDuringBearerAcquisition_CannotSendUnderNewOwner(
        bool publishOwner)
    {
        using var scenario = new Scenario(publishOwner);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Auth.Acquire = async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return scenario.Auth.Token;
        };

        using (scenario.EnterReadiness())
        {
            var pending = scenario.SendAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            scenario.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, OtherOwner);
            scenario.Scope.SetActiveUser(OtherOwner);
            scenario.Auth.Token = TokenFor(OtherOwner);
            release.TrySetResult();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pending);
        }

        Assert.Equal(0, scenario.Server.Calls);
        Assert.True(scenario.Scope.TryGetUserProfileId(out var currentOwner));
        Assert.Equal(OtherOwner, currentOwner);
    }

    [Fact]
    public async Task RevokedTransitionInheritedByChild_CannotSendOrFallBackToNewOwner()
    {
        using var scenario = new Scenario(publishOwner: false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task send;
        using (scenario.EnterReadiness())
        {
            send = Task.Run(async () =>
            {
                await release.Task;
                await scenario.SendAsync();
            });
        }

        scenario.Scope.SetActiveUser(OtherOwner);
        scenario.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, OtherOwner);
        scenario.Auth.Token = TokenFor(OtherOwner);
        release.SetResult();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => send);
        Assert.Equal(0, scenario.Server.Calls);
    }

    private static string TokenFor(string? owner)
    {
        Claim[] claims = owner is null
            ? []
            : [new Claim(AuthClaimTypes.UserProfileId, owner)];
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: claims, expires: DateTime.UtcNow.AddHours(1)));
    }

    private sealed class Scenario : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly DeviceUserScopeProvider.ScopeStamp _readinessStamp;
        private readonly HttpClient _client;

        public Scenario(bool publishOwner = true)
        {
            Auth.Token = TokenFor(Owner);
            Preferences.Set(UserProfileRepository.ActiveProfileIdKey, Owner);
            Assert.True(Scope.TryBeginAuthentication(null, out var transition));
            if (publishOwner)
            {
                Assert.True(Scope.TryPublishAuthenticatedUser(
                    Owner, transition, CancellationToken.None, out var published));
                _readinessStamp = published;
            }
            else
            {
                _readinessStamp = transition;
            }

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IAuthService>(Auth);
            services.AddSingleton<IPreferencesService>(Preferences);
            services.AddSingleton(Scope);
            services.AddSingleton<IUserScopeProvider>(sp =>
                sp.GetRequiredService<DeviceUserScopeProvider>());
            services.AddSingleton<IConnectivityService>(new OnlineConnectivity());
            services.AddSyncServices(
                Path.Combine(Path.GetTempPath(), $"native-auth-{Guid.NewGuid():N}.sqlite"),
                new Uri("https://api.example.invalid/"));
            services.AddSingleton<ISyncService, SyncService>();
            services.AddOwnerBoundNativeSyncReadiness();
            services.AddHttpClient("HttpClientToServer")
                .ConfigurePrimaryHttpMessageHandler(() => Server);
            _services = services.BuildServiceProvider(validateScopes: true);
            _client = _services.GetRequiredService<IHttpClientFactory>()
                .CreateClient("HttpClientToServer");
        }

        public FakeAuth Auth { get; } = new();
        public Preferences Preferences { get; } = new();
        public DeviceUserScopeProvider Scope { get; } = new();
        public RecordingServer Server { get; } = new();
        public IServiceProvider Services => _services;

        public DeviceUserScopeProvider.ReadinessLease EnterReadiness() =>
            Scope.TryBeginReadiness(Owner, _readinessStamp)
            ?? throw new InvalidOperationException("The owner readiness lease was refused.");

        public async Task SendAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sync");
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public void Dispose()
        {
            _client.Dispose();
            _services.Dispose();
        }
    }

    private sealed class FakeAuth : IAuthService
    {
        public string? Token { get; set; }
        public Func<Task<string?>>? Acquire { get; set; }
        public bool IsSignedIn => true;
        public string? UserName => null;
        public Task<string?> GetAccessTokenAsync(string[] scopes) =>
            Acquire?.Invoke() ?? Task.FromResult(Token);
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

    private sealed class RecordingServer : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationToken = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
