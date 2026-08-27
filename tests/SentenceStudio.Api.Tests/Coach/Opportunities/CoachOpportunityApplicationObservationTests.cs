using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// What the running application records, driven through the real conversation stack.
/// </summary>
/// <remarks>
/// <para>
/// The detector tests prove a predicate. These prove the <em>consequence</em>: a real turn goes
/// through <c>CoachConversationService</c> into <c>CoachSessionService</c>, with the real
/// acceptance classifier, the real encrypted message ledger, the real offer grading, and the real
/// mapper — and the assertion is on what reached the recorder. That distinction is the whole
/// point of this file, because the defect this revision corrects was invisible at the predicate
/// level: the predicate did exactly what it said, and what it said was wrong for every successful
/// turn.
/// </para>
/// <para>
/// The recorder is a capturing fake rather than the production one, so the assertion is on the
/// signal the application <em>decided</em> to emit rather than on a row that happened to survive
/// normalization. A wrong decision the recorder later dropped would still be a wrong decision.
/// </para>
/// </remarks>
public class CoachOpportunityApplicationObservationTests
{
    // ------------------------------------------------------ the screenshot, end to end

    /// <summary>
    /// The exact flow from the screenshot, through the application.
    /// </summary>
    /// <remarks>
    /// Sam reads the study duration and offers, in prose, to change it — a question, so
    /// <c>CoachOfferShape</c> grades it <c>PriorCoachQuestion</c>. The learner types a decisive
    /// yes. The turn stops by asking what they meant. Exactly one <c>Product</c> row,
    /// <c>AmbiguousFollowUp</c> / <c>referent_lost_after_offer</c>, with both evidence pointers.
    /// Narrowing the stop-reason guard must not touch this.
    /// </remarks>
    [Fact]
    public async Task TheScreenshotFlowRecordsExactlyOneReferentLossRow()
    {
        var recorder = new RecordingCoachOpportunityRecorder();
        using var harness = new CoachConversationHarness(
            opportunities: recorder, withUnboundAnswerDetector: true);

        var conversationId = await harness.CreateConversationAsync();

        // Turn 1: Sam reads the setting and offers, in prose, to change it.
        harness.Coach.NextResult = Answer(
            "Your daily study time is 10 minutes. Shall I change it to 45 minutes?");

        var offer = await harness.TurnAsync(conversationId, "how long is my daily study time?");
        offer.IsOk.Should().BeTrue(offer.Detail);
        recorder.Signals.Should().BeEmpty("the offer turn itself completed and lost nothing");

        // Turn 2: the learner says yes, and Sam asks what they meant.
        harness.Coach.NextResult = Clarification("I am not sure what you would like me to change.");

        var answered = await harness.TurnAsync(conversationId, "yes");
        answered.IsOk.Should().BeTrue(answered.Detail);
        answered.Value!.Result!.StopReason.Should().Be(CoachStopReason.ClarificationRequested);

        var signal = recorder.Signals.Should().ContainSingle().Subject;

        signal.Kind.Should().Be(CoachOpportunityKind.AmbiguousFollowUp);
        signal.CapabilityCode.Should().Be(CoachOpportunityCapabilityCodes.ReferentLostAfterOffer);
        signal.Disposition.Should().Be(CoachOpportunityDisposition.Product);
        signal.Surface.Should().Be(CoachOpportunitySurface.TurnOutcome);
        signal.OfferLink.Should().Be(CoachOpportunityOfferLink.PriorCoachQuestion);
        signal.StopReason.Should().Be(CoachStopReason.ClarificationRequested);
        signal.Evidence.ConversationId.Should().Be(conversationId);
        signal.Evidence.MessageId.Should().NotBeNullOrWhiteSpace();
        signal.Evidence.OfferMessageId.Should().NotBeNullOrWhiteSpace();

        // The pointers name the two messages a reviewer would read: the learner's "yes", and the
        // coach offer immediately before it.
        var ledger = await harness.LedgerAsync(conversationId);
        var learner = ledger.Single(m => m.Id == signal.Evidence.MessageId);
        var coach = ledger.Single(m => m.Id == signal.Evidence.OfferMessageId);

        learner.Role.Should().Be(CoachMessageRole.Learner);
        coach.Role.Should().Be(CoachMessageRole.Coach);
        coach.Sequence.Should().BeLessThan(learner.Sequence);
    }

