using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;

namespace SentenceStudio.Native.Tests;

public sealed class OwnerBoundNativeAiGatewayTests
{
    private const string OwnerA = NativeSliceFixture.UserA;
    private const string OwnerB = NativeSliceFixture.UserB;
    private static readonly Uri ApiRoot = new("https://api.example.invalid/");

    [Fact]
    public async Task PublishedOwnerAndMatchingTokenAuthorizeOnlyThatOwnersPrompt()
    {
        var scenario = new Scenario();
        var response = await scenario.Gateway.SendPromptAsync<string>(
            OwnerA, "owner A transcript", default);

        Assert.Equal("generated response", response);
        Assert.Equal(1, scenario.Server.Calls);
        Assert.Equal(OwnerA, scenario.Server.LastOwner);
        Assert.Equal("/api/v1/ai/chat", scenario.Server.LastPath);
        Assert.Equal(scenario.Auth.Token, scenario.Server.LastBearer);
        Assert.Equal(1, scenario.Auth.TokenRequests);
    }

    [Fact]
    public async Task AbsentOrMismatchedOwnerAndTokenNeverSendThePrompt()
    {
        var scenario = new Scenario();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            scenario.Gateway.SendPromptAsync<string>(
                OwnerB, "wrong owner transcript", default));
        scenario.Auth.Token = TokenFor(OwnerB);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            scenario.Gateway.SendPromptAsync<string>(
                OwnerA, "wrong token transcript", default));
        scenario.Auth.Token = TokenFor(OwnerA);
        scenario.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, OwnerB);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            scenario.Gateway.SendPromptAsync<string>(
                OwnerA, "stale preference transcript", default));
        Assert.Equal(0, scenario.Server.Calls);
    }

    [Fact]
    public async Task OwnerSwitchDuringTokenRefreshCannotSendThePrompt()
    {
        var scenario = new Scenario();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Auth.BeforeTokenAsync = async () =>
        {
            entered.SetResult();
            await release.Task;
        };
        var pending = scenario.Gateway.SendPromptAsync<string>(
            OwnerA, "private transcript", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scenario.Scope.SetActiveUser(OwnerB);
        scenario.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, OwnerB);
        release.SetResult();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pending);
        Assert.Equal(0, scenario.Server.Calls);
    }

    [Fact]
    public async Task OwnerSwitchDuringResponseCannotReturnStaleGeneratedContent()
    {
        var scenario = new Scenario();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Server.BeforeResponseAsync = async () =>
        {
            entered.SetResult();
            await release.Task;
        };
        var pending = scenario.Gateway.SendPromptAsync<string>(
            OwnerA, "private transcript", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scenario.Scope.SetActiveUser(OwnerB);
        scenario.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, OwnerB);
        release.SetResult();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pending);
        Assert.Equal(1, scenario.Server.Calls);
        Assert.Equal(OwnerA, scenario.Server.LastOwner);
    }

    [Fact]
    public async Task AnotherOriginCannotReceiveTheBearer()
    {
        var scenario = new Scenario();
        using var client = new HttpClient(new OwnerBoundNativeAiBearerHandler(
            scenario.Auth, ApiRoot, OwnerA, scenario.IsOwner)
        {
            InnerHandler = scenario.Server
        }, disposeHandler: false);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://other.example.invalid/api/v1/ai/chat");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            client.SendAsync(request));
        Assert.Equal(0, scenario.Server.Calls);
        Assert.Equal(0, scenario.Auth.TokenRequests);
    }

    [Fact]
    public void NativeRegistrationUsesOnlyTheStrictGatewayAndDisablesRedirects()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuthService>(new ScratchAuth());
        services.AddSingleton<IPreferencesService>(
            new NativeSliceFixture.ScratchPreferences());
        services.AddSingleton<DeviceUserScopeProvider>();
        services.AddOwnerBoundNativeAiGateway(ApiRoot);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IOwnerBoundNativeAiGateway>());
        Assert.Null(provider.GetService<IAiGatewayClient>());
        HttpMessageHandler handler = provider
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(OwnerBoundNativeAiGateway.ClientName);
        while (handler is DelegatingHandler wrapper)
            handler = wrapper.InnerHandler!;
        Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
    }

    private static string TokenFor(string owner) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim(AuthClaimTypes.UserProfileId, owner)],
            expires: DateTime.UtcNow.AddHours(1)));

    private sealed class Scenario
    {
        public readonly NativeSliceFixture.ScratchPreferences Preferences = new();
        public readonly DeviceUserScopeProvider Scope = new();
        public readonly ScratchAuth Auth = new();
        public readonly ScratchAiServer Server = new();
        public readonly OwnerBoundNativeAiGateway Gateway;

        public Scenario()
        {
            Assert.True(Scope.TryBeginAuthentication(null, out var transition));
            Assert.True(Scope.TryPublishAuthenticatedUser(
                OwnerA, transition, default, out _));
            Preferences.Set(UserProfileRepository.ActiveProfileIdKey, OwnerA);
            Auth.Token = TokenFor(OwnerA);
            Gateway = new OwnerBoundNativeAiGateway(
                Auth, Preferences, Scope, new ScratchHandlerFactory(Server),
                NullLogger<AiGatewayClient>.Instance, ApiRoot);
        }

        public bool IsOwner() =>
            Scope.TryGetUserProfileId(out var active) && active == OwnerA
            && Preferences.Get(UserProfileRepository.ActiveProfileIdKey,
                string.Empty) == OwnerA;
    }

    private sealed class ScratchHandlerFactory(HttpMessageHandler handler)
        : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name)
        {
            Assert.Equal(OwnerBoundNativeAiGateway.ClientName, name);
            return handler;
        }
    }

    private sealed class ScratchAiServer : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastOwner { get; private set; }
        public string? LastBearer { get; private set; }
        public string? LastPath { get; private set; }
        public Func<Task>? BeforeResponseAsync { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastBearer = request.Headers.Authorization?.Parameter;
            LastOwner = LastBearer is null ? null
                : new JwtSecurityTokenHandler().ReadJwtToken(LastBearer).Claims
                    .SingleOrDefault(claim =>
                        claim.Type == AuthClaimTypes.UserProfileId)?.Value;
            LastPath = request.RequestUri?.AbsolutePath;
            if (BeforeResponseAsync is { } beforeResponse)
                await beforeResponse();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { response = "generated response" })
            };
        }
    }

    private sealed class ScratchAuth : IAuthService
    {
        public string? Token { get; set; }
        public int TokenRequests { get; private set; }
        public Func<Task>? BeforeTokenAsync { get; set; }
        public bool IsSignedIn => Token is not null;
        public string? UserName => null;

        public async Task<string?> GetAccessTokenAsync(string[] scopes)
        {
            TokenRequests++;
            if (BeforeTokenAsync is { } beforeToken)
                await beforeToken();
            return Token;
        }

        public Task<AuthResult?> SignInAsync() => Task.FromResult<AuthResult?>(null);
        public Task<AuthResult?> SignInAsync(string email, string password) =>
            Task.FromResult<AuthResult?>(null);
        public Task<AuthResult?> RegisterAsync(
            string email, string password, string displayName) =>
            Task.FromResult<AuthResult?>(null);
        public Task SignOutAsync()
        {
            Token = null;
            return Task.CompletedTask;
        }
        public Task<bool> DeleteAccountAsync() => Task.FromResult(false);
        public Task<bool> ChangePasswordAsync(string currentPassword, string newPassword) =>
            Task.FromResult(false);
        public Task<bool> HasStoredSessionAsync() => Task.FromResult(false);
    }
}
