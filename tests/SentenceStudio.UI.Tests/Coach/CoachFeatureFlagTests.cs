using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// How the two optional coach surfaces decide whether they exist for this learner.
/// </summary>
/// <remarks>
/// The rule these tests hold in place is that the availability response decides whether to ask,
/// and the route decides what is actually there. A flag that is off means no request goes out at
/// all — the old behaviour spent every learner without the feature a 404 to discover they did not
/// have it, and a 404 could never tell "switched off" from "not your data". A flag that is on is
/// still not a promise, so a 404 arriving anyway remains authoritative.
/// </remarks>
public class CoachFeatureFlagTests
{
    private static FakeCoachApiClient ClientWith(bool history, bool memory)
        => new()
        {
            DurableHistoryAvailable = true,
            Availability = new CoachAvailabilityResponse
            {
                IsAvailable = true,
                State = CoachAvailabilityState.Available,
                CanEditPlan = true,
                IsDurableHistoryAvailable = history,
                IsMemoryAvailable = memory
            }
        };

    /// <summary>
    /// A server from before either feature existed: it sends the availability response it always
    /// sent, and says nothing about history or saved preferences.
    /// </summary>
    private static FakeCoachApiClient OlderServerClient()
        => new()
        {
            DurableHistoryAvailable = true,
            Availability = new CoachAvailabilityResponse
            {
                IsAvailable = true,
                State = CoachAvailabilityState.Available,
                CanEditPlan = true
            }
        };

