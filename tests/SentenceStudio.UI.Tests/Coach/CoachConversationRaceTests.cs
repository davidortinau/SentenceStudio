using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

public class CoachConversationRaceTests
{
    [Fact]
    public async Task Switching_conversations_during_a_turn_discards_the_late_visible_result_and_restores_its_proposal_to_the_owner()
    {
        var (state, client) = await OpenConversationAAsync();
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proposal = VocabularyProposal();
        var completed = false;

        client.OnGetConversationVocabularyState = conversationId => new CoachConversationVocabularyStateDto
        {
            PendingProposal = completed && conversationId == "conversation-a" ? proposal : null
        };
        client.OnSubmitConversationTurnAsync = async (conversationId, request, cancellationToken) =>
        {
            submitted.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            completed = true;
            var learner = client.Seed(conversationId, CoachMessageRole.Learner, request.Turn.Text!);
            var coach = client.Seed(conversationId, CoachMessageRole.Coach, "A late answer.");
            return CompletedOperation(
                conversationId,
                request,
                CoachStateMachineTests.Turn().WithVocabularySetProposal(proposal),
                learner,
                coach);
        };

        state.Draft = "Create a food vocabulary set.";
        var turn = state.SendDraftAsync();
        await submitted.Task;

        await state.OpenConversationAsync(CoachPresentation.Overlay, "conversation-b");
        release.SetResult();
        await turn;

        state.ConversationId.Should().Be("conversation-b");
        state.Timeline.Select(entry => entry.ReadableText()).Should().Equal("B stays visible.");
        state.PendingVocabularySet.Should().BeNull();
        state.ApprovedVocabularySet.Should().BeNull();
        state.VocabularySetDecisionMessage.Should().BeNull();

        await state.OpenConversationAsync(CoachPresentation.Overlay, "conversation-a");

        state.PendingVocabularySet.Should().BeEquivalentTo(proposal);
        state.Timeline.Select(entry => entry.ReadableText())
            .Should().ContainInOrder("Create a food vocabulary set.", "A late answer.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Switching_conversations_during_a_vocabulary_decision_never_projects_the_result_into_the_new_conversation(
        bool approve)
    {
        var proposal = VocabularyProposal();
        var proposalActive = true;
        var (state, client) = await OpenConversationAAsync(conversationId => new CoachConversationVocabularyStateDto
        {
            PendingProposal = proposalActive && conversationId == "conversation-a" ? proposal : null
        });
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.OnApproveVocabularySetAsync = async (request, cancellationToken) =>
        {
            submitted.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            proposalActive = false;
            return new CoachVocabularySetApprovalResponse
            {
                ProposalId = request.ProposalReference,
                Decision = request.Decision,
                ResourceId = request.Decision == ApplicationOperationDecision.Accept ? "resource-a" : null,
                ActivityPath = request.Decision == ApplicationOperationDecision.Accept
                    ? "/vocabulary/review"
                    : null
            };
        };

        var decision = approve
            ? state.ApproveVocabularySetAsync()
            : state.RejectVocabularySetAsync();
        await submitted.Task;

        await state.OpenConversationAsync(CoachPresentation.Overlay, "conversation-b");
        release.SetResult();
        await decision;

        state.ConversationId.Should().Be("conversation-b");
        state.Timeline.Select(entry => entry.ReadableText()).Should().Equal("B stays visible.");
        state.PendingVocabularySet.Should().BeNull();
        state.ApprovedVocabularySet.Should().BeNull();
        state.VocabularySetDecisionMessage.Should().BeNull();

        await state.OpenConversationAsync(CoachPresentation.Overlay, "conversation-a");

        state.PendingVocabularySet.Should().BeNull();
        if (approve)
        {
            state.ApprovedVocabularySet.Should().NotBeNull();
            state.VocabularySetDecisionMessage.Should().StartWith("Vocabulary set approved.");
        }
        else
        {
            state.ApprovedVocabularySet.Should().BeNull();
            state.VocabularySetDecisionMessage.Should().Be("Vocabulary set declined. Nothing was saved.");
        }
    }

    [Fact]
    public async Task A_late_vocabulary_refresh_failure_cannot_clear_or_fabricate_state_in_the_new_conversation()
    {
        var (state, client) = await OpenConversationAAsync();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.OnSubmitConversationTurn = (conversationId, request) =>
        {
            var learner = client.Seed(conversationId, CoachMessageRole.Learner, request.Turn.Text!);
            var coach = client.Seed(conversationId, CoachMessageRole.Coach, "A completed answer.");
            return CompletedOperation(
                conversationId,
                request,
                CoachStateMachineTests.Turn(),
                learner,
                coach);
        };
        client.OnGetConversationVocabularyStateAsync = async (conversationId, cancellationToken) =>
        {
            if (conversationId != "conversation-a")
            {
                return new CoachConversationVocabularyStateDto();
            }

            refreshStarted.SetResult();
            await releaseRefresh.Task.WaitAsync(cancellationToken);
            throw new HttpRequestException("A refresh failed late.");
        };

        state.Draft = "Answer in A.";
        var turn = state.SendDraftAsync();
        await refreshStarted.Task;

        await state.OpenConversationAsync(CoachPresentation.Overlay, "conversation-b");
        releaseRefresh.SetResult();
        await turn;

        state.ConversationId.Should().Be("conversation-b");
        state.Timeline.Select(entry => entry.ReadableText()).Should().Equal("B stays visible.");
        state.PendingVocabularySet.Should().BeNull();
        state.ApprovedVocabularySet.Should().BeNull();
        state.VocabularySetDecisionMessage.Should().BeNull();
        state.ConversationNoticeKey.Should().BeNull();
    }

    [Fact]
    public async Task A_late_missing_operation_cannot_fail_the_new_conversations_pending_turn()
    {
        var (state, client) = await OpenConversationAAsync();
        var pollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bSubmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.OnSubmitConversationTurnAsync = async (conversationId, request, cancellationToken) =>
        {
            if (conversationId == "conversation-a")
            {
                return new CoachTurnOperationDto
                {
                    OperationId = request.OperationId,
                    ConversationId = conversationId,
                    State = CoachTurnOperationState.Running,
                    Messages = Array.Empty<CoachHistoryMessageDto>(),
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };
            }

            bSubmitted.SetResult();
            await releaseB.Task.WaitAsync(cancellationToken);
            return CompletedOperation(
                conversationId,
                request,
                CoachStateMachineTests.Turn(),
                client.Seed(conversationId, CoachMessageRole.Learner, request.Turn.Text!),
                client.Seed(conversationId, CoachMessageRole.Coach, "B answered."));
        };
        client.OnGetConversationOperationAsync = async (conversationId, _, cancellationToken) =>
        {
            conversationId.Should().Be("conversation-a");
            pollStarted.SetResult();
            await releasePoll.Task.WaitAsync(cancellationToken);
            return null;
        };

        state.Draft = "Ask from A.";
        var turnA = state.SendDraftAsync();
        await pollStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await state.OpenConversationAsync(CoachPresentation.Overlay, "conversation-b");
        state.Draft = "Ask from B.";
        var turnB = state.SendDraftAsync();

        releasePoll.SetResult();
        await turnA;
        await bSubmitted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var pendingB = state.Timeline.Single(entry =>
            entry.Kind == CoachTimelineKind.LearnerMessage
            && entry.ReadableText() == "Ask from B.");
        pendingB.Status.Should().NotBe(CoachTimelineStatus.Failed);
        state.State.Should().Be(CoachUiState.Running);
        state.PendingOperationId.Should().NotBeNull();
        state.HasRecoverableTurn.Should().BeFalse();

        releaseB.SetResult();
        await turnB;
    }

    [Fact]
    public async Task A_missing_operation_still_fails_the_pending_turn_when_its_conversation_remains_active()
    {
        var (state, client) = await OpenConversationAAsync();
        client.OnSubmitConversationTurn = (conversationId, request) => new CoachTurnOperationDto
        {
            OperationId = request.OperationId,
            ConversationId = conversationId,
            State = CoachTurnOperationState.Running,
            Messages = Array.Empty<CoachHistoryMessageDto>(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        client.OnGetConversationOperation = (_, _) => null;

        state.Draft = "Ask from A.";
        await state.SendDraftAsync();

        state.Timeline.Single(entry =>
                entry.Kind == CoachTimelineKind.LearnerMessage
                && entry.ReadableText() == "Ask from A.")
            .Status.Should().Be(CoachTimelineStatus.Failed);
        state.HasRecoverableTurn.Should().BeTrue();
    }

    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)> OpenConversationAAsync(
        Func<string, CoachConversationVocabularyStateDto?>? vocabularyState = null)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("conversation-a");
        client.AddConversation("conversation-b");
        client.Seed("conversation-b", CoachMessageRole.Coach, "B stays visible.");
        client.OnGetConversationVocabularyState = vocabularyState;
        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client));
        await state.OpenConversationAsync(CoachPresentation.Overlay, "conversation-a");
        return (state, client);
    }

    private static CoachTurnOperationDto CompletedOperation(
        string conversationId,
        CoachConversationTurnRequest request,
        CoachTurnResponse result,
        params CoachHistoryMessageDto[] messages) =>
        new()
        {
            OperationId = request.OperationId,
            ConversationId = conversationId,
            State = CoachTurnOperationState.Completed,
            Result = result,
            Messages = messages,
            FirstResponseSequence = messages.FirstOrDefault()?.Sequence,
            LastResponseSequence = messages.LastOrDefault()?.Sequence,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static CoachVocabularySetProposal VocabularyProposal() => new()
    {
        ProposalId = "proposal-a",
        Topic = "food",
        Title = "Food vocabulary",
        TargetLanguageTag = "ko",
        Terms =
        [
            new CoachVocabularyTermDto { TargetTerm = "밥", NativeTerm = "rice or meal" }
        ]
    };
}
