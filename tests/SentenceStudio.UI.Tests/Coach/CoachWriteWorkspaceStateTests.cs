using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The learner-side rules for a proposed change: what may be acted on, what the card says
/// afterwards, and what happens to the one-use confirmation.
/// </summary>
/// <remarks>
/// These are written against the real <see cref="CoachWorkspaceState"/> and a fake transport, so
/// the transition code under test is the code that ships. The assertions are deliberately about
/// what the learner ends up looking at rather than about which method was called: "the card says
/// applied" is the property that matters, and it must be reachable only from a server state that
/// actually says so.
/// </remarks>
public class CoachWriteWorkspaceStateTests
{
    private const string Conversation = "conv-1";

    private static FakeCoachApiClient NewClient(bool writeAvailable = true)
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
                IsMemoryAvailable = true,
                IsSamOverlayAvailable = true,
                IsSamWriteAvailable = writeAvailable
            }
        };

        client.AddConversation(Conversation);
        return client;
    }

    /// <summary>Opens a conversation whose newest exchange carries the given proposal.</summary>
    private static async Task<CoachWorkspaceState> OpenWithWriteAsync(
        FakeCoachApiClient client,
        CoachWriteOperationDto write)
    {
        client.Seed(Conversation, CoachMessageRole.Learner, "add a word for me");
        client.Seed(Conversation, CoachMessageRole.Coach, "I can do that.", writeOperation: write);

        var flags = new CoachFeatureFlags(client);
        await flags.EnsureLoadedAsync();

        var directory = new CoachConversationDirectory(client, flags);
        var state = new CoachWorkspaceState(client, directory, flags);

        await state.RefreshAvailabilityAsync();
        await state.OpenConversationAsync(CoachPresentation.Overlay, Conversation);

        return state;
    }

    private static CoachWriteOperationDto? CardFor(CoachWorkspaceState state, string operationId) =>
        state.Timeline
            .Select(entry => entry.WriteOperation)
            .FirstOrDefault(write => write?.OperationId == operationId);

    // ---------------------------------------------------------------- placement and reload

    [Fact]
    public async Task A_proposal_renders_inside_the_exchange_that_produced_it()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");

        var state = await OpenWithWriteAsync(client, write);

        var carrying = state.Timeline.Where(e => e.WriteOperation is not null).ToList();

        carrying.Should().ContainSingle("one exchange proposed one change");
        carrying[0].Kind.Should().Be(CoachTimelineKind.CoachMessage,
            "the card belongs after what Sam said, not beside the learner's question");
        carrying[0].Should().NotBeSameAs(state.Timeline[0],
            "a card stacked at the top of the thread has lost the context that explains it");
    }

    /// <summary>
    /// Reload is the whole point of putting the state on the history row rather than in the
    /// client: a second circuit, a refresh, or another device rebuilds the same card.
    /// </summary>
    [Fact]
    public async Task A_reload_rebuilds_the_same_card_from_the_server()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");
        await OpenWithWriteAsync(client, write);

        var flags = new CoachFeatureFlags(client);
        await flags.EnsureLoadedAsync();
        var reopened = new CoachWorkspaceState(client, new CoachConversationDirectory(client, flags), flags);
        await reopened.RefreshAvailabilityAsync();
        await reopened.OpenConversationAsync(CoachPresentation.Overlay, Conversation);

        reopened.ActiveWriteOperation?.OperationId.Should().Be("op-1");
        CardFor(reopened, "op-1")!.Status.Should().Be(CoachWriteStatus.Proposed);
    }

    [Fact]
    public async Task Only_the_newest_proposal_can_be_acted_on()
    {
        var client = NewClient();
        var older = client.AddWrite(Conversation, "op-old");
        var newer = client.AddWrite(Conversation, "op-new");

        client.Seed(Conversation, CoachMessageRole.Coach, "first offer", writeOperation: older);
        client.Seed(Conversation, CoachMessageRole.Coach, "second offer", writeOperation: newer);

        var flags = new CoachFeatureFlags(client);
        await flags.EnsureLoadedAsync();
        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client, flags), flags);
        await state.RefreshAvailabilityAsync();
        await state.OpenConversationAsync(CoachPresentation.Overlay, Conversation);

        state.ActiveWriteOperation?.OperationId.Should().Be("op-new");
        state.IsActionable(older).Should().BeFalse("two live Accept buttons invite approving the wrong one");
        state.IsActionable(newer).Should().BeTrue();
    }

    // ---------------------------------------------------------------- soft writes

    [Fact]
    public async Task Accepting_shows_applied_only_after_the_server_says_so()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");
        var state = await OpenWithWriteAsync(client, write);

        CardFor(state, "op-1")!.Status.Should().Be(CoachWriteStatus.Proposed);

        await state.AcceptWriteAsync("op-1");

        var settled = CardFor(state, "op-1")!;
        settled.Status.Should().Be(CoachWriteStatus.Executed);
        settled.Receipt.Should().NotBeNull("applied is claimed from the receipt, never from a status code");
        settled.Receipt!.Summary.Should().StartWith("Applied:");
        state.ActiveWriteOperation.Should().BeNull("an executed change is no longer awaiting an answer");
    }

    [Fact]
    public async Task Declining_leaves_the_card_declined_and_writes_nothing()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");
        var state = await OpenWithWriteAsync(client, write);

        await state.RejectWriteAsync("op-1");

        CardFor(state, "op-1")!.Status.Should().Be(CoachWriteStatus.Rejected);
        CardFor(state, "op-1")!.Receipt.Should().BeNull("nothing ran, so there is nothing to receipt");
        client.WriteCalls.Should().Contain("reject op-1");
    }

    [Fact]
    public async Task A_second_press_while_the_first_is_in_flight_sends_one_request()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");
        var state = await OpenWithWriteAsync(client, write);

        var gate = new TaskCompletionSource();
        client.WriteGate = gate;

        var first = state.AcceptWriteAsync("op-1");
        var second = state.AcceptWriteAsync("op-1");

        gate.SetResult();
        await Task.WhenAll(first, second);

        client.WriteCalls.Count(call => call == "accept op-1").Should().Be(1,
            "a double press must not produce a second approval request");
    }

    // ---------------------------------------------------------------- protected writes

    [Fact]
    public async Task A_protected_change_is_not_accepted_through_the_ordinary_channel()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1", requiresConfirmation: true, isReversible: false);
        var state = await OpenWithWriteAsync(client, write);

        await state.AcceptWriteAsync("op-1");

        client.WriteCalls.Should().BeEmpty("a protected change has no ordinary Accept to press");
        CardFor(state, "op-1")!.Status.Should().Be(CoachWriteStatus.Proposed);
    }

    [Fact]
    public async Task Confirming_a_protected_change_sends_the_server_issued_value_once()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1", requiresConfirmation: true, isReversible: false);
        var state = await OpenWithWriteAsync(client, write);

        await state.BeginWriteConfirmationAsync("op-1");
        state.ConfirmingWriteOperationId.Should().Be("op-1");

        await state.ConfirmWriteAsync();

        client.SentConfirmations.Should().ContainSingle().Which.Should().Be("one-use-op-1");
        CardFor(state, "op-1")!.Status.Should().Be(CoachWriteStatus.Executed);
        state.ConfirmingWriteOperationId.Should().BeNull("the value is spent and the step is closed");
        state.ConfirmationExpiresAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Confirming_twice_sends_the_value_once()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1", requiresConfirmation: true, isReversible: false);
        var state = await OpenWithWriteAsync(client, write);

        await state.BeginWriteConfirmationAsync("op-1");
        await state.ConfirmWriteAsync();
        await state.ConfirmWriteAsync();

        client.SentConfirmations.Should().ContainSingle(
            "a one-use value that is still in hand after it was spent is a value waiting to be replayed");
    }

    [Fact]
    public async Task Backing_out_of_a_confirmation_drops_the_value()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1", requiresConfirmation: true, isReversible: false);
        var state = await OpenWithWriteAsync(client, write);

        await state.BeginWriteConfirmationAsync("op-1");
        state.CancelWriteConfirmation();

        state.ConfirmingWriteOperationId.Should().BeNull();
        state.ConfirmationExpiresAtUtc.Should().BeNull();

        await state.ConfirmWriteAsync();
        client.SentConfirmations.Should().BeEmpty("there is nothing left to confirm with");
    }

    /// <summary>
    /// A confirmation is not resumable across a reload, and the surface must not pretend it is.
    /// </summary>
    [Fact]
    public async Task A_protected_change_reloaded_without_a_confirmation_offers_no_confirm()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1", requiresConfirmation: true, isReversible: false);
        await OpenWithWriteAsync(client, write);

        var flags = new CoachFeatureFlags(client);
        await flags.EnsureLoadedAsync();
        var reopened = new CoachWorkspaceState(client, new CoachConversationDirectory(client, flags), flags);
        await reopened.RefreshAvailabilityAsync();
        await reopened.OpenConversationAsync(CoachPresentation.Overlay, Conversation);

        reopened.ConfirmingWriteOperationId.Should().BeNull();

        await reopened.ConfirmWriteAsync();
        client.SentConfirmations.Should().BeEmpty("a Confirm with nothing behind it would be a broken button");
    }

    [Fact]
    public async Task An_expired_confirmation_is_refused_before_it_is_sent()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1", requiresConfirmation: true, isReversible: false);
        client.OnRequestConfirmation = operationId => new CoachWriteConfirmation
        {
            OperationId = operationId,
            Value = "stale",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1)
        };

        var state = await OpenWithWriteAsync(client, write);
        await state.BeginWriteConfirmationAsync("op-1");

        state.ConfirmingWriteOperationId.Should().BeNull();
        state.WriteErrorKey.Should().Be("Coach_WriteUnavailable");
        client.SentConfirmations.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- undo

    [Fact]
    public async Task Undo_reverses_and_the_card_says_undone()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");
        var state = await OpenWithWriteAsync(client, write);

        await state.AcceptWriteAsync("op-1");
        await state.UndoWriteAsync("op-1");

        CardFor(state, "op-1")!.Status.Should().Be(CoachWriteStatus.Undone);
        client.WriteCalls.Should().Contain("undo op-1");
    }

    [Fact]
    public async Task An_irreversible_change_never_offers_an_undo()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1", requiresConfirmation: true, isReversible: false);
        var state = await OpenWithWriteAsync(client, write);

        await state.BeginWriteConfirmationAsync("op-1");
        await state.ConfirmWriteAsync();

        var receipt = CardFor(state, "op-1")!.Receipt!;
        receipt.CanUndo.Should().BeFalse(
            "an undo button on something the server cannot put back is worse than none");
        receipt.UndoExpiresAtUtc.Should().BeNull();
    }

    // ---------------------------------------------------------------- refusals

    [Theory]
    [InlineData(System.Net.HttpStatusCode.NotFound, "Coach_WriteUnavailable")]
    [InlineData(System.Net.HttpStatusCode.UnprocessableEntity, "Coach_WriteRefused")]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, "Coach_WriteLimited")]
    [InlineData(System.Net.HttpStatusCode.Conflict, "Coach_WriteRefused")]
    public async Task A_refusal_becomes_a_truthful_localized_message(
        System.Net.HttpStatusCode status,
        string expectedKey)
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");
        var state = await OpenWithWriteAsync(client, write);

        client.OnWriteRefusal = (verb, _) => verb == "accept"
            ? new CoachApiException(status, CoachProblemTypes.InvalidTurnInput, "refused", "detail")
            : null;

        await state.AcceptWriteAsync("op-1");

        state.WriteErrorKey.Should().Be(expectedKey);
        state.WriteErrorOperationId.Should().Be("op-1");
    }

    [Fact]
    public async Task A_network_failure_says_the_request_did_not_arrive()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");
        var state = await OpenWithWriteAsync(client, write);

        client.OnWriteRefusal = (_, _) => throw new HttpRequestException("offline");

        await state.AcceptWriteAsync("op-1");

        state.WriteErrorKey.Should().Be("Coach_WriteNetworkFailed");
        CardFor(state, "op-1")!.Status.Should().Be(CoachWriteStatus.Proposed,
            "a request that never arrived changed nothing, and the card must not imply it did");
    }

    /// <summary>
    /// A refusal is followed by a re-read, so a learner refused because the change had already run
    /// is not left staring at a card that still offers to run it.
    /// </summary>
    [Fact]
    public async Task A_refusal_refreshes_the_card_from_the_server()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");
        var state = await OpenWithWriteAsync(client, write);

        client.OnWriteRefusal = (verb, _) => verb == "accept"
            ? new CoachApiException(
                System.Net.HttpStatusCode.UnprocessableEntity, CoachProblemTypes.InvalidTurnInput, null, null)
            : null;

        await state.AcceptWriteAsync("op-1");

        client.WriteCalls.Should().Contain("get op-1");
    }

    [Fact]
    public async Task A_change_that_is_gone_says_so_and_stops_offering_controls()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");
        var state = await OpenWithWriteAsync(client, write);

        client.Writes.Remove("op-1");

        await state.AcceptWriteAsync("op-1");

        state.WriteErrorKey.Should().Be("Coach_WriteUnavailable");
        state.ActiveWriteOperation.Should().BeNull("a change the server cannot find is not approvable");
        state.IsWriteUnavailable("op-1").Should().BeTrue();

        // The card stays so the explanation has somewhere to live. Deleting it would take the
        // sentence off the screen along with the thing it explains.
        CardFor(state, "op-1").Should().NotBeNull();
    }

    // ---------------------------------------------------------------- gating and malformed state

    [Fact]
    public async Task The_write_surface_is_hidden_when_the_server_does_not_offer_it()
    {
        var client = NewClient(writeAvailable: false);
        var write = client.AddWrite(Conversation, "op-1");
        var state = await OpenWithWriteAsync(client, write);

        state.IsWriteSurfaceEnabled.Should().BeFalse();

        await state.AcceptWriteAsync("op-1");
        client.WriteCalls.Should().BeEmpty("a hidden surface must also refuse to act");
    }

    /// <summary>
    /// A card whose state the client cannot interpret must offer nothing at all.
    /// </summary>
    /// <remarks>
    /// The dangerous version of this bug is silent: an approval mode that disagrees with the risk
    /// class renders a plausible card that sends the wrong request. Refusing to treat it as
    /// actionable is what makes it visible instead.
    /// </remarks>
    [Theory]
    [InlineData(CoachWriteStatus.Unknown, CoachWriteRiskClass.WriteSoft, "accept", false)]
    [InlineData(CoachWriteStatus.Proposed, CoachWriteRiskClass.Unknown, "accept", false)]
    [InlineData(CoachWriteStatus.Proposed, CoachWriteRiskClass.WriteHard, "accept", true)]
    [InlineData(CoachWriteStatus.Proposed, CoachWriteRiskClass.WriteSoft, "confirm", false)]
    [InlineData(CoachWriteStatus.Proposed, CoachWriteRiskClass.WriteSoft, "accept", false)]
    public void Malformed_state_is_never_well_formed(
        CoachWriteStatus status,
        CoachWriteRiskClass riskClass,
        string approvalMode,
        bool requiresConfirmation)
    {
        var malformed = new CoachWriteOperationDto
        {
            OperationId = "op-1",
            ConversationId = Conversation,
            ChangeKind = CoachWriteChangeKind.VocabularyAdd,
            RiskClass = riskClass,
            Status = status,
            ApprovalMode = approvalMode,
            Summary = "something",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
            RequiresConfirmation = requiresConfirmation
        };

        var wellFormed = CoachWorkspaceState.IsWellFormed(malformed);

        // The last row is the only coherent one in the set.
        var coherent = status == CoachWriteStatus.Proposed
                       && riskClass == CoachWriteRiskClass.WriteSoft
                       && approvalMode == "accept"
                       && !requiresConfirmation;

        wellFormed.Should().Be(coherent);
    }

    [Fact]
    public async Task A_malformed_proposal_is_not_actionable()
    {
        var client = NewClient();
        var write = client.AddWrite(Conversation, "op-1");

        // A server that says "protected" in one field and "ordinary" in the other.
        client.Writes["op-1"] = new CoachWriteOperationDto
        {
            OperationId = "op-1",
            ConversationId = Conversation,
            ChangeKind = write.ChangeKind,
            RiskClass = CoachWriteRiskClass.WriteHard,
            Status = CoachWriteStatus.Proposed,
            ApprovalMode = "accept",
            Summary = write.Summary,
            ExpiresAtUtc = write.ExpiresAtUtc,
            RequiresConfirmation = false
        };

        var state = await OpenWithWriteAsync(client, client.Writes["op-1"]);

        state.ActiveWriteOperation.Should().BeNull();

        await state.AcceptWriteAsync("op-1");
        client.WriteCalls.Should().BeEmpty();
    }
}
