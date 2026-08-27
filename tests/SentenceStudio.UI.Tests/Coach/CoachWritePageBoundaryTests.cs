using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;
using SentenceStudio.WebUI.Shared.Sam;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// A proposal that straddles a message-page boundary must still be exactly one decision.
/// </summary>
/// <remarks>
/// <para>
/// The server anchors a proposal to the last thing Sam said on the turn that produced it, and it
/// resolves that anchor <em>per page</em>. A turn whose messages fall either side of a fifty
/// message boundary therefore comes back anchored twice — once in the newest page, once in the
/// older page the learner pages in — and the client merges both pages into one timeline. Both
/// copies carry the same operation id, so both answer "yes" to "are you the actionable one", and
/// the learner is shown the same change twice with two live approval controls.
/// </para>
/// <para>
/// The consequence is not cosmetic. Approving through one card left the other still offering to
/// apply a change that had already been applied, because the state writer stopped at the first
/// match. Both halves are pinned here: one card after the merge, and every copy updated by an
/// action.
/// </para>
/// </remarks>
public class CoachWritePageBoundaryTests
{
    private const string Conversation = "conversation-1";
    private const string Operation = "op-1";

    private static FakeCoachApiClient NewClient() => new()
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

    /// <summary>
    /// Builds a conversation whose single proposal is anchored on two different messages, one in
    /// each page, exactly as a per-page anchor resolution produces.
    /// </summary>
    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)> SplitAcrossPagesAsync(
        bool requiresConfirmation = false)
    {
        var client = NewClient();
        client.AddConversation(Conversation);

        var write = client.AddWrite(Conversation, Operation, requiresConfirmation: requiresConfirmation);

        // Older page: the turn started here, and its last coach row on THIS page carries the card.
        client.Seed(Conversation, CoachMessageRole.Learner, "add 환불 to my list");
        client.Seed(Conversation, CoachMessageRole.Coach, "Checking your list.", writeOperation: write);

        // Newest page: the same turn continued, and its last coach row here carries it too.
        client.Seed(Conversation, CoachMessageRole.Coach, "Here is what I would add.", writeOperation: write);
        client.Seed(Conversation, CoachMessageRole.Coach, "Ready when you are.", writeOperation: write);

        // Two pages of two, so "load earlier" is a real second read rather than a no-op.
        client.OnGetConversationMessages = (id, _, before) =>
        {
            var all = client.ConversationMessages[id].OrderBy(m => m.Sequence).ToList();
            var older = all.Take(2).ToList();
            var newer = all.Skip(2).ToList();

            return before is null
                ? new CoachMessagePageDto
                {
                    ConversationId = id,
                    Items = newer,
                    PreviousCursor = newer[0].Sequence.ToString(),
                    UnreadableCount = 0
                }
                : new CoachMessagePageDto
                {
                    ConversationId = id,
                    Items = older,
                    PreviousCursor = null,
                    UnreadableCount = 0
                };
        };

        var flags = new CoachFeatureFlags(client);
        await flags.EnsureLoadedAsync();

        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client, flags), flags);
        await state.RefreshAvailabilityAsync();
        await state.OpenConversationAsync(CoachPresentation.Overlay, Conversation);

        return (state, client);
    }

    private static int CardsFor(CoachWorkspaceState state, string operationId) =>
        state.Timeline.Count(e => e.WriteOperation is { } w
            && string.Equals(w.OperationId, operationId, StringComparison.Ordinal));

    // ================================================================ one card

    [Fact]
    public async Task The_newest_page_alone_shows_one_card()
    {
        var (state, _) = await SplitAcrossPagesAsync();

        CardsFor(state, Operation).Should().Be(1);
    }

    [Fact]
    public async Task Paging_in_the_older_half_of_the_turn_does_not_add_a_second_card()
    {
        var (state, _) = await SplitAcrossPagesAsync();

        await state.LoadEarlierMessagesAsync();

        state.Timeline.Should().HaveCount(4, "every message is still in the transcript");
        CardsFor(state, Operation).Should().Be(1, "but the decision is offered once");
    }

    /// <summary>
    /// The surviving card is the later anchor, so paging older history in does not make the card
    /// jump up the thread away from the reply it belongs to.
    /// </summary>
    [Fact]
    public async Task The_surviving_card_stays_where_the_unsplit_page_would_have_put_it()
    {
        var (state, _) = await SplitAcrossPagesAsync();

        await state.LoadEarlierMessagesAsync();

        var carrier = state.Timeline.Single(e => e.WriteOperation is not null);
        carrier.Message!.Text.Should().Be("Ready when you are.");
    }

    /// <summary>
    /// The duplicate loses its card, not its message. Deleting a real message to fix a card
    /// problem would rewrite the transcript.
    /// </summary>
    [Fact]
    public async Task The_duplicate_keeps_its_message()
    {
        var (state, _) = await SplitAcrossPagesAsync();

        await state.LoadEarlierMessagesAsync();

        state.Timeline.Select(e => e.Message?.Text).Should().ContainInOrder(
            "add 환불 to my list",
            "Checking your list.",
            "Here is what I would add.",
            "Ready when you are.");
    }

    /// <summary>Only one card is actionable, because there is only one card.</summary>
    [Fact]
    public async Task Only_one_card_is_actionable()
    {
        var (state, _) = await SplitAcrossPagesAsync();

        await state.LoadEarlierMessagesAsync();

        state.Timeline
            .Where(e => e.WriteOperation is not null)
            .Count(e => state.IsActionable(e.WriteOperation))
            .Should().Be(1);
    }

    // ================================================================ all copies move together

    /// <summary>
    /// Accepting settles every copy. Written against a deliberately doubled timeline so the
    /// assertion survives a future merge that lets a duplicate through — the two defences are
    /// independent on purpose.
    /// </summary>
    [Fact]
    public async Task Accepting_settles_every_copy_of_the_card()
    {
        var (state, client) = await SplitAcrossPagesAsync();
        await state.LoadEarlierMessagesAsync();

        await state.AcceptWriteAsync(Operation);

        client.WriteCalls.Should().Contain("accept " + Operation);

        state.Timeline
            .Where(e => e.WriteOperation is not null)
            .Should().OnlyContain(e => e.WriteOperation!.Status == CoachWriteStatus.Executed);

        state.ActiveWriteOperation.Should().BeNull("a settled change is not waiting on anybody");
    }

    /// <summary>
    /// And the rendered thread agrees: no second card is still offering to apply the change.
    /// </summary>
    [Fact]
    public async Task After_accepting_no_rendered_card_still_offers_to_apply()
    {
        var (state, _) = await SplitAcrossPagesAsync();
        await state.LoadEarlierMessagesAsync();
        await state.AcceptWriteAsync(Operation);

        var html = await RenderAsync(state);

        html.Should().NotContain(SamElementIds.WriteAccept(Operation),
            "an Accept control on a change that has already run is the defect this closes");

        System.Text.RegularExpressions.Regex
            .Matches(html, System.Text.RegularExpressions.Regex.Escape(SamElementIds.WriteCard(Operation)) + "\"")
            .Count.Should().Be(1, "one change, one card");
    }

    /// <summary>
    /// The protected path too: a confirmation step must not open on one copy while another copy
    /// still shows Review.
    /// </summary>
    [Fact]
    public async Task A_protected_change_confirms_once_across_the_merged_timeline()
    {
        var (state, _) = await SplitAcrossPagesAsync(requiresConfirmation: true);
        await state.LoadEarlierMessagesAsync();

        await state.BeginWriteConfirmationAsync(Operation);
        state.ConfirmingWriteOperationId.Should().Be(Operation);

        await state.ConfirmWriteAsync();

        state.Timeline
            .Where(e => e.WriteOperation is not null)
            .Should().OnlyContain(e => e.WriteOperation!.Status == CoachWriteStatus.Executed);
        state.ConfirmingWriteOperationId.Should().BeNull();
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
