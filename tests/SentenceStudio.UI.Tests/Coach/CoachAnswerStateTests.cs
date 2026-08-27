using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Stage B: the coach answers language questions as well as editing Today's Plan, in one
/// conversation with no mode switch.
/// </summary>
/// <remarks>
/// The server returns a pedagogical answer TWICE — once as structured <c>Answer</c> blocks on the
/// turn, and once as a <c>PedagogicalAnswer</c> message carrying the same PlainText. The client
/// pairs them so the blocks render in place and the plain text stays a genuine fallback; without
/// that pairing the learner would read the answer twice.
/// </remarks>
public class CoachAnswerStateTests
{
    private static (CoachWorkspaceState State, FakeCoachApiClient Client) Create()
    {
        var client = new FakeCoachApiClient();
        return (new CoachWorkspaceState(client), client);
    }

    // ---------------------------------------------------------------- builders

    internal static CoachAnswerSpanDto Span(string text, CoachLanguageRole role, string tag) =>
        new() { Text = text, Language = role, LanguageTag = tag };

    internal static CoachAnswerBlockDto Block(CoachAnswerBlockKind kind, params CoachAnswerSpanDto[] spans) =>
        new() { Kind = kind, Spans = spans };

    /// <summary>A Korean contrast answer: the shape the feature exists to render.</summary>
    internal static CoachAnswerDto KoreanContrastAnswer() => new()
    {
        Topic = CoachAnswerTopic.Grammar,
        PlainText = "은/는 marks the topic; 이/가 marks the subject.",
        TargetLanguageTag = "ko",
        DisplayLanguageTag = "en",
        Blocks =
        [
            Block(CoachAnswerBlockKind.Answer,
                Span("은/는 marks the topic; 이/가 marks the subject.", CoachLanguageRole.Display, "en")),
            Block(CoachAnswerBlockKind.Contrast,
                Span("저는 학생이에요", CoachLanguageRole.Target, "ko"),
                Span("introduces you as a topic.", CoachLanguageRole.Display, "en")),
            Block(CoachAnswerBlockKind.Example,
                Span("제가 했어요", CoachLanguageRole.Target, "ko"),
                Span("answers who did it.", CoachLanguageRole.Display, "en"))
        ]
    };

    internal static CoachMessageDto AnswerMessage(CoachAnswerDto answer, string id = "m-answer") => new()
    {
        MessageId = id,
        Role = CoachMessageRole.Coach,
        Kind = CoachMessageKind.PedagogicalAnswer,
        Text = answer.PlainText,
        CreatedAtUtc = DateTime.UtcNow
    };

    private static CoachTurnResponse AnswerTurn(
        CoachAnswerDto answer,
        PendingCoachSuggestionDto? pending = null,
        string messageId = "m-answer") =>
        CoachStateMachineTests.Turn(
            sessionStatus: pending is null ? CoachSessionStatus.Active : CoachSessionStatus.SuggestionPending,
            suggestion: pending,
            messages: [AnswerMessage(answer, messageId)],
            answer: answer);

    // ---------------------------------------------------------------- pure answer

