using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Reports;

/// <summary>
/// What a learner's report does, and — more importantly — what it refuses to do.
/// </summary>
/// <remarks>
/// The refusals are the load-bearing half. A report endpoint that answers differently for
/// "somebody else's message" and "no such message" is an existence oracle for message
/// identifiers, and one that pairs a response to the wrong request sends a reviewer to read a
/// conversation the learner never complained about.
/// </remarks>
public class CoachResponseReportServiceTests
{
    private static CoachResponseReportRequest Request(
        CoachResponseReportReason reason = CoachResponseReportReason.DidNotAnswer) =>
        new() { Reason = reason };

    // ---------------------------------------------------------------- the owned path

    [Fact]
    public async Task AnOwnedResponseIsReportedAndPairedToItsOwnRequest()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        var result = await service.ReportAsync(
            turn.ConversationId, turn.ResponseMessageId, Request(CoachResponseReportReason.Confusing));

        result.IsOk.Should().BeTrue();
        result.Value!.State.Should().Be(CoachResponseReportState.Recorded);
        result.Value.MessageId.Should().Be(turn.ResponseMessageId);
        result.Value.Reason.Should().Be(CoachResponseReportReason.Confusing);

        var rows = await harness.RowsAsync();
        var row = rows.Should().ContainSingle().Subject;

