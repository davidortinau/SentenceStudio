using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Detection;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// The screenshot defect, as a test.
/// </summary>
/// <remarks>
/// <para>
/// The exchange: the learner asks their daily study duration; Sam reads it and reports 10
/// minutes; Sam then offers, in prose, to change it to 45; the learner types <c>yes</c>; Sam
/// replies that it cannot tell what they want changed. Nothing unsafe happened and nothing was
/// written — the learner's reasonable answer to Sam's own question simply did nothing.
/// </para>
/// <para>
/// The positives prove one row is recorded with both pointers. The negatives are the more
/// important half: an out-of-the-blue "yes", a hedged "yes maybe", an open plan suggestion, an
/// open write proposal, and a turn that actually applied something must all record nothing.
/// Without those, this detector would fill the ledger with ordinary conversation and the rollup
/// would be worthless.
/// </para>
/// </remarks>
public class CoachOpportunityReferentLossTests
{
    private const string Owner = "learner-a";
    private const string Conversation = "conv-screenshot";

    [Fact]
    public async Task AYesAfterAProseOfferRecordsExactlyOneRow()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(harness, "Your daily study time is 10 minutes. Shall I change it to 45 minutes?", "yes");

        await using var db = harness.NewContext();
        var detector = harness.NewDetector(harness.NewMessageStore(db));

