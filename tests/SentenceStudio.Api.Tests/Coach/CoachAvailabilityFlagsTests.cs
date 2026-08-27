using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The two capability flags on availability: durable history and learner memory.
/// </summary>
/// <remarks>
/// <para>
/// These run through the real host rather than the application harness on purpose. The flags
/// exist to answer "may I show this surface", and the only honest place to ask that is a booted
/// host reading real configuration, where a wrong option key or a missing registration shows up
/// as a wrong answer instead of a passing stub.
/// </para>
/// <para>
/// The matrix below is four hosts rather than one parameterised host because the whole claim is
/// that the flags are independent. A single host with both flags flipped together would pass
/// even if the endpoint returned one boolean twice.
/// </para>
/// </remarks>
public class CoachAvailabilityFlagsTests
{
    private const string CohortUser = "coach-flags-user";

    [Fact]
    public async Task HistoryOnAndMemoryOff_ReportsHistoryOnly()
    {
        var body = await ReadAvailabilityAsync(durableHistory: true, memory: false);

        body.IsDurableHistoryAvailable.Should().BeTrue();
        body.IsMemoryAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task MemoryOnAndHistoryOff_ReportsMemoryOnly()
    {
        // The direction that matters most for the claim of independence: an approved preference
        // is not part of any conversation, so memory has to work on a host that keeps no
        // durable transcript at all.
        var body = await ReadAvailabilityAsync(durableHistory: false, memory: true);

        body.IsDurableHistoryAvailable.Should().BeFalse();
        body.IsMemoryAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task BothOn_ReportsBoth()
    {
        var body = await ReadAvailabilityAsync(durableHistory: true, memory: true);

        body.IsDurableHistoryAvailable.Should().BeTrue();
        body.IsMemoryAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task BothOff_ReportsNeither()
    {
        var body = await ReadAvailabilityAsync(durableHistory: false, memory: false);

        body.IsDurableHistoryAvailable.Should().BeFalse();
        body.IsMemoryAvailable.Should().BeFalse();
        body.IsAvailable.Should().BeTrue("the coach itself is on; only its optional surfaces are off");
    }

    [Fact]
    public async Task CoachOff_ReportsNeitherEvenWithBothFeaturesConfiguredOn()
    {
        // This response comes from the endpoint's own Disabled fallback, not from the service,
        // so it is a separate construction site that could easily have been left behind. A
        // client must not be told to render history for a coach it cannot enter.
        await using var factory = new CoachApiFactory
        {
            CoachEnabled = false,
            CohortUserProfileId = CohortUser,
            DurableHistory = true,
            Memory = true
        };

        var body = await ReadAvailabilityAsync(factory);

        body.IsAvailable.Should().BeFalse();
        body.State.Should().Be(CoachAvailabilityState.Disabled);
        body.IsDurableHistoryAvailable.Should().BeFalse();
        body.IsMemoryAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task TheFlagsAreOnTheWireAndSayNothingAboutConfiguration()
    {
        await using var factory = new CoachApiFactory
        {
            CoachEnabled = true,
            CohortUserProfileId = CohortUser,
            DurableHistory = true,
            Memory = true
        };

        using var client = factory.CreateClient();
        Authenticate(client, CohortUser);

        using var response = await client.GetAsync("/api/v1/coach/availability");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.TryGetProperty("isDurableHistoryAvailable", out var history).Should().BeTrue();
        root.TryGetProperty("isMemoryAvailable", out var memory).Should().BeTrue();
        history.GetBoolean().Should().BeTrue();
        memory.GetBoolean().Should().BeTrue();

        // A capability answer, not a configuration dump. Anyone who can call this route learns
        // what they may open and nothing about how the host was deployed: no section names, no
        // key names, no cohort list, no reason the feature is off.
        foreach (var leak in new[]
                 {
                     "Coach:", "DurableHistory:", "Memory:", "Enabled",
                     "AllowedUserProfileIds", "appsettings", "ConnectionStrings", CohortUser
                 })
        {
            json.Should().NotContain(leak);
        }
    }

    [Fact]
    public void AResponseFromAnOlderServerReadsAsNeitherFeatureAvailable()
    {
        // Backward compatibility in the direction that actually happens: a new client parsing an
        // old server's body. Absent means false, which hides a surface that exists rather than
        // offering one that does not.
        var json = """
            {"isAvailable":true,"state":"ResumeAvailable","entryPointLabel":"Resume coach"}
            """;

        var restored = JsonSerializer.Deserialize<CoachAvailabilityResponse>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        restored!.IsAvailable.Should().BeTrue();
        restored.IsDurableHistoryAvailable.Should().BeFalse();
        restored.IsMemoryAvailable.Should().BeFalse();
    }

    private static async Task<CoachAvailabilityResponse> ReadAvailabilityAsync(
        bool durableHistory,
        bool memory)
    {
        await using var factory = new CoachApiFactory
        {
            CoachEnabled = true,
            CohortUserProfileId = CohortUser,
            DurableHistory = durableHistory,
            Memory = memory
        };

        return await ReadAvailabilityAsync(factory);
    }

    private static async Task<CoachAvailabilityResponse> ReadAvailabilityAsync(CoachApiFactory factory)
    {
        using var client = factory.CreateClient();
        Authenticate(client, CohortUser);

        using var response = await client.GetAsync("/api/v1/coach/availability");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CoachAvailabilityResponse>();
        body.Should().NotBeNull();

        // Reading availability never reaches a model. If a flag were ever computed by asking the
        // agent whether it could do something, this is where that would surface.
        factory.ChatClient.CallCount.Should().Be(0);

        return body!;
    }

    private static void Authenticate(HttpClient client, string userProfileId) =>
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                TestJwtGenerator.GenerateToken(userProfileId: userProfileId));
}