    // ------------------------------- the screenshot's other half: a turn that COMPLETED

    /// <summary>
    /// The reproduced flow, in the state the learner was actually in: no plan for today.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the variant that recorded nothing and made the ledger come back empty. The learner
    /// says yes, the model declares the constraint change it was offering, and the application
    /// runs <c>ApplyDeltaAsync</c> — which finds no Today's Plan to edit and returns through
    /// <c>NoPlanToEditAsync</c>: <see cref="CoachStopReason.Completed"/>, a notice saying there is
    /// no plan, and <b>no receipt</b>. Nothing asked a second question, so the stop reason is not
    /// <see cref="CoachStopReason.ClarificationRequested"/> and the original guard declined.
    /// </para>
    /// <para>
    /// Every other conjunct held, which is what makes the declared intent the right discriminant:
    /// the turn said it was going to change a setting, and then changed nothing at all. The
    /// learner's "yes" was dropped exactly as visibly as in the clarification variant.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AYesThatDeclaresAChangeAndChangesNothingRecordsOneReferentLossRow()
    {
        var recorder = new RecordingCoachOpportunityRecorder();
        using var harness = new CoachConversationHarness(
            opportunities: recorder, withUnboundAnswerDetector: true);

        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = Answer(
            "Your daily study time is 20 minutes. Shall I change it to 45 minutes?");

        var offer = await harness.TurnAsync(conversationId, "how long is my daily study time?");
        offer.IsOk.Should().BeTrue(offer.Detail);
        recorder.Signals.Should().BeEmpty("the offer turn itself completed and lost nothing");

        // The condition the reproduced conversation ran under, and the reason Sam answered
        // "there is no plan": nothing was generated for today.
        harness.App.PlanService.SetItems([]);

        harness.Coach.NextResult = DirectChange(45);

        var answered = await harness.TurnAsync(conversationId, "yes");
        answered.IsOk.Should().BeTrue(answered.Detail);

        // The shape that used to be invisible, asserted before the ledger is: this is a completed
        // turn, not a refused one, and it applied and proposed nothing.
        answered.Value!.Result!.StopReason.Should().Be(CoachStopReason.Completed);
        answered.Value.Result.ChangeReceipt.Should().BeNull("there was no plan to change");
        answered.Value.Result.WriteOperation.Should().BeNull("nothing was proposed either");

        var signal = recorder.Signals.Should().ContainSingle(
            "the learner's answer bound to nothing, exactly as in the clarification variant").Subject;

        signal.Kind.Should().Be(CoachOpportunityKind.AmbiguousFollowUp);
        signal.CapabilityCode.Should().Be(CoachOpportunityCapabilityCodes.ReferentLostAfterOffer);
        signal.Disposition.Should().Be(CoachOpportunityDisposition.Product);
        signal.Surface.Should().Be(CoachOpportunitySurface.TurnOutcome);
        signal.OfferLink.Should().Be(CoachOpportunityOfferLink.PriorCoachQuestion);
        signal.StopReason.Should().Be(CoachStopReason.Completed,
            "the row records what actually happened, and what happened is that the turn finished");

        // Both pointers, naming the two messages a reviewer would read.
        signal.Evidence.ConversationId.Should().Be(conversationId);
        signal.Evidence.MessageId.Should().NotBeNullOrWhiteSpace();
        signal.Evidence.OfferMessageId.Should().NotBeNullOrWhiteSpace();

        var ledger = await harness.LedgerAsync(conversationId);
        var learner = ledger.Single(m => m.Id == signal.Evidence.MessageId);
        var coach = ledger.Single(m => m.Id == signal.Evidence.OfferMessageId);

        learner.Role.Should().Be(CoachMessageRole.Learner);
        coach.Role.Should().Be(CoachMessageRole.Coach);
        coach.Sequence.Should().BeLessThan(learner.Sequence);
    }

