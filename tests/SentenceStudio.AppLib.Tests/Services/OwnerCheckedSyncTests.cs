using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using CoreSync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Services;

namespace SentenceStudio.AppLib.Tests.Services;

/// <summary>
/// The MAUI heads' owner-checked sync wiring (Captain 2026-11-04, decision 1): the sync owner is
/// the learner the sign-in token names, only while that learner is the active profile, and every
/// sync request then carries the bearer and owner header from that one token.
/// </summary>
public sealed class OwnerCheckedSyncTests
{
    private static readonly string Owner = Guid.NewGuid().ToString();

    [Fact]
    public async Task SignedInActiveLearner_IsTheSyncOwner()
    {
        var token = Token(Owner);
        var source = new SignedInSyncOwnerSource(Auth(token).Object, Preferences(Owner).Object);

        var current = await source.GetAsync(CancellationToken.None);

        current.Should().Be(new SyncOwnerCredentials(Owner, token));
    }

    [Fact]
    public async Task NoOwner_WhenSignedOut_ProfilesDisagree_OrTheTokenCannotNameOne()
    {
        var cases = new (Mock<IAuthService> Auth, Mock<IPreferencesService> Preferences)[]
        {
            (Auth(Token(Owner), signedIn: false), Preferences(Owner)),
            (Auth(Token(Owner)), Preferences(Guid.NewGuid().ToString())),
            (Auth(Token(Owner)), Preferences(string.Empty)),
            (Auth(Token(Owner, expired: true)), Preferences(Owner)),
            (Auth(Token("not-a-profile-id")), Preferences("not-a-profile-id")),
            (Auth("not a token"), Preferences(Owner)),
            (Auth(null), Preferences(Owner)),
        };
        foreach (var (auth, preferences) in cases)
            (await new SignedInSyncOwnerSource(auth.Object, preferences.Object)
                .GetAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AddOwnerCheckedSync_WrapsTheLocalStore_AndSendsTheOwnerHeader()
    {
        var token = Token(Owner);
        var captured = new CapturingHandler();
        await using var provider = MauiSyncServices(Auth(token), captured);

        provider.GetRequiredService<ISyncProvider>().Should().BeOfType<OwnerCheckedSyncProvider>();
        provider.GetRequiredService<ISyncResendRequester>()
            .Should().BeOfType<OwnerCheckedSyncResendRequester>();

        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ServiceCollectionExtentions.SyncHttpClientName);
        using var response = await client.GetAsync("/api/sync-agent/store-id");
        captured.Request!.Headers.Authorization!.Parameter.Should().Be(token);
        captured.Request.Headers.GetValues(SharedSyncRegistration.NativeOwnerHeader)
            .Should().Equal(Owner);
    }

    [Fact]
    public async Task WithoutAnOwner_NoSyncRequestLeaves()
    {
        var captured = new CapturingHandler();
        await using var provider = MauiSyncServices(Auth(Token(Owner), signedIn: false), captured);

        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ServiceCollectionExtentions.SyncHttpClientName);
        await FluentActions.Awaiting(() => client.GetAsync("/api/sync-agent/store-id"))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        captured.Request.Should().BeNull();
    }

    [Fact]
    public async Task ResendRequest_IsNotSent_ForAnOwnerWhoIsNoLongerSignedIn()
    {
        var captured = new CapturingHandler();
        await using var provider = MauiSyncServices(Auth(Token(Owner)), captured);
        var requester = provider.GetRequiredService<ISyncResendRequester>();

        (await requester.RequestAsync(Owner, ["MinimalPair"], CancellationToken.None)).Should().BeTrue();
        captured.Request!.RequestUri!.AbsolutePath.Should().Be(SyncResendQueue.Route);

        captured.Request = null;
        await FluentActions.Awaiting(() => requester.RequestAsync(
                Guid.NewGuid().ToString(), ["MinimalPair"], CancellationToken.None))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        captured.Request.Should().BeNull();
    }

    /// <summary>The MAUI registration order: sync, owner-checked sync, then auth.</summary>
    private static ServiceProvider MauiSyncServices(Mock<IAuthService> auth, CapturingHandler captured)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(auth.Object);
        services.AddSingleton(Preferences(Owner).Object);
        services.AddSyncServices(
            Path.Combine(Path.GetTempPath(), $"owner-checked-{Guid.NewGuid():N}.db3"),
            new Uri("https://api.example.test"));
        services.AddOwnerCheckedSync();
        services.AddTransient<AuthenticatedHttpMessageHandler>();
        services.AddHttpClient(ServiceCollectionExtentions.SyncHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => captured);
        return services.BuildServiceProvider();
    }

    private static string Token(string owner, bool expired = false)
    {
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim(AuthClaimTypes.UserProfileId, owner)],
            notBefore: now.AddHours(-2),
            expires: expired ? now.AddMinutes(-5) : now.AddMinutes(30)));
    }

    private static Mock<IAuthService> Auth(string? token, bool signedIn = true)
    {
        var auth = new Mock<IAuthService>();
        auth.SetupGet(service => service.IsSignedIn).Returns(signedIn);
        auth.Setup(service => service.GetAccessTokenAsync(It.IsAny<string[]>())).ReturnsAsync(token);
        return auth;
    }

    private static Mock<IPreferencesService> Preferences(string activeProfile)
    {
        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(service => service.Get(UserProfileRepository.ActiveProfileIdKey, It.IsAny<string>()))
            .Returns(activeProfile);
        return preferences;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