        row.UserProfileId.Should().Be("learner-a");
        row.ConversationId.Should().Be(turn.ConversationId);
        row.CoachMessageId.Should().Be(turn.ResponseMessageId);
        row.RequestMessageId.Should().Be(turn.LearnerMessageId,
            "the request is derived from the ledger's own turn correlation, not from the caller");
        row.RequestMessageSequence.Should().BeLessThan(row.CoachMessageSequence);
        row.TurnOperationId.Should().Be(turn.OperationId);
        row.SchemaVersion.Should().Be(CoachResponseReportLimits.SchemaVersion);
    }

    [Theory]
    [InlineData(CoachResponseReportReason.DidNotAnswer)]
    [InlineData(CoachResponseReportReason.IncorrectOrMisleading)]
    [InlineData(CoachResponseReportReason.ExpectedAppAction)]
    [InlineData(CoachResponseReportReason.Confusing)]
    [InlineData(CoachResponseReportReason.Other)]
    public async Task EveryReasonIsAcceptedAndStoredAsItself(CoachResponseReportReason reason)
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        var result = await service.ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request(reason));

        result.IsOk.Should().BeTrue();
        (await harness.RowsAsync()).Should().ContainSingle().Which.Reason.Should().Be(reason);
    }

    [Fact]
    public async Task AReasonThisServerDoesNotKnowIsRefused()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        var result = await service.ReportAsync(
            turn.ConversationId,
            turn.ResponseMessageId,
            new CoachResponseReportRequest { Reason = (CoachResponseReportReason)99 });

        result.Status.Should().Be(CoachOperationStatus.InvalidInput);
        (await harness.RowsAsync()).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- idempotency

    [Fact]
    public async Task ASecondReportOfTheSameResponseChangesNothing()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        await service.ReportAsync(turn.ConversationId, turn.ResponseMessageId,
            Request(CoachResponseReportReason.Confusing));

        var repeat = await service.ReportAsync(turn.ConversationId, turn.ResponseMessageId,
            Request(CoachResponseReportReason.Other));

        repeat.IsOk.Should().BeTrue();
        repeat.Value!.State.Should().Be(CoachResponseReportState.AlreadyReported);
        repeat.Value.Reason.Should().Be(CoachResponseReportReason.Confusing,
            "the first reason won, and the answer says which one that was");

        (await harness.RowsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task ARepeatOnAFollowingDayIsStillTheSameReport()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        await service.ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());

        // The ledger's dedup bucket is a UTC day; the report's identity is not.
        harness.Time.Advance(TimeSpan.FromDays(3));

        var repeat = await service.ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());

        repeat.Value!.State.Should().Be(CoachResponseReportState.AlreadyReported);
        (await harness.RowsAsync()).Should().ContainSingle(
            "a response is reported once, for the life of the row, not once per day");
    }

    [Fact]
    public async Task TwoResponsesInOneConversationAreReportedIndependently()
    {
        using var harness = new CoachResponseReportHarness();
        var first = await harness.SeedTurnAsync(operationId: "op-1");

        await using (var setup = harness.NewContext())
        {
            var messages = harness.NewMessageStore(setup);
            var owner = CoachOwner.ForUser("learner-a");

            await messages.AppendAsync(owner, new AppendCoachMessageRequest(
                first.ConversationId, CoachMessageRole.Learner, CoachMessageKind.Text,
                new CoachMessagePayload
                {
                    Kind = CoachMessagePayloadKind.LearnerText,
                    Text = "And 이/가?",
                    CreatedAtUtc = new DateTime(2026, 8, 20, 12, 5, 0, DateTimeKind.Utc)
                },
                "op-2"));

            var second = await messages.AppendAsync(owner, new AppendCoachMessageRequest(
                first.ConversationId, CoachMessageRole.Coach, CoachMessageKind.Text,
                new CoachMessagePayload
                {
                    Kind = CoachMessagePayloadKind.CoachText,
                    Text = "이/가 marks the subject.",
                    CreatedAtUtc = new DateTime(2026, 8, 20, 12, 5, 1, DateTimeKind.Utc)
                },
                "op-2"));

            await using var db = harness.NewContext();
            var service = harness.NewService(db);

            await service.ReportAsync(first.ConversationId, first.ResponseMessageId,
                Request(CoachResponseReportReason.Confusing));
            await service.ReportAsync(first.ConversationId, second.Message!.Id,
                Request(CoachResponseReportReason.Confusing));
        }

        (await harness.RowsAsync()).Should().HaveCount(2,
            "one bad answer does not condemn the next one, and the ledger's day bucket must not collapse two responses into one report");
    }

    // ---------------------------------------------------------------- indistinguishability

    [Fact]
    public async Task AnUnknownResponseAndAForeignResponseAnswerIdentically()
    {
        using var harness = new CoachResponseReportHarness();
        var mine = await harness.SeedTurnAsync(owner: "learner-a", conversationId: "c-1");
        var theirs = await harness.SeedTurnAsync(
            owner: "learner-b", conversationId: "c-2", operationId: "op-2");

        await using var db = harness.NewContext();
        var service = harness.NewService(db, "learner-a");

        var unknown = await service.ReportAsync("c-1", "no-such-message", Request());
        var foreign = await service.ReportAsync("c-2", theirs.ResponseMessageId, Request());

        unknown.Status.Should().Be(foreign.Status);
        unknown.ProblemType.Should().Be(foreign.ProblemType);
        unknown.Detail.Should().Be(foreign.Detail,
            "a distinct answer for 'somebody else owns this' would confirm the id exists");

        unknown.Status.Should().Be(CoachOperationStatus.SessionNotFound);

        (await harness.RowsAsync()).Should().BeEmpty();
        _ = mine;
    }

    [Fact]
    public async Task AResponseInAnotherOfTheLearnersConversationsIsNotReportable()
    {
        using var harness = new CoachResponseReportHarness();
        await harness.SeedTurnAsync(conversationId: "c-1", operationId: "op-1");
        var other = await harness.SeedTurnAsync(conversationId: "c-2", operationId: "op-2");

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        // The right message, named under the wrong conversation.
        var result = await service.ReportAsync("c-1", other.ResponseMessageId, Request());

        result.Status.Should().Be(CoachOperationStatus.SessionNotFound);
        (await harness.RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnauthenticatedCallerLearnsNothingAboutAnyConversation()
    {
        using var harness = new CoachResponseReportHarness(userProfileId: null);
        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        var result = await service.ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());

        result.Status.Should().Be(CoachOperationStatus.SessionNotFound,
            "no trusted owner reads exactly like an unknown conversation");
        (await harness.RowsAsync()).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- pairing

    [Fact]
    public async Task ALearnersOwnMessageCannotBeReported()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        var result = await service.ReportAsync(turn.ConversationId, turn.LearnerMessageId, Request());

        result.Status.Should().Be(CoachOperationStatus.InvalidInput);
        (await harness.RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AResponseWithNoTurnCorrelationIsRefusedRatherThanGuessedAt()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync(correlate: false);

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        var result = await service.ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());

        result.Status.Should().Be(CoachOperationStatus.InvalidInput);
        result.Detail.Should().Contain("could not be matched",
            "the learner is told the truth rather than shown a success that filed nothing");

        (await harness.RowsAsync()).Should().BeEmpty(
            "'the learner message just above it' would be right most of the time and wrong exactly when it matters");
    }

    [Fact]
    public async Task AResponseWhoseTurnHasNoLearnerMessageIsRefused()
    {
        using var harness = new CoachResponseReportHarness();

        await using (var setup = harness.NewContext())
        {
            var conversations = harness.NewConversationStore(setup);
            var messages = harness.NewMessageStore(setup);
            var owner = CoachOwner.ForUser("learner-a");

            await conversations.CreateAsync(owner, new CreateCoachConversationRequest(
                Title: "Chip", TitleSource: CoachConversationTitleSource.Generated,
                TargetLanguageCode: "ko", ConversationId: "c-chip"));

            // A tapped chip writes no learner message, so the turn has a response and nothing to
            // pair it with. That is a real state, and it must fail closed rather than reach back
            // into an earlier exchange.
            var response = await messages.AppendAsync(owner, new AppendCoachMessageRequest(
                "c-chip", CoachMessageRole.Coach, CoachMessageKind.Text,
                new CoachMessagePayload
                {
                    Kind = CoachMessagePayloadKind.CoachText,
                    Text = "Shortened today's plan.",
                    CreatedAtUtc = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc)
                },
                "op-chip"));

            await using var db = harness.NewContext();
            var service = harness.NewService(db);

            var result = await service.ReportAsync("c-chip", response.Message!.Id, Request());

            result.Status.Should().Be(CoachOperationStatus.InvalidInput);
        }

        (await harness.RowsAsync()).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the read

    [Fact]
    public async Task TheReportedListReturnsOnlyThisLearnersOwnReports()
    {
        using var harness = new CoachResponseReportHarness();
        var mine = await harness.SeedTurnAsync(owner: "learner-a", conversationId: "c-1");
        var theirs = await harness.SeedTurnAsync(
            owner: "learner-b", conversationId: "c-2", operationId: "op-2");

        await using var db = harness.NewContext();

        await harness.NewService(db, "learner-a")
            .ReportAsync(mine.ConversationId, mine.ResponseMessageId, Request());
        await harness.NewService(db, "learner-b")
            .ReportAsync(theirs.ConversationId, theirs.ResponseMessageId, Request());

        var listed = await harness.NewService(db, "learner-a").ListReportedAsync("c-1");

        listed.IsOk.Should().BeTrue();
        listed.Value!.MessageIds.Should().ContainSingle().Which.Should().Be(mine.ResponseMessageId);
    }

    [Fact]
    public async Task AnUnknownConversationAndAnEmptyOneAnswerTheSameList()
    {
        using var harness = new CoachResponseReportHarness();
        await harness.SeedTurnAsync(conversationId: "c-1");

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        var real = await service.ListReportedAsync("c-1");
        var invented = await service.ListReportedAsync("c-does-not-exist");

        real.Value!.MessageIds.Should().BeEmpty();
        invented.Value!.MessageIds.Should().BeEmpty();
        real.Status.Should().Be(invented.Status,
            "the response shape carries no 'found' bit for a caller to read");
    }

    [Fact]
    public async Task AForeignConversationAnswersAnEmptyListRatherThanARefusal()
    {
        using var harness = new CoachResponseReportHarness();
        var theirs = await harness.SeedTurnAsync(owner: "learner-b", conversationId: "c-2");

        await using var db = harness.NewContext();

        await harness.NewService(db, "learner-b")
            .ReportAsync(theirs.ConversationId, theirs.ResponseMessageId, Request());

        var listed = await harness.NewService(db, "learner-a").ListReportedAsync("c-2");

        listed.IsOk.Should().BeTrue();
        listed.Value!.MessageIds.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the switches

    [Fact]
    public async Task ReportingOffAnswersUnavailableAndWritesNothing()
    {
        using var harness = new CoachResponseReportHarness(reportsEnabled: false);
        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        var reported = await service.ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());
        var listed = await service.ListReportedAsync(turn.ConversationId);

        reported.Status.Should().Be(CoachOperationStatus.Unavailable);
        listed.Status.Should().Be(CoachOperationStatus.Unavailable);
        (await harness.RowsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// The switch that matters. A deployment that turned automatic capture off said "stop
    /// inferring problems from my turns"; it did not say "discard the reports my learners
    /// deliberately filed", and the learner was told the report goes somewhere a person looks.
    /// </summary>
    [Fact]
    public async Task AutomaticCaptureOffDoesNotSuppressADeliberateReport()
    {
        using var harness = new CoachResponseReportHarness(
            reportsEnabled: true, opportunitiesEnabled: false);

        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        var result = await service.ReportAsync(turn.ConversationId, turn.ResponseMessageId,
            Request(CoachResponseReportReason.IncorrectOrMisleading));

        result.IsOk.Should().BeTrue();
        (await harness.RowsAsync()).Should().ContainSingle();

        var ledger = await harness.OpportunitiesAsync();
        ledger.Should().ContainSingle("an explicitly enabled report still raises its product signal")
            .Which.Kind.Should().Be(CoachOpportunityKind.UserReportedResponse);
    }

    [Fact]
    public async Task ReportsOffMeansNoLedgerRowEitherEvenWithCaptureOn()
    {
        using var harness = new CoachResponseReportHarness(
            reportsEnabled: false, opportunitiesEnabled: true);

        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        await service.ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());

        (await harness.OpportunitiesAsync()).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the ledger row

    /// <summary>
    /// A hostile or corrupt stored outcome must not silently break the link between the report
    /// and the ledger row it raised.
    /// </summary>
    /// <remarks>
    /// The failure this pins is quiet by nature. An undefined stop reason reaching the fingerprint
    /// on one side and being nulled on the other looks like nothing at all: the report saves, the
    /// ledger row saves, and only the back-reference is missing — which is a documented normal
    /// state, so nobody would go looking.
    /// </remarks>
    [Theory]
    [InlineData("\"NotAStopReason\"")]
    [InlineData("999")]
    [InlineData("\"999\"")]
    [InlineData("\"1,2\"")]
    public void AnUndefinedStopReasonIsReadAsAbsent(string json)
    {
        var outcome = $$"""{"stopReason": {{json}}}""";

        CoachResponseReportService.ReadStopReason(outcome).Should().BeNull(
            "a value outside the closed set is not a stop reason, and storing one would break " +
            "both the column's contract and the fingerprint the report is linked by");
    }

    [Fact]
    public void ADefinedStopReasonIsReadInEitherWireForm()
    {
        CoachResponseReportService.ReadStopReason("""{"stopReason": "ClarificationRequested"}""")
            .Should().Be(CoachStopReason.ClarificationRequested);

        CoachResponseReportService.ReadStopReason($$"""{"stopReason": {{(int)CoachStopReason.ToolFailure}}}""")
            .Should().Be(CoachStopReason.ToolFailure);
    }

    [Fact]
    public void AnUnreadableOutcomeIsAbsentRatherThanAFailure()
    {
        CoachResponseReportService.ReadStopReason("not json at all").Should().BeNull();
        CoachResponseReportService.ReadStopReason("{}").Should().BeNull();
        CoachResponseReportService.ReadStopReason(null).Should().BeNull();
    }

    [Fact]
    public async Task TheLedgerRowIsProductReviewableAndCarriesBothMessagePointers()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        await service.ReportAsync(turn.ConversationId, turn.ResponseMessageId,
            Request(CoachResponseReportReason.ExpectedAppAction));

        var row = (await harness.OpportunitiesAsync()).Should().ContainSingle().Subject;

        row.Kind.Should().Be(CoachOpportunityKind.UserReportedResponse);
        row.Disposition.Should().Be(CoachOpportunityDisposition.Product,
            "a learner spent an action to say this; collapsing it into a counter would throw away the one signal that arrived with intent");
        row.CapabilityCode.Should().Be(CoachOpportunityCapabilityCodes.LearnerReportedExpectedAppAction);
        row.ConversationId.Should().Be(turn.ConversationId);
        row.TurnOperationId.Should().Be(turn.OperationId);
        row.EvidenceMessageId.Should().Be(turn.LearnerMessageId);
        row.EvidenceOfferMessageId.Should().Be(turn.ResponseMessageId);
        row.EvidenceMessageSequence.Should().NotBeNull();
        row.EvidenceOfferMessageSequence.Should().NotBeNull();
    }

    [Fact]
    public async Task TheReportPointsAtTheLedgerRowItRaised()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        await service.ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());

        var report = (await harness.RowsAsync()).Should().ContainSingle().Subject;
        var ledger = (await harness.OpportunitiesAsync()).Should().ContainSingle().Subject;

        report.OpportunityId.Should().Be(ledger.Id);
    }

    [Fact]
    public async Task TwoLearnersReportingTheSameReasonRollUpTogether()
    {
        using var harness = new CoachResponseReportHarness();
        var mine = await harness.SeedTurnAsync(owner: "learner-a", conversationId: "c-1");
        var theirs = await harness.SeedTurnAsync(
            owner: "learner-b", conversationId: "c-2", operationId: "op-2");

        await using var db = harness.NewContext();

        await harness.NewService(db, "learner-a").ReportAsync(
            mine.ConversationId, mine.ResponseMessageId, Request(CoachResponseReportReason.Confusing));
        await harness.NewService(db, "learner-b").ReportAsync(
            theirs.ConversationId, theirs.ResponseMessageId, Request(CoachResponseReportReason.Confusing));

        var ledger = await harness.OpportunitiesAsync();

        ledger.Should().HaveCount(2, "one row per learner, which is what the dedup key says");
        ledger.Select(row => row.Fingerprint).Distinct().Should().ContainSingle(
            "the fingerprint answers 'which problem is this', so the cross-learner rollup still groups them");
    }

    // ---------------------------------------------------------------- erasure and retention

    [Fact]
    public async Task ErasureRemovesEveryReportTheLearnerFiledAndNobodyElses()
    {
        using var harness = new CoachResponseReportHarness();
        var mine = await harness.SeedTurnAsync(owner: "learner-a", conversationId: "c-1");
        var theirs = await harness.SeedTurnAsync(
            owner: "learner-b", conversationId: "c-2", operationId: "op-2");

        await using var db = harness.NewContext();

        await harness.NewService(db, "learner-a").ReportAsync(
            mine.ConversationId, mine.ResponseMessageId, Request());
        await harness.NewService(db, "learner-b").ReportAsync(
            theirs.ConversationId, theirs.ResponseMessageId, Request());

        var deleted = await harness.NewDeletionContributor(db)
            .DeleteAllAsync(CoachOwner.ForUser("learner-a"));

        deleted.Should().Be(1);

        var remaining = await harness.RowsAsync();
        remaining.Should().ContainSingle().Which.UserProfileId.Should().Be("learner-b");
    }

    [Fact]
    public async Task AnEmptyOwnerDeletesNothing()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        await harness.NewService(db).ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());

        var deleted = await harness.NewDeletionContributor(db).DeleteAllAsync(default);

        deleted.Should().Be(0, "'no owner' can only ever mean 'delete nothing'");
        (await harness.RowsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task RetentionAgesOutReportsPastTheWindow()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        await harness.NewService(db).ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());

        harness.Time.Advance(TimeSpan.FromDays(181));

        var swept = await harness.NewRetentionSweep(db).RunAsync();

        swept.RowsDeleted.Should().Be(1);
        (await harness.RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RetentionLeavesAFreshReportAlone()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        await harness.NewService(db).ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());

        harness.Time.Advance(TimeSpan.FromDays(30));

        var swept = await harness.NewRetentionSweep(db).RunAsync();

        swept.IsEmpty.Should().BeTrue();
        (await harness.RowsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task ADisabledSweepRemovesNothing()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        await using var db = harness.NewContext();
        await harness.NewService(db).ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());

        harness.Options.Set(new CoachResponseReportOptions
        {
            Enabled = true,
            RetentionSweepEnabled = false
        });

        var swept = await harness.NewRetentionSweep(db).RunAsync();

        swept.IsEmpty.Should().BeTrue();
        (await harness.RowsAsync()).Should().ContainSingle();
    }
}
