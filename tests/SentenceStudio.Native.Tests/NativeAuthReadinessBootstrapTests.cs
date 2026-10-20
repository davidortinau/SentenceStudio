using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Tests;

public sealed class NativeAuthReadinessBootstrapTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task InvalidCredentialsLeaveSignInAvailableAndAllowCorrectedSubmission(
        HttpStatusCode rejection)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        fixture.SetAuthStatus(rejection);
        var auth = fixture.Auth;

        var rejected = await auth.SignInAsync(
            NativeSliceFixture.EmailA, "bad-password");
        Assert.Equal(NativePhase.Error, rejected.Phase);
        Assert.Equal(NativeDestination.SignIn, rejected.Destination);
        Assert.Equal(NativeText.InvalidCredentials, rejected.ErrorKey);
        Assert.Null(await fixture.SecureStorage.GetAsync(AuthTokenStore.JwtKey));
        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));

        fixture.SetAuthStatus(null);
        var corrected = await auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");
        Assert.Equal(NativePhase.Ready, corrected.Phase);
        Assert.Equal(NativeDestination.Dashboard, corrected.Destination);
    }

    [Fact]
    public async Task IncompleteOwnedProfileRoutesOnboardingWithoutRemoteConfirmation()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        using (var serviceScope = fixture.Scope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var incomplete = await db.UserProfiles.SingleAsync(profile =>
                profile.Id == NativeSliceFixture.UserA);
            incomplete.TargetLanguage = "";
            await db.SaveChangesAsync();
        }
        fixture.SetConnectivity(false);
        var auth = fixture.Auth;
        var routed = await auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");

        Assert.Equal(NativePhase.Ready, routed.Phase);
        Assert.Equal(NativeDestination.Onboarding, routed.Destination);
        Assert.Equal(NativeSliceFixture.UserA, routed.UserId);
        Assert.True(routed.HasPendingSync);
        Assert.False(fixture.Preferences.Get("is_onboarded", false));
        Assert.True(fixture.UserScope.TryGetUserProfileId(out var active));
        Assert.Equal(NativeSliceFixture.UserA, active);
        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Equal(NativePhase.Blocked, auth.State.Phase);
        Assert.Null(auth.State.UserId);
    }

    [Fact]
    public async Task FreshSignIn_PublishesTrustedOwnerOnlyAfterVerifiedSync_ThenRoutesDashboard()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        var auth = fixture.Auth;
        auth.StateChanged += state =>
        {
            if (state.Phase == NativePhase.Loading
                && state.Destination == NativeDestination.Dashboard)
            {
                Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
                Assert.False(fixture.UserScope.TryGetLeasedReadinessOwner(out _));
            }
        };
        fixture.RemoteSync.RunAsync = async cancellationToken =>
        {
            Assert.True(fixture.UserScope.TryGetUserProfileId(out var scopedOwner));
            Assert.Equal(NativeSliceFixture.UserA, scopedOwner);
            Assert.True(fixture.UserScope.TryGetLeasedReadinessOwner(out var leasedOwner));
            Assert.Equal(NativeSliceFixture.UserA, leasedOwner);
            Assert.False(fixture.UserScope.TryGetPublishedReadinessOwner(out _));
            Assert.Null(fixture.UserScope.CaptureStamp().PublishedOwner);
            await fixture.SeedProfileAsync(scopedOwner);
            cancellationToken.ThrowIfCancellationRequested();
        };

        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
        var signedIn = await auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");

        Assert.Equal(NativePhase.Ready, signedIn.Phase);
        Assert.Equal(NativeDestination.Dashboard, signedIn.Destination);
        Assert.Equal(NativeSliceFixture.UserA, signedIn.UserId);
        Assert.False(signedIn.HasPendingSync);
        Assert.Equal(1, fixture.RemoteSync.Calls);
        Assert.Equal(1, fixture.RemoteProfileRequests);
        Assert.True(fixture.UserScope.TryGetUserProfileId(out var publishedOwner));
        Assert.Equal(NativeSliceFixture.UserA, publishedOwner);
        Assert.False(fixture.UserScope.TryGetLeasedReadinessOwner(out _));

        Assert.Equal(signedIn, await auth.RetryReadinessAsync());
        Assert.Equal(1, fixture.RemoteSync.Calls);
        Assert.Equal(1, fixture.RemoteProfileRequests);
    }

    [Fact]
    public async Task InFlightSync_DoesNotExposeUnpublishedOwnerToIndependentReader()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var independentReader = Task.Run(async () =>
        {
            await entered.Task;
            return fixture.UserScope.TryGetUserProfileId(out var owner) ? owner : null;
        });
        fixture.RemoteSync.RunAsync = async cancellationToken =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        };

        var auth = fixture.Auth;
        var signingIn = auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password");
        try
        {
            Assert.Null(await independentReader.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
            Assert.Null(fixture.UserScope.CaptureStamp().PublishedOwner);
            Assert.Equal(NativePhase.Loading, auth.State.Phase);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(NativeDestination.Dashboard,
            (await signingIn.WaitAsync(TimeSpan.FromSeconds(5))).Destination);
        Assert.True(fixture.UserScope.TryGetUserProfileId(out var publishedOwner));
        Assert.Equal(NativeSliceFixture.UserA, publishedOwner);
    }

    [Fact]
    public async Task StoredTokenRestore_RevalidatesOwnerFromEmptyScopeWithoutDuplicateBootstrap()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        var original = await fixture.Auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");
        Assert.Equal(NativeDestination.Dashboard, original.Destination);
        Assert.Equal(1, fixture.RemoteSync.Calls);

        fixture.Restart();
        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
        var restoredAuth = fixture.Auth;
        restoredAuth.StateChanged += state =>
        {
            if (state.Phase == NativePhase.Loading
                && state.Destination == NativeDestination.Dashboard)
            {
                Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
                Assert.False(fixture.UserScope.TryGetLeasedReadinessOwner(out _));
            }
        };
        var restored = await restoredAuth.RestoreAsync();

        Assert.Equal(NativePhase.Ready, restored.Phase);
        Assert.Equal(NativeDestination.Dashboard, restored.Destination);
        Assert.Equal(NativeSliceFixture.UserA, restored.UserId);
        Assert.Equal(2, fixture.RemoteSync.Calls);
        Assert.Equal(2, fixture.RemoteProfileRequests);
        Assert.Equal(restored, await restoredAuth.RetryReadinessAsync());
        Assert.Equal(restored, await restoredAuth.RestoreAsync());
        Assert.Equal(2, fixture.RemoteSync.Calls);
    }

    [Fact]
    public async Task OwnerlessOfflineRestore_RetryStaysPendingInsteadOfInvalidCredentials()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(
                NativeSliceFixture.EmailA, "fixture-password")).Phase);
        fixture.Restart();
        await fixture.SecureStorage.SetAsync(
            AuthTokenStore.ExpiresKey,
            DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));
        fixture.SetAuthOffline(true);

        var auth = fixture.Auth;
        var first = await auth.RestoreAsync();
        Assert.Equal(NativePhase.PendingSync, first.Phase);
        Assert.Null(first.UserId);
        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));

        var retried = await auth.RetryReadinessAsync();
        Assert.Equal(NativePhase.PendingSync, retried.Phase);
        Assert.Equal(NativeDestination.SignIn, retried.Destination);
        Assert.Null(retried.UserId);
        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
        Assert.Equal(1, fixture.RemoteSync.Calls);
    }

    [Fact]
    public async Task OfflineMissingLocalProfile_StaysPendingUntilExplicitConfirmedRetry()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        fixture.SetConnectivity(false);
        var auth = fixture.Auth;
        auth.StateChanged += state =>
        {
            if (state is { Phase: NativePhase.PendingSync, UserId: NativeSliceFixture.UserA })
            {
                Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
                Assert.False(fixture.UserScope.TryGetLeasedReadinessOwner(out _));
            }
        };

        var pending = await auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");
        Assert.Equal(NativePhase.PendingSync, pending.Phase);
        Assert.Equal(NativeDestination.SignIn, pending.Destination);
        Assert.Equal(NativeSliceFixture.UserA, pending.UserId);
        Assert.True(pending.HasPendingSync);
        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
        Assert.Null(fixture.UserScope.CaptureStamp().PublishedOwner);
        Assert.False(fixture.UserScope.TryGetPublishedReadinessOwner(out _));
        Assert.Equal(0, fixture.RemoteSync.Calls);
        Assert.Equal(0, fixture.RemoteProfileRequests);

        fixture.SetConnectivity(true);
        fixture.RemoteSync.RunAsync = async _ =>
            await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        var ready = await auth.RetryReadinessAsync();
        Assert.Equal(NativePhase.Ready, ready.Phase);
        Assert.Equal(NativeDestination.Dashboard, ready.Destination);
        Assert.Equal(1, fixture.RemoteSync.Calls);
        Assert.Equal(1, fixture.RemoteProfileRequests);
        Assert.True(fixture.UserScope.TryGetUserProfileId(out var publishedOwner));
        Assert.Equal(NativeSliceFixture.UserA, publishedOwner);
    }

    [Fact]
    public async Task SwallowedStyleFailureAndAbsentServerOwner_NeverConfirmOrNavigate()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        var auth = fixture.Auth;
        fixture.RemoteSync.RunAsync = _ =>
            throw new InvalidOperationException("Synthetic remote sync failure.");

        var failed = await auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");
        Assert.Equal(NativePhase.PendingSync, failed.Phase);
        Assert.Equal(1, fixture.RemoteSync.Calls);
        Assert.Equal(0, fixture.RemoteProfileRequests);
        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));

        fixture.RemoteSync.RunAsync = _ => Task.CompletedTask;
        fixture.SetRemoteProfileStatus(HttpStatusCode.NotFound);
        var absent = await auth.RetryReadinessAsync();
        Assert.Equal(NativePhase.PendingSync, absent.Phase);
        Assert.Equal(2, fixture.RemoteSync.Calls);
        Assert.Equal(1, fixture.RemoteProfileRequests);
        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));

        fixture.SetRemoteProfileStatus(null);
        fixture.RemoteSync.RunAsync = async _ =>
            await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        Assert.Equal(NativeDestination.Dashboard,
            (await auth.RetryReadinessAsync()).Destination);
        Assert.Equal(3, fixture.RemoteSync.Calls);
        Assert.True(fixture.UserScope.TryGetUserProfileId(out var confirmedOwner));
        Assert.Equal(NativeSliceFixture.UserA, confirmedOwner);
    }

    [Theory]
    [InlineData("")]
    [InlineData(NativeSliceFixture.UserB)]
    public async Task MissingOrForeignJwtOwner_RejectsBeforeAnyRemoteSync(string tokenOwner)
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        fixture.SetTokenOwner(tokenOwner);
        var denied = await fixture.Auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");

        Assert.Equal(NativePhase.Blocked, denied.Phase);
        Assert.Equal(NativeDestination.SignIn, denied.Destination);
        Assert.Equal(0, fixture.RemoteSync.Calls);
        Assert.Equal(0, fixture.RemoteProfileRequests);
        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
    }

    [Fact]
    public async Task DuplicateJwtOwnerClaim_RejectsBeforeRemoteSync()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        fixture.SetDuplicateTokenOwners(true);

        Assert.Equal(NativePhase.Blocked,
            (await fixture.Auth.SignInAsync(
                NativeSliceFixture.EmailA, "fixture-password")).Phase);
        Assert.Equal(0, fixture.RemoteSync.Calls);
        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
    }

    [Fact]
    public async Task FreshSignIn_WithForeignProfilePointer_RejectsBeforeReadiness()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        fixture.Preferences.Written += (key, value) =>
        {
            if (key == UserProfileRepository.ActiveProfileIdKey
                && value is string owner && owner == NativeSliceFixture.UserA)
                fixture.Preferences.Set(key, NativeSliceFixture.UserB);
        };

        var denied = await fixture.Auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");

        Assert.Equal(NativePhase.Blocked, denied.Phase);
        Assert.Equal(0, fixture.RemoteSync.Calls);
        Assert.Equal(0, fixture.RemoteProfileRequests);
        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
    }

    [Fact]
    public async Task StoredTokenRestore_WithForeignProfilePointer_RejectsBeforeReadiness()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(
                NativeSliceFixture.EmailA, "fixture-password")).Phase);
        fixture.Restart();
        fixture.Preferences.Set(UserProfileRepository.ActiveProfileIdKey,
            NativeSliceFixture.UserB);

        Assert.Equal(NativePhase.Blocked, (await fixture.Auth.RestoreAsync()).Phase);
        Assert.Equal(1, fixture.RemoteSync.Calls);
        Assert.Equal(1, fixture.RemoteProfileRequests);
        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(NativeSliceFixture.UserB)]
    public async Task StoredTokenRestore_WithMissingOrForeignJwtOwner_RejectsBeforeReadiness(
        string tokenOwner)
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(
                NativeSliceFixture.EmailA, "fixture-password")).Phase);
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim(AuthClaimTypes.UserProfileId, tokenOwner)],
            expires: DateTime.UtcNow.AddHours(1)));
        await fixture.SecureStorage.SetAsync(AuthTokenStore.JwtKey, token);
        fixture.Restart();

        Assert.Equal(NativePhase.Blocked, (await fixture.Auth.RestoreAsync()).Phase);
        Assert.Equal(1, fixture.RemoteSync.Calls);
        Assert.Equal(1, fixture.RemoteProfileRequests);
        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
    }

    [Fact]
    public async Task SwitchingToAnotherUserDuringSync_DoesNotPublishOrSignOutTheNewUser()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RemoteSync.RunAsync = async cancellationToken =>
        {
            if (fixture.RemoteSync.Calls == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };

        var oldAuth = fixture.Auth;
        var pendingOld = oldAuth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(fixture.UserScope.TryBeginReadiness(
            NativeSliceFixture.UserB,
            new DeviceUserScopeProvider.ScopeStamp(-1, null)));

        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
        Assert.Null(fixture.UserScope.CaptureStamp().PublishedOwner);
        fixture.LoseOwner();
        var originalStamp = fixture.UserScope.CaptureStamp();
        var newAuth = fixture.Auth;
        var pendingNew = newAuth.SignInAsync(
            NativeSliceFixture.EmailB, "fixture-password");
        await WaitForStampChangeAsync(fixture.UserScope, originalStamp);
        release.TrySetResult();

        Assert.Equal(NativePhase.Blocked, (await pendingOld).Phase);
        var newOwner = await pendingNew.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NativePhase.Ready, newOwner.Phase);
        Assert.Equal(NativeSliceFixture.UserB, newOwner.UserId);
        Assert.True(fixture.UserScope.TryGetUserProfileId(out var scopedOwner));
        Assert.Equal(NativeSliceFixture.UserB, scopedOwner);
        Assert.Equal(NativeSliceFixture.UserB,
            fixture.Preferences.Get(UserProfileRepository.ActiveProfileIdKey, ""));
        Assert.Equal(NativePhase.Blocked, (await oldAuth.SignOutAsync()).Phase);
        Assert.True(fixture.UserScope.TryGetUserProfileId(out scopedOwner));
        Assert.Equal(NativeSliceFixture.UserB, scopedOwner);
        Assert.Equal(NativeDestination.Dashboard, newAuth.State.Destination);
        Assert.Equal(2, fixture.RemoteSync.Calls);
    }

    [Fact]
    public async Task SlowOldLogin_CannotOverwriteTheNewerOwnersStoredCredentials()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetLoginResponseHook(async (email, cancellationToken) =>
        {
            if (email == NativeSliceFixture.EmailA)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        });

        var oldAuth = fixture.Auth;
        var pendingOld = oldAuth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var originalStamp = fixture.UserScope.CaptureStamp();
        var newAuth = fixture.Auth;
        var pendingNew = newAuth.SignInAsync(
            NativeSliceFixture.EmailB, "fixture-password");
        await WaitForStampChangeAsync(fixture.UserScope, originalStamp);
        release.TrySetResult();

        Assert.Equal(NativePhase.Blocked, (await pendingOld).Phase);
        var signedIn = await pendingNew.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NativePhase.Ready, signedIn.Phase);
        Assert.Equal(NativeSliceFixture.UserB, signedIn.UserId);
        Assert.Equal(NativeSliceFixture.UserB,
            fixture.Preferences.Get(UserProfileRepository.ActiveProfileIdKey, ""));
        Assert.True(fixture.UserScope.TryGetUserProfileId(out var owner));
        Assert.Equal(NativeSliceFixture.UserB, owner);
        var stored = await fixture.SecureStorage.GetAsync(AuthTokenStore.JwtKey);
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(stored).Claims;
        Assert.Equal(NativeSliceFixture.UserB,
            Assert.Single(claims, claim => claim.Type == AuthClaimTypes.UserProfileId).Value);
        Assert.Equal(1, fixture.RemoteSync.Calls);
        Assert.Equal(1, fixture.RemoteProfileRequests);
    }

    [Fact]
    public async Task PublishedAuthStateRedactsItselfAfterTheDeviceOwnerSwitches()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        var oldAuth = fixture.Auth;
        Assert.Equal(NativeDestination.Dashboard,
            (await oldAuth.SignInAsync(
                NativeSliceFixture.EmailA, "fixture-password")).Destination);

        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Equal(NativePhase.Blocked, oldAuth.State.Phase);
        Assert.Null(oldAuth.State.UserId);
        Assert.Equal(NativePhase.Blocked,
            (await oldAuth.RetryReadinessAsync()).Phase);
        Assert.Equal(NativePhase.Blocked,
            (await oldAuth.SignOutAsync()).Phase);
        Assert.True(fixture.UserScope.TryGetUserProfileId(out var active));
        Assert.Equal(NativeSliceFixture.UserB, active);
        Assert.Equal(1, fixture.RemoteSync.Calls);
    }

    [Fact]
    public async Task OwnerSwitchAfterReadinessBeforeFinalPublicationCheck_DoesNotSetOnboardedOrClearNewOwner()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        fixture.MarkPendingForTest(NativeSliceFixture.UserA);
        using var serviceScope = fixture.Scope();
        var services = serviceScope.ServiceProvider;
        var auth = new ExpiringAuth(services.GetRequiredService<IAuthService>())
        {
            AccessTokenRequested = () =>
            {
                if (fixture.UserScope.CaptureStamp().PublishedOwner == NativeSliceFixture.UserA)
                {
                    fixture.UserScope.SetActiveUser(NativeSliceFixture.UserB);
                    fixture.Preferences.Set(
                        UserProfileRepository.ActiveProfileIdKey, NativeSliceFixture.UserB);
                }
            }
        };
        var presenter = new NativeAuthPresenter(
            auth, fixture.Preferences,
            services.GetRequiredService<UserProfileRepository>(),
            fixture.UserScope,
            services.GetRequiredService<IPostLoginRouter>(),
            services.GetRequiredService<INativeSyncReadiness>(),
            services.GetRequiredService<NativePendingWrites>(),
            services.GetRequiredService<NativeResourcePresenter>(),
            services.GetRequiredService<SkillProfileRepository>(),
            services.GetRequiredService<ILogger<NativeAuthPresenter>>());

        var result = await presenter.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password");

        Assert.Equal(NativePhase.Blocked, result.Phase);
        Assert.Equal(NativeDestination.SignIn, result.Destination);
        Assert.True(fixture.UserScope.TryGetUserProfileId(out var activeOwner));
        Assert.Equal(NativeSliceFixture.UserB, activeOwner);
        Assert.Equal(NativeSliceFixture.UserB,
            fixture.Preferences.Get(UserProfileRepository.ActiveProfileIdKey, ""));
        Assert.False(fixture.Preferences.Get("is_onboarded", false));
        Assert.Equal(1, fixture.RemoteSync.Calls);
        Assert.Equal(1, fixture.RemoteProfileRequests);
        Assert.True(services.GetRequiredService<NativePendingWrites>()
            .IsPending(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task PendingReadinessRedactsItselfAfterTheDeviceOwnerSwitches()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        fixture.SetRemoteProfileStatus(HttpStatusCode.NotFound);
        var auth = fixture.Auth;
        Assert.Equal(NativePhase.PendingSync,
            (await auth.SignInAsync(
                NativeSliceFixture.EmailA, "fixture-password")).Phase);

        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Equal(NativePhase.Blocked, auth.State.Phase);
        Assert.Null(auth.State.UserId);
        Assert.Equal(NativePhase.Blocked, (await auth.RetryReadinessAsync()).Phase);
        Assert.True(fixture.UserScope.TryGetUserProfileId(out var active));
        Assert.Equal(NativeSliceFixture.UserB, active);
        Assert.Equal(1, fixture.RemoteSync.Calls);
    }

    [Fact]
    public async Task ExpiredAccessToken_DoesNotPreventCurrentOwnerFromRemovingStoredCredentials()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        using var serviceScope = fixture.Scope();
        var services = serviceScope.ServiceProvider;
        var expiringAuth = new ExpiringAuth(services.GetRequiredService<IAuthService>());
        var presenter = new NativeAuthPresenter(
            expiringAuth, fixture.Preferences,
            services.GetRequiredService<UserProfileRepository>(),
            fixture.UserScope,
            services.GetRequiredService<IPostLoginRouter>(),
            services.GetRequiredService<INativeSyncReadiness>(),
            services.GetRequiredService<NativePendingWrites>(),
            services.GetRequiredService<NativeResourcePresenter>(),
            services.GetRequiredService<SkillProfileRepository>(),
            services.GetRequiredService<ILogger<NativeAuthPresenter>>());

        Assert.Equal(NativePhase.Ready,
            (await presenter.SignInAsync(
                NativeSliceFixture.EmailA, "fixture-password")).Phase);
        expiringAuth.Expired = true;

        var signedOut = await presenter.SignOutAsync();
        Assert.Equal(NativePhase.Ready, signedOut.Phase);
        Assert.Equal(NativeDestination.SignIn, signedOut.Destination);
        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
        Assert.Null(await fixture.SecureStorage.GetAsync(AuthTokenStore.JwtKey));
        Assert.Null(await fixture.SecureStorage.GetAsync(AuthTokenStore.RefreshKey));
        Assert.Equal(string.Empty,
            fixture.Preferences.Get(UserProfileRepository.ActiveProfileIdKey, ""));
    }

    [Fact]
    public async Task CancellationDuringAwaitedSync_RevokesScopeAndAllowsStoredTokenRestart()
    {
        using var fixture = new NativeSliceFixture(ownerBoundReadiness: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RemoteSync.RunAsync = async cancellationToken =>
        {
            Assert.True(fixture.UserScope.TryGetLeasedReadinessOwner(out var owner));
            Assert.Equal(NativeSliceFixture.UserA, owner);
            Assert.False(fixture.UserScope.TryGetPublishedReadinessOwner(out _));
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Auth.SignInAsync(
            NativeSliceFixture.EmailA, "fixture-password",
            cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(fixture.UserScope.TryGetUserProfileId(out _));
        Assert.Equal(1, fixture.RemoteSync.Calls);
        Assert.Equal(0, fixture.RemoteProfileRequests);

        fixture.RemoteSync.RunAsync = async _ =>
            await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        fixture.Restart();
        Assert.Equal(NativeDestination.Dashboard,
            (await fixture.Auth.RestoreAsync()).Destination);
        Assert.Equal(2, fixture.RemoteSync.Calls);
    }

    [Fact]
    public async Task RevokedLease_InheritedByDetachedTaskNeverFallsBackToNewPublishedOwner()
    {
        var scope = new DeviceUserScopeProvider();
        Assert.True(scope.TryBeginAuthentication(null, out var transition));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(bool Found, string Owner, bool Leased)> inherited;
        using (var lease = scope.TryBeginReadiness(
            NativeSliceFixture.UserA, transition)!)
        {
            Assert.True(scope.TryGetUserProfileId(out var temporary));
            Assert.Equal(NativeSliceFixture.UserA, temporary);
            inherited = Task.Run(async () =>
            {
                await release.Task;
                var found = scope.TryGetUserProfileId(out var owner);
                return (found, owner, scope.TryGetLeasedReadinessOwner(out _));
            });
        }

        scope.SetActiveUser(NativeSliceFixture.UserB);
        release.TrySetResult();
        var after = await inherited.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(after.Found);
        Assert.Equal(string.Empty, after.Owner);
        Assert.False(after.Leased);
        Assert.True(scope.TryGetUserProfileId(out var published));
        Assert.Equal(NativeSliceFixture.UserB, published);
    }

    [Fact]
    public void ScopeStampRejectsOwnerSwitchAndReturnToTheSameOwner()
    {
        var scope = new DeviceUserScopeProvider();
        Assert.True(scope.TryBeginAuthentication(null, out var original));
        using var lease = scope.TryBeginReadiness(NativeSliceFixture.UserA, original)!;
        scope.SetActiveUser(NativeSliceFixture.UserB);
        scope.SetActiveUser(null);
        scope.SetActiveUser(NativeSliceFixture.UserA);

        Assert.False(lease.IsCurrent);
        Assert.False(scope.TryGetUserProfileId(out _));
        Assert.False(scope.TryPublishAuthenticatedUser(
            NativeSliceFixture.UserA, original, CancellationToken.None, out _));
        Assert.False(scope.TryClearIfCurrent(original));
    }

    [Fact]
    public async Task PublishedReadinessLease_RejectsForeignOwnerAndRevokedChildFlow()
    {
        var scope = new DeviceUserScopeProvider();
        Assert.True(scope.TryBeginAuthentication(null, out var transition));
        using (scope.TryBeginReadiness(NativeSliceFixture.UserA, transition)!)
        {
            Assert.True(scope.TryGetLeasedReadinessOwner(out var temporary));
            Assert.Equal(NativeSliceFixture.UserA, temporary);
            Assert.False(scope.TryGetPublishedReadinessOwner(out _));
        }
        Assert.True(scope.TryPublishAuthenticatedUser(
            NativeSliceFixture.UserA, transition, CancellationToken.None, out var published));
        Assert.Null(scope.TryBeginReadiness(NativeSliceFixture.UserB, published));
        Assert.False(scope.TryGetPublishedReadinessOwner(out _));

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> inherited;
        using (scope.TryBeginReadiness(NativeSliceFixture.UserA, published)!)
        {
            Assert.True(scope.TryGetPublishedReadinessOwner(out var owner));
            Assert.Equal(NativeSliceFixture.UserA, owner);
            inherited = Task.Run(async () =>
            {
                await release.Task;
                return scope.TryGetPublishedReadinessOwner(out _);
            });
        }

        scope.SetActiveUser(NativeSliceFixture.UserB);
        release.TrySetResult();
        Assert.False(await inherited.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(scope.TryGetPublishedReadinessOwner(out _));
        Assert.True(scope.TryGetUserProfileId(out var currentOwner));
        Assert.Equal(NativeSliceFixture.UserB, currentOwner);
    }

    private static async Task WaitForStampChangeAsync(
        DeviceUserScopeProvider scope, DeviceUserScopeProvider.ScopeStamp original)
    {
        for (var retries = 0; scope.IsCurrent(original) && retries < 500; retries++)
            await Task.Delay(10);
        Assert.False(scope.IsCurrent(original));
    }

    private sealed class ExpiringAuth(IAuthService inner) : IAuthService
    {
        public bool Expired { get; set; }
        public Action? AccessTokenRequested { get; set; }
        public bool IsSignedIn => !Expired && inner.IsSignedIn;
        public string? UserName => inner.UserName;
        public Task<AuthResult?> SignInAsync() => inner.SignInAsync();
        public Task<AuthResult?> SignInAsync(string email, string password) =>
            inner.SignInAsync(email, password);
        public Task<AuthResult?> RegisterAsync(
            string email, string password, string displayName) =>
            inner.RegisterAsync(email, password, displayName);
        public Task SignOutAsync() => inner.SignOutAsync();
        public Task<bool> DeleteAccountAsync() => inner.DeleteAccountAsync();
        public Task<bool> ChangePasswordAsync(
            string currentPassword, string newPassword) =>
            inner.ChangePasswordAsync(currentPassword, newPassword);
        public Task<string?> GetAccessTokenAsync(string[] scopes)
        {
            AccessTokenRequested?.Invoke();
            return inner.GetAccessTokenAsync(scopes);
        }
        public Task<bool> HasStoredSessionAsync() => inner.HasStoredSessionAsync();
    }
}
