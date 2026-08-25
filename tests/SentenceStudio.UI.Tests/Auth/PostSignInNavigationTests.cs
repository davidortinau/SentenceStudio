using System.Security.Claims;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Services;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Auth;

/// <summary>
/// What the login page is allowed to do <em>after</em> it has published the sign-in.
/// </summary>
/// <remarks>
/// <para>
/// Publishing the authentication state is synchronous, and the shell reacts to it: on the
/// false → true transition <c>MainLayout</c> re-runs <c>ApplyPostLoginRouteAsync</c>, which owns
/// the fresh-install <c>/onboarding</c> redirect. So by the time <c>NotifySignedIn()</c> returns,
/// the learner may already be somewhere better than the login page's own default of <c>"/"</c>.
/// </para>
/// <para>
/// Navigating unconditionally at that point sends a brand-new learner from <c>/onboarding</c> to
/// the dashboard, with no profile and no language set. That is the hazard these tests pin.
/// </para>
/// </remarks>
public class PostSignInNavigationTests
{
    // -------------------------------------------------------------- the decision

    [Theory]
    [InlineData("/auth/login")]
    [InlineData("/auth/login/")]
    [InlineData("/AUTH/LOGIN")]
    public void Still_on_the_login_page_means_route_to_the_return_url(string path) =>
        PostSignInNavigation.ShouldRouteToReturnUrl(path).Should().BeTrue();

    [Theory]
    [InlineData("/onboarding")]
    [InlineData("/onboarding/")]
    [InlineData("/")]
    [InlineData("/dashboard")]
    public void Anywhere_else_means_somebody_already_routed(string path) =>
        PostSignInNavigation.ShouldRouteToReturnUrl(path).Should().BeFalse(
            "the login page must not override a routing decision the shell already made");

    /// <summary>
    /// A missing path is not evidence that we were routed — <c>AbsolutePath</c> is never empty for
    /// a real URI, so this only happens when something degenerate is passed. Skipping would strand
    /// the learner on the sign-in form, which is worse than a redundant navigation to the dashboard.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unknown_location_falls_back_to_routing(string? path) =>
        PostSignInNavigation.ShouldRouteToReturnUrl(path).Should().BeTrue();

    // ------------------------------------------------ the ordering reproduction

    /// <summary>
    /// THE regression, reproduced end to end: a real <see cref="MauiAuthenticationStateProvider"/>,
    /// a real <see cref="NavigationManager"/>, and a subscriber that routes to <c>/onboarding</c>
    /// synchronously on the authentication transition — exactly what <c>MainLayout</c> does.
    /// </summary>
    [Fact]
    public void A_synchronous_onboarding_redirect_is_not_clobbered_by_the_login_page()
    {
        var nav = new LoginPageNavigationManager();
        var auth = new StubAuthService { IsSignedIn = true, AccessToken = TestJwt("new@example.test") };
        var provider = new MauiAuthenticationStateProvider(
            auth, NullLogger<MauiAuthenticationStateProvider>.Instance);

        // MainLayout's behaviour: on the false -> true transition, re-run post-login routing, which
        // for a learner with no profile decides /onboarding.
        provider.AuthenticationStateChanged += task =>
        {
            if (task.Result.User.Identity?.IsAuthenticated == true)
            {
                nav.NavigateTo("/onboarding");
            }
        };

        // --- the login page's exact sequence ---
        provider.NotifySignedIn();
        var routed = PostSignInNavigation.RouteAfterSignIn(nav, "/", forceLoad: false);

        routed.Should().BeFalse("the shell already routed this learner");
        nav.ToAbsoluteUri(nav.Uri).AbsolutePath.Should().Be(
            "/onboarding",
            "a fresh install must stay in onboarding, not be bounced to the dashboard");
        nav.NavigationCount.Should().Be(1, "the login page must not add a second navigation");
    }

