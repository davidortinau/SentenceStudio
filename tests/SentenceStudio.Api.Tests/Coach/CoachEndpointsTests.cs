using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The HTTP surface: authentication, the feature/cohort gate, and the shape of the failures.
/// </summary>
public class CoachEndpointsTests
{
    private const string CohortUser = "coach-cohort-user";

    [Fact]
    public async Task CoachDiGraph_ResolvesWithNoChatClientRegistered()
    {
        // The precise requirement: nothing in the coach graph may need an IChatClient to be
        // constructed. Only an actual turn resolves one, and only then can it 503.
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddCoachRuntime(new ConfigurationBuilder().Build());
        services.AddCoachBaseline();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = true
        });

        var factory = provider.GetRequiredService<ICoachAgentFactory>();

        factory.Should().NotBeNull();
        factory.IsModelAvailable.Should().BeFalse();
        factory.TryCreateAgent(Array.Empty<Microsoft.Extensions.AI.AIFunction>()).Should().BeNull();
    }

    [Fact]
    public async Task Host_ValidatesAndBootsWithoutElevenLabsConfiguration()
    {
        await using var factory = new CoachApiFactory { CoachEnabled = false };
        factory.Services.GetService<IVoiceDiscoveryService>().Should().BeNull(
            "voice discovery is only available when its ElevenLabs client is configured");
        using var client = factory.CreateClient();

        // Building factory.Services runs the Development service-provider validation. A route
        // unrelated to speech then proves the validated host also starts accepting requests.
        using var response = await client.GetAsync("/api/v1/version");

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        factory.ChatClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task VoiceDiscovery_WithNoElevenLabsConfiguration_Returns503()
    {
        await using var factory = new CoachApiFactory();
        using var client = factory.CreateClient();
        Authenticate(client, CohortUser);

        using var response = await client.GetAsync("/api/v1/speech/voices?language=ko");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public void Host_WithElevenLabsConfiguration_RegistersVoiceDiscovery()
    {
        using var factory = new CoachApiFactory { ElevenLabsConfigured = true };

        factory.Services.GetRequiredService<IVoiceDiscoveryService>()
            .Should().BeOfType<VoiceDiscoveryService>();
    }

    [Fact]
    public async Task Coach_WithNoToken_Returns401()
    {
        await using var factory = new CoachApiFactory { CoachEnabled = true, CohortUserProfileId = CohortUser };
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/coach/availability");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Coach_WithATokenButNoUserProfileClaim_Returns401NotAServerError()
    {
        await using var factory = new CoachApiFactory { CoachEnabled = true, CohortUserProfileId = CohortUser };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestJwtGenerator.GenerateToken());

        using var response = await client.PostAsJsonAsync(
            "/api/v1/coach/sessions", new StartCoachSessionRequest { Resume = false });

        // Same behaviour as every other authenticated route: a missing claim is an auth
        // problem, never a 500.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Availability_FeatureOff_ReportsUnavailableWithoutTouchingTheModel()
    {
        await using var factory = new CoachApiFactory { CoachEnabled = false };
        using var client = factory.CreateClient();
        Authenticate(client, CohortUser);

        using var response = await client.GetAsync("/api/v1/coach/availability");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CoachAvailabilityResponse>();
        body!.IsAvailable.Should().BeFalse();
        body.State.Should().Be(CoachAvailabilityState.Disabled);
        factory.ChatClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Sessions_FeatureOff_Returns404()
    {
        await using var factory = new CoachApiFactory { CoachEnabled = false };
        using var client = factory.CreateClient();
        Authenticate(client, CohortUser);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/coach/sessions", new StartCoachSessionRequest { Resume = false });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.ChatClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Sessions_LearnerOutsideCohort_Returns404()
    {
        await using var factory = new CoachApiFactory { CoachEnabled = true, CohortUserProfileId = CohortUser };
        using var client = factory.CreateClient();
        Authenticate(client, "a-different-learner");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/coach/sessions", new StartCoachSessionRequest { Resume = false });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Session_OfAnotherLearner_Returns404NotForbidden()
    {
        await using var factory = new CoachApiFactory { CoachEnabled = true, CohortUserProfileId = CohortUser };
        using var client = factory.CreateClient();
        Authenticate(client, CohortUser);

        using var response = await client.GetAsync("/api/v1/coach/sessions/some-other-learners-session");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_UnknownSession_Returns404ProblemDetails()
    {
        await using var factory = new CoachApiFactory { CoachEnabled = true, CohortUserProfileId = CohortUser };
        using var client = factory.CreateClient();
        Authenticate(client, CohortUser);

        using var response = await client.DeleteAsync("/api/v1/coach/sessions/nope");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Cancel_WithNoRunInFlight_IsANoOp()
    {
        await using var factory = new CoachApiFactory { CoachEnabled = true, CohortUserProfileId = CohortUser };
        using var client = factory.CreateClient();
        Authenticate(client, CohortUser);

        using var response = await client.PostAsync("/api/v1/coach/sessions/anything/cancel", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task NoCoachToolCanWrite()
    {
        await using var factory = new CoachApiFactory { CoachEnabled = true, CohortUserProfileId = CohortUser };
        using var scope = factory.Services.CreateScope();

        var tools = scope.ServiceProvider.GetRequiredService<ICoachToolFactory>().CreateTools();

        tools.Should().HaveCount(CoachToolNames.All.Count);
        tools.Select(t => t.Name).Should().BeEquivalentTo(CoachToolNames.All);

        // A read-only surface: every tool name is a get_/preview_ verb, and none of them
        // accepts a user, profile, or tenant argument.
        foreach (var tool in tools)
        {
            tool.Name.Should().Match(n => n.StartsWith("get_", StringComparison.Ordinal)
                                          || n.StartsWith("preview_", StringComparison.Ordinal));

            var schema = tool.JsonSchema.GetRawText();
            schema.Should().NotContainEquivalentOf("userprofileid");
            schema.Should().NotContainEquivalentOf("tenantid");
        }
    }

    [Fact]
    public async Task CoachSchema_IsCreatedOnItsOwnContext()
    {
        await using var factory = new CoachApiFactory { CoachEnabled = true, CohortUserProfileId = CohortUser };
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<CoachDbContext>();

        (await db.CoachSessions.CountAsync()).Should().Be(0);
        (await db.CoachPlanRevisions.CountAsync()).Should().Be(0);
        (await db.CoachUsages.CountAsync()).Should().Be(0);
    }

    private static void Authenticate(HttpClient client, string userProfileId) =>
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                TestJwtGenerator.GenerateToken(userProfileId: userProfileId));
}
