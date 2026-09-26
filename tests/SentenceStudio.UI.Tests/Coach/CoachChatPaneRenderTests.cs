using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Renders the conversation to real HTML: one answer not two, log semantics, and the
/// plan affordances withdrawing when there is no plan to edit.
/// </summary>
public class CoachChatPaneRenderTests
{
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
        services.AddScoped<NavigationManager, TestNavigationManager>();
        services.AddScoped(_ => state);

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachChatPane>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    private static CoachTurnResponse AnswerTurn(CoachAnswerDto answer, PendingCoachSuggestionDto? pending = null) =>
        CoachStateMachineTests.Turn(
            sessionStatus: pending is null ? CoachSessionStatus.Active : CoachSessionStatus.SuggestionPending,
            suggestion: pending,
            messages: [CoachAnswerStateTests.AnswerMessage(answer)],
            answer: answer);

    private static async Task<CoachWorkspaceState> AfterAnswerAsync(
        Action<FakeCoachApiClient>? configure = null,
        PendingCoachSuggestionDto? pending = null)
    {
        var client = new FakeCoachApiClient();
        configure?.Invoke(client);

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        var answer = CoachAnswerStateTests.KoreanContrastAnswer();
        client.OnSubmitTurn = _ => AnswerTurn(answer, pending);
        state.Draft = "What is the difference between 은/는 and 이/가?";
        await state.SendDraftAsync();

        return state;
    }

    // ---------------------------------------------------------------- no duplication

    [Fact]
    public async Task AnAnswerRendersOnceAsBlocksNotAlsoAsPlainText()
    {
        var html = await RenderAsync(await AfterAnswerAsync());

        // The server sends the same text twice — structured and as the message body. Exactly one
        // copy may reach the learner.
        Regex.Matches(html, Regex.Escape("은/는 marks the topic; 이/가 marks the subject.")).Count
            .Should().Be(1, "the plain text must not render beside the blocks");

        html.Should().Contain("coach-answer", "the structured blocks are what rendered");
    }

    [Fact]
    public async Task TheKoreanExampleIsLanguageTaggedInsideTheConversation()
    {
        var html = await RenderAsync(await AfterAnswerAsync());

        html.Should().Contain("lang=\"ko\"");
        html.Should().Contain("제가 했어요");
    }

    // ---------------------------------------------------------------- log semantics

    [Fact]
    public async Task TheConversationIsANamedLogRegion()
    {
        var html = await RenderAsync(await AfterAnswerAsync());

        html.Should().Contain("role=\"log\"", "appended turns are announced in order");
        html.Should().Contain("aria-label=\"Conversation with Sam\"");
    }

    // ---------------------------------------------------------------- plan affordances

    [Fact]
    public async Task QuickConstraintsAppearAsStartersInAnEmptyConversation()
    {
        // Before the learner has said anything the chips are prompts: something to start from.
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        var html = await RenderAsync(state);

        html.Should().Contain("coach-chip-row", "an empty conversation needs somewhere to start");
    }

    [Fact]
    public async Task QuickConstraintsLeaveTheConversationOnceItHasStarted()
    {
        // Once there is a conversation they are no longer prompts, they are plan controls, and
        // plan controls live in the canvas.
        var html = await RenderAsync(await AfterAnswerAsync());

        html.Should().NotContain("coach-chip-row",
            "after the first turn the chips belong to the plan pane, not the chat");
    }

    [Fact]
    public async Task QuickConstraintsAreWithdrawnWhenThereIsNoPlanToEdit()
    {
        var state = await AfterAnswerAsync(client => client.Availability = new CoachAvailabilityResponse
        {
            IsAvailable = true,
            State = CoachAvailabilityState.Available,
            CanEditPlan = false
        });

        var html = await RenderAsync(state);

        html.Should().NotContain("coach-chip-row", "there is no plan for these to change");
        html.Should().Contain("You can still ask language questions.",
            "and the learner is told plainly that the conversation still works");
    }

    [Fact]
    public async Task TheNoPlanExplanationIsNeutralNotAnError()
    {
        var state = await AfterAnswerAsync(client => client.Availability = new CoachAvailabilityResponse
        {
            IsAvailable = true,
            State = CoachAvailabilityState.Available,
            CanEditPlan = false
        });

        var html = await RenderAsync(state);

        html.Should().NotContain("role=\"alert\"", "no plan is a normal state");
        html.Should().NotContain("coach-card-alert");
    }

