using System.IdentityModel.Tokens.Jwt;
using System.Net;
using SentenceStudio.WebApp.Tests.Infrastructure;

namespace SentenceStudio.WebApp.Tests.Operator;

/// <summary>
/// The Development-only Sam opportunity operator page, exercised through the WebApp's real
/// pipeline: real Identity cookie, real authorization, real endpoint routing, real components.
/// </summary>
/// <remarks>
/// The behaviour these tests pin down is that visiting an operator route is never an event in the
/// learner's session. It does not sign them out, it does not bounce them to the login form, and a
/// refusal from the operator API is rendered as an absence rather than propagated as an
/// authentication failure.
/// </remarks>
public sealed class SamOperatorSurfaceSessionTests : IAsyncLifetime
{
    private const string OperatorPath = "/operator/sam-opportunities";
    private const string LearnerPath = "/";
    private const string Password = "IntegrationTest1!";

    /// <summary>The cookie the learner's session lives in.</summary>
    private const string IdentityCookie = ".AspNetCore.Identity.Application";

    private static readonly string RollupWithOneRow = """
    [{"fingerprint":"a1b2c3d4e5f6a7b8","kind":"AmbiguousFollowUp","disposition":"Product",
      "capabilityCode":"referent_lost_after_offer","toolName":null,"failureCode":null,
      "offerLink":"AfterOffer","totalOccurrences":1,"distinctLearners":1,"rowCount":1,
      "firstObservedAtUtc":"2026-08-20T00:00:00Z","lastObservedAtUtc":"2026-08-20T00:00:00Z",
      "statuses":["New"]}]
    """;

    private readonly OperatorWebAppFactory _factory = new(nameof(SamOperatorSurfaceSessionTests));

    public Task InitializeAsync() => _factory.InitializeAsync();

    public Task DisposeAsync() => _factory.DisposeAsync();

    // ------------------------------------------------------- the allowed caller

