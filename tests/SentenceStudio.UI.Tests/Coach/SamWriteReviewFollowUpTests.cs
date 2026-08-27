using System.Text.Json;
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
/// The follow-ups from the write-surface review: what happens to the one-use confirmation when
/// the workspace goes away, what a card says when the server stops answering for it, and what an
/// older client does with a value a newer server sends.
/// </summary>
/// <remarks>
/// Each of these is a small rule with a large failure mode. A confirmation that outlives its
/// prompt is a credential nobody is watching; a card that says "Expired" because a read failed is
/// the client inventing a verdict; a payload that throws on an unrecognised name takes the whole
/// answer down with the card.
/// </remarks>
public class SamWriteReviewFollowUpTests
{
    private const string Conversation = "conv-1";

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
                IsMemoryAvailable = true,
                IsSamOverlayAvailable = true,
                IsSamWriteAvailable = true
            }
        };

        client.AddConversation(Conversation);
        return client;
    }

    private static async Task<CoachWorkspaceState> OpenConfirmingAsync(FakeCoachApiClient client)
    {
        var write = client.AddWrite(Conversation, "op-1", requiresConfirmation: true, isReversible: false);
        client.Seed(Conversation, CoachMessageRole.Coach, "I can do that.", writeOperation: write);

        var flags = new CoachFeatureFlags(client);
        await flags.EnsureLoadedAsync();

        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client, flags), flags);
        await state.RefreshAvailabilityAsync();
        await state.OpenConversationAsync(CoachPresentation.Overlay, Conversation);
        await state.BeginWriteConfirmationAsync("op-1");

        return state;
    }

    // ================================================================ confirmation lifetime

    /// <summary>
    /// Closing the workspace drops the confirmation in hand.
    /// </summary>
    /// <remarks>
    /// Closing is a deliberate exit from the prompt that minted it. The session is preserved for
    /// resume and the card will be rebuilt from the server, but the one-use value is not part of
    /// what gets resumed: the learner will be asked again, which is the point of asking at all.
    /// </remarks>
    [Fact]
    public async Task Closing_the_workspace_drops_the_confirmation()
    {
        var client = NewClient();
        var state = await OpenConfirmingAsync(client);

        state.ConfirmingWriteOperationId.Should().Be("op-1", "the step is open before the close");
        state.ConfirmationExpiresAtUtc.Should().NotBeNull("a confirmation is genuinely in hand");

        state.Close();

        state.ConfirmingWriteOperationId.Should().BeNull();
        state.ConfirmationExpiresAtUtc.Should().BeNull(
            "the expiry is projected from the confirmation, so a null expiry is the value being gone");
    }

    /// <summary>
    /// Disposing the workspace drops the confirmation.
    /// </summary>
    /// <remarks>
    /// The one release that is not a decision the learner made: a closed tab, a dropped circuit, a
    /// navigation away. The scoped lifetime is ending either way; the question is only whether the
    /// secret is released deliberately or left for the collector.
    /// </remarks>
    [Fact]
    public async Task Disposing_the_workspace_drops_the_confirmation()
    {
        var client = NewClient();
        var state = await OpenConfirmingAsync(client);

        state.ConfirmationExpiresAtUtc.Should().NotBeNull();

        state.Dispose();

        state.ConfirmingWriteOperationId.Should().BeNull();
        state.ConfirmationExpiresAtUtc.Should().BeNull();
    }

    /// <summary>Disposing twice is harmless.</summary>
    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        var client = NewClient();
        var state = await OpenConfirmingAsync(client);

        state.Dispose();
        var act = () => state.Dispose();

        act.Should().NotThrow();
        state.ConfirmationExpiresAtUtc.Should().BeNull();
    }

    /// <summary>Closing keeps the session, so this is not a reset in disguise.</summary>
    [Fact]
    public async Task Closing_drops_the_confirmation_without_dropping_the_conversation()
    {
        var client = NewClient();
        var state = await OpenConfirmingAsync(client);

        state.Close();

        state.ConversationId.Should().Be(Conversation, "closing preserves the session for resume");
        state.ConfirmingWriteOperationId.Should().BeNull("only the confirmation is released");
    }

    // ================================================================ unavailable staging

    private static CoachWriteOperationDto Operation(
        CoachWriteStatus status,
        bool requiresConfirmation = false,
        CoachWriteReceiptDto? receipt = null) => new()
        {
            OperationId = "op-1",
            ConversationId = Conversation,
            TurnId = "turn-1",
            ChangeKind = CoachWriteChangeKind.VocabularyAdd,
            RiskClass = requiresConfirmation
                ? CoachWriteRiskClass.WriteHard
                : CoachWriteRiskClass.WriteSoft,
            Status = status,
            ApprovalMode = requiresConfirmation ? "confirm" : "accept",
            Summary = "Add a word",
            Lines = Array.Empty<string>(),
            RequiresConfirmation = requiresConfirmation,
            IsReversible = true,
            ExpiresAtUtc = new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc),
            Receipt = receipt
        };

    /// <summary>A receipt as the server writes one, with the undo window under test.</summary>
    private static CoachWriteReceiptDto Receipt(bool canUndo, DateTime? undoClosesAt) => new()
    {
        OperationId = "op-1",
        ChangeKind = CoachWriteChangeKind.VocabularyAdd,
        RiskClass = CoachWriteRiskClass.WriteSoft,
        Status = CoachWriteStatus.Executed,
        ExecutedAtUtc = Now,
        Summary = "Added a word",
        Lines = Array.Empty<string>(),
        CanUndo = canUndo,
        UndoExpiresAtUtc = undoClosesAt
    };

    private static readonly DateTime Now = new(2026, 8, 19, 11, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A settled change keeps what the server said about it when a later read fails.
    /// </summary>
    /// <remarks>
    /// This is the defect. A refused read is a fact about the request; "Expired" is a verdict
    /// about the change. Reporting the second when only the first happened tells the learner a
    /// decision was reached that nobody reached — and for an Applied change it also implies the
    /// change did not happen, which is the opposite of the truth.
    /// </remarks>
    [Theory]
    [InlineData(CoachWriteStatus.Executed, SamWriteStage.Applied)]
    [InlineData(CoachWriteStatus.Undone, SamWriteStage.Undone)]
    [InlineData(CoachWriteStatus.Rejected, SamWriteStage.Declined)]
    [InlineData(CoachWriteStatus.Failed, SamWriteStage.Failed)]
    [InlineData(CoachWriteStatus.Expired, SamWriteStage.Expired)]
    public void A_settled_change_is_not_relabelled_when_a_read_fails(
        CoachWriteStatus status, SamWriteStage expected)
    {
        var stage = SamWritePresentation.Stage(
            Operation(status), isActionable: true, isConfirming: false, Now, isUnavailable: true);

        stage.Should().Be(expected);
    }

    /// <summary>
    /// A change still in flight genuinely is unknown, and says so rather than guessing.
    /// </summary>
    [Theory]
    [InlineData(CoachWriteStatus.Proposed)]
    [InlineData(CoachWriteStatus.Executing)]
    public void An_unsettled_change_reads_as_unavailable_not_expired(CoachWriteStatus status)
    {
        var stage = SamWritePresentation.Stage(
            Operation(status), isActionable: true, isConfirming: false, Now, isUnavailable: true);

        stage.Should().Be(
            SamWriteStage.Unavailable,
            "the outcome is unknown, which is a different sentence from the window having closed");
        stage.Should().NotBe(SamWriteStage.Expired);
    }

    /// <summary>Unavailable is its own label, so the learner is not told a window closed.</summary>
    [Fact]
    public void The_unavailable_stage_has_its_own_wording_and_style()
    {
        SamWritePresentation.StateKey(SamWriteStage.Unavailable)
            .Should().Be("Coach_WriteStateUnavailable")
            .And.NotBe(SamWritePresentation.StateKey(SamWriteStage.Expired));

        SamWritePresentation.StageCss(SamWriteStage.Unavailable).Should().Be("unavailable");
    }

    /// <summary>
    /// An applied change we can no longer read offers no Undo.
    /// </summary>
    /// <remarks>
    /// Failing closed is the whole point of keeping the Applied label. The card still says what
    /// last happened, because that is true; it stops promising the change can be taken back,
    /// because that is a promise the server has stopped confirming.
    /// </remarks>
    [Fact]
    public void An_applied_change_that_cannot_be_read_offers_no_undo()
    {
        var operation = Operation(
            CoachWriteStatus.Executed, receipt: Receipt(canUndo: true, Now.AddMinutes(5)));
        var stage = SamWritePresentation.Stage(
            operation, isActionable: true, isConfirming: false, Now, isUnavailable: true);

        stage.Should().Be(SamWriteStage.Applied);

        SamWritePresentation.ShowsUndo(operation, stage, Now, isUnavailable: true)
            .Should().BeFalse("we can no longer confirm the reversal is there to offer");

        SamWritePresentation.ShowsUndo(operation, stage, Now)
            .Should().BeTrue("and it is offered normally when the server is answering");
    }

    /// <summary>
    /// The Undo button is read from the server's receipt; the clock can only take it away.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Without <c>CanUndo</c> no clock reading may produce a button, so a
    /// device running slow cannot invent one. With it, a device running fast simply stops offering
    /// a press the server would refuse. The clock is never the thing that grants.
    /// </remarks>
    [Fact]
    public void Undo_needs_the_servers_receipt_and_the_clock_can_only_withdraw_it()
    {
        var withoutServerOffer = Operation(
            CoachWriteStatus.Executed, receipt: Receipt(canUndo: false, Now.AddYears(1)));

        SamWritePresentation.ShowsUndo(withoutServerOffer, SamWriteStage.Applied, Now)
            .Should().BeFalse("no clock reading substitutes for the server offering the reversal");

        var elapsed = Operation(
            CoachWriteStatus.Executed, receipt: Receipt(canUndo: true, Now.AddMinutes(-1)));

        SamWritePresentation.ShowsUndo(elapsed, SamWriteStage.Applied, Now)
            .Should().BeFalse("a window the server said had closed is not offered");
    }

    // ================================================================ turn presentation order

    /// <summary>
    /// A turn's proposal renders inside that turn, after what Sam said and before the next turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the order the pane's own comment now describes, pinned so the two cannot drift
    /// again. Two turns are seeded rather than one, because a card that rendered at the foot of
    /// the thread would still pass a single-turn test — and "the decision sits beside the sentence
    /// that produced it" is exactly the property that fails when it does.
    /// </para>
    /// <para>
    /// The preferred order is inline for a reason: an approval control collected at the end of the
    /// conversation is a control the learner reaches without the exchange that explains it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Each_turns_proposal_renders_inside_that_turn()
    {
        var client = NewClient();

        var first = client.AddWrite(Conversation, "op-1");
        var second = client.AddWrite(Conversation, "op-2");

        client.Seed(Conversation, CoachMessageRole.Learner, "add a word for me");
        client.Seed(Conversation, CoachMessageRole.Coach, "First reply.", writeOperation: first);
        client.Seed(Conversation, CoachMessageRole.Learner, "and another");
        client.Seed(Conversation, CoachMessageRole.Coach, "Second reply.", writeOperation: second);

        var flags = new CoachFeatureFlags(client);
        await flags.EnsureLoadedAsync();

        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client, flags), flags);
        await state.RefreshAvailabilityAsync();
        await state.OpenConversationAsync(CoachPresentation.Overlay, Conversation);

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

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachChatPane>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });

        var firstReply = html.IndexOf("First reply.", StringComparison.Ordinal);
        var firstCard = html.IndexOf("sam-write-op-1", StringComparison.Ordinal);
        var secondReply = html.IndexOf("Second reply.", StringComparison.Ordinal);
        var secondCard = html.IndexOf("sam-write-op-2", StringComparison.Ordinal);

        firstReply.Should().BeGreaterThan(-1);
        firstCard.Should().BeGreaterThan(-1);
        secondReply.Should().BeGreaterThan(-1);
        secondCard.Should().BeGreaterThan(-1);

        firstCard.Should().BeGreaterThan(firstReply, "the card reads after what Sam said");
        firstCard.Should().BeLessThan(
            secondReply, "and before the next exchange, not collected at the end of the thread");
        secondCard.Should().BeGreaterThan(secondReply);
    }

    // ================================================================ forward compatibility

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// An enum name this build has never heard of costs the whole payload, not just the card.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This pins the cost of the current contract rather than arguing with it. Two house rules
    /// require it and both are deliberate: every coach enum must carry
    /// <c>JsonStringEnumConverter</c> (<c>CoachEnumContractTests</c>), and an unknown write status
    /// must be refused rather than coerced (<c>CoachWriteContractSerializationTests</c>). A
    /// per-enum tolerant converter that mapped unknown names to the zero value was built and
    /// measured against them, and it contradicts both, so it was not kept.
    /// </para>
    /// <para>
    /// What the test records is the consequence, so the trade is visible to whoever revisits it:
    /// the failure is not scoped to the field. <see cref="CoachWriteOperationDto"/> is a member of
    /// the turn response, so a server that ships a new change kind takes an older client's whole
    /// reply down with the card — the learner loses the answer, not just the proposal. That is a
    /// product decision about versioning, not a review follow-up, and it is stated here rather
    /// than changed.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("\"changeKind\": \"SomethingShippedLater\"")]
    [InlineData("\"status\": \"PartiallySettled\"")]
    public void An_unrecognised_enum_name_refuses_the_whole_payload(string unknownMember)
    {
        var members = new List<string>
        {
            "\"operationId\": \"op-1\"",
            "\"conversationId\": \"conv-1\"",
            "\"turnId\": \"turn-1\"",
            "\"approvalMode\": \"accept\"",
            "\"summary\": \"Add a word\"",
            "\"lines\": []",
            "\"requiresConfirmation\": false",
            "\"isReversible\": true",
            "\"riskClass\": \"WriteSoft\"",
            "\"expiresAtUtc\": \"2026-08-19T12:00:00Z\"",
            unknownMember
        };

        if (!unknownMember.Contains("changeKind", StringComparison.Ordinal))
        {
            members.Add("\"changeKind\": \"VocabularyAdd\"");
        }

        if (!unknownMember.Contains("status", StringComparison.Ordinal))
        {
            members.Add("\"status\": \"Proposed\"");
        }

        var json = "{" + string.Join(",", members) + "}";

        var act = () => JsonSerializer.Deserialize<CoachWriteOperationDto>(json, Wire);

        act.Should().Throw<JsonException>(
            "the closed set is a deliberate contract; this records what it costs, not a defect");
    }

    /// <summary>The card's own fail-closed rules still hold for the zero values.</summary>
    /// <remarks>
    /// The half of forward compatibility that is already in place: if an unknown value ever does
    /// reach the card — from a default, or from a future decision to tolerate names — it lands in
    /// the one stage with no controls at all. The tolerance question is about parsing, not about
    /// whether the surface would then do something unsafe.
    /// </remarks>
    [Fact]
    public void An_unknown_status_would_render_no_control_if_it_ever_arrived()
    {
        var operation = Operation(CoachWriteStatus.Unknown);

        SamWritePresentation.IsWellFormed(operation).Should().BeFalse();
        SamWritePresentation.Stage(operation, true, false, Now).Should().Be(SamWriteStage.Unreadable);
        SamWritePresentation.ShowsAccept(operation, SamWriteStage.Unreadable).Should().BeFalse();
        SamWritePresentation.HeadingKey(CoachWriteChangeKind.Unknown).Should().Be("Coach_WriteKindUnknown");
    }

    /// <summary>Known values round-trip by name, which is what the house rule requires.</summary>
    [Fact]
    public void Known_values_round_trip_by_name()
    {
        var json = JsonSerializer.Serialize(
            Operation(CoachWriteStatus.Executed, requiresConfirmation: true), Wire);

        json.Should().Contain("\"status\":\"Executed\"");
        json.Should().Contain("\"riskClass\":\"WriteHard\"");
        json.Should().Contain("\"changeKind\":\"VocabularyAdd\"");

        JsonSerializer.Deserialize<CoachWriteOperationDto>(json, Wire)!
            .Status.Should().Be(CoachWriteStatus.Executed);
    }
}