    /// <summary>
    /// The same completed turn, when the coach was tutoring rather than acting.
    /// </summary>
    /// <remarks>
    /// Identical in every respect the server can observe except the declared intent — same typed
    /// decisive answer, same prior coach question, same completed turn, same absence of a receipt,
    /// a write operation, a pending suggestion and an open proposal. The intent is the only thing
    /// telling the two apart, which is exactly why it is the discriminant and why this test sits
    /// next to the one above.
    /// </remarks>
    [Fact]
    public async Task AYesAfterAPedagogicalQuestionWithNoPlanStillRecordsNothing()
    {
        var recorder = new RecordingCoachOpportunityRecorder();
        using var harness = new CoachConversationHarness(
            opportunities: recorder, withUnboundAnswerDetector: true);

        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = Answer("Shall we start with the reading piece?");
        await harness.TurnAsync(conversationId, "what should I do first?");

        harness.App.PlanService.SetItems([]);

        // The coach answers the language question. Nothing is written because nothing needed to be.
        harness.Coach.NextResult = Teaching(
            "Good. \uD55C\uAD6D\uC5B4 reading first, then the two words you missed yesterday.");

        var taught = await harness.TurnAsync(conversationId, "yes");

        taught.IsOk.Should().BeTrue(taught.Detail);
        taught.Value!.Result!.StopReason.Should().Be(CoachStopReason.Completed);
        taught.Value.Result.ChangeReceipt.Should().BeNull();
        taught.Value.Result.WriteOperation.Should().BeNull();

        recorder.Signals.Should().BeEmpty(
            "a pedagogical answer is the coach doing its job; recording it would attach " +
            "decryptable evidence pointers to a conversation that worked");
    }

    /// <summary>
    /// The learner sees the same turn whether or not the new branch records anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The existing neutrality test covers a refusal path. This one covers the branch this
    /// revision added, and it matters more: the completed no-plan turn is a <em>successful</em>
    /// response the learner reads, and the observation now runs on it where before it declined
    /// immediately. Two harnesses run the identical script, one with a recorder and one without,
    /// and the responses are compared field by field.
    /// </para>
    /// <para>
    /// Asserted rather than argued, because "the observation cannot change the response" is a
    /// property of where the call sits, and where a call sits is exactly the kind of thing a later
    /// refactor moves.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ObservingTheCompletedNoPlanTurnDoesNotChangeTheResponse()
    {
        var recorder = new RecordingCoachOpportunityRecorder();

        using var observed = new CoachConversationHarness(
            opportunities: recorder, withUnboundAnswerDetector: true);
        using var silent = new CoachConversationHarness();

        async Task<Contracts.Coach.CoachTurnResponse> RunAsync(CoachConversationHarness harness)
        {
            var conversationId = await harness.CreateConversationAsync();

            harness.Coach.NextResult = Answer(
                "Your daily study time is 20 minutes. Shall I change it to 45 minutes?");
            await harness.TurnAsync(conversationId, "how long is my daily study time?");

            harness.App.PlanService.SetItems([]);
            harness.Coach.NextResult = DirectChange(45);

            var answered = await harness.TurnAsync(conversationId, "yes");
            answered.IsOk.Should().BeTrue(answered.Detail);
            return answered.Value!.Result!;
        }

        var withCapture = await RunAsync(observed);
        var withoutCapture = await RunAsync(silent);

        withCapture.StopReason.Should().Be(withoutCapture.StopReason);
        withCapture.Status.Should().Be(withoutCapture.Status);
        withCapture.ChangeReceipt.Should().BeEquivalentTo(withoutCapture.ChangeReceipt);
        withCapture.WriteOperation.Should().BeEquivalentTo(withoutCapture.WriteOperation);
        withCapture.PendingSuggestion.Should().BeEquivalentTo(withoutCapture.PendingSuggestion);
        withCapture.ClarifyingQuestion.Should().Be(withoutCapture.ClarifyingQuestion);
        // Kind and text, not the whole record: every message carries a freshly generated id, so
        // comparing those would assert that two independent runs produced the same GUIDs rather
        // than that they said the same thing.
        withCapture.Messages.Select(m => (m.Kind, m.Text))
            .Should().BeEquivalentTo(
                withoutCapture.Messages.Select(m => (m.Kind, m.Text)),
                options => options.WithStrictOrdering(),
                "the learner reads these; capture must be invisible in every one of them");

        recorder.Signals.Should().ContainSingle(
            "the observed run really did record, so the comparison is not vacuous");
    }

