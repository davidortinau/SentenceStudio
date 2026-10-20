using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android.Tests;

public sealed class AndroidAuthCompositionTests
{
    [Fact]
    public void NormalHeadUsesOwnerBoundCoreSyncAndProductionPlanAndQuiz()
    {
        var root = Path.GetFullPath(typeof(AndroidAuthCompositionTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!);
        var source = File.ReadAllText(Path.Combine(root, "AndroidServiceHost.cs"));

        Assert.Contains("services.AddHttpClient(\"AuthClient\"", source);
        Assert.Contains("services.AddSyncServices(databasePath, apiUri, downloadOnlyProfile: true);", source);
        Assert.DoesNotContain("services.AddApiClients(apiUri);", source);
        var normalOnly = source.IndexOf(
            "if (!preview)\n        {\n            services.AddSyncServices(databasePath, apiUri, downloadOnlyProfile: true);",
            StringComparison.Ordinal);
        Assert.True(normalOnly >= 0,
            "Owner-bound CoreSync must be normal-only.");
        var normalEnd = source.IndexOf("services.AddTransient<IPlanDateContext>",
            normalOnly, StringComparison.Ordinal);
        Assert.True(normalEnd > normalOnly);
        Assert.DoesNotContain("IAiGatewayClient", source[normalOnly..normalEnd]);
        Assert.DoesNotContain("AuthenticatedHttpMessageHandler", source);
        Assert.Contains("services.AddOwnerBoundNativeAiGateway(apiUri);",
            source[normalOnly..normalEnd]);
        Assert.Contains("services.AddSingleton<ISyncService, SyncService>();", source);
        Assert.Contains("services.AddSentenceStudioNativeSlice();", source);
        Assert.Contains("services.AddSentenceStudioNativeIdentityAuth();", source);
        Assert.Contains("services.AddOwnerBoundNativeSyncReadiness();", source);
        Assert.Contains("services.AddSentenceStudioNativeProductionAdapters();", source);
        Assert.True(source.IndexOf("services.AddSentenceStudioNativeSlice();", StringComparison.Ordinal)
            < source.IndexOf("services.AddSentenceStudioNativeProductionAdapters();",
                StringComparison.Ordinal));
        Assert.Contains("AndroidPlanServiceRegistrations.AddProduction(services);", source);
        Assert.Contains("AndroidPlanServiceRegistrations.AddCommon(services);", source);
        Assert.Contains("services.Replace(ServiceDescriptor.Singleton<INativeQuizMedia>(sp =>",
            source);
        Assert.Contains("sp.GetRequiredService<AndroidQuizMedia>()", source);
        Assert.Contains("if (!preview)\n        {\n            services.AddSentenceStudioNativeProductionAdapters();",
            source);
    }

    [Fact]
    public async Task NativeSliceOnlineWithoutExplicitSyncOptIn_RemainsUnconfirmed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectivityService>(new OnlineConnectivity());
        services.AddSentenceStudioNativeSlice();
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var readiness = provider.GetRequiredService<INativeSyncReadiness>();
        Assert.Equal(NativeSyncOutcome.Unconfirmed,
            await readiness.SynchronizeAsync("native-owner-a", CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            readiness.SynchronizeAsync("", CancellationToken.None));
    }

    [Fact]
    public void OwnerSwitchClearsCachedDashboardAndQuizBeforeRendering()
    {
        var root = Path.GetFullPath(typeof(AndroidAuthCompositionTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!);
        var shell = File.ReadAllText(Path.Combine(root, "AndroidSliceShell.cs"));

        Assert.Contains("if (Auth.UserId != state.UserId)", shell);
        Assert.Contains("Dashboard = new(NativePhase.Loading)", shell);
        Assert.Contains("Quiz = new(NativePhase.Idle, NativeDestination.VocabularyQuiz)", shell);
        Assert.Contains("HasDashboardOwner(Auth.UserId)", shell);
        Assert.Contains("_dashboard?.State ?? state", shell);
        Assert.Contains("_quiz?.State ?? state", shell);
    }

    [Fact]
    public void RegistrationAndRecoveryUseTypedSharedOutcomesWithoutRawHttp()
    {
        var root = Path.GetFullPath(typeof(AndroidAuthCompositionTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!);
        var source = File.ReadAllText(Path.Combine(root, "AndroidSliceShell.Auth.cs"));

        Assert.Contains("_auth.RegisterAsync(", source);
        Assert.Contains("_auth.RequestPasswordRecoveryAsync(", source);
        Assert.Contains("NativeRegistrationOutcome.SignedIn", source);
        Assert.Contains("NativeRegistrationOutcome.ConfirmationRequired", source);
        Assert.Contains("NativeAuth_RegistrationAccepted", source);
        Assert.Contains("NativePasswordRecoveryOutcome.Accepted", source);
        Assert.Contains("ForgotPassword_ResetSent", source);
        Assert.Contains("Localize(result.ErrorKey ?? NativeText.UnexpectedError)",
            source);
        Assert.DoesNotContain("PostAsJsonAsync", source);
        Assert.DoesNotContain("HasIssuedTokensAsync", source);
        Assert.DoesNotContain("ReadFromJsonAsync", source);
        Assert.DoesNotContain("userId", source);
    }

    [Fact]
    public void NativeRegistrationAndRecoverySuccessRequireTypedAcceptedOutcomes()
    {
        var root = Path.GetFullPath(typeof(AndroidAuthCompositionTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!);
        var source = File.ReadAllText(Path.Combine(root, "AndroidSliceShell.Auth.cs"));
        var register = source[source.IndexOf(
            "private void SubmitRegistration()", StringComparison.Ordinal)
            ..source.IndexOf("private void SendResetLink()", StringComparison.Ordinal)];
        var recovery = source[source.IndexOf(
            "private void SendResetLink()", StringComparison.Ordinal)
            ..source.IndexOf("private ComposableNode RenderAuthentication(",
                StringComparison.Ordinal)];

        Assert.Contains("await HandleAuthAsync(signedIn, cancellationToken)",
            register);
        var registrationOutcome = register.IndexOf(
            "result.Outcome == NativeRegistrationOutcome.ConfirmationRequired",
            StringComparison.Ordinal);
        Assert.True(registrationOutcome >= 0
            && register.IndexOf("_registrationComplete = true",
                StringComparison.Ordinal) > registrationOutcome);
        Assert.Equal(1, register.Split("_registrationComplete = true").Length - 1);
        var recoveryOutcome = recovery.IndexOf(
            "result.Outcome == NativePasswordRecoveryOutcome.Accepted",
            StringComparison.Ordinal);
        Assert.True(recoveryOutcome >= 0
            && recovery.IndexOf("_resetSent = true",
                StringComparison.Ordinal) > recoveryOutcome);
        Assert.Equal(1, recovery.Split("_resetSent = true").Length - 1);
        Assert.Contains("ShowError(Localize(result.ErrorKey", register);
        Assert.Contains("ShowError(Localize(result.ErrorKey", recovery);
    }

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void TypedAuthFeedbackHasLocalizedCopy(string culture)
    {
        var root = Path.GetFullPath(typeof(AndroidAuthCompositionTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!);
        var resources = XDocument.Load(Path.Combine(root, "..",
            "SentenceStudio.Shared", "Resources", "Strings", culture));
        var keys = resources.Descendants("data")
            .Select(entry => (string?)entry.Attribute("name") ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in new[]
        {
            "NativeAuth_RegistrationAccepted", "NativeAuth_RecoveryUnavailable",
            "NativeAuth_RecoveryFailed", "ForgotPassword_ResetSent",
            "Register_Failed", "Register_NotAvailable"
        })
            Assert.Contains(key, keys);
    }

    [Fact]
    public void OnboardingCannotSendAnUnsafeProfileWrite()
    {
        var root = Path.GetFullPath(typeof(AndroidAuthCompositionTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!);
        var onboarding = File.ReadAllText(Path.Combine(
            root, "AndroidSliceShell.Onboarding.cs"));
        var services = File.ReadAllText(Path.Combine(
            root, "AndroidServiceHost.cs"));

        Assert.Contains("native.auth.onboarding.unavailable", onboarding);
        Assert.DoesNotContain("AndroidOnboardingGateway", onboarding);
        Assert.DoesNotContain("AndroidOnboardingGateway", services);
        Assert.DoesNotContain("SaveOnboardingProfileAsync(", onboarding);
        Assert.DoesNotContain("FinishOnboardingAsync(", onboarding);
    }

    [Fact]
    public void ApiPhotosWaitForSharedOwnerTokenVerification()
    {
        var root = Path.GetFullPath(typeof(AndroidAuthCompositionTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!);
        var media = File.ReadAllText(Path.Combine(root, "AndroidQuizMedia.cs"));

        Assert.Contains("CanRequestWithoutOwnerToken(uri, apiRoot)", media);
        Assert.DoesNotContain("GetAccessTokenAsync", media);
        Assert.DoesNotContain("AuthenticationHeaderValue", media);
        Assert.False(File.Exists(Path.Combine(root, "AndroidOwnerTokenPolicy.cs")));
    }

    private sealed class OnlineConnectivity : IConnectivityService
    {
        public bool IsInternetAvailable => true;
    }
}
