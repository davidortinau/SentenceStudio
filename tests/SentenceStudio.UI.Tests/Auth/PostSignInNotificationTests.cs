using System.Security.Claims;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;

namespace SentenceStudio.UI.Tests.Auth;

/// <summary>
/// Publishing an authentication state change after an interactive sign-in.
/// </summary>
/// <remarks>
/// <para>
/// The login page signs in through <see cref="IAuthService"/> directly, so Blazor is not told
/// anything unless the page says so. That was invisible while the page finished with
/// <c>NavigateTo(url, forceLoad: true)</c>, because a forced document load makes
/// <c>WebViewManager.AttachToPageAsync</c> rebuild the page context and re-read the auth state from
/// scratch. Once the forced load was removed — it destroys the renderer and the DI scope, which is
/// what dropped render batches with "There is no browser renderer with ID 3" — the missing
/// notification became a redirect loop back to the login form.
/// </para>
/// <para>
/// These tests pin which publication path is used, because the obvious one is wrong in a way that
/// is silent: <c>LogInSilentlyAsync</c> re-runs the silent sign-in and, when that fails, republishes
/// whatever principal it currently holds — anonymous, for someone who just arrived at the login
/// page.
/// </para>
/// </remarks>
public class PostSignInNotificationTests
{
    private static ClaimsPrincipal Anonymous => new(new ClaimsIdentity());

    // ------------------------------------------------------------- the happy path

    [Fact]
    public async Task NotifySignedIn_publishes_the_authenticated_principal_from_the_warm_cache()
    {
        var auth = new StubAuthService { IsSignedIn = true, AccessToken = TestJwt("a@example.test") };
        var sut = new MauiAuthenticationStateProvider(auth, NullLogger<MauiAuthenticationStateProvider>.Instance);

        AuthenticationState? published = null;
        sut.AuthenticationStateChanged += t => published = t.Result;

        sut.NotifySignedIn();

        published.Should().NotBeNull();
        published!.User.Identity?.IsAuthenticated.Should().BeTrue();
        auth.StoredSessionProbes.Should().Be(0, "the warm path must not touch secure storage");
        auth.SilentSignIns.Should().Be(0, "the warm path must not re-run the silent sign-in");
    }

    /// <summary>
    /// The regression. A silent sign-in that fails right after a successful credential login must
    /// not be able to publish an anonymous principal — that bounces the learner straight back to
    /// the login form, with no error, forever.
    /// </summary>
    [Fact]
    public async Task NotifySignedIn_cannot_publish_anonymous_when_the_silent_path_would_fail()
    {
        var auth = new StubAuthService
        {
            IsSignedIn = true,                 // the credential login just succeeded
            AccessToken = TestJwt("a@example.test"),
            SilentSignInResult = null,         // ...but a silent re-sign-in would fail
            HasStoredSession = true,
        };
        var sut = new MauiAuthenticationStateProvider(auth, NullLogger<MauiAuthenticationStateProvider>.Instance);

        AuthenticationState? published = null;
        sut.AuthenticationStateChanged += t => published = t.Result;

        sut.NotifySignedIn();

        published!.User.Identity?.IsAuthenticated.Should().BeTrue(
            "the learner is signed in; a failing silent path must not be able to say otherwise");
    }

    /// <summary>
    /// Demonstrates the trap directly, so the choice in LoginPage is documented by a test rather
    /// than only by a comment.
    /// </summary>
    [Fact]
    public async Task LogInSilentlyAsync_would_publish_anonymous_in_the_same_situation()
    {
        var auth = new StubAuthService
        {
            IsSignedIn = true,
            AccessToken = TestJwt("a@example.test"),
            SilentSignInResult = null,
            HasStoredSession = true,
        };
        var sut = new MauiAuthenticationStateProvider(auth, NullLogger<MauiAuthenticationStateProvider>.Instance);

        // Nobody has read the state yet, so the provider's current principal is the anonymous one.
        AuthenticationState? published = null;
        sut.AuthenticationStateChanged += t => published = t.Result;

        await sut.LogInSilentlyAsync();

        published!.User.Identity?.IsAuthenticated.Should().BeFalse(
            "this is exactly why LoginPage must not use this method to publish a credential login");
        auth.SilentSignIns.Should().Be(1);
    }

    // ------------------------------------------------------------ source contract

    [Fact]
    public void LoginPage_publishes_through_NotifySignedIn_not_LogInSilentlyAsync()
    {
        var source = ReadSource("src/SentenceStudio.UI/Pages/LoginPage.razor");

        source.Should().Contain("mauiAuth.NotifySignedIn()");
        source.Should().NotContain("mauiAuth.LogInSilentlyAsync()");
    }

    /// <summary>
    /// MainLayout is the DefaultLayout for both /auth/login and /, so a client-side navigation
    /// after sign-in keeps the same instance: OnInitializedAsync does not run again and the
    /// post-login routing decision it owns would never happen. That decision owns the issue-#187
    /// sync overlay and the fresh-install /onboarding redirect, so it has to be driven off the
    /// authentication transition instead.
    /// </summary>
    [Fact]
    public void MainLayout_reruns_post_login_routing_on_the_authentication_transition()
    {
        var source = ReadSource("src/SentenceStudio.UI/Layout/MainLayout.razor");

        source.Should().MatchRegex(
            @"if\s*\(!wasAuthenticated\s*&&\s*isAuthenticated",
            "the false->true transition is the only signal left that a sign-in happened");

        Regex.Matches(source, @"await ApplyPostLoginRouteAsync\(").Count.Should().BeGreaterThanOrEqualTo(
            2,
            "post-login routing must run from OnInitializedAsync AND from the auth transition");
    }

    private static string ReadSource(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            dir = dir.Parent;

        dir.Should().NotBeNull();
        var path = Path.Combine(dir!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue($"expected source at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>An unsigned JWT with an email claim — enough for the provider to parse.</summary>
    private static string TestJwt(string email)
    {
        static string B64(string s) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{B64("{\"alg\":\"none\",\"typ\":\"JWT\"}")}." +
               $"{B64($"{{\"sub\":\"user-1\",\"email\":\"{email}\",\"name\":\"{email}\"}}")}.sig";
    }

    private sealed class StubAuthService : IAuthService
    {
        public bool IsSignedIn { get; set; }
        public string? UserName => "a@example.test";
        public string? AccessToken { get; set; }
        public AuthResult? SilentSignInResult { get; set; }
        public bool HasStoredSession { get; set; }

        public int SilentSignIns { get; private set; }
        public int StoredSessionProbes { get; private set; }

        public Task<bool> HasStoredSessionAsync()
        {
            StoredSessionProbes++;
            return Task.FromResult(HasStoredSession);
        }

        public Task<AuthResult?> SignInAsync()
        {
            SilentSignIns++;
            return Task.FromResult(SilentSignInResult);
        }

        public Task<AuthResult?> SignInAsync(string email, string password) =>
            Task.FromResult<AuthResult?>(null);

        public Task<AuthResult?> RegisterAsync(string email, string password, string displayName) =>
            Task.FromResult<AuthResult?>(null);

        public Task SignOutAsync() => Task.CompletedTask;

        public Task<bool> DeleteAccountAsync() => Task.FromResult(false);

        public Task<bool> ChangePasswordAsync(string currentPassword, string newPassword) =>
            Task.FromResult(false);

        public Task<string?> GetAccessTokenAsync(string[] scopes) => Task.FromResult(AccessToken);
    }
}