    /// <summary>
    /// The other half of the contract: when nothing routed, the login page still has to.
    /// Without this, the guard would be indistinguishable from deleting the navigation.
    /// </summary>
    [Fact]
    public void The_login_page_still_routes_when_nothing_else_did()
    {
        var nav = new LoginPageNavigationManager();
        var auth = new StubAuthService { IsSignedIn = true, AccessToken = TestJwt("returning@example.test") };
        var provider = new MauiAuthenticationStateProvider(
            auth, NullLogger<MauiAuthenticationStateProvider>.Instance);

        // A returning learner with a populated profile: routing decides "stay on the dashboard",
        // i.e. it does not navigate at all.
        provider.NotifySignedIn();
        var routed = PostSignInNavigation.RouteAfterSignIn(nav, "/", forceLoad: false);

        routed.Should().BeTrue();
        nav.ToAbsoluteUri(nav.Uri).AbsolutePath.Should().Be("/");
        nav.LastForceLoad.Should().BeFalse();
    }

    [Fact]
    public void The_web_cookie_hand_off_still_forces_a_document_load()
    {
        var nav = new LoginPageNavigationManager("https://localhost:5001/", "https://localhost:5001/auth/login");

        PostSignInNavigation.RouteAfterSignIn(
            nav, "/", forceLoad: BlazorHostKind.ShouldForceLoadAfterSignIn(nav.BaseUri))
            .Should().BeTrue();

        nav.LastForceLoad.Should().BeTrue("the web host must leave the page so the server can set the cookie");
    }

    [Fact]
    public void The_webview_host_never_forces_a_document_load()
    {
        var nav = new LoginPageNavigationManager("app://0.0.0.1/", "app://0.0.0.1/auth/login");

        PostSignInNavigation.RouteAfterSignIn(
            nav, "/", forceLoad: BlazorHostKind.ShouldForceLoadAfterSignIn(nav.BaseUri))
            .Should().BeTrue();

        nav.LastForceLoad.Should().BeFalse();
    }

    // ------------------------------------------------------------ source contract

    [Fact]
    public void LoginPage_routes_through_the_guard_rather_than_navigating_directly()
    {
        var source = ReadSource("src/SentenceStudio.UI/Pages/LoginPage.razor");

        source.Should().Contain("PostSignInNavigation.RouteAfterSignIn(");
        source.Should().NotContain(
            "NavManager.NavigateTo(returnUrl",
            "the unguarded navigation is what clobbered onboarding");
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

    private static string TestJwt(string email)
    {
        static string B64(string s) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{B64("{\"alg\":\"none\",\"typ\":\"JWT\"}")}." +
               $"{B64($"{{\"sub\":\"user-1\",\"email\":\"{email}\",\"name\":\"{email}\"}}")}.sig";
    }

    /// <summary>A navigation manager that starts on the sign-in page and records what happens.</summary>
    private sealed class LoginPageNavigationManager : NavigationManager
    {
        public LoginPageNavigationManager(
            string baseUri = "app://0.0.0.1/",
            string startUri = "app://0.0.0.1/auth/login")
            => Initialize(baseUri, startUri);

        public int NavigationCount { get; private set; }

        public bool LastForceLoad { get; private set; }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            NavigationCount++;
            LastForceLoad = forceLoad;
            Uri = ToAbsoluteUri(uri).ToString();
        }
    }

    private sealed class StubAuthService : IAuthService
    {
        public bool IsSignedIn { get; set; }
        public string? UserName => "learner@example.test";
        public string? AccessToken { get; set; }

        public Task<bool> HasStoredSessionAsync() => Task.FromResult(IsSignedIn);
        public Task<AuthResult?> SignInAsync() => Task.FromResult<AuthResult?>(null);
        public Task<AuthResult?> SignInAsync(string email, string password) => Task.FromResult<AuthResult?>(null);
        public Task<AuthResult?> RegisterAsync(string email, string password, string displayName) =>
            Task.FromResult<AuthResult?>(null);
        public Task SignOutAsync() => Task.CompletedTask;
        public Task<bool> DeleteAccountAsync() => Task.FromResult(false);
        public Task<bool> ChangePasswordAsync(string a, string b) => Task.FromResult(false);
        public Task<string?> GetAccessTokenAsync(string[] scopes) => Task.FromResult(AccessToken);
    }
}

