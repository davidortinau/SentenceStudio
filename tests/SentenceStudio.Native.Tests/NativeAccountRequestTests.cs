using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Contracts;
using SentenceStudio.Native;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Tests;

public sealed class NativeAccountRequestTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericRegistrationResponseNeverSignsInOrDisclosesDuplicate(bool includesUserId)
    {
        using var fixture = new NativeSliceFixture();
        fixture.SetRegistrationResponder((_, _) =>
        {
            object body = includesUserId
                ? new { message = "Check your email", userId = "not-a-login-owner" }
                : new { message = "Check your email" };
            return Task.FromResult(Response(HttpStatusCode.OK, body));
        });

        var presenter = fixture.Auth;
        var result = await presenter.RegisterAsync(
            "new@example.invalid", "fixture-password", "Learner");
        Assert.Equal(NativeRegistrationOutcome.ConfirmationRequired, result.Outcome);
        Assert.Equal("NativeAuth_RegistrationAccepted", result.ErrorKey);
        Assert.Null(result.AuthState);
        Assert.Equal(NativePhase.Ready, presenter.State.Phase);
        Assert.Equal(NativeDestination.SignIn, presenter.State.Destination);
        Assert.Null(presenter.State.UserId);
        Assert.False(fixture.Provider.GetRequiredService<IAuthService>().IsSignedIn);
        Assert.Empty(fixture.Sync.RequestedOwners);
    }

    [Fact]
    public async Task IssuedRegistrationRequiresMatchingOwnerBeforePublishingSession()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        var token = TokenFor(NativeSliceFixture.UserA);
        fixture.SetRegistrationResponder((_, _) => Task.FromResult(Response(
            HttpStatusCode.OK, new {
                token, refreshToken = "fixture-refresh",
                expiresAt = DateTime.UtcNow.AddHours(1),
                userName = "Learner", userProfileId = NativeSliceFixture.UserA
            })));

        var presenter = fixture.Auth;
        var result = await presenter.RegisterAsync(
            NativeSliceFixture.EmailA, "fixture-password", "Learner");
        Assert.Equal(NativeRegistrationOutcome.SignedIn, result.Outcome);
        Assert.Equal(NativePhase.Ready, result.AuthState!.Phase);
        Assert.Equal(NativeSliceFixture.UserA, result.AuthState.UserId);
        Assert.Equal(NativeDestination.Dashboard, result.AuthState.Destination);
        Assert.Equal(NativeSliceFixture.UserA, presenter.State.UserId);
        Assert.Equal([NativeSliceFixture.UserA], fixture.Sync.RequestedOwners);
        Assert.Equal(token,
            await fixture.Provider.GetRequiredService<IAuthService>()
                .GetAccessTokenAsync([]));
    }

    [Fact]
    public async Task MismatchedIssuedTokenAndMalformedAcceptanceNeverStoreCredentials()
    {
        using var fixture = new NativeSliceFixture();
        fixture.SetRegistrationResponder((_, _) => Task.FromResult(Response(
            HttpStatusCode.OK, new {
                token = TokenFor(NativeSliceFixture.UserB),
                refreshToken = "fixture-refresh",
                expiresAt = DateTime.UtcNow.AddHours(1),
                userName = "Learner", userProfileId = NativeSliceFixture.UserA
            })));
        var presenter = fixture.Auth;
        Assert.Equal(NativeRegistrationOutcome.Rejected,
            (await presenter.RegisterAsync(
                NativeSliceFixture.EmailA, "fixture-password", "Learner")).Outcome);
        Assert.False(await fixture.Provider.GetRequiredService<IAuthService>()
            .HasStoredSessionAsync());
        Assert.Empty(fixture.Sync.RequestedOwners);

        fixture.SetRegistrationResponder((_, _) => Task.FromResult(Response(
            HttpStatusCode.OK, new { arbitrary = "not an accepted response" })));
        Assert.Equal(NativeRegistrationOutcome.Rejected,
            (await presenter.RegisterAsync(
                NativeSliceFixture.EmailA, "fixture-password", "Learner")).Outcome);
        Assert.False(fixture.Provider.GetRequiredService<IAuthService>().IsSignedIn);
    }

    [Fact]
    public async Task IssuedTokenWithFailedCredentialStorageNeverBecomesConfirmationSuccess()
    {
        using var fixture = new NativeSliceFixture();
        fixture.SecureStorage.RejectWrites = true;
        fixture.SetRegistrationResponder((_, _) => Task.FromResult(Response(
            HttpStatusCode.OK, new {
                token = TokenFor(NativeSliceFixture.UserA),
                refreshToken = "fixture-refresh",
                expiresAt = DateTime.UtcNow.AddHours(1),
                userName = "Learner",
                userProfileId = NativeSliceFixture.UserA
            })));

        var presenter = fixture.Auth;
        var result = await presenter.RegisterAsync(
            NativeSliceFixture.EmailA, "fixture-password", "Learner");
        Assert.Equal(NativeRegistrationOutcome.Failed, result.Outcome);
        Assert.NotEqual(NativeRegistrationOutcome.ConfirmationRequired, result.Outcome);
        Assert.Equal(NativeText.UnexpectedError, result.ErrorKey);
        Assert.Equal(NativePhase.Error, presenter.State.Phase);
        Assert.False(fixture.Provider.GetRequiredService<IAuthService>().IsSignedIn);
        Assert.False(await fixture.Provider.GetRequiredService<IAuthService>()
            .HasStoredSessionAsync());
        Assert.Empty(fixture.Sync.RequestedOwners);
    }

    [Fact]
    public async Task RegistrationAndRecoverySeparateRejectionsFromTransportAndAcceptedRequests()
    {
        using var fixture = new NativeSliceFixture();
        fixture.SetRegistrationResponder((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)));
        var presenter = fixture.Auth;
        Assert.Equal(NativeRegistrationOutcome.Rejected,
            (await presenter.RegisterAsync(
                NativeSliceFixture.EmailA, "fixture-password", "Learner")).Outcome);
        fixture.SetAuthOffline(true);
        Assert.Equal(NativeRegistrationOutcome.TransportFailure,
            (await presenter.RegisterAsync(
                NativeSliceFixture.EmailA, "fixture-password", "Learner")).Outcome);
        fixture.SetAuthOffline(false);

        fixture.SetRecoveryResponder((_, _) => Task.FromResult(Response(
            HttpStatusCode.OK, new {
                message = "If the account exists, a link has been sent."
            })));
        var accepted = await presenter.RequestPasswordRecoveryAsync(
            NativeSliceFixture.EmailA);
        Assert.Equal(NativePasswordRecoveryOutcome.Accepted, accepted.Outcome);
        Assert.Equal("ForgotPassword_ResetSent", accepted.ErrorKey);
        fixture.SetRecoveryResponder((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        Assert.Equal(NativePasswordRecoveryOutcome.Rejected,
            (await presenter.RequestPasswordRecoveryAsync(
                NativeSliceFixture.EmailA)).Outcome);
        fixture.SetAuthOffline(true);
        var offline = await presenter.RequestPasswordRecoveryAsync(
            NativeSliceFixture.EmailB);
        Assert.Equal(NativePasswordRecoveryOutcome.TransportFailure, offline.Outcome);
        Assert.Equal(NativeText.ConnectionError, offline.ErrorKey);
        Assert.False(fixture.Provider.GetRequiredService<IAuthService>().IsSignedIn);
    }

    [Fact]
    public async Task CancellingRegistrationDoesNotLeaveAnUnownedLoadingScreen()
    {
        using var fixture = new NativeSliceFixture();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetRegistrationResponder(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Response(HttpStatusCode.OK, new { message = "Not reached" });
        });
        var presenter = fixture.Auth;
        using var cancellation = new CancellationTokenSource();
        var pending = presenter.RegisterAsync(
            NativeSliceFixture.EmailA, "fixture-password", "Learner",
            cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(NativePhase.Ready, presenter.State.Phase);
        Assert.Equal(NativeDestination.SignIn, presenter.State.Destination);
        Assert.Null(presenter.State.UserId);
        Assert.False(fixture.Provider.GetRequiredService<IAuthService>().IsSignedIn);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, object body) =>
        new(status) { Content = JsonContent.Create(body) };

    private static string TokenFor(string owner) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim(AuthClaimTypes.UserProfileId, owner)],
            expires: DateTime.UtcNow.AddHours(1)));
}
