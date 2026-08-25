using System.Net;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebApp.Tests.Infrastructure;

namespace SentenceStudio.WebApp.Tests.Auth;

/// <summary>
/// The same sign-in hand-off, against the host that is actually running.
/// </summary>
/// <remarks>
/// <para>
/// The in-process tests prove the pipeline. This proves the deployment: it completes the real
/// <c>/account-action/AutoSignIn</c> round trip against the running Aspire WebApp and then loads
/// the routes that were failing, on <b>both</b> origins that host publishes.
/// </para>
/// <para>
/// Skipped unless <c>WEBAPP_LIVE_*</c> and <c>WEBAPP_LIVE_BASE_*</c> are set, so it never runs in
/// CI — there is no running host there, and a test that silently passed without one would be
/// worse than no test.
/// </para>
/// </remarks>
public sealed class LiveWebSignInVerificationTests
{
    private const string Email = "squad-jayne@sentencestudio.test";

    private static string? HttpsBase => Environment.GetEnvironmentVariable("WEBAPP_LIVE_BASE_HTTPS");
    private static string? HttpBase => Environment.GetEnvironmentVariable("WEBAPP_LIVE_BASE_HTTP");

    [LiveWebAppFact]
    public async Task ARealSignInOnTheRunningHostSurvivesDirectNavigationOnBothOrigins()
    {

        var pair = await LiveWebAppSession.MintAutoSignInPairAsync(Email);
        var url = PostSignInNavigation.AutoSignInUrl(pair, "/")!;

        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var client = new HttpClient(handler);

        var handoff = await client.GetAsync(HttpsBase + url);
        handoff.StatusCode.Should().Be(HttpStatusCode.Redirect, "the hand-off must complete");
        handoff.Headers.Location!.OriginalString.Should().NotContain("/auth/login");

        foreach (var origin in new[] { HttpsBase!, HttpBase! })
        {
            foreach (var route in new[] { "/", "/skills", "/profile", "/operator/sam-opportunities" })
            {
                var response = await client.GetAsync(origin + route);

                response.StatusCode.Should().Be(
                    HttpStatusCode.OK,
                    $"{origin}{route} must stay signed in after a real sign-in; it answered "
                    + $"{(int)response.StatusCode} -> {response.Headers.Location}");
            }
        }
    }
}