    // ------------------------------------------------------ the correction

    /// <summary>
    /// Ordinary successful tutoring, through the application, records nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the defect the previous revision was rejected for. Every conjunct of the detector
    /// holds — typed text, a decisive answer, no open suggestion, no open proposal, no receipt,
    /// no write operation, and a prior coach message that grades as a question — and the turn
    /// simply completed, because the coach used the answer and taught.
    /// </para>
    /// <para>
    /// Under the previous predicate each of these wrote a <c>Product</c> row carrying pointers
    /// into both encrypted messages, for a conversation in which nothing went wrong. The Korean
    /// answers are included because the acceptance classifier treats them as decisive, so they
    /// took exactly the same path.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("yes")]
    [InlineData("ok")]
    [InlineData("네")]
    [InlineData("좋아요")]
    [InlineData("no")]
    public async Task ACompletedTutoringTurnAfterACoachQuestionRecordsNothing(string answer)
    {
        var recorder = new RecordingCoachOpportunityRecorder();
        using var harness = new CoachConversationHarness(
            opportunities: recorder, withUnboundAnswerDetector: true);

        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = Answer("Shall we start with the reading piece?");
        await harness.TurnAsync(conversationId, "what should I do first?");

        // The coach uses the answer and teaches. Nothing is written because nothing needed to be.
        harness.Coach.NextResult = Answer(
            "Good. Reading first, then we will look at the two words you missed yesterday.");

        var taught = await harness.TurnAsync(conversationId, answer);

        taught.IsOk.Should().BeTrue(taught.Detail);
        taught.Value!.Result!.StopReason.Should().Be(CoachStopReason.Completed);
        taught.Value.Result.ChangeReceipt.Should().BeNull("nothing was applied");
        taught.Value.Result.WriteOperation.Should().BeNull("nothing was proposed");

        recorder.Signals.Should().BeEmpty(
            "a coach that asked a pedagogical question, got a decisive answer, and then taught " +
            "did nothing wrong — recording it would attach decryptable evidence pointers to a " +
            "conversation that worked");
    }

    /// <summary>
    /// The two cases told apart inside one conversation.
    /// </summary>
    /// <remarks>
    /// Same learner, same coach, same ledger, four turns, one row. A per-turn test cannot show
    /// that the working turns and the failing turn stay distinguishable in the same history.
    /// </remarks>
    [Fact]
    public async Task OnlyTheClarifyingTurnIsRecordedWithinOneConversation()
    {
        var recorder = new RecordingCoachOpportunityRecorder();
        using var harness = new CoachConversationHarness(
            opportunities: recorder, withUnboundAnswerDetector: true);

        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = Answer("Shall we start with the reading piece?");
        await harness.TurnAsync(conversationId, "what should I do first?");

        harness.Coach.NextResult = Answer("Reading first, then the two words you missed.");
        await harness.TurnAsync(conversationId, "yes");

        harness.Coach.NextResult = Answer("Your daily study time is 10 minutes. Shall I change it to 45?");
        await harness.TurnAsync(conversationId, "how long do I study each day?");

        harness.Coach.NextResult = Clarification("I cannot tell what you would like changed.");
        await harness.TurnAsync(conversationId, "yes");

        recorder.Signals.Should().ContainSingle(
            "three of those four turns worked; only the last one dropped the learner's answer");

        recorder.Signals[0].CapabilityCode
            .Should().Be(CoachOpportunityCapabilityCodes.ReferentLostAfterOffer);
    }

    /// <summary>
    /// A clarification that follows a statement is still not a referent loss.
    /// </summary>
    /// <remarks>
    /// The stop-reason guard is necessary but not sufficient: the offer grade still has to say
    /// the learner was answering something. This keeps the narrowed guard from being read as
    /// "any clarification after a decisive answer".
    /// </remarks>
    [Fact]
    public async Task AClarificationAfterAStatementRecordsNothing()
    {
        var recorder = new RecordingCoachOpportunityRecorder();
        using var harness = new CoachConversationHarness(
            opportunities: recorder, withUnboundAnswerDetector: true);

        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = Answer("Here is your plan for today. Three activities, 10 minutes.");
        await harness.TurnAsync(conversationId, "what is my plan?");

        harness.Coach.NextResult = Clarification("I am not sure what you would like me to change.");
        await harness.TurnAsync(conversationId, "yes");

        recorder.Signals.Should().BeEmpty(
            "an unprompted yes is noise; the prior coach message was a statement, so there was " +
            "no offer to lose");
    }