    // ---------------------------------------------------------------- mixed turn

    [Fact]
    public async Task AMixedTurnShowsTheAnswerThenExactlyOneActionPair()
    {
        var state = await AfterAnswerAsync(pending: CoachStateMachineTests.Suggestion("sug-mixed"));
        var html = await RenderAsync(state);

        var answerAt = html.IndexOf("은/는 marks the topic", StringComparison.Ordinal);
        var cardAt = html.IndexOf("coach-card-suggestion", StringComparison.Ordinal);

        answerAt.Should().BeGreaterThan(-1);
        cardAt.Should().BeGreaterThan(answerAt, "the answer is read before the offer");

        // Counting every button in the pane would also catch the quick-constraint chips, so
        // assert on the decision pair itself: exactly one accept and exactly one decline.
        Regex.Matches(html, Regex.Escape("Include speaking")).Count.Should().Be(1, "exactly one Accept");
        Regex.Matches(html, Regex.Escape("Not now")).Count.Should().Be(1, "exactly one Not now");
        Regex.Matches(html, Regex.Escape("coach-card-suggestion")).Count.Should().Be(1, "one offer, one card");
    }

    [Fact]
    public async Task APureAnswerShowsNoSuggestionCardAndNoReceipt()
    {
        var html = await RenderAsync(await AfterAnswerAsync());

        html.Should().NotContain("coach-card-suggestion");
        html.Should().NotContain("coach-card-receipt", "a language question changes no plan");
    }

    // ---------------------------------------------------------------- vocabulary proposal

    [Fact]
    public async Task Direct_and_durable_vocabulary_proposal_renders_one_card()
    {
        var state = await AfterVocabularyTurnAsync(
            includeResponseProposal: true,
            includeDurableProposal: true);

        state.PendingVocabularySet.Should().NotBeNull();
        state.PendingVocabularySet!.Terms.Should().HaveCount(10);

        var html = await RenderAsync(state);
        html.Should().Contain("Food vocabulary");
        html.Should().Contain("밥");
        html.Should().Contain("rice or meal");
        html.Should().Contain("식당");
        html.Should().Contain("restaurant");
        html.Should().Contain("Approve complete set");
        html.Should().Contain("Decline");
        Regex.Matches(html, "<li>").Count.Should().Be(10);
        Regex.Matches(html, "id=\"coach-vocabulary-set-title\"").Count.Should().Be(1);
    }

