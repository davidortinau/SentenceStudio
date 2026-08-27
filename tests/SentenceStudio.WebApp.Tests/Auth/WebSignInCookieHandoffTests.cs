using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Services;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebApp.Tests.Infrastructure;

namespace SentenceStudio.WebApp.Tests.Auth;

/// <summary>
/// Signing in on the web has to leave a durable Identity cookie behind, and that cookie has to
/// work on every full-document request the dev host can serve.
/// </summary>
/// <remarks>
/// <para>
/// The regression these pin down looked like an operator-page problem and was neither operator
/// nor page. A learner signed in, the dashboard rendered interactively — the circuit was holding
/// an authenticated principal — and then the first typed URL or deep link arrived at the server
/// with no usable cookie and was sent to <c>/auth/login</c>. It reproduced on <c>/skills</c> and
/// <c>/profile</c> just as readily as on the operator route.
/// </para>
/// <para>
/// Two independent things have to hold, so both are tested: the sign-in must actually round-trip
/// through <c>/account-action/AutoSignIn</c> (a circuit cannot set a cookie over its own
/// WebSocket), and the cookie it writes must not be scheme-locked to one of the two origins the
/// Development host publishes.
/// </para>
/// <para>
/// <b>Nothing here injects a cookie.</b> Every test starts from the real password sign-in and the
/// URL the login page itself would navigate to, because a test that fabricated the cookie would
/// have passed throughout the outage.
/// </para>
/// </remarks>
public sealed class WebSignInCookieHandoffTests : IAsyncLifetime
{
    private const string Email = "signin-handoff@sentencestudio.test";
    private const string Password = "IntegrationTest1!";
    private const string IdentityCookie = ".AspNetCore.Identity.Application";

    /// <summary>Every full-document route the reproduced failure covered.</summary>
    public static TheoryData<string> FullDocumentRoutes() =>
        ["/", "/skills", "/profile", "/operator/sam-opportunities"];

    private readonly OperatorWebAppFactory _factory = new(nameof(WebSignInCookieHandoffTests));

    public Task InitializeAsync() => _factory.InitializeAsync();

    public Task DisposeAsync() => _factory.DisposeAsync();

    // ------------------------------------------------- the handoff itself

    /// <summary>
    /// A real password sign-in produces a cookie round trip, and the cookie exists before anything
    /// treats the learner as signed in.
    /// </summary>
    /// <remarks>
    /// Driven through the same two steps the login page performs: ask the host's real
    /// <see cref="IAuthService"/> to validate the password, then follow the URL
    /// <see cref="PostSignInNavigation.AutoSignInUrl"/> builds from the result. If the first step
    /// ever stops returning a <c>userId|token</c> pair, or the second stops being reachable, this
    /// fails — which is precisely the outage.
    /// </remarks>
    [WebAppPostgresFact]
    public async Task ARealSignInWritesTheIdentityCookieThroughAutoSignIn()
    {
        await _factory.SeedLearnerAsync(Email, Password);
        var client = _factory.CreateBrowserClient();

        var url = await SignInAndBuildHandoffUrlAsync();

        url.Should().NotBeNull(
            "the web host validates the password itself and hands back a one-time pair, because a "
            + "circuit cannot set a cookie over its own WebSocket; a null here means the learner "
            + "would be left with circuit state and no session");

        var handoff = await client.GetAsync(url);

        handoff.StatusCode.Should().Be(HttpStatusCode.Redirect);
        handoff.Headers.Location!.OriginalString.Should().NotContain("/auth/login",
            "a bounce back to the login form means the one-time token was refused");

        handoff.Headers.GetValues("Set-Cookie").Should().Contain(
            c => c.StartsWith(IdentityCookie, StringComparison.Ordinal),
            "the durable session is this cookie; without it every full-document request is "
            + "anonymous however healthy the circuit looks");

        var dashboard = await client.GetAsync("/");
        dashboard.StatusCode.Should().Be(HttpStatusCode.OK,
            "the dashboard must be reachable as a document, not only as a circuit render");
    }

    // ------------------------------------------------- what the cookie must authorize