        var loss = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation,
            CoachTurnInputKind.Text, "yes",
            pendingSuggestionId: null,
            hasOpenWriteProposal: false,
            hasChangeReceipt: false,
            hasWriteOperation: false,
            CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification);

        loss.Should().NotBeNull();
        loss!.Value.OfferLink.Should().Be(CoachOpportunityOfferLink.PriorCoachQuestion);
        loss.Value.Evidence.MessageId.Should().NotBeNullOrWhiteSpace();
        loss.Value.Evidence.OfferMessageId.Should().NotBeNullOrWhiteSpace();
        loss.Value.Evidence.MessageSequence.Should().Be(2);
        loss.Value.Evidence.OfferMessageSequence.Should().Be(1);

        var signal = Api.Coach.Opportunities.Mapping.CoachTurnOutcomeOpportunityMapper.Map(
            loss, CoachStopReason.ClarificationRequested, null, null,
            Conversation, "turn-1", null);

        await harness.Recorder.RecordAsync(signal!.Value);

        var rows = await harness.RowsAsync();
        rows.Should().ContainSingle();

        var row = rows[0];
        row.Kind.Should().Be(CoachOpportunityKind.AmbiguousFollowUp);
        row.CapabilityCode.Should().Be(CoachOpportunityCapabilityCodes.ReferentLostAfterOffer);
        row.Disposition.Should().Be(CoachOpportunityDisposition.Product);
        row.Surface.Should().Be(CoachOpportunitySurface.TurnOutcome);
        row.OfferLink.Should().Be(CoachOpportunityOfferLink.PriorCoachQuestion);
        row.StopReason.Should().Be(CoachStopReason.ClarificationRequested);
        row.ToolName.Should().BeNull("no tool call was made on this turn");
        row.FailureCode.Should().BeNull();
        row.ConversationId.Should().Be(Conversation);
        row.EvidenceMessageId.Should().NotBeNullOrWhiteSpace();
        row.EvidenceOfferMessageId.Should().NotBeNullOrWhiteSpace();
        row.OccurrenceCount.Should().Be(1);
    }

    [Fact]
    public async Task AClarificationKindGradesStructurallyWithoutReadingText()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(
            harness,
            // No question mark and no question word: only the stored Kind makes this an offer.
            "Let me know which one you meant.",
            "yes",
            coachKind: CoachMessageKind.Clarification);

        await using var db = harness.NewContext();
        var detector = harness.NewDetector(harness.NewMessageStore(db));

        var loss = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, "yes",
            null, false, false, false, CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification);

        loss.Should().NotBeNull();
        loss!.Value.OfferLink.Should().Be(CoachOpportunityOfferLink.PriorClarification);
    }

    [Fact]
    public async Task AnOutOfTheBlueYesRecordsNothing()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(harness, "Here is your plan for today. Three activities, 10 minutes.", "yes");

        await using var db = harness.NewContext();
        var detector = harness.NewDetector(harness.NewMessageStore(db));

        var loss = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, "yes",
            null, false, false, false, CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification);

        loss.Should().BeNull(
            "an unprompted yes is noise; requiring a prior offer is what keeps this precise");
    }

    [Fact]
    public async Task AnOpenPlanSuggestionRecordsNothing()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(harness, "Shall I shorten today's plan to 10 minutes?", "yes");

        await using var db = harness.NewContext();
        var detector = harness.NewDetector(harness.NewMessageStore(db));

        var loss = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, "yes",
            pendingSuggestionId: "suggestion-1",
            hasOpenWriteProposal: false, hasChangeReceipt: false, hasWriteOperation: false,
            CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification);

        loss.Should().BeNull("the typed-decision shortcut owns that turn; the answer bound");
    }

    [Fact]
    public async Task AnOpenWriteProposalRecordsNothing()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(harness, "Shall I add that word to your list?", "yes");

        await using var db = harness.NewContext();
        var detector = harness.NewDetector(harness.NewMessageStore(db));

        var loss = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, "yes",
            pendingSuggestionId: null,
            hasOpenWriteProposal: true,
            hasChangeReceipt: false, hasWriteOperation: false,
            CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification);

        loss.Should().BeNull("the learner had somewhere to say yes");
    }

    [Theory]
    [InlineData("yes maybe")]
    [InlineData("yes but not the speaking one")]
    [InlineData("yes?")]
    [InlineData("does 좋아요 mean good")]
    [InlineData("I think yes")]
    [InlineData("")]
    public async Task AnAmbiguousAnswerRecordsNothing(string text)
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(harness, "Shall I change it to 45 minutes?", text);

        await using var db = harness.NewContext();
        var detector = harness.NewDetector(harness.NewMessageStore(db));

        var loss = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, text,
            null, false, false, false, CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification);

        loss.Should().BeNull(
            "the detector reuses the acceptance classifier the write gate already trusts, so the " +
            "two can never disagree about what a clear yes is");
    }

    [Fact]
    public async Task AClearNoIsAlsoALostReferent()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(harness, "Shall I change it to 45 minutes?", "no thanks");

        await using var db = harness.NewContext();
        var detector = harness.NewDetector(harness.NewMessageStore(db));

        var loss = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, "no thanks",
            null, false, false, false, CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification);

        loss.Should().NotBeNull(
            "a clear no that binds to nothing is the same defect as a clear yes that binds to " +
            "nothing — the coach failed to keep track of its own question either way");
    }

    [Fact]
    public async Task ATurnThatAppliedSomethingRecordsNothing()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(harness, "Shall I change it to 45 minutes?", "yes");

        await using var db = harness.NewContext();
        var detector = harness.NewDetector(harness.NewMessageStore(db));

        var applied = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, "yes",
            null, false, hasChangeReceipt: true, hasWriteOperation: false,
            CoachStopReason.Completed, CoachIntentKind.DirectConstraintChange);

        applied.Should().BeNull();

        var proposed = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, "yes",
            null, false, hasChangeReceipt: false, hasWriteOperation: true,
            CoachStopReason.Completed, CoachIntentKind.DirectConstraintChange);

        proposed.Should().BeNull();
    }

    [Theory]
    [InlineData(CoachTurnInputKind.Chip)]
    [InlineData(CoachTurnInputKind.ConstraintAction)]
    public async Task AStructuredInputRecordsNothing(CoachTurnInputKind inputKind)
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(harness, "Shall I change it to 45 minutes?", "yes");

        await using var db = harness.NewContext();
        var detector = harness.NewDetector(harness.NewMessageStore(db));

        var loss = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, inputKind, "yes",
            null, false, false, false, CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification);

        loss.Should().BeNull("a tap is already unambiguous and never loses its referent");
    }

    [Theory]
    [InlineData(CoachStopReason.Failed)]
    [InlineData(CoachStopReason.Timeout)]
    [InlineData(CoachStopReason.RateLimit)]
    [InlineData(CoachStopReason.ValidationFailed)]
    public async Task ATurnThatFailedForAnotherReasonRecordsNothingHere(CoachStopReason reason)
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(harness, "Shall I change it to 45 minutes?", "yes");

        await using var db = harness.NewContext();
        var detector = harness.NewDetector(harness.NewMessageStore(db));

        var loss = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, "yes",
            null, false, false, false, reason, CoachIntentKind.AskClarification);

        loss.Should().BeNull(
            "the learner was told the turn failed; that is a different signal with its own row");
    }

    // ------------------------------------------------------------ ordinary successful tutoring

    /// <summary>
    /// The case that made this detector wrong: a working turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A coach asks a pedagogical question, the learner answers decisively, and the coach teaches.
    /// Nothing is written because nothing needed to be written — this is a conversation, not a
    /// settings change. Every conjunct except the stop reason holds, which is exactly why the
    /// stop reason has to be the one that says no.
    /// </para>
    /// <para>
    /// Recording these was not merely noisy: each row is <c>Product</c> disposition, so it carries
    /// pointers into the learner's encrypted messages and is individually revealable on the
    /// operator surface. The ledger would have accumulated decryptable evidence for conversations
    /// in which nothing went wrong.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("yes")]
    [InlineData("ok")]
    [InlineData("네")]
    [InlineData("좋아요")]
    [InlineData("no")]
    public async Task ACompletedTurnAfterAPedagogicalQuestionRecordsNothing(string answer)
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(harness, "Shall we start with the reading piece?", answer);

        await using var db = harness.NewContext();
        var detector = harness.NewDetector(harness.NewMessageStore(db));

        // The pure predicate first: this is the guard, isolated from history and grading.
        detector.IsUnboundDecisiveAnswer(
                CoachTurnInputKind.Text, answer,
                pendingSuggestionId: null,
                hasOpenWriteProposal: false,
                hasChangeReceipt: false,
                hasWriteOperation: false,
                CoachStopReason.Completed, CoachIntentKind.PedagogicalAnswer)
            .Should().BeFalse(
                "a completed turn is ordinary successful tutoring, and the server has no " +
                "authoritative signal that the answer was ignored");

        var loss = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, answer,
            pendingSuggestionId: null,
            hasOpenWriteProposal: false,
            hasChangeReceipt: false,
            hasWriteOperation: false,
            CoachStopReason.Completed, CoachIntentKind.PedagogicalAnswer);

        loss.Should().BeNull();

        // And the whole turn boundary records nothing: no referent loss, and Completed is Never.
        var signal = Api.Coach.Opportunities.Mapping.CoachTurnOutcomeOpportunityMapper.Map(
            loss, CoachStopReason.Completed, CoachIntentKind.PedagogicalAnswer, null,
            Conversation, "turn-ok", null);

        signal.Should().BeNull("a turn that did what it was asked is not an opportunity");
    }

    /// <summary>
    /// The same working turn, driven all the way through the recorder.
    /// </summary>
    /// <remarks>
    /// The predicate tests above prove the decision; this proves the consequence. A row written
    /// here would be a Product row with evidence pointers for a conversation in which nothing
    /// went wrong, so the assertion is on the table being empty rather than on a return value.
    /// </remarks>
    [Fact]
    public async Task ASuccessfulTutoringTurnLeavesTheLedgerEmpty()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedAsync(harness, "Which one did you mean, the reading piece or the listening one?", "네");

        await using var db = harness.NewContext();
        var detector = harness.NewDetector(harness.NewMessageStore(db));

        var loss = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, "네",
            null, false, false, false, CoachStopReason.Completed, CoachIntentKind.PedagogicalAnswer);

        var signal = Api.Coach.Opportunities.Mapping.CoachTurnOutcomeOpportunityMapper.Map(
            loss, CoachStopReason.Completed, CoachIntentKind.PedagogicalAnswer, null,
            Conversation, "turn-ok", null);

        if (signal is { } value)
        {
            await harness.Recorder.RecordAsync(value);
        }

        (await harness.RowsAsync()).Should().BeEmpty(
            "the coach asked, the learner answered, the coach taught — there is no gap here, and " +
            "a row would carry decryptable pointers into a conversation that worked");
    }

    [Fact]
    public async Task WithNoHistoryStoreNothingIsDetected()
    {
        using var harness = new CoachOpportunityHarness();
        var detector = harness.NewDetector(messages: null);

        var loss = await detector.DetectAsync(
            CoachOwner.ForUser(Owner), Conversation, CoachTurnInputKind.Text, "yes",
            null, false, false, false, CoachStopReason.ClarificationRequested, CoachIntentKind.AskClarification);

        loss.Should().BeNull("the detector fails closed rather than guessing");
    }

    /// <summary>
    /// The offer-shape predicate is the one text-reading step, and it reads the server's own
    /// output. These pin its behaviour so a refactor cannot quietly widen it.
    /// </summary>
    [Theory]
    [InlineData("Shall I change it to 45 minutes?", true)]
    [InlineData("Would you like me to update that?", true)]
    [InlineData("45분으로 바꿀까요?", true)]
    [InlineData("Which one did you mean", true)]
    [InlineData("Here is your plan for today.", false)]
    [InlineData("I updated your plan. Three activities, 10 minutes.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheOfferPredicateReadsOnlyTheCoachsOwnMessage(string? text, bool expected) =>
        CoachOfferShape.EndsWithQuestion(text).Should().Be(expected);

    [Theory]
    [InlineData(CoachMessageKind.Receipt)]
    [InlineData(CoachMessageKind.Notice)]
    public void AStatementIsNeverGradedAsAnOffer(CoachMessageKind kind) =>
        CoachOfferShape.Grade(kind, "Shall I change it to 45 minutes?")
            .Should().Be(CoachOpportunityOfferLink.None,
                "a receipt or a notice is a statement, whatever words it happens to contain");

    private static async Task SeedAsync(
        CoachOpportunityHarness harness,
        string coachText,
        string learnerText,
        CoachMessageKind coachKind = CoachMessageKind.Text)
    {
        await using var db = harness.NewContext();
        var conversations = harness.NewConversationStore(db);
        var messages = harness.NewMessageStore(db);
        var owner = CoachOwner.ForUser(Owner);

        await conversations.CreateAsync(
            owner,
            new CreateCoachConversationRequest(
                "Screenshot", CoachConversationTitleSource.Generated, null, Conversation));

        await messages.AppendAsync(owner, new AppendCoachMessageRequest(
            Conversation, CoachMessageRole.Coach, coachKind,
            new CoachMessagePayload
            {
                Kind = CoachMessagePayloadKind.CoachText,
                Text = coachText,
                CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
            }));

        await messages.AppendAsync(owner, new AppendCoachMessageRequest(
            Conversation, CoachMessageRole.Learner, CoachMessageKind.Text,
            new CoachMessagePayload
            {
                Kind = CoachMessagePayloadKind.LearnerText,
                Text = string.IsNullOrEmpty(learnerText) ? "(empty)" : learnerText,
                CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
            }));
    }
}