    private static async Task<string> RenderPanelAsync(CoachMemoryDirectory memory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
        services.AddScoped(_ => memory);

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachMemoryPanel>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    // ---------------------------------------------------------------- history flag

    [Fact]
    public async Task HistoryOffHidesTheShelfWithoutAskingTheRoute()
    {
        var client = ClientWith(history: false, memory: true);
        var directory = new CoachConversationDirectory(client);

        var availability = await directory.EnsureLoadedAsync();

        availability.Should().Be(CoachDurableHistoryAvailability.Unavailable);

        // The point of the flag: no request is sent to learn what the flag already said.
        client.ListConversationCalls.Should().Be(0);
        directory.ErrorKey.Should().BeNull("a feature that is off is not a failure a learner can act on");
    }

    [Fact]
    public async Task HistoryOnLoadsTheShelf()
    {
        var client = ClientWith(history: true, memory: false);
        client.AddConversation("c-1");
        var directory = new CoachConversationDirectory(client);

        var availability = await directory.EnsureLoadedAsync();

        availability.Should().Be(CoachDurableHistoryAvailability.Available);
        directory.Conversations.Should().HaveCount(1);
        client.ListConversationCalls.Should().Be(1);
    }

    [Fact]
    public async Task HistoryOnButTheRouteAnswers404StillEndsUpUnavailable()
    {
        var client = ClientWith(history: true, memory: true);
        client.OnListConversations = (_, _) => null;
        var directory = new CoachConversationDirectory(client);

        var availability = await directory.EnsureLoadedAsync();

        // The race: switched off between the availability read and the list call, or a request
        // that resolved no owner. Whoever holds the data gets the last word.
        availability.Should().Be(CoachDurableHistoryAvailability.Unavailable);
        client.ListConversationCalls.Should().Be(1);
    }

    // ---------------------------------------------------------------- memory flag

    [Fact]
    public async Task MemoryOffHidesTheDirectoryWithoutAskingTheRoutes()
    {
        var client = ClientWith(history: true, memory: false);
        var memory = new CoachMemoryDirectory(client);

        await memory.EnsureLoadedAsync();

        memory.Availability.Should().Be(CoachMemoryAvailability.Unavailable);
        client.ListActiveMemoriesCalls.Should().Be(0);
        client.ListMemoryCandidatesCalls.Should().Be(0);
    }

    [Fact]
    public async Task MemoryOffRendersNoPanelAtAll()
    {
        var client = ClientWith(history: true, memory: false);
        var memory = new CoachMemoryDirectory(client);
        await memory.EnsureLoadedAsync();

        var html = await RenderPanelAsync(memory);

        // Absent, not disabled. A disabled control would tell a learner the feature exists for
        // them, which is exactly what the server declined to say.
        html.Should().NotContain("coach-memory-panel");
    }

    [Fact]
    public async Task MemoryOnLoadsBothLists()
    {
        var client = ClientWith(history: false, memory: true);
        client.ActiveFacts.Add(FakeCoachApiClient.Fact());
        var memory = new CoachMemoryDirectory(client);

        await memory.EnsureLoadedAsync();

        memory.Availability.Should().Be(CoachMemoryAvailability.Available);
        client.ListActiveMemoriesCalls.Should().Be(1);
        client.ListMemoryCandidatesCalls.Should().Be(1);
    }

    [Fact]
    public async Task MemoryOnButTheRouteAnswers404StillEndsUpUnavailable()
    {
        var client = ClientWith(history: true, memory: true);
        client.OnListActiveMemories = () => null;
        var memory = new CoachMemoryDirectory(client);

        await memory.EnsureLoadedAsync();

        memory.Availability.Should().Be(CoachMemoryAvailability.Unavailable);
        client.ListActiveMemoriesCalls.Should().Be(1);
    }

    // ---------------------------------------------------------------- independence

    [Fact]
    public async Task EitherFeatureCanBeOnWithoutTheOther()
    {
        var historyOnly = ClientWith(history: true, memory: false);
        var memoryOnly = ClientWith(history: false, memory: true);

        var shelfWithHistory = new CoachConversationDirectory(historyOnly);
        var memoryWithHistory = new CoachMemoryDirectory(historyOnly);
        var shelfWithMemory = new CoachConversationDirectory(memoryOnly);
        var memoryWithMemory = new CoachMemoryDirectory(memoryOnly);

        await shelfWithHistory.EnsureLoadedAsync();
        await memoryWithHistory.EnsureLoadedAsync();
        await shelfWithMemory.EnsureLoadedAsync();
        await memoryWithMemory.EnsureLoadedAsync();

        shelfWithHistory.IsDurableHistoryAvailable.Should().BeTrue();
        memoryWithHistory.Availability.Should().Be(CoachMemoryAvailability.Unavailable);

        shelfWithMemory.IsDurableHistoryAvailable.Should().BeFalse();
        memoryWithMemory.Availability.Should().Be(CoachMemoryAvailability.Available);
    }

    [Fact]
    public async Task BothOffHidesBothSurfacesAndSendsNoRequests()
    {
        var client = ClientWith(history: false, memory: false);
        var directory = new CoachConversationDirectory(client);
        var memory = new CoachMemoryDirectory(client);

        await directory.EnsureLoadedAsync();
        await memory.EnsureLoadedAsync();

        directory.IsDurableHistoryAvailable.Should().BeFalse();
        memory.Availability.Should().Be(CoachMemoryAvailability.Unavailable);
        client.ListConversationCalls.Should().Be(0);
        client.ListActiveMemoriesCalls.Should().Be(0);
    }

    // ---------------------------------------------------------------- older servers

    [Fact]
    public async Task AnOlderServerThatSendsNoFlagsHidesBothFeatures()
    {
        var client = OlderServerClient();
        var directory = new CoachConversationDirectory(client);
        var memory = new CoachMemoryDirectory(client);

        await directory.EnsureLoadedAsync();
        await memory.EnsureLoadedAsync();

        // Absent fields deserialize to false, and that is the right answer rather than a gap to
        // work around: a server that does not mention the feature does not have it.
        directory.IsDurableHistoryAvailable.Should().BeFalse();
        memory.Availability.Should().Be(CoachMemoryAvailability.Unavailable);
        client.ListConversationCalls.Should().Be(0);
        client.ListActiveMemoriesCalls.Should().Be(0);
    }

    // ---------------------------------------------------------------- unreadable availability

    [Fact]
    public async Task AvailabilityThatCannotBeReadLeavesBothFeaturesHidden()
    {
        var client = ClientWith(history: true, memory: true);
        client.OnGetAvailability = () => throw new HttpRequestException("api down");
        var directory = new CoachConversationDirectory(client);
        var memory = new CoachMemoryDirectory(client);

        await directory.EnsureLoadedAsync();
        await memory.EnsureLoadedAsync();

        // A gate that can crash the page it guards is worse than a gate that closes.
        directory.IsDurableHistoryAvailable.Should().BeFalse();
        memory.Availability.Should().Be(CoachMemoryAvailability.Unavailable);
        client.ListConversationCalls.Should().Be(0);
        client.ListActiveMemoriesCalls.Should().Be(0);
    }

    // ---------------------------------------------------------------- shared reading

    [Fact]
    public async Task TheWorkspaceAvailabilityReadServesBothDirectories()
    {
        var client = ClientWith(history: true, memory: true);
        client.AddConversation("c-1");
        var flags = new CoachFeatureFlags(client);
        var directory = new CoachConversationDirectory(client, flags);
        var memory = new CoachMemoryDirectory(client, flags);
        var state = new CoachWorkspaceState(client, directory, flags);

        await state.RefreshAvailabilityAsync();
        await directory.EnsureLoadedAsync();
        await memory.EnsureLoadedAsync();

        directory.IsDurableHistoryAvailable.Should().BeTrue();
        memory.Availability.Should().Be(CoachMemoryAvailability.Available);

        // One availability read for the circuit. Three surfaces asking the same question three
        // times could also answer it three different ways as a deploy rolled through.
        client.AvailabilityCalls.Should().Be(1);
    }

    [Fact]
    public async Task FlagsReadOnceAreNotReReadOnEveryLoad()
    {
        var client = ClientWith(history: false, memory: false);
        var flags = new CoachFeatureFlags(client);
        var directory = new CoachConversationDirectory(client, flags);
        var memory = new CoachMemoryDirectory(client, flags);

        await directory.EnsureLoadedAsync();
        await memory.EnsureLoadedAsync();

        client.AvailabilityCalls.Should().Be(1);
    }
}