    [WebAppPostgresTheory]
    [MemberData(nameof(FullDocumentRoutes))]
    public async Task TheSignInCookieAuthorizesEveryFullDocumentRoute(string route)
    {
        await _factory.SeedLearnerAsync(Email, Password);
        var client = _factory.CreateBrowserClient();
        await CompleteSignInAsync(client);

        var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"a signed-in learner must be able to load {route} directly; instead it went to "
            + $"{response.Headers.Location}");
    }

    /// <summary>
    /// The same session works on both origins the Development host publishes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the defect, stated exactly. Aspire publishes the app on <c>http://localhost:{p}</c>
    /// and <c>https://localhost:{q}</c>, and <c>UseHttpsRedirection</c> is off in Development, so
    /// both are live. Under the framework default of <c>SameAsRequest</c> a sign-in completed over
    /// https writes a <c>Secure</c> cookie the browser will never send to the http origin — so a
    /// learner who signed in on one and opened a link on the other was anonymous, with no
    /// indication why.
    /// </para>
    /// <para>
    /// The sign-in here is completed over https and the routes are then requested over http, which
    /// is the direction that failed. Cookies are not otherwise scheme-scoped, so one session is
    /// expected to serve both.
    /// </para>
    /// </remarks>
    [WebAppPostgresTheory]
    [MemberData(nameof(FullDocumentRoutes))]
    public async Task TheSignInCookieIsNotLockedToTheSchemeItWasIssuedOn(string route)
    {
        await _factory.SeedLearnerAsync(Email, Password);
        var client = _factory.CreateBrowserClient();

        await CompleteSignInAsync(client, origin: "https://localhost");

        var response = await client.GetAsync("http://localhost" + route);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"the Development host serves {route} on both schemes, so a session established on one "
            + "must not be invisible on the other");
    }

    [WebAppPostgresFact]
    public async Task TheIssuedCookieCarriesNoSecureFlagInDevelopment()
    {
        await _factory.SeedLearnerAsync(Email, Password);
        var client = _factory.CreateBrowserClient();

        var handoff = await client.GetAsync(await SignInAndBuildHandoffUrlAsync());

        var cookie = handoff.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(IdentityCookie, StringComparison.Ordinal));

        cookie.Should().NotContain("secure",
            "Secure is what locked the session to one of the two origins this host serves; the "
            + "policy is Always everywhere except Development, where both origins are loopback");
        cookie.Should().Contain("httponly", "the session cookie is never script-readable");
        cookie.Should().Contain("samesite=lax");
    }

    // ------------------------------------------------- reloads, deep links, anonymity

    /// <summary>A deep link and a reload both keep the session.</summary>
    /// <remarks>
    /// Separate from the route theory because the failure mode is different: the first request to
    /// a route can succeed while a repeat fails if anything rotates or drops the cookie. Each
    /// route is requested twice and both must hold.
    /// </remarks>
    [WebAppPostgresFact]
    public async Task TheSessionSurvivesReloadsAndDeepLinks()
    {
        await _factory.SeedLearnerAsync(Email, Password);
        var client = _factory.CreateBrowserClient();
        await CompleteSignInAsync(client);

        foreach (var route in new[] { "/skills", "/profile", "/operator/sam-opportunities" })
        {
            (await client.GetAsync(route)).StatusCode.Should().Be(
                HttpStatusCode.OK, $"deep link to {route}");

            (await client.GetAsync(route)).StatusCode.Should().Be(
                HttpStatusCode.OK, $"reload of {route}");
        }
    }

    [WebAppPostgresTheory]
    [MemberData(nameof(FullDocumentRoutes))]
    public async Task AnAnonymousVisitorIsStillSentToTheLoginPage(string route)
    {
        var client = _factory.CreateBrowserClient();

        var response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "loosening the cookie policy must not have made anything anonymous readable");
        response.Headers.Location!.OriginalString.Should().Contain("/auth/login");
    }

    [WebAppPostgresFact]
    public async Task SigningOutEndsTheSessionOnEveryRoute()
    {
        await _factory.SeedLearnerAsync(Email, Password);
        var client = _factory.CreateBrowserClient();
        await CompleteSignInAsync(client);

        await client.GetAsync("/account-action/SignOut");

        foreach (var route in new[] { "/", "/skills", "/operator/sam-opportunities" })
        {
            (await client.GetAsync(route)).StatusCode.Should().Be(
                HttpStatusCode.Redirect, $"{route} after sign-out");
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Validates the password through the host's real auth service and returns the URL the login
    /// page would navigate to.
    /// </summary>
    private async Task<string?> SignInAndBuildHandoffUrlAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var result = await auth.SignInAsync(Email, Password);
        result.Should().NotBeNull("the seeded password must be accepted");

        return PostSignInNavigation.AutoSignInUrl(result!.AccessToken, "/");
    }

    /// <summary>Runs the whole sign-in, leaving the Identity cookie in the client's jar.</summary>
    private async Task CompleteSignInAsync(HttpClient client, string origin = "")
    {
        var url = await SignInAndBuildHandoffUrlAsync();
        url.Should().NotBeNull();

        var response = await client.GetAsync(origin + url);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the handoff must complete");
        response.Headers.Location!.OriginalString.Should().NotContain("/auth/login");
    }
}

/// <summary>
/// The sign-in hand-off decision itself, which needs neither a database nor a host.
/// </summary>
/// <remarks>
/// Deliberately a separate class with no <see cref="IAsyncLifetime"/>. These assertions are pure
/// logic, and living alongside the PostgreSQL-backed tests meant they failed — rather than ran —
/// on a machine with no server, which is the one place their answer is still worth having.
/// </remarks>
public sealed class SignInHandoffDecisionTests
{
    /// <summary>
    /// A MAUI sign-in has no cookie to fetch and must not be sent through the endpoint.
    /// </summary>
    /// <remarks>
    /// <c>IdentityAuthService</c> returns a JWT it keeps itself, so there is no <c>userId|token</c>
    /// pair, no round trip, and — per <see cref="BlazorHostKind.ShouldForceLoadAfterSignIn"/> — no
    /// forced document load either. Forcing one inside a BlazorWebView tears down the renderer and
    /// the DI scope, so this stays asserted next to the web behaviour it must not acquire.
    /// </remarks>
    [Fact]
    public void ANativeSignInNeitherRoundTripsNorForcesALoad()
    {
        PostSignInNavigation.AutoSignInUrl("header.payload.signature", "/")
            .Should().BeNull("a JWT is not a one-time cookie handoff pair");

        BlazorHostKind.ShouldForceLoadAfterSignIn("app://0.0.0.0/")
            .Should().BeFalse("a forced load in a BlazorWebView buys nothing and costs the renderer");

        BlazorHostKind.ShouldForceLoadAfterSignIn("https://localhost:7071/")
            .Should().BeTrue("the web host has to leave the page for the server to set the cookie");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-separator-here")]
    [InlineData("|missing-user-id")]
    [InlineData("missing-token|")]
    public void AMalformedSignInResultIsNeverTurnedIntoAHandoffUrl(string? accessToken) =>
        PostSignInNavigation.AutoSignInUrl(accessToken, "/")
            .Should().BeNull("a fabricated pair would send the learner to an InvalidLink bounce");

}
