using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;
using SentenceStudio.WebUI.Shared.Sam;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// A conversation that turns out to be unavailable takes its content off the screen with it.
/// </summary>
/// <remarks>
/// <para>
/// The route answers "gone", "never existed" and "not yours" identically, on purpose, and the
/// client renders one notice for all three. What it did not do was clear what was already on
/// screen, so the notice appeared above a full transcript of the thread it was saying was
/// unavailable — including its proposal cards and their approval controls. On a shared device
/// that is the previous learner's conversation still legible under a sentence claiming it is not
/// there.
/// </para>
/// <para>
/// A refusal is the same claim as a not-found — this thread is not readable by whoever is asking —
/// so 401 and 403 clear exactly as hard as a 404.
/// </para>
/// </remarks>
public class CoachTranscriptUnavailableTests
{
    private const string Conversation = "conversation-1";
    private const string Operation = "op-1";
    private const string LearnerText = "How do I ask for a refund?";

    private static FakeCoachApiClient NewClient()
    {
        var client = new FakeCoachApiClient
        {
            DurableHistoryAvailable = true,
            Availability = new CoachAvailabilityResponse
            {
                IsAvailable = true,
                State = CoachAvailabilityState.Available,
                CanEditPlan = true,
                IsDurableHistoryAvailable = true,
                IsSamWriteAvailable = true
            }
        };

        client.AddConversation(Conversation, title: "Refund letter for my landlord");
        var write = client.AddWrite(Conversation, Operation);
        client.Seed(Conversation, CoachMessageRole.Learner, LearnerText);
        client.Seed(Conversation, CoachMessageRole.Coach, "Try 환불하고 싶은데요.", writeOperation: write);

        return client;
    }

    private static async Task<CoachWorkspaceState> LoadedAsync(FakeCoachApiClient client)
    {
        var flags = new CoachFeatureFlags(client);
        await flags.EnsureLoadedAsync();

        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client, flags), flags);
        await state.RefreshAvailabilityAsync();
        await state.OpenConversationAsync(CoachPresentation.Overlay, Conversation);

        state.Timeline.Should().NotBeEmpty("the harness must load the thread before removing it");
        return state;
    }

    // ================================================================ not found

    [Fact]
    public async Task A_conversation_that_answers_not_found_clears_the_transcript_under_the_notice()
    {
        var client = NewClient();
        var state = await LoadedAsync(client);

        client.OnGetConversationMessages = (_, _, _) => null;
        await state.LoadTranscriptAsync();

        state.ConversationNoticeKey.Should().Be("Coach_ConversationGone");
        state.Timeline.Should().BeEmpty();
        state.Messages.Should().BeEmpty();
        state.ConversationId.Should().BeNull();
        state.Conversation.Should().BeNull();
    }

    [Fact]
    public async Task The_proposal_cards_go_with_it()
    {
        var client = NewClient();
        var state = await LoadedAsync(client);
        state.ActiveWriteOperation.Should().NotBeNull();

        client.OnGetConversationMessages = (_, _, _) => null;
        await state.LoadTranscriptAsync();

        state.ActiveWriteOperation.Should().BeNull(
            "an approval control for a thread that is not there would be a control that cannot work");
        state.IsWriteSurfaceEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task An_open_confirmation_is_dropped()
    {
        var client = NewClient();
        client.AddWrite(Conversation, "op-hard", requiresConfirmation: true);

        var state = await LoadedAsync(client);
        await state.BeginWriteConfirmationAsync(Operation);

        client.OnGetConversationMessages = (_, _, _) => null;
        await state.LoadTranscriptAsync();

        state.ConfirmingWriteOperationId.Should().BeNull();
        state.ConfirmationExpiresAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task The_shelf_forgets_the_row_and_its_title()
    {
        var client = NewClient();
        var flags = new CoachFeatureFlags(client);
        await flags.EnsureLoadedAsync();

        var directory = new CoachConversationDirectory(client, flags);
        var state = new CoachWorkspaceState(client, directory, flags);
        await state.RefreshAvailabilityAsync();
        await directory.EnsureLoadedAsync();
        await state.OpenConversationAsync(CoachPresentation.Overlay, Conversation);

        directory.Conversations.Should().ContainSingle();

        client.OnGetConversationMessages = (_, _, _) => null;
        await state.LoadTranscriptAsync();

        directory.Conversations.Should().BeEmpty("a title names a conversation as surely as its text does");
        directory.SelectedConversationId.Should().BeNull();
    }

    /// <summary>The paging affordance goes too, so nothing offers to fetch more of a gone thread.</summary>
    [Fact]
    public async Task Nothing_still_offers_to_load_more_of_it()
    {
        var client = NewClient();
        var state = await LoadedAsync(client);

        client.OnGetConversationMessages = (_, _, _) => null;
        await state.LoadTranscriptAsync();

        state.HasEarlierMessages.Should().BeFalse();
        state.IsAtHistoryBoundary.Should().BeFalse();
        state.UnreadableMessageCount.Should().Be(0);
        state.HistoryStartsAtUtc.Should().BeNull();
    }

    // ================================================================ refused

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    public async Task A_refusal_clears_as_hard_as_a_not_found(System.Net.HttpStatusCode status)
    {
        var client = NewClient();
        var state = await LoadedAsync(client);

        client.OnGetConversationMessages = (_, _, _) =>
            throw new CoachApiException(status, CoachProblemTypes.Unavailable, null, null);

        await state.LoadTranscriptAsync();

        state.Timeline.Should().BeEmpty();
        state.Messages.Should().BeEmpty();
        state.ConversationId.Should().BeNull();
        state.ActiveWriteOperation.Should().BeNull();
        state.ConversationNoticeKey.Should().Be("Coach_ConversationGone");
    }

    /// <summary>
    /// An outage is not a claim about ownership, so it leaves the thread alone. Clearing here
    /// would throw away a readable conversation because the network blinked.
    /// </summary>
    [Fact]
    public async Task A_network_failure_leaves_the_thread_where_it_is()
    {
        var client = NewClient();
        var state = await LoadedAsync(client);

        client.OnGetConversationMessages = (_, _, _) => throw new HttpRequestException("offline");

        await state.LoadTranscriptAsync();

        state.Timeline.Should().NotBeEmpty();
        state.ConversationId.Should().Be(Conversation);
        state.State.Should().Be(CoachUiState.Offline);
    }

    // ================================================================ rendered

    [Fact]
    public async Task No_markup_survives_the_notice()
    {
        var client = NewClient();
        var state = await LoadedAsync(client);

        (await RenderAsync(state)).Should().Contain(LearnerText, "the harness must render it first");

        client.OnGetConversationMessages = (_, _, _) => null;
        await state.LoadTranscriptAsync();

        var html = await RenderAsync(state);
        html.Should().NotContain(LearnerText);
        html.Should().NotContain(SamElementIds.WriteCard(Operation));
        html.Should().NotContain(SamElementIds.WriteAccept(Operation));
    }

    private static async Task<string> RenderAsync(CoachWorkspaceState state)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
        services.AddScoped(_ => state);

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachChatPane>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }
}