    // ------------------------------------------------------ the rate-limit boundary

    /// <summary>
    /// A denied run records the one content-free row that says so.
    /// </summary>
    /// <remarks>
    /// The turn never reaches the ordinary observation point, so without an observation at the
    /// early return a learner hitting the cap every day is invisible to the rollup — the ledger
    /// would report zero capacity refusals on a host that was refusing every request.
    /// </remarks>
    [Fact]
    public async Task ADeniedRunRecordsAContentFreeDailyRunLimitRow()
    {
        var recorder = new RecordingCoachOpportunityRecorder();
        using var harness = new CoachConversationHarness(
            opportunities: recorder, maxRunsPerDay: 1);

        var conversationId = await harness.CreateConversationAsync();

        var first = await harness.TurnAsync(conversationId, "first question");
        first.IsOk.Should().BeTrue(first.Detail);
        recorder.Signals.Should().BeEmpty("the first run was inside the cap");

        var denied = await harness.TurnAsync(conversationId, "second question");
        denied.Status.Should().Be(CoachOperationStatus.RateLimited);

        var signal = recorder.Signals.Should().ContainSingle().Subject;

        signal.Kind.Should().Be(CoachOpportunityKind.CapacityOrBudgetRefusal);
        signal.CapabilityCode.Should().Be(CoachOpportunityCapabilityCodes.DailyRunLimit);
        signal.Disposition.Should().Be(CoachOpportunityDisposition.AggregateOnly,
            "how many learners hit the cap is the whole signal; which conversation they were in " +
            "is a dossier");
        signal.StopReason.Should().Be(CoachStopReason.RateLimit);

        // Content-free by construction, asserted rather than assumed.
        signal.Evidence.Should().Be(CoachOpportunityEvidencePointer.None);
        signal.TurnId.Should().BeNull();
        signal.TurnOperationId.Should().BeNull();
        signal.WriteOperationId.Should().BeNull();
        signal.ToolName.Should().BeNull();
        signal.FailureCode.Should().BeNull();
    }

    /// <summary>
    /// The denial the learner sees is identical with capture on and off.
    /// </summary>
    /// <remarks>
    /// The observation runs after the problem result is built, so this is structural rather than
    /// timing-dependent — but the reason capture is allowed near a refusal path at all is that it
    /// cannot change what the learner is told, so it is asserted instead of argued.
    /// </remarks>
    [Fact]
    public async Task ObservingADeniedRunDoesNotChangeTheResponse()
    {
        var recorder = new RecordingCoachOpportunityRecorder();

        using var observed = new CoachConversationHarness(opportunities: recorder, maxRunsPerDay: 1);
        using var silent = new CoachConversationHarness(maxRunsPerDay: 1);

        var observedConversation = await observed.CreateConversationAsync();
        var silentConversation = await silent.CreateConversationAsync();

        await observed.TurnAsync(observedConversation, "first");
        await silent.TurnAsync(silentConversation, "first");

        var observedDenial = await observed.TurnAsync(observedConversation, "second");
        var silentDenial = await silent.TurnAsync(silentConversation, "second");

        observedDenial.Status.Should().Be(silentDenial.Status);
        observedDenial.ProblemType.Should().Be(silentDenial.ProblemType);
        observedDenial.Detail.Should().Be(silentDenial.Detail);

        recorder.Signals.Should().ContainSingle();
    }

    /// <summary>
    /// A broken recorder cannot turn a rate-limit refusal into an unexplained failure.
    /// </summary>
    /// <remarks>
    /// <see cref="ThrowingCoachOpportunityRecorder"/> throws synchronously from
    /// <c>RecordAsync</c>, which is the worst case: it escapes before any await. The learner
    /// still gets the same "you have reached your limit" refusal they would have got on a host
    /// with no ledger at all.
    /// </remarks>
    [Fact]
    public async Task ABrokenRecorderDoesNotReplaceTheRateLimitRefusal()
    {
        var thrower = new ThrowingCoachOpportunityRecorder();
        using var harness = new CoachConversationHarness(
            opportunities: thrower, maxRunsPerDay: 1);

        var conversationId = await harness.CreateConversationAsync();
        await harness.TurnAsync(conversationId, "first");

        var denied = await harness.TurnAsync(conversationId, "second");

        denied.Status.Should().Be(CoachOperationStatus.RateLimited);
        thrower.Calls.Should().Be(1, "the observation was attempted and its failure was contained");
    }

