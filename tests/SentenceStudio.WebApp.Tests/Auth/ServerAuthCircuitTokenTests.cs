using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Contracts;
using SentenceStudio.Services;
using SentenceStudio.WebApp.Auth;
using SentenceStudio.WebApp.Platform;
using SentenceStudio.WebApp.Tests.Infrastructure;

namespace SentenceStudio.WebApp.Tests.Auth;

/// <summary>
/// Minting an API token for a call that originates inside a Blazor circuit rather than an HTTP
/// request.
/// </summary>
/// <remarks>
/// <para>
/// This is the regression that made the operator page useless. Every server-side API call the
/// WebApp makes goes through <c>AuthenticatedHttpMessageHandler</c>, which asks
/// <see cref="ServerAuthService"/> for a token. That method used to read the caller from
/// <c>IHttpContextAccessor</c> alone, and during the InteractiveServer pass <c>HttpContext</c> is
/// null — so it returned null and the request went out anonymous.
/// </para>
/// <para>
/// The visible symptom was specific to the operator surface and worth spelling out, because it is
/// not the 401 one would expect. In Development the API admits an anonymous request through its dev
/// auth fallback as a principal carrying no <c>user_profile_id</c>. That principal then fails the
/// operator cohort check, and the surface answers a cohort failure with the same 404 it uses for
/// "no such row" — deliberately, so it cannot be used as an existence oracle. The page therefore
/// rendered its rows during prerender and replaced them with the unavailable state the instant the
/// circuit re-ran initialisation.
/// </para>
/// <para>
/// The circuit tier is <see cref="CircuitUserStateAccessor"/> — an <c>AsyncLocal</c> snapshot the
/// circuit handler publishes for every inbound activity, including the first interactive render.
/// It is the same tier <c>WebPreferencesService</c> already uses to resolve the active profile
/// during that pass, so this is the host's existing pattern rather than a second one.
/// </para>
/// </remarks>
public sealed class ServerAuthCircuitTokenTests : IAsyncLifetime
{
    private const string Password = "IntegrationTest1!";

    private readonly OperatorWebAppFactory _factory = new(nameof(ServerAuthCircuitTokenTests));

    public Task InitializeAsync() => _factory.InitializeAsync();

    public Task DisposeAsync() => _factory.DisposeAsync();

    [WebAppPostgresFact]
    public async Task ATokenIsMintedFromTheCircuitSnapshotWhenThereIsNoHttpContext()
    {
        var learner = await _factory.SeedLearnerAsync("circuit-token@sentencestudio.test", Password);

        var token = await MintWithCircuitStateAsync(
            new CircuitUserState(learner.UserId, learner.UserProfileId));

        token.Should().NotBeNullOrEmpty(
            "an interactive component's API call must be authenticated as the learner who is "
            + "driving the circuit, even though there is no HttpContext behind it");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.Should().Contain(
            c => c.Type == AuthClaimTypes.UserProfileId && c.Value == learner.UserProfileId,
            "the API's cohort and ownership checks read user_profile_id, so a token without it "
            + "authenticates as somebody with no learner identity at all");
    }

    [WebAppPostgresFact]
    public async Task TheCircuitTokenNamesOnlyTheLearnerDrivingTheCircuit()
    {
        var learner = await _factory.SeedLearnerAsync("circuit-owner@sentencestudio.test", Password);
        var bystander = await _factory.SeedLearnerAsync("circuit-other@sentencestudio.test", Password);

        var token = await MintWithCircuitStateAsync(
            new CircuitUserState(learner.UserId, learner.UserProfileId));

        var claims = new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.ToList();

        claims.Should().Contain(c => c.Type == "user_id" && c.Value == learner.UserId);
        claims.Should().NotContain(
            c => c.Value == bystander.UserProfileId || c.Value == bystander.UserId,
            "the snapshot is per-circuit; one learner's identity must never be minted into "
            + "another's token");
    }

    [WebAppPostgresFact]
    public async Task NoTokenIsMintedWhenThereIsNeitherARequestNorACircuit()
    {
        var token = await MintWithCircuitStateAsync(circuitState: null);

        token.Should().BeNull(
            "a background call that belongs to no signed-in caller must stay anonymous rather "
            + "than borrow an identity");
    }

    [WebAppPostgresFact]
    public async Task AnEmptyCircuitSnapshotMintsNothing()
    {
        var token = await MintWithCircuitStateAsync(CircuitUserState.Empty);

        token.Should().BeNull(
            "an unauthenticated circuit is not a caller; the snapshot being present is not the "
            + "same as the snapshot naming somebody");
    }

    [WebAppPostgresFact]
    public void TheWebAppUsesTheAuthServiceThatDoesNotDiscardCredentialsOnApiRefusal()
    {
        using var scope = _factory.Services.CreateScope();

        var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();

        authService.Should().BeOfType<ServerAuthService>(
            "the shared IdentityAuthService clears stored credentials after consecutive 401/403 "
            + "answers, which is right for a device holding its own refresh token and wrong for a "
            + "host whose session is an Identity cookie — wiring it in here would let a refusal "
            + "from an operator endpoint sign the learner out of the learner app");
    }

    /// <summary>
    /// Asks the real <see cref="IAuthService"/> for a token with no ambient HTTP request and the
    /// supplied circuit snapshot, which is exactly the state an interactive render runs under.
    /// </summary>
    private async Task<string?> MintWithCircuitStateAsync(CircuitUserState? circuitState)
    {
        var accessor = _factory.Services.GetRequiredService<CircuitUserStateAccessor>();
        var previous = accessor.Current;
        accessor.Current = circuitState;

        try
        {
            using var scope = _factory.Services.CreateScope();
            var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
            return await auth.GetAccessTokenAsync([]);
        }
        finally
        {
            accessor.Current = previous;
        }
    }
}