    [Fact]
    public async Task APureAnswerLeavesTheWorkspaceReadyAndWritesNothing()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => AnswerTurn(KoreanContrastAnswer());
        state.Draft = "What is the difference between 은/는 and 이/가?";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.Ready, "a language question is not a plan change");
        state.Receipts.Should().BeEmpty("no receipt for an answer");
        state.LatestReceipt.Should().BeNull();
        state.AlertKey.Should().BeNull("an answer is not an error");
    }

    [Fact]
    public async Task APureAnswerNeverOpensThePlanCanvas()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);
        state.IsCanvasOpen.Should().BeFalse();

        client.OnSubmitTurn = _ => AnswerTurn(KoreanContrastAnswer());
        state.Draft = "How do I say hello?";
        await state.SendDraftAsync();

        state.IsCanvasOpen.Should().BeFalse("nothing about the plan changed");
        state.PlanBadgeCount.Should().Be(0);
    }

    [Fact]
    public async Task APureAnswerAnnouncesNoPlanChange()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => AnswerTurn(KoreanContrastAnswer());
        state.Draft = "What does 밥 mean?";
        await state.SendDraftAsync();

        // The conversation is a role=log region, so the answer is announced by being appended.
        // The plan live region must stay silent.
        state.PoliteAnnouncementKey.Should().NotBe("Coach_StatusUpdated");
        state.PoliteAnnouncementKey.Should().NotBe("Coach_StatusSuggested");
    }

    [Fact]
    public async Task TheStructuredAnswerIsPairedToItsMessageSoItRendersOnce()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        var answer = KoreanContrastAnswer();
        client.OnSubmitTurn = _ => AnswerTurn(answer);
        state.Draft = "Explain 은/는.";
        await state.SendDraftAsync();

        var message = state.Messages.Single(m => m.Kind == CoachMessageKind.PedagogicalAnswer);

        state.AnswerFor(message).Should().BeSameAs(answer,
            "the chat renders blocks for a paired message and plain text otherwise, never both");
        state.LatestAnswer.Should().BeSameAs(answer);
    }

    [Fact]
    public async Task AnUnpairedAnswerMessageFallsBackToItsPlainText()
    {
        // Defensive: a PedagogicalAnswer message with no structured answer on the turn must
        // still render, using the plain text the message already carries.
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        var answer = KoreanContrastAnswer();
        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            messages: [AnswerMessage(answer)],
            answer: null);

        state.Draft = "Explain 은/는.";
        await state.SendDraftAsync();

        var message = state.Messages.Single(m => m.Kind == CoachMessageKind.PedagogicalAnswer);

        state.AnswerFor(message).Should().BeNull();
        message.Text.Should().Be(answer.PlainText, "the fallback text is what the chat will show");
    }

    // ---------------------------------------------------------------- pending preserved

    [Fact]
    public async Task AnExistingPendingSuggestionSurvivesALanguageQuestion()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(
            id, CoachSessionStatus.SuggestionPending, CoachStateMachineTests.Suggestion());
        await state.OpenAsync(CoachPresentation.Overlay, "session-1");
        state.PendingSuggestion.Should().NotBeNull();

        client.OnSubmitTurn = _ => AnswerTurn(KoreanContrastAnswer(), CoachStateMachineTests.Suggestion());
        state.Draft = "Unrelated: what does 밥 mean?";
        await state.SendDraftAsync();

        state.PendingSuggestion.Should().NotBeNull("the offer is still open and answerable");
        state.State.Should().Be(CoachUiState.SuggestionPending);
        state.Receipts.Should().BeEmpty("answering a question never writes the plan");
    }

    [Fact]
    public async Task AnAnswerBesideAPreservedOfferDoesNotReopenTheCanvas()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(
            id, CoachSessionStatus.SuggestionPending, CoachStateMachineTests.Suggestion());
        await state.OpenAsync(CoachPresentation.Overlay, "session-1");
        state.CloseCanvas();

        client.OnSubmitTurn = _ => AnswerTurn(KoreanContrastAnswer(), CoachStateMachineTests.Suggestion());
        state.Draft = "What does 밥 mean?";
        await state.SendDraftAsync();

        state.IsCanvasOpen.Should().BeFalse(
            "the same offer must not force the canvas back open just because a question was asked");
    }

    // ---------------------------------------------------------------- mixed turn

    [Fact]
    public async Task AMixedTurnRendersTheAnswerBeforeTheSuggestionAndWritesNothing()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        var answer = KoreanContrastAnswer();
        var suggestion = CoachStateMachineTests.Suggestion("sug-mixed");

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            sessionStatus: CoachSessionStatus.SuggestionPending,
            suggestion: suggestion,
            messages:
            [
                AnswerMessage(answer),
                new CoachMessageDto
                {
                    MessageId = "m-suggestion",
                    Role = CoachMessageRole.Coach,
                    Kind = CoachMessageKind.Suggestion,
                    Text = suggestion.Rationale,
                    CreatedAtUtc = DateTime.UtcNow,
                    RelatedSuggestionId = suggestion.SuggestionId
                }
            ],
            answer: answer);

        state.Draft = "What does 밥 mean, and can you shorten today?";
        await state.SendDraftAsync();

        // Answer first, suggestion second, in message order.
        var kinds = state.Messages.Select(m => m.Kind).ToList();
        kinds.IndexOf(CoachMessageKind.PedagogicalAnswer)
            .Should().BeLessThan(kinds.IndexOf(CoachMessageKind.Suggestion));

        state.State.Should().Be(CoachUiState.SuggestionPending);
        state.Receipts.Should().BeEmpty("no plan write until Accept");
        state.PlanState!.PlanVersion.Should().Be("v1");
    }

    [Fact]
    public async Task AMixedTurnStillOffersExactlyOneAcceptAndOneReject()
    {
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        var answer = KoreanContrastAnswer();
        client.OnSubmitTurn = _ => AnswerTurn(answer, CoachStateMachineTests.Suggestion("sug-mixed"));
        state.Draft = "Explain 은/는 and shorten today.";
        await state.SendDraftAsync();

        var actions = CoachStateMachine.SuggestionActions(
            state.State,
            hasPendingSuggestion: state.PendingSuggestion is not null,
            hasClarification: false);

        actions.ConsequentialActionCount.Should().Be(2);
    }

    // ---------------------------------------------------------------- typed acceptance

    [Fact]
    public async Task ALexicalQuestionIsNotTreatedAsAcceptanceByTheClient()
    {
        // "좋아요?" asks what the word means. The client does no local acceptance detection at
        // all — it submits the text and renders whatever the server decides — so a lexical
        // question comes back as an answer with the offer still open, and nothing is written.
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(
            id, CoachSessionStatus.SuggestionPending, CoachStateMachineTests.Suggestion());
        await state.OpenAsync(CoachPresentation.Overlay, "session-1");

        client.OnSubmitTurn = _ => AnswerTurn(KoreanContrastAnswer(), CoachStateMachineTests.Suggestion());

        state.Draft = "좋아요?";
        await state.SendDraftAsync();

        state.Receipts.Should().BeEmpty("a lexical question must never accept an offer");
        state.PendingSuggestion.Should().NotBeNull("the offer is still open");
        state.State.Should().Be(CoachUiState.SuggestionPending);
    }

    [Fact]
    public async Task TappingAcceptStillWritesBecauseButtonsAreAuthoritative()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(
            id, CoachSessionStatus.SuggestionPending, CoachStateMachineTests.Suggestion());
        await state.OpenAsync(CoachPresentation.Overlay, "session-1");

        await state.AcceptSuggestionAsync();

        state.State.Should().Be(CoachUiState.PlanUpdated);
        state.Receipts.Should().ContainSingle();
    }

    // ---------------------------------------------------------------- downgraded plan command

    [Fact]
    public async Task ADowngradedPlanCommandArrivesAsAnOfferNotAReceipt()
    {
        // Stage A may downgrade a typed plan command to a suggestion. The UI must present it as
        // an offer awaiting Accept, not as something already applied.
        var (state, client) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            sessionStatus: CoachSessionStatus.SuggestionPending,
            suggestion: CoachStateMachineTests.Suggestion("sug-downgraded"));

        state.Draft = "Make it 10 minutes.";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.SuggestionPending);
        state.Receipts.Should().BeEmpty("nothing is applied until the learner accepts");
    }

    // ---------------------------------------------------------------- no plan

    [Fact]
    public async Task TheCoachIsAvailableWithNoPlanToEdit()
    {
        var (state, client) = Create();
        client.Availability = new CoachAvailabilityResponse
        {
            IsAvailable = true,
            State = CoachAvailabilityState.Available,
            CanEditPlan = false
        };

        var availability = await state.RefreshAvailabilityAsync();

        availability.IsAvailable.Should().BeTrue("language questions do not need a plan");
        state.CanEditPlan.Should().BeFalse("plan-editing affordances withdraw themselves");
    }

    [Fact]
    public async Task OpeningViaADeepLinkStillLearnsThatThereIsNoPlan()
    {
        // The Dashboard entry never ran, so availability has to be read on open or the plan
        // affordances would appear for a learner with no plan.
        var (state, client) = Create();
        client.Availability = new CoachAvailabilityResponse
        {
            IsAvailable = true,
            State = CoachAvailabilityState.Available,
            CanEditPlan = false
        };

        await state.OpenAsync(CoachPresentation.Overlay);

        state.CanEditPlan.Should().BeFalse();
        state.SessionId.Should().NotBeNull("the conversation still opens");
    }

    [Fact]
    public async Task APlanIsEditableByDefault()
    {
        var (state, _) = Create();
        await state.OpenAsync(CoachPresentation.Overlay);

        state.CanEditPlan.Should().BeTrue();
    }

    [Fact]
    public async Task ALanguageQuestionStillWorksWithNoPlan()
    {
        var (state, client) = Create();
        client.Availability = new CoachAvailabilityResponse
        {
            IsAvailable = true,
            State = CoachAvailabilityState.Available,
            CanEditPlan = false
        };
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => AnswerTurn(KoreanContrastAnswer());
        state.Draft = "What does 밥 mean?";
        await state.SendDraftAsync();

        state.State.Should().Be(CoachUiState.Ready);
        state.Messages.Should().Contain(m => m.Kind == CoachMessageKind.PedagogicalAnswer);
        state.CanEditPlan.Should().BeFalse();
    }

    // ---------------------------------------------------------------- history and resume

    [Fact]
    public async Task LearnerMessagesStayVisibleAcrossACloseAndReopen()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id);
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => AnswerTurn(KoreanContrastAnswer());
        state.Draft = "What does 밥 mean?";
        await state.SendDraftAsync();
        state.Messages.Should().NotBeEmpty();

        state.Close();
        await state.OpenAsync(CoachPresentation.Overlay, state.SessionId);

        state.Messages.Should().Contain(m => m.Kind == CoachMessageKind.PedagogicalAnswer);
        state.AnswerFor(state.Messages.First(m => m.Kind == CoachMessageKind.PedagogicalAnswer))
            .Should().NotBeNull("the paired blocks survive within the circuit");
    }

    [Fact]
    public async Task ResumeAfterAReloadKeepsThePrivacyPreservingNotice()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id);

        await state.OpenAsync(CoachPresentation.Overlay, "session-7");

        state.Messages.Should().BeEmpty("the server holds no plaintext transcript");
        state.IsResumedWithoutHistory.Should().BeTrue("the UI says so rather than inventing turns");
    }

    [Fact]
    public async Task SwitchingSessionsDropsTheOldAnswers()
    {
        var (state, client) = Create();
        client.OnGetSession = id => FakeCoachApiClient.Session(id);
        await state.OpenAsync(CoachPresentation.Overlay, "session-a");

        client.OnSubmitTurn = _ => AnswerTurn(KoreanContrastAnswer());
        state.Draft = "What does 밥 mean?";
        await state.SendDraftAsync();
        state.LatestAnswer.Should().NotBeNull();

        await state.OpenAsync(CoachPresentation.Overlay, "session-b");

        state.Messages.Should().BeEmpty();
        state.LatestAnswer.Should().BeNull("another session's answers must not leak in");
    }
}