    [WebAppPostgresFact]
    public async Task ADirectLoadOfTheOperatorRouteKeepsAnAuthenticatedLearnerSignedIn()
    {
        var learner = await _factory.SeedLearnerAsync("operator-direct@sentencestudio.test", Password);
        var client = _factory.CreateBrowserClient();
        await _factory.SignInAsync(client, learner);

        var response = await client.GetAsync(OperatorPath);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a direct load of the operator route must render for a signed-in learner, not bounce "
            + $"to {response.Headers.Location}");
    }

    [WebAppPostgresFact]
    public async Task ADeepLinkRendersTheRollupForAnAllowedCaller()
    {
        var learner = await _factory.SeedLearnerAsync("operator-rows@sentencestudio.test", Password);
        var client = _factory.CreateBrowserClient();
        await _factory.SignInAsync(client, learner);

        _factory.Operator.RollupBody = RollupWithOneRow;

        var response = await client.GetAsync(OperatorPath);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Runtime telemetry for capability gaps",
            "the operator surface must render, not the not-found placeholder");
        body.Should().Contain("referent_lost_after_offer",
            "the rollup the API returned must reach the page");
        body.Should().NotContain("not available for this caller",
            "an allowed caller must not be told the surface is unavailable");
    }

    [WebAppPostgresFact]
    public async Task TheOperatorRequestCarriesATokenNamingTheSignedInLearnerAndNobodyElse()
    {
        var learner = await _factory.SeedLearnerAsync("operator-token@sentencestudio.test", Password);
        var bystander = await _factory.SeedLearnerAsync("operator-bystander@sentencestudio.test", Password);

        var client = _factory.CreateBrowserClient();
        await _factory.SignInAsync(client, learner);

        await client.GetAsync(OperatorPath);

        var operatorCall = _factory.Operator.Requests
            .FirstOrDefault(r => r.Path.Contains("/operator/opportunities", StringComparison.Ordinal));

        operatorCall.Should().NotBeNull("the page must call the operator API while rendering");
        operatorCall!.AuthorizationScheme.Should().Be(
            "Bearer", "the operator API is behind RequireAuthorization and only accepts a bearer token");

        var claims = new JwtSecurityTokenHandler().ReadJwtToken(operatorCall.BearerToken).Claims.ToList();
        var profileClaim = claims
            .FirstOrDefault(c => c.Type == SentenceStudio.Contracts.AuthClaimTypes.UserProfileId)?.Value;

        profileClaim.Should().Be(
            learner.UserProfileId,
            "the token must name the caller, so the API's cohort check decides on the real learner");
        profileClaim.Should().NotBe(
            bystander.UserProfileId, "no other learner's identity may reach the operator API");
    }

    // ------------------------------------------------- refusals are not sign-outs

    [WebAppPostgresFact]
    public async Task ANonOperatorCallerSeesTheUnavailableStateAndKeepsTheirSession()
    {
        var learner = await _factory.SeedLearnerAsync("operator-outsider@sentencestudio.test", Password);
        var client = _factory.CreateBrowserClient();
        await _factory.SignInAsync(client, learner);

        // What the API answers a caller outside the cohort with: the same 404 it uses for a row
        // that does not exist, so the surface is indistinguishable from absent.
        _factory.Operator.RollupStatus = HttpStatusCode.NotFound;

        var response = await client.GetAsync(OperatorPath);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "a refusal from the operator API is a state to render, not a redirect");
        body.Should().Contain("not available for this caller",
            "the caller must be shown the safe unavailable state");

        await AssertStillSignedInAsync(client, response);
    }

    [WebAppPostgresFact]
    public async Task A401FromTheOperatorEndpointDoesNotClearAnOtherwiseValidIdentity()
    {
        var learner = await _factory.SeedLearnerAsync("operator-401@sentencestudio.test", Password);
        var client = _factory.CreateBrowserClient();
        await _factory.SignInAsync(client, learner);

        _factory.Operator.RollupStatus = HttpStatusCode.Unauthorized;

        var response = await client.GetAsync(OperatorPath);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a 401 from a downstream operator API says nothing about the learner's WebApp session");
        body.Should().Contain("not available for this caller");

        await AssertStillSignedInAsync(client, response);
    }

    // -------------------------------------------------- sessions that really are gone

    [WebAppPostgresFact]
    public async Task AnAnonymousVisitorIsSentToTheLoginPage()
    {
        var client = _factory.CreateBrowserClient();

        var response = await client.GetAsync(OperatorPath);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain(
            "/auth/login", "an unauthenticated visitor signs in, exactly as on any other route");
    }

    [WebAppPostgresFact]
    public async Task AnExpiredSessionIsSentToTheLoginPage()
    {
        var learner = await _factory.SeedLearnerAsync("operator-expired@sentencestudio.test", Password);
        var client = _factory.CreateBrowserClient();
        await _factory.SignInAsync(client, learner);

        // The real end of a session: the sign-out endpoint deletes the Identity cookie.
        await client.GetAsync("/account-action/SignOut");

        var response = await client.GetAsync(OperatorPath);

        response.StatusCode.Should().Be(
            HttpStatusCode.Redirect,
            "when the session really is gone the operator route behaves like every other route");
        response.Headers.Location!.OriginalString.Should().Contain("/auth/login");
    }

    /// <summary>
    /// Asserts the learner's session survived <paramref name="operatorResponse"/> intact.
    /// </summary>
    /// <remarks>
    /// Two independent checks, because either one alone can pass while the session is broken: the
    /// operator response must not have deleted the Identity cookie, and a subsequent learner route
    /// must still render rather than redirect to the login form.
    /// </remarks>
    private static async Task AssertStillSignedInAsync(
        HttpClient client, HttpResponseMessage operatorResponse)
    {
        if (operatorResponse.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            setCookies.Should().NotContain(
                c => c.Contains(IdentityCookie, StringComparison.Ordinal)
                     && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase),
                "an operator refusal must never delete the learner's session cookie");
        }

        var afterwards = await client.GetAsync(LearnerPath);
        afterwards.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the learner must still be signed in to the app after the operator surface refused; "
            + $"instead the next request went to {afterwards.Headers.Location}");
    }
}