    [Fact]
    public async Task Same_turn_retries_until_its_committed_vocabulary_proposal_is_visible()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client));
        var proposal = FoodVocabularyProposal();
        var postTurnReads = 0;
        var turnCompleted = false;
        string? proposalConversationId = null;
        client.OnGetConversationVocabularyState = conversationId =>
        {
            if (!turnCompleted)
            {
                return new CoachConversationVocabularyStateDto();
            }

            conversationId.Should().Be(proposalConversationId);
            postTurnReads++;
            return new CoachConversationVocabularyStateDto
            {
                PendingProposal = postTurnReads == 1 ? null : proposal
            };
        };
        client.OnSubmitConversationTurn = (conversationId, request) =>
        {
            proposalConversationId = conversationId;
            turnCompleted = true;
            return CompletedVocabularyOperation(
                client,
                conversationId,
                request,
                proposal,
                includeResponseProposal: false);
        };

        await state.OpenAsync(CoachPresentation.Overlay);
        var proposalNotifications = 0;
        state.Changed += () =>
        {
            if (state.PendingVocabularySet?.ProposalId == proposal.ProposalId)
            {
                proposalNotifications++;
            }
        };
        state.Draft = "start a vocabulary review activity with words about food";
        await state.SendDraftAsync();

        postTurnReads.Should().Be(2);
        state.ConversationId.Should().Be(proposalConversationId);
        state.PendingVocabularySet!.ProposalId.Should().Be(proposal.ProposalId);
        proposalNotifications.Should().BeGreaterThan(0, "the live Blazor circuit must be notified");
        var html = await RenderAsync(state);
        Regex.Matches(html, "id=\"coach-vocabulary-set-title\"").Count.Should().Be(1);
        Regex.Matches(html, "Approve complete set").Count.Should().Be(1);
    }

    [Fact]
    public async Task Mounted_chat_rerenders_vocabulary_card_when_same_turn_proposal_arrives()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client));
        var proposal = FoodVocabularyProposal();
        var turnCompleted = false;
        client.OnGetConversationVocabularyState = _ => new CoachConversationVocabularyStateDto
        {
            PendingProposal = turnCompleted ? proposal : null
        };
        client.OnSubmitConversationTurn = (conversationId, request) =>
        {
            turnCompleted = true;
            return CompletedVocabularyOperation(
                client,
                conversationId,
                request,
                proposal,
                includeResponseProposal: true);
        };

        await state.OpenAsync(CoachPresentation.Overlay);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        services.AddScoped<CoachPersona>();
        services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
        services.AddScoped<NavigationManager, TestNavigationManager>();
        services.AddScoped(_ => state);

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var output = await renderer.Dispatcher.InvokeAsync(
            () => renderer.RenderComponentAsync<CoachChatPane>(ParameterView.Empty));

        (await renderer.Dispatcher.InvokeAsync(output.ToHtmlString))
            .Should().NotContain("Approve complete set");

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            state.Draft = "start a vocabulary review activity with words about food";
            await state.SendDraftAsync();
        });

        var html = await renderer.Dispatcher.InvokeAsync(output.ToHtmlString);
        html.Should().Contain("Approve complete set",
            "the already-mounted card must consume the workspace notification");
        Regex.Matches(html, "<li>").Count.Should().Be(10);
    }

    [Fact]
    public async Task Disposed_vocabulary_card_drops_a_render_already_queued_by_an_in_flight_callback()
    {
        var state = new CoachWorkspaceState(new FakeCoachApiClient());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<NavigationManager, TestNavigationManager>();
        services.AddScoped(_ => state);

        await using var provider = services.BuildServiceProvider();
        using var renderer = new InteractiveTestRenderer(
            provider, provider.GetRequiredService<ILoggerFactory>());
        var componentId = await renderer.RenderAsync<CoachVocabularySetCard>();
        var component = (IDisposable)renderer.LastRootComponent!;
        var renderedUpdates = renderer.DisplayUpdateCount;

        using var blockerStarted = new ManualResetEventSlim();
        using var releaseBlocker = new ManualResetEventSlim();
        var blocker = Task.Run(() => renderer.Dispatcher.InvokeAsync(() =>
            {
                blockerStarted.Set();
                releaseBlocker.Wait();
            }));
        blockerStarted.Wait();

        var changed = typeof(CoachWorkspaceState)
            .GetField(nameof(CoachWorkspaceState.Changed), BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(state) as Delegate;
        changed.Should().NotBeNull("the mounted card must have subscribed");

        changed!.DynamicInvoke();
        component.Dispose();
        releaseBlocker.Set();

        await blocker;
        await renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);

        renderer.DisplayUpdateCount.Should().Be(renderedUpdates,
            "the queued callback must re-check disposal before rendering");
        renderer.Unhandled.Should().BeEmpty();

        await renderer.DisposeRootComponentAsync(componentId);
    }

    [Fact]
    public async Task Same_turn_vocabulary_commit_visibility_retry_is_bounded()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client));
        var proposal = FoodVocabularyProposal();
        var postTurnReads = 0;
        var turnCompleted = false;
        client.OnGetConversationVocabularyState = _ =>
        {
            if (turnCompleted)
            {
                postTurnReads++;
            }

            return new CoachConversationVocabularyStateDto();
        };
        client.OnSubmitConversationTurn = (conversationId, request) =>
        {
            turnCompleted = true;
            return CompletedVocabularyOperation(
                client,
                conversationId,
                request,
                proposal,
                includeResponseProposal: false);
        };

        await state.OpenAsync(CoachPresentation.Overlay);
        state.Draft = "start a vocabulary review activity with words about food";
        await state.SendDraftAsync();

        postTurnReads.Should().Be(5, "commit visibility retries must have a fixed upper bound");
        state.PendingVocabularySet.Should().BeNull();
        var html = await RenderAsync(state);
        Regex.Matches(html, "id=\"coach-vocabulary-set-title\"").Count.Should().Be(0);
    }

    [Fact]
    public async Task Durable_refresh_projects_a_missing_turn_proposal_in_the_same_turn()
    {
        var state = await AfterVocabularyTurnAsync(
            includeResponseProposal: false,
            includeDurableProposal: true);

        state.PendingVocabularySet.Should().NotBeNull();
        var html = await RenderAsync(state);
        html.Should().Contain("Food vocabulary");
        html.Should().Contain("Approve complete set");
    }

    [Fact]
    public async Task Ordinary_no_change_turn_does_not_project_a_vocabulary_card()
    {
        var state = await AfterVocabularyTurnAsync(
            includeResponseProposal: false,
            includeDurableProposal: false);

        state.PendingVocabularySet.Should().BeNull();
        var html = await RenderAsync(state);
        html.Should().NotContain("Approve complete set");
        html.Should().NotContain("Review all 10 terms");
    }

    [Fact]
    public async Task Household_turn_with_no_durable_proposal_clears_the_stale_card_only()
    {
        var (state, client) = await AfterVocabularyTurnWithClientAsync(
            includeResponseProposal: true,
            includeDurableProposal: true);
        state.PendingVocabularySet.Should().NotBeNull();
        var messageCount = state.Messages.Count;

        client.OnGetConversationVocabularyState = _ => new CoachConversationVocabularyStateDto();
        client.OnSubmitConversationTurn = (conversationId, request) =>
        {
            var learner = client.Seed(conversationId, CoachMessageRole.Learner, request.Turn.Text ?? string.Empty);
            var coach = client.Seed(conversationId, CoachMessageRole.Coach, "Household vocabulary needs clarification.");
            return new CoachTurnOperationDto
            {
                OperationId = request.OperationId,
                ConversationId = conversationId,
                State = CoachTurnOperationState.Completed,
                Result = CoachStateMachineTests.Turn(),
                Messages = [learner, coach],
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
        };

        state.Draft = "household words";
        await state.SendDraftAsync();

        state.PendingVocabularySet.Should().BeNull();
        state.Messages.Count.Should().BeGreaterThan(messageCount, "the vocabulary refresh must not clear the conversation");
        (await RenderAsync(state)).Should().NotContain("Approve complete set");
    }

    [Fact]
    public async Task Durable_vocabulary_refresh_failure_surfaces_the_existing_load_error_without_a_card()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client));
        await state.OpenAsync(CoachPresentation.Overlay);
        client.OnGetConversationVocabularyState = _ => throw new HttpRequestException("refresh unavailable");

        state.Draft = "start a vocabulary review activity with words about food";
        await state.SendDraftAsync();

        state.ConversationNoticeKey.Should().Be("Coach_ConversationsLoadFailed");
        state.PendingVocabularySet.Should().BeNull();
        (await RenderAsync(state)).Should().NotContain("Approve complete set");
    }

    [Fact]
    public async Task New_conversation_clears_the_previous_vocabulary_review_action()
    {
        var (state, _, _) = await ApprovedFoodConversationAsync();
        (await RenderAsync(state)).Should().Contain("Start Vocab Review");

        await state.OpenConversationAsync(CoachPresentation.Overlay);

        state.PendingVocabularySet.Should().BeNull();
        state.ApprovedVocabularySet.Should().BeNull();
        state.VocabularySetDecisionMessage.Should().BeNull();
        (await RenderAsync(state)).Should().NotContain("Start Vocab Review");
        (await RenderAsync(state)).Should().NotContain("Vocabulary set approved");
    }

    [Fact]
    public async Task Household_clarification_in_a_new_conversation_has_no_prior_food_action()
    {
        var (state, client, _) = await ApprovedFoodConversationAsync();
        await state.OpenConversationAsync(CoachPresentation.Overlay);

        client.OnSubmitConversationTurn = (conversationId, request) =>
        {
            var learner = client.Seed(conversationId, CoachMessageRole.Learner, request.Turn.Text ?? string.Empty);
            var coach = client.Seed(
                conversationId,
                CoachMessageRole.Coach,
                "Would you like a direct vocabulary review activity right now, or do you want me to replace the vocabulary in Today's Plan with house/rooms vocabulary?");

            return new CoachTurnOperationDto
            {
                OperationId = request.OperationId,
                ConversationId = conversationId,
                State = CoachTurnOperationState.Completed,
                Result = CoachStateMachineTests.Turn(
                    sessionStatus: CoachSessionStatus.AwaitingClarification,
                    clarifyingQuestion: coach.Message.Text),
                Messages = [learner, coach],
                FirstResponseSequence = learner.Sequence,
                LastResponseSequence = coach.Sequence,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
        };

        state.Draft = "I wnto study vocabulary about house rooms and things I'd see in a house.";
        await state.SendDraftAsync();

        var html = await RenderAsync(state);
        html.Should().Contain("Would you like a direct vocabulary review activity right now");
        html.Should().NotContain("Start Vocab Review");
        html.Should().NotContain("Vocabulary set approved");
    }

    [Fact]
    public async Task Returning_to_the_original_conversation_restores_only_its_vocabulary_action()
    {
        var (state, _, foodConversationId) = await ApprovedFoodConversationAsync();
        await state.OpenConversationAsync(CoachPresentation.Overlay);
        var householdConversationId = state.ConversationId;

        await state.OpenConversationAsync(CoachPresentation.Overlay, foodConversationId);

        state.ConversationId.Should().Be(foodConversationId);
        state.ApprovedVocabularySet.Should().NotBeNull();
        (await RenderAsync(state)).Should().Contain("Start Vocab Review");

        await state.OpenConversationAsync(CoachPresentation.Overlay, householdConversationId);

        state.ApprovedVocabularySet.Should().BeNull();
        state.VocabularySetDecisionMessage.Should().BeNull();
        (await RenderAsync(state)).Should().NotContain("Start Vocab Review");
    }

    // ---------------------------------------------------------------- resume

    [Fact]
    public async Task AResumedSessionWithNoTranscriptExplainsItselfWithoutInventingTurns()
    {
        var client = new FakeCoachApiClient();
        client.OnGetSession = id => FakeCoachApiClient.Session(id);

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay, "session-7");

        var html = await RenderAsync(state);

        html.Should().Contain("Earlier messages are not shown after a reload.");
        html.Should().NotContain("coach-answer", "no answers are reconstructed");
    }

    private static async Task<CoachWorkspaceState> AfterVocabularyTurnAsync(
        bool includeResponseProposal,
        bool includeDurableProposal)
    {
        var (state, _) = await AfterVocabularyTurnWithClientAsync(
            includeResponseProposal,
            includeDurableProposal);
        return state;
    }

    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)>
        AfterVocabularyTurnWithClientAsync(
            bool includeResponseProposal,
            bool includeDurableProposal)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client));
        var turnCompleted = false;
        var proposalActive = true;
        string? proposalConversationId = null;
        var proposal = FoodVocabularyProposal();
        client.OnGetConversationVocabularyState = conversationId => new CoachConversationVocabularyStateDto
        {
            PendingProposal = turnCompleted
                && proposalActive
                && includeDurableProposal
                && string.Equals(conversationId, proposalConversationId, StringComparison.Ordinal)
                    ? proposal
                    : null
        };
        client.OnApproveVocabularySet = request =>
        {
            proposalActive = false;
            return new CoachVocabularySetApprovalResponse
            {
                ProposalId = request.ProposalReference,
                Decision = request.Decision,
                ResourceId = request.Decision == SentenceStudio.Contracts.AppOperation.ApplicationOperationDecision.Accept
                    ? "resource-vocabulary"
                    : null,
                ActivityPath = request.Decision == SentenceStudio.Contracts.AppOperation.ApplicationOperationDecision.Accept
                    ? "/vocabulary/review"
                    : null
            };
        };
        client.OnSubmitConversationTurn = (conversationId, request) =>
        {
            var learner = client.Seed(
                conversationId,
                CoachMessageRole.Learner,
                request.Turn.Text ?? string.Empty);
            var coach = client.Seed(
                conversationId,
                CoachMessageRole.Coach,
                "I prepared a complete vocabulary set about food.");
            var result = CoachStateMachineTests.Turn();
            if (includeResponseProposal)
            {
                result = result.WithVocabularySetProposal(proposal);
            }
            proposalConversationId = conversationId;
            turnCompleted = true;

            return new CoachTurnOperationDto
            {
                OperationId = request.OperationId,
                ConversationId = conversationId,
                State = CoachTurnOperationState.Completed,
                Result = result,
                Messages = [learner, coach],
                FirstResponseSequence = learner.Sequence,
                LastResponseSequence = coach.Sequence,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
        };

        await state.OpenAsync(CoachPresentation.Overlay);
        state.Draft = includeResponseProposal || includeDurableProposal
            ? "start a vocabulary review activity with words about food"
            : "Thanks";
        await state.SendDraftAsync();
        return (state, client);
    }

    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client, string ConversationId)>
        ApprovedFoodConversationAsync()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client));
        var proposal = FoodVocabularyProposal();
        var turnCompleted = false;
        var proposalActive = true;
        string? proposalConversationId = null;
        client.OnGetConversationVocabularyState = conversationId => new CoachConversationVocabularyStateDto
        {
            PendingProposal = turnCompleted
                && proposalActive
                && string.Equals(conversationId, proposalConversationId, StringComparison.Ordinal)
                    ? proposal
                    : null
        };
        client.OnApproveVocabularySet = request =>
        {
            proposalActive = false;
            return new CoachVocabularySetApprovalResponse
            {
                ProposalId = request.ProposalReference,
                Decision = request.Decision,
                ResourceId = request.Decision == SentenceStudio.Contracts.AppOperation.ApplicationOperationDecision.Accept
                    ? "resource-vocabulary"
                    : null,
                ActivityPath = request.Decision == SentenceStudio.Contracts.AppOperation.ApplicationOperationDecision.Accept
                    ? "/vocabulary/review"
                    : null
            };
        };
        client.OnSubmitConversationTurn = (conversationId, request) =>
        {
            var learner = client.Seed(conversationId, CoachMessageRole.Learner, request.Turn.Text ?? string.Empty);
            var coach = client.Seed(
                conversationId,
                CoachMessageRole.Coach,
                "I prepared a complete vocabulary set about food.");

            proposalConversationId = conversationId;
            turnCompleted = true;
            return new CoachTurnOperationDto
            {
                OperationId = request.OperationId,
                ConversationId = conversationId,
                State = CoachTurnOperationState.Completed,
                Result = CoachStateMachineTests.Turn().WithVocabularySetProposal(proposal),
                Messages = [learner, coach],
                FirstResponseSequence = learner.Sequence,
                LastResponseSequence = coach.Sequence,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
        };

        await state.OpenAsync(CoachPresentation.Overlay);
        state.Draft = "start a vocabulary review activity with words about food";
        await state.SendDraftAsync();
        await state.ApproveVocabularySetAsync();

        return (state, client, state.ConversationId!);
    }

    private static CoachVocabularySetProposal FoodVocabularyProposal() => new()
    {
        ProposalId = "proposal-food",
        Topic = "food",
        Title = "Food vocabulary",
        TargetLanguageTag = "ko",
        Terms =
        [
            Pair("밥", "rice or meal"),
            Pair("빵", "bread"),
            Pair("물", "water"),
            Pair("과일", "fruit"),
            Pair("채소", "vegetable"),
            Pair("고기", "meat"),
            Pair("생선", "fish"),
            Pair("국", "soup"),
            Pair("김치", "kimchi"),
            Pair("식당", "restaurant")
        ]
    };

    private static CoachVocabularyTermDto Pair(string target, string native) => new()
    {
        TargetTerm = target,
        NativeTerm = native
    };

    private static CoachTurnOperationDto CompletedVocabularyOperation(
        FakeCoachApiClient client,
        string conversationId,
        CoachConversationTurnRequest request,
        CoachVocabularySetProposal proposal,
        bool includeResponseProposal = true)
    {
        var learner = client.Seed(
            conversationId,
            CoachMessageRole.Learner,
            request.Turn.Text ?? string.Empty);
        var coach = client.Seed(
            conversationId,
            CoachMessageRole.Coach,
            "I prepared a complete vocabulary set about food.");
        return new CoachTurnOperationDto
        {
            OperationId = request.OperationId,
            ConversationId = conversationId,
            State = CoachTurnOperationState.Completed,
            Result = includeResponseProposal
                ? CoachStateMachineTests.Turn().WithVocabularySetProposal(proposal)
                : CoachStateMachineTests.Turn(),
            Messages = [learner, coach],
            FirstResponseSequence = learner.Sequence,
            LastResponseSequence = coach.Sequence,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }
}