/// <summary>
/// Every navigation that can run on a native head must decide its forced-load flag from
/// <see cref="BlazorHostKind"/> rather than hard-coding <c>true</c>.
/// </summary>
/// <remarks>
/// A forced document load inside a BlazorWebView makes <c>WebViewManager.AttachToPageAsync</c>
/// dispose the current <c>PageContext</c> — destroying the <c>WebViewRenderer</c> — and build a new
/// DI scope. The only navigations that legitimately keep <c>forceLoad: true</c> are the ones whose
/// entire purpose is to leave the page so the ASP.NET Core server can write a cookie; those are
/// unreachable on a native head because they are gated on an <c>AccessToken</c> containing
/// <c>'|'</c>, which only <c>ServerAuthService</c> produces.
/// </remarks>
public class NativeForceLoadAuditTests
{
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

    /// <summary>
    /// The completion flows the review named. None of these is a cookie hand-off, so none of them
    /// may force a document load on a host that might be native.
    /// </summary>
    [Theory]
    [InlineData("src/SentenceStudio.UI/Pages/Onboarding.razor")]
    [InlineData("src/SentenceStudio.UI/Pages/Index.razor")]
    [InlineData("src/SentenceStudio.UI/Pages/Auth.razor")]
    public void Completion_flows_do_not_force_a_document_load(string relativePath)
    {
        var source = ReadSource(relativePath);

        Regex.Matches(source, @"forceLoad:\s*true").Should().BeEmpty(
            $"{relativePath} has no cookie hand-off, so nothing in it may force a document load");
        source.Should().Contain("BlazorHostKind.ShouldForceLoadForCompletionRoute(");
    }

    /// <summary>
    /// The two pages that DO have a cookie hand-off keep exactly one forced load each — the
    /// <c>/account-action/AutoSignIn</c> redirect — and nothing else.
    /// </summary>
    [Theory]
    [InlineData("src/SentenceStudio.UI/Pages/LoginPage.razor")]
    [InlineData("src/SentenceStudio.UI/Pages/RegisterPage.razor")]
    public void Cookie_hand_off_pages_force_exactly_one_load(string relativePath)
    {
        var source = ReadSource(relativePath);

        Regex.Matches(source, @"forceLoad:\s*true").Count.Should().Be(
            1,
            "only the AutoSignIn cookie hand-off may force a document load");

        var handOffLine = source.Split('\n').Single(l => Regex.IsMatch(l, @"forceLoad:\s*true"));
        handOffLine.Should().Contain(
            "autoSignInUrl",
            "the single forced load must be the cookie hand-off, not an in-app route");

        // Bound to the shared helper rather than to a locally built string, so the two pages that
        // perform this hand-off cannot drift apart again — one of them used to leave returnUrl
        // unescaped.
        source.Should().Contain(
            "PostSignInNavigation.AutoSignInUrl(",
            "the hand-off URL is built in one place so both pages agree on its shape");
    }

    /// <summary>
    /// Profile's remaining forced loads target ASP.NET Core <c>/account-action/</c> endpoints. The
    /// culture one is already inside an <c>IsWebHost</c> branch; this pins that it stays there.
    /// </summary>
    [Fact]
    public void Profile_gates_its_culture_round_trip_on_the_web_host()
    {
        var source = ReadSource("src/SentenceStudio.UI/Pages/Profile.razor");

        source.Should().Contain("BlazorHostKind.IsWebHost(baseUri)");
        source.Should().Contain("/account-action/SetCulture");
    }

    /// <summary>
    /// The web/WebView rule must not be re-implemented inline anywhere. It had already drifted once.
    /// </summary>
    [Theory]
    [InlineData("src/SentenceStudio.UI/Pages/LoginPage.razor")]
    [InlineData("src/SentenceStudio.UI/Pages/RegisterPage.razor")]
    [InlineData("src/SentenceStudio.UI/Pages/Onboarding.razor")]
    [InlineData("src/SentenceStudio.UI/Pages/Index.razor")]
    [InlineData("src/SentenceStudio.UI/Pages/Auth.razor")]
    [InlineData("src/SentenceStudio.UI/Pages/Profile.razor")]
    [InlineData("src/SentenceStudio.UI/Pages/Feedback.razor")]
    [InlineData("src/SentenceStudio.UI/Layout/NavMenu.razor")]
    public void Host_detection_is_never_reimplemented_inline(string relativePath)
    {
        var source = ReadSource(relativePath);

        source.Should().NotContain("StartsWith(\"app://\")");
        source.Should().NotContain("Contains(\"0.0.0.0\")");
    }
}