    /// <summary>
    /// A cancelled observation cannot replace a rate-limit refusal either.
    /// </summary>
    /// <remarks>
    /// The narrower half of the guard. A recorder that throws
    /// <see cref="OperationCanceledException"/> used to escape the observation catch on the tool
    /// and write boundaries, so the turn boundary's behaviour is pinned explicitly rather than
    /// left to the shape of a <c>when</c> clause somebody might add back.
    /// </remarks>
    [Fact]
    public async Task ACancellingRecorderDoesNotReplaceTheRateLimitRefusal()
    {
        var canceller = new CancellingCoachOpportunityRecorder();
        using var harness = new CoachConversationHarness(
            opportunities: canceller, maxRunsPerDay: 1);

        var conversationId = await harness.CreateConversationAsync();
        await harness.TurnAsync(conversationId, "first");

        var denied = await harness.TurnAsync(conversationId, "second");

        denied.Status.Should().Be(CoachOperationStatus.RateLimited,
            "a cancelled observation must not turn a clear 'you have reached your limit' into an " +
            "unexplained error");
        canceller.Calls.Should().Be(1);
    }

    // ------------------------------------------------------ helpers

    /// <summary>
    /// An ordinary coach turn that changes nothing and shows its message verbatim.
    /// </summary>
    /// <remarks>
    /// <c>NoChange</c> reaches the default reducer branch, which renders <c>CoachMessage</c> as a
    /// <c>CoachMessageKind.Text</c> row — the shape <c>CoachOfferShape</c> grades. Going through
    /// the real reducer rather than writing history by hand is what makes these application-level
    /// tests rather than detector tests with extra steps.
    /// </remarks>
    private static CoachAgentTurnResult Answer(string message) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.NoChange,
            CoachMessage = message
        },
        AgentSessionJson = """{"turn":1}"""
    };

    /// <summary>
    /// A turn that declares the constraint change the coach had offered.
    /// </summary>
    /// <remarks>
    /// The delta is what makes this a settings change rather than a sentence about one: the real
    /// intent validator requires <c>DirectConstraintChange</c> to carry at least one field, and
    /// the real reducer routes it into <c>ApplyDeltaAsync</c>. With no plan for today that path
    /// completes through <c>NoPlanToEditAsync</c> without a receipt, which is the reproduced shape.
    /// </remarks>
    private static CoachAgentTurnResult DirectChange(int minutes) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = minutes }
        },
        AgentSessionJson = """{"turn":1}"""
    };

    /// <summary>A turn that answers a language question and changes nothing.</summary>
    /// <remarks>
    /// Built as the real intent validator requires — a structured answer with at least one block —
    /// so this goes through the same reducer a real pedagogical turn does. A bare CoachMessage is
    /// rejected as <c>answer_required</c> and would have made the turn ValidationFailed, which is
    /// a different shape from the one this test needs to exclude.
    /// </remarks>
    private static CoachAgentTurnResult Teaching(string message) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.PedagogicalAnswer,
            CoachMessage = string.Empty,
            PedagogicalAnswer = new CoachPedagogicalAnswerIntent
            {
                Topic = CoachAnswerTopic.Vocabulary,
                Blocks =
                [
                    new CoachAnswerBlockIntent
                    {
                        Kind = CoachAnswerBlockKind.Answer,
                        Spans = [new CoachAnswerSpanIntent
                        {
                            Text = message,
                            Language = CoachLanguageRole.Display
                        }]
                    }
                ]
            }
        },
        AgentSessionJson = """{"turn":1}"""
    };

    /// <summary>A turn that stops by asking the learner what they meant.</summary>
    private static CoachAgentTurnResult Clarification(string message) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = message
        },
        AgentSessionJson = """{"turn":1}"""
    };
}
