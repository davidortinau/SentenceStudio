using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Services;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// W1: source onboarding name suggestions (Onboarding.razor LoadSuggestedNames) and the
/// Dashboard Quick Start route to onboarding for a learner with no target language
/// (Index.razor CreateStarterResourceAsync).
/// </summary>
public sealed class NativeOnboardingRouteAndNamesTests
{
    private static async Task ClearTargetLanguageAsync(NativeSliceFixture fixture, string owner)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.UserProfiles.SingleAsync(profile => profile.Id == owner)).TargetLanguage = "";
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task DashboardLearnerWithNoTargetLanguageIsRoutedToOnboardingAndResumesThereAfterRestart()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        var auth = fixture.Auth;
        Assert.Equal(NativeDestination.Dashboard,
            (await auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password")).Destination);

        // A populated profile stays on the Dashboard and nothing is marked.
        Assert.False(await auth.BeginOnboardingForMissingTargetLanguageAsync());
        Assert.Equal(NativeDestination.Dashboard, auth.State.Destination);

        // Another learner's empty profile is never read for this owner.
        await ClearTargetLanguageAsync(fixture, NativeSliceFixture.UserB);
        Assert.False(await auth.BeginOnboardingForMissingTargetLanguageAsync());

        await ClearTargetLanguageAsync(fixture, NativeSliceFixture.UserA);
        Assert.True(await auth.BeginOnboardingForMissingTargetLanguageAsync());
        Assert.Equal(NativePhase.Ready, auth.State.Phase);
        Assert.Equal(NativeDestination.Onboarding, auth.State.Destination);
        Assert.Equal(NativeSliceFixture.UserA, auth.State.UserId);
        // Already in onboarding: the route is not taken twice.
        Assert.False(await auth.BeginOnboardingForMissingTargetLanguageAsync());

        fixture.Restart();
        var restarted = fixture.Auth;
        Assert.Equal(NativeDestination.Onboarding, (await restarted.RestoreAsync()).Destination);
        Assert.Equal(NativeOnboardingOutcome.Success,
            (await restarted.SaveOnboardingProfileAsync(
                new NativeOnboardingDraft("Chosen", "English", "Korean"))).Outcome);
        Assert.Equal(NativeOnboardingOutcome.Success,
            (await restarted.FinishOnboardingAsync(createStarter: false)).Outcome);
        Assert.Equal(NativeDestination.Dashboard, restarted.State.Destination);

        fixture.Restart();
        Assert.Equal(NativeDestination.Dashboard, (await fixture.Auth.RestoreAsync()).Destination);
        using var check = fixture.Scope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("Korean", (await db.UserProfiles.AsNoTracking()
            .SingleAsync(profile => profile.Id == NativeSliceFixture.UserA)).TargetLanguage);
        Assert.Equal("", (await db.UserProfiles.AsNoTracking()
            .SingleAsync(profile => profile.Id == NativeSliceFixture.UserB)).TargetLanguage);
    }

    [Fact]
    public async Task OnboardingRouteFailsClosedWithoutTheSignedInOwner()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        var auth = fixture.Auth;
        Assert.False(await auth.BeginOnboardingForMissingTargetLanguageAsync());
        Assert.Equal(NativeDestination.SignIn, auth.State.Destination);

        await auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password");
        await ClearTargetLanguageAsync(fixture, NativeSliceFixture.UserA);
        fixture.Activate(NativeSliceFixture.UserB);
        Assert.False(await auth.BeginOnboardingForMissingTargetLanguageAsync());
        Assert.NotEqual(NativeDestination.Onboarding, auth.State.Destination);
    }

    [Fact]
    public async Task OnboardingOwnerGetsEightNamesForTheChosenLanguageThroughTheOwnerGateway()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await ClearTargetLanguageAsync(fixture, NativeSliceFixture.UserA);
        var auth = fixture.Auth;

        // Not signed in: no request leaves the device.
        Assert.Empty((await auth.SuggestOnboardingNamesAsync("Korean")).ToArray());
        Assert.Equal(0, fixture.ResourceAi.Calls);

        Assert.Equal(NativeDestination.Onboarding,
            (await auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password")).Destination);
        fixture.ResourceAi.StarterResponse = "지훈, 민수, 현우, 태양, 지은, 수빈, 예린, 하은";
        var names = await auth.SuggestOnboardingNamesAsync("Korean");
        Assert.Equal(new[] { "지훈", "민수", "현우", "태양", "지은", "수빈", "예린", "하은" }, names.ToArray());
        Assert.Equal(NativeSliceFixture.UserA, fixture.ResourceAi.LastOwner);
        Assert.Equal(NameGenerationService.BuildPrompt("Korean"), fixture.ResourceAi.LastPrompt);

        // An unknown language is refused before any request.
        var calls = fixture.ResourceAi.Calls;
        Assert.Empty(await auth.SuggestOnboardingNamesAsync("Klingon"));
        Assert.Equal(calls, fixture.ResourceAi.Calls);

        // A reply without eight names falls back to the source's built-in names.
        fixture.ResourceAi.StarterResponse = "Only, Three, Names";
        Assert.Equal(NameGenerationService.GetHardcodedNames("Korean"),
            (await auth.SuggestOnboardingNamesAsync("Korean")).ToArray());
    }

    [Fact]
    public async Task NamesFallBackToBuiltInNamesWithoutAGatewayOrWhenTheCallFails()
    {
        var withoutGateway = new NativeOnboardingNames(
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<NativeOnboardingNames>.Instance);
        Assert.Equal(NameGenerationService.GetHardcodedNames("English"),
            (await withoutGateway.SuggestAsync("owner", "English", CancellationToken.None)).ToArray());
        // Languages without built-in names use the English ones, as the source does.
        Assert.Equal(NameGenerationService.GetHardcodedNames("English"),
            (await withoutGateway.SuggestAsync("owner", "French", CancellationToken.None)).ToArray());
        Assert.Empty((await withoutGateway.SuggestAsync("", "Korean", CancellationToken.None)).ToArray());

        var failing = new NativeOnboardingNames(
            new ServiceCollection()
                .AddSingleton<IOwnerBoundNativeAiGateway>(new FailingGateway())
                .BuildServiceProvider(),
            NullLogger<NativeOnboardingNames>.Instance);
        Assert.Equal(NameGenerationService.GetHardcodedNames("Korean"),
            (await failing.SuggestAsync("owner", "Korean", CancellationToken.None)).ToArray());

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            failing.SuggestAsync("owner", "Korean", canceled.Token));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("a, b, c, d, e, f, g", null)]
    [InlineData(" a , b,c,d, e,f,g,h ", "a|b|c|d|e|f|g|h")]
    [InlineData("a,b,c,d,e,f,g,h,i,j", "a|b|c|d|e|f|g|h")]
    [InlineData("a,,b,c,d,e,f,g", null)]
    public void SourceParsingKeepsExactlyEightNames(string? response, string? expected)
    {
        var parsed = NameGenerationService.ParseNames(response);
        Assert.Equal(expected, parsed is null ? null : string.Join("|", parsed));
    }

    private sealed class FailingGateway : IOwnerBoundNativeAiGateway
    {
        public Task<T?> SendPromptAsync<T>(string owner, string prompt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new HttpRequestException("offline");
        }
    }
}
