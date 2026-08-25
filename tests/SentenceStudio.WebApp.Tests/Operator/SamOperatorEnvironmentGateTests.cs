using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SentenceStudio.WebApp.Operator;
using SentenceStudio.WebApp.Tests.Infrastructure;

namespace SentenceStudio.WebApp.Tests.Operator;

/// <summary>
/// The operator surface is absent outside Development, and stays absent after the token-forwarding
/// fix.
/// </summary>
/// <remarks>
/// Making a server-side API call authenticate from inside a circuit is exactly the kind of change
/// that could quietly turn a dead screen into a live one somewhere it was never meant to exist, so
/// the environment gates are asserted here rather than assumed. This class covers the two the
/// WebApp owns; the API's own gate — the routes are not mapped outside Development — is covered by
/// <c>CoachOpportunityRolloutTests</c> in the API suite.
/// </remarks>
public sealed class SamOperatorEnvironmentGateTests : IAsyncLifetime
{
    private const string OperatorPath = "/operator/sam-opportunities";
    private const string Password = "IntegrationTest1!";

    private readonly OperatorWebAppFactory _factory =
        new(nameof(SamOperatorEnvironmentGateTests), Environments.Production);

    public Task InitializeAsync() => _factory.InitializeAsync();

    public Task DisposeAsync() => _factory.DisposeAsync();

    [WebAppPostgresFact]
    public void TheOperatorClientIsNotEvenRegisteredOutsideDevelopment()
    {
        using var scope = _factory.Services.CreateScope();

        var client = scope.ServiceProvider.GetService<SamOpportunityOperatorClient>();

        client.Should().BeNull(
            "the typed client is registered only in Development, so a surviving route "
            + "registration in another environment has nothing to call");
    }

    [WebAppPostgresFact]
    public async Task TheOperatorRouteRendersTheNotFoundStateOutsideDevelopment()
    {
        var learner = await _factory.SeedLearnerAsync("operator-prod@sentencestudio.test", Password);
        var client = _factory.CreateBrowserClient();
        await _factory.SignInAsync(client, learner);

        var response = await client.GetAsync(OperatorPath);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Page not found",
            "outside Development the page renders the standard not-found state");
        body.Should().NotContain("Runtime telemetry for capability gaps",
            "no part of the operator surface may render outside Development");

        _factory.Operator.Requests.Should().NotContain(
            r => r.Path.Contains("/operator/opportunities", StringComparison.Ordinal),
            "outside Development the page must not reach for the operator API at all");
    }
}
