using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace SentenceStudio.Native.iOS.Tests;

/// <summary>
/// Source LoginPage/RegisterPage/ForgotPasswordPage and the MainLayout sync
/// overlay (AF-N01, AF-N03, AF-N05) rendered as C# SwiftUI declarations.
/// </summary>
public sealed class NativeAccountRoutesContractTests
{
    [Fact]
    public void SignIn_LinksToRegisterAndRecovery_AndNeverRetriesAFailedFormAsRestore()
    {
        var signIn = Source("Features/Authentication/SignInView.cs");
        var account = Source("App/ApplicationController.Account.cs");

        Assert.Contains("\"Login_ForgotPassword\"", signIn);
        Assert.Contains("\"Login_NoAccountCreateOne\"", signIn);
        Assert.Contains("\"Login_CreateOne\"", signIn);
        Assert.Contains("native.auth.forgot", signIn);
        Assert.Contains("native.auth.register", signIn);
        // AF-N05: an Error after a submitted form is corrected with Sign In;
        // only an offline stored session gets a labelled restore action.
        Assert.DoesNotContain("NativePhase.PendingSync or NativePhase.Error", signIn);
        Assert.Contains("state is { Phase: NativePhase.PendingSync, UserId: null }", signIn);
        Assert.Contains("\"NativeAuth_RestoreSessionRetry\"", signIn);
        Assert.Contains("_password = string.Empty;", account);
    }

    [Fact]
    public void Register_KeepsSourceValidationAndLiveUpdatesWithoutRebuildingFields()
    {
        var views = Source("Features/Authentication/AccountViews.cs");
        var account = Source("App/ApplicationController.Account.cs");

        foreach (var key in new[]
        {
            "Register_CreateAccount", "Register_DisplayName", "Register_YourName",
            "Register_ConfirmPassword", "Register_PasswordsDoNotMatch",
            "Register_CreatingAccount", "Register_AlreadyHaveAccount",
            "Register_CheckYourEmail", "Register_VerificationSent", "Register_GoToSignIn"
        })
            Assert.Contains($"\"{key}\"", views);
        // Rules come from the shared policy that also configures the server.
        Assert.Contains("PasswordPolicy.IsSatisfied(password)", views);
        Assert.Contains("PasswordPolicy.Rules", views);
        Assert.Contains("PasswordPolicy.LabelKey(rule)", views);
        Assert.Contains("inputs.Password == inputs.ConfirmPassword", views);
        Assert.Contains("_auth.RegisterAsync(", account);
        Assert.Contains("NativeRegistrationOutcome.ConfirmationRequired", account);
        Assert.Contains("AccountViews.UpdateRegisterLive(", account);
        Assert.DoesNotContain("value => { _registerPassword = value; Publish(); }", account);
    }

    [Fact]
    public void Recovery_ShowsTheSameAcceptedMessageForEveryServerAcceptance()
    {
        var views = Source("Features/Authentication/AccountViews.cs");
        var account = Source("App/ApplicationController.Account.cs");

        Assert.Contains("\"ForgotPassword_Instructions\"", views);
        Assert.Contains("\"ForgotPassword_SendResetLink\"", views);
        Assert.Contains("\"ForgotPassword_ResetSent\"", views);
        Assert.Contains("\"ForgotPassword_BackToSignIn\"", views);
        Assert.Contains("_auth.RequestPasswordRecoveryAsync(", account);
        Assert.Contains("NativePasswordRecoveryOutcome.Accepted", account);
    }

    [Fact]
    public void Readiness_MasksTheShellWhileOwnerSyncRuns_AndOffersRetryAndSignOut()
    {
        var views = Source("Features/Authentication/AccountViews.cs");
        var account = Source("App/ApplicationController.Account.cs");
        var app = Source("App/ApplicationController.cs");

        Assert.Contains("\"MainLayout_SyncingProfile\"", views);
        Assert.Contains("\"MainLayout_SyncingDescription\"", views);
        Assert.Contains("\"MainLayout_ThisMayTakeAMoment\"", views);
        Assert.Contains("native.auth.sync.retry", views);
        Assert.Contains("native.auth.sync.signout", views);
        Assert.Contains("live is { UserId: not null, Phase: NativePhase.Loading }", account);
        Assert.Contains("_auth.StateChanged += OnAuthProgress;", app);
    }

    [Fact]
    public void ProfileAuthAdapter_ForwardsOutcomeBearingAccountCalls()
    {
        var adapter = Source("Platform/NativeProfileAuthService.cs");
        Assert.Contains("identity.RegisterWithOutcomeAsync(", adapter);
        Assert.Contains("identity.RequestPasswordRecoveryAsync(", adapter);
        Assert.Contains("profiles.EnsureLocalProfileAsync(session.AccessToken)", adapter);
    }

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void RestoreSessionRetry_IsTranslated(string resourceFile)
    {
        var path = Path.GetFullPath(Path.Combine(HeadPath,
            "../SentenceStudio.Shared/Resources/Strings", resourceFile));
        Assert.Contains(XDocument.Load(path).Descendants("data"), data =>
            data.Attribute("name")?.Value == "NativeAuth_RestoreSessionRetry"
            && !string.IsNullOrWhiteSpace(data.Element("value")?.Value));
    }

    private static string HeadPath => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(CurrentFile())!,
        "../../src/SentenceStudio.Native.iOS"));

    private static string Source(string name) =>
        File.ReadAllText(Path.Combine(HeadPath, name));

    private static string CurrentFile([CallerFilePath] string file = "") => file;
}
