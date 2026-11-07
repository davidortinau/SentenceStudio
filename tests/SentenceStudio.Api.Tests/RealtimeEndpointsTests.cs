using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Tests.Infrastructure;

namespace SentenceStudio.Api.Tests;

/// <summary>
/// The realtime client-secret endpoint behind the native Conversation voice mode: the owner is the
/// trusted profile claim (fail closed), and the per-learner budget counts only issued secrets.
/// No test reaches the model provider: the endpoint is configured off, so a profile token gets 503.
/// </summary>
public sealed class RealtimeEndpointsTests : IClassFixture<ProfileSpeechApiFactory>
{
    private const string Path = "/api/v1/realtime/client-secret";
    private readonly WebApplicationFactory<Program> _factory;

    public RealtimeEndpointsTests(ProfileSpeechApiFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.UseSetting("AI:OpenAI:Endpoint", ""));
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        var response = await _factory.CreateClient().PostAsync(Path, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenWithoutProfileClaim_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtGenerator.GenerateToken());

        var response = await client.PostAsync(Path, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProfileToken_ReachesTheIssuer()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            TestJwtGenerator.GenerateToken(userProfileId: $"voice-{Guid.NewGuid():N}"));

        var response = await client.PostAsync(Path, content: null);

        // Accepted as the owner; the realtime endpoint is not configured in this host.
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not configured");
    }

    [Fact]
    public void Budget_CountsReservationsAndGivesBackFailedOnes()
    {
        var issuer = new RealtimeClientSecretIssuer(new ConfigurationBuilder().Build(),
            NullLogger<RealtimeClientSecretIssuer>.Instance);
        var reservations = new List<DateTimeOffset>();
        for (var i = 0; i < RealtimeClientSecretIssuer.MaxSecretsPerWindow; i++)
        {
            issuer.TryReserve("owner-a", out var reservation).Should().BeTrue();
            reservations.Add(reservation);
        }

        issuer.TryReserve("owner-a", out _).Should().BeFalse("the learner used the whole window");
        issuer.TryReserve("owner-b", out _).Should().BeTrue("each learner has their own budget");

        issuer.Release("owner-a", reservations[^1]);
        issuer.TryReserve("owner-a", out _).Should().BeTrue("a failed mint gives its slot back");
    }

    [Fact]
    public async Task NotConfigured_DoesNotSpendTheBudget()
    {
        var issuer = new RealtimeClientSecretIssuer(new ConfigurationBuilder().Build(),
            NullLogger<RealtimeClientSecretIssuer>.Instance);

        for (var i = 0; i < RealtimeClientSecretIssuer.MaxSecretsPerWindow + 2; i++)
            (await issuer.IssueAsync("owner-a", CancellationToken.None)).Status
                .Should().Be(RealtimeSecretStatus.NotConfigured);

        issuer.TryReserve("owner-a", out _).Should().BeTrue();
    }
}
