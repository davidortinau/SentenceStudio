using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Endpoints;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Security.DataProtection;
using SentenceStudio.Api.Tests.Coach.Opportunities;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Reports;

/// <summary>
/// A notice the learner reads as an answer can be reported, and the report names the exact request
/// it answered.
/// </summary>
/// <remarks>
/// <para>
/// The gap this closes was found on a real account. The learner asked Sam to change today's plan,
/// Sam answered <i>"There is no plan for today yet"</i>, and that response — the one worth
/// complaining about — carried no flag, because the client excluded every notice on the theory
/// that a notice is the machinery describing itself.
/// </para>
/// <para>
/// The client half of that fix is a render decision and is tested where it renders. What is tested
/// here is the half that has to be true regardless of which client asked: a notice pairs to its own
/// learner request through the turn correlation the ledger stamped, a receipt does not become
/// reportable along with it, and a notice the server wrote outside a learner's turn still cannot be
/// filed. None of that may rest on adjacency — "the learner message just above it" is right most of
/// the time and wrong exactly when a reviewer needs it to be right.
/// </para>
/// </remarks>
public class CoachNoticeReportServiceTests
{
    private static CoachResponseReportRequest Request(
        CoachResponseReportReason reason = CoachResponseReportReason.DidNotAnswer) =>
        new() { Reason = reason };

    private static CoachMessagePayload Payload(string text) => new()
    {
        Kind = CoachMessagePayloadKind.CoachText,
        Text = text,
        CreatedAtUtc = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc)
    };

    // ---------------------------------------------------------------- the notice reports

    /// <summary>
    /// The case from the Canvas run, end to end: a notice answering a learner turn is accepted and
    /// filed against that learner's own request.
    /// </summary>
    [Fact]
    public async Task ANoticeAnsweringALearnerTurnIsReportedAndPairedToThatTurn()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync(responseKind: CoachMessageKind.Notice);

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        var result = await service.ReportAsync(
            turn.ConversationId, turn.ResponseMessageId, Request(CoachResponseReportReason.ExpectedAppAction));

        result.IsOk.Should().BeTrue(
            "a notice that answers a learner's request is the coach answering, however short");
        result.Value!.State.Should().Be(CoachResponseReportState.Recorded);

        var row = (await harness.RowsAsync()).Should().ContainSingle().Subject;

        row.CoachMessageId.Should().Be(turn.ResponseMessageId, "the exact notice");
        row.RequestMessageId.Should().Be(turn.LearnerMessageId, "the exact request it answered");
        row.TurnOperationId.Should().Be(turn.OperationId);
        row.ResponseKind.Should().Be(CoachMessageKind.Notice,
            "a reviewer reading the queue needs to know this was a notice and not prose");
        row.Reason.Should().Be(CoachResponseReportReason.ExpectedAppAction);
    }

    /// <summary>
    /// Every reason can be filed against a notice, not just the one that fits a refusal.
    /// </summary>
    [Theory]
    [InlineData(CoachResponseReportReason.DidNotAnswer)]
    [InlineData(CoachResponseReportReason.IncorrectOrMisleading)]
    [InlineData(CoachResponseReportReason.ExpectedAppAction)]
    [InlineData(CoachResponseReportReason.Confusing)]
    [InlineData(CoachResponseReportReason.Other)]
    public async Task EveryReasonIsAcceptedAgainstANotice(CoachResponseReportReason reason)
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync(responseKind: CoachMessageKind.Notice);

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        var result = await service.ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request(reason));

        result.IsOk.Should().BeTrue();
        (await harness.RowsAsync()).Should().ContainSingle().Which.Reason.Should().Be(reason);
    }

    /// <summary>
    /// Reporting a notice twice files one row and replays the first answer.
    /// </summary>
    [Fact]
    public async Task ReportingTheSameNoticeTwiceIsIdempotent()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync(responseKind: CoachMessageKind.Notice);

        await using var db = harness.NewContext();
        var service = harness.NewService(db);

        var first = await service.ReportAsync(
            turn.ConversationId, turn.ResponseMessageId, Request(CoachResponseReportReason.Confusing));
        var second = await service.ReportAsync(
            turn.ConversationId, turn.ResponseMessageId, Request(CoachResponseReportReason.Other));

        first.Value!.State.Should().Be(CoachResponseReportState.Recorded);
        second.IsOk.Should().BeTrue();
        second.Value!.State.Should().Be(CoachResponseReportState.AlreadyReported);
        second.Value.Reason.Should().Be(CoachResponseReportReason.Confusing,
            "the replay is the report that was actually filed, not the one just attempted");

        (await harness.RowsAsync()).Should().ContainSingle(
            "a second press is the learner checking it worked, not a second complaint");
    }

    /// <summary>
    /// Two reports of the same notice arriving together still leave one row.
    /// </summary>
    /// <remarks>
    /// A double-tap on a slow connection is the ordinary way this happens. SQLite here; the
    /// PostgreSQL suite runs the same race where the unique index is the database's own.
    /// </remarks>
    [Fact]
    public async Task ConcurrentReportsOfTheSameNoticeLeaveOneRow()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync(responseKind: CoachMessageKind.Notice);

        await using var dbA = harness.NewContext();
        await using var dbB = harness.NewContext();

        var results = await Task.WhenAll(
            harness.NewService(dbA).ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request()),
            harness.NewService(dbB).ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request()));

        results.Should().AllSatisfy(r => r.IsOk.Should().BeTrue());
        (await harness.RowsAsync()).Should().ContainSingle();
    }

    /// <summary>
    /// The filed row survives being read back on a fresh context — it is a row, not a cache entry.
    /// </summary>
    [Fact]
    public async Task AReportedNoticeReadsBackOnAFreshContext()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync(responseKind: CoachMessageKind.Notice);

        await using (var db = harness.NewContext())
        {
            var result = await harness.NewService(db)
                .ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());
            result.IsOk.Should().BeTrue();
        }

        await using var reread = harness.NewContext();
        var row = await reread.CoachResponseReports.AsNoTracking().SingleAsync();

        row.CoachMessageId.Should().Be(turn.ResponseMessageId);
        row.RequestMessageId.Should().Be(turn.LearnerMessageId);
        row.ResponseKind.Should().Be(CoachMessageKind.Notice);
    }

    // ---------------------------------------------------------------- pairing, not adjacency

    /// <summary>
    /// A notice reported after a later, unrelated exchange still names the request from its own
    /// turn — not the message that happens to sit above it once the transcript has moved on.
    /// </summary>
    /// <remarks>
    /// This is the test that would pass under an adjacency rule only by luck. The notice is
    /// reported after two more messages have been appended, so "the nearest learner message" and
    /// "the learner message of this turn" are still the same row — which is why the second half of
    /// the assertion matters: the row named must be the one correlated by operation, and the later
    /// learner message must not be it.
    /// </remarks>
    [Fact]
    public async Task ALaterExchangeDoesNotBecomeTheNoticesRequest()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync(responseKind: CoachMessageKind.Notice);

        string laterLearnerId;

        await using (var setup = harness.NewContext())
        {
            var messages = harness.NewMessageStore(setup);
            var owner = CoachOwner.ForUser("learner-a");

            var later = await messages.AppendAsync(owner, new AppendCoachMessageRequest(
                turn.ConversationId, CoachMessageRole.Learner, CoachMessageKind.Text,
                Payload("Then what should I study?"), "op-2"));

            await messages.AppendAsync(owner, new AppendCoachMessageRequest(
                turn.ConversationId, CoachMessageRole.Coach, CoachMessageKind.Text,
                Payload("Start with 은/는."), "op-2"));

            later.Status.Should().Be(CoachHistoryStatus.Success);
            laterLearnerId = later.Message!.Id;
        }

        await using var db = harness.NewContext();
        var result = await harness.NewService(db)
            .ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());

        result.IsOk.Should().BeTrue();

        var row = (await harness.RowsAsync()).Should().ContainSingle().Subject;
        row.RequestMessageId.Should().Be(turn.LearnerMessageId);
        row.RequestMessageId.Should().NotBe(laterLearnerId);
        row.RequestMessageSequence.Should().BeLessThan(row.CoachMessageSequence);
    }

    /// <summary>
    /// When a turn appends a notice after other coach messages, the notice is reported as itself
    /// and still pairs to the one learner message that opened the turn.
    /// </summary>
    [Fact]
    public async Task ANoticeLaterInItsOwnTurnStillPairsToThatTurnsRequest()
    {
        using var harness = new CoachResponseReportHarness();

        string conversationId = "c-multi";
        string learnerId;
        string noticeId;

        await using (var setup = harness.NewContext())
        {
            var conversations = harness.NewConversationStore(setup);
            var messages = harness.NewMessageStore(setup);
            var owner = CoachOwner.ForUser("learner-a");

            await conversations.CreateAsync(owner, new CreateCoachConversationRequest(
                Title: "Plan", TitleSource: CoachConversationTitleSource.Generated,
                TargetLanguageCode: "ko", ConversationId: conversationId));

            var learner = await messages.AppendAsync(owner, new AppendCoachMessageRequest(
                conversationId, CoachMessageRole.Learner, CoachMessageKind.Text,
                Payload("Make today's plan shorter."), "op-1"));

            await messages.AppendAsync(owner, new AppendCoachMessageRequest(
                conversationId, CoachMessageRole.Coach, CoachMessageKind.Text,
                Payload("Checking today's plan."), "op-1"));

            var notice = await messages.AppendAsync(owner, new AppendCoachMessageRequest(
                conversationId, CoachMessageRole.Coach, CoachMessageKind.Notice,
                Payload("There is no plan for today yet, so there is nothing to change."), "op-1"));

            learnerId = learner.Message!.Id;
            noticeId = notice.Message!.Id;
        }

        await using var db = harness.NewContext();
        var result = await harness.NewService(db).ReportAsync(conversationId, noticeId, Request());

        result.IsOk.Should().BeTrue();

        var row = (await harness.RowsAsync()).Should().ContainSingle().Subject;
        row.CoachMessageId.Should().Be(noticeId, "the learner flagged the notice, not the one before it");
        row.RequestMessageId.Should().Be(learnerId);
    }

    // ---------------------------------------------------------------- what stays unreportable

    /// <summary>
    /// A receipt is refused by the server, not merely hidden by the client.
    /// </summary>
    /// <remarks>
    /// The client withholds the flag, so this refusal is only ever reached by a request that did
    /// not come from the client. That is exactly why it has to exist: without it, the exclusion
    /// would be a rendering preference rather than a rule.
    /// </remarks>
    [Fact]
    public async Task AReceiptIsRefusedEvenWhenItPairsCleanly()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync(responseKind: CoachMessageKind.Receipt);

        await using var db = harness.NewContext();
        var result = await harness.NewService(db)
            .ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());

        result.Status.Should().Be(CoachOperationStatus.InvalidInput);
        result.Detail.Should().Contain("record of a change",
            "the learner is told which surface owns the complaint, not that the message is missing");

        (await harness.RowsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// The kind rule the client and the server share admits everything the coach says and refuses
    /// the receipt, so the two surfaces cannot drift apart.
    /// </summary>
    [Theory]
    [InlineData(CoachMessageKind.Text, true)]
    [InlineData(CoachMessageKind.Clarification, true)]
    [InlineData(CoachMessageKind.Suggestion, true)]
    [InlineData(CoachMessageKind.Notice, true)]
    [InlineData(CoachMessageKind.PedagogicalAnswer, true)]
    [InlineData(CoachMessageKind.Receipt, false)]
    public void TheSharedKindRuleIsTheOneBothSidesRead(CoachMessageKind kind, bool reportable) =>
        CoachResponseReportability.IsReportableKind(kind).Should().Be(reportable);

    /// <summary>
    /// A notice the server wrote outside a learner's turn cannot be reported, because there is no
    /// request to pair it to and the service will not guess one.
    /// </summary>
    /// <remarks>
    /// This is the "internal notice" case stated in the only terms the server can check. A notice
    /// raised by a background sweep, a session expiry, or anything else that was not a learner
    /// asking for something has no learner message in its operation — so the same fail-closed
    /// pairing that protects an ordinary response protects this without needing a list of which
    /// notices are internal.
    /// </remarks>
    [Fact]
    public async Task AStandaloneNoticeWithNoLearnerTurnIsRefused()
    {
        using var harness = new CoachResponseReportHarness();

        string conversationId = "c-standalone";
        string noticeId;

        await using (var setup = harness.NewContext())
        {
            var conversations = harness.NewConversationStore(setup);
            var messages = harness.NewMessageStore(setup);
            var owner = CoachOwner.ForUser("learner-a");

            await conversations.CreateAsync(owner, new CreateCoachConversationRequest(
                Title: "Session", TitleSource: CoachConversationTitleSource.Generated,
                TargetLanguageCode: "ko", ConversationId: conversationId));

            var notice = await messages.AppendAsync(owner, new AppendCoachMessageRequest(
                conversationId, CoachMessageRole.Coach, CoachMessageKind.Notice,
                Payload("Your session expired."), "op-sweep"));

            notice.Status.Should().Be(CoachHistoryStatus.Success);
            noticeId = notice.Message!.Id;
        }

        await using var db = harness.NewContext();
        var result = await harness.NewService(db).ReportAsync(conversationId, noticeId, Request());

        result.Status.Should().Be(CoachOperationStatus.InvalidInput);
        result.Detail.Should().Contain("could not be matched");

        (await harness.RowsAsync()).Should().BeEmpty(
            "reaching back to an earlier exchange would send a reviewer to read a turn the learner never complained about");
    }

    /// <summary>
    /// A notice with no turn correlation at all is refused rather than paired by position.
    /// </summary>
    [Fact]
    public async Task ANoticeWithNoTurnCorrelationIsRefused()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync(responseKind: CoachMessageKind.Notice, correlate: false);

        await using var db = harness.NewContext();
        var result = await harness.NewService(db)
            .ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());

        result.Status.Should().Be(CoachOperationStatus.InvalidInput);
        (await harness.RowsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Another learner's notice is not reportable, and reads as absent rather than as forbidden.
    /// </summary>
    [Fact]
    public async Task AnotherLearnersNoticeIsNotReportable()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync(
            owner: "learner-b", conversationId: "c-b", responseKind: CoachMessageKind.Notice);

        await using var db = harness.NewContext();
        var result = await harness.NewService(db, "learner-a")
            .ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request());

        result.Status.Should().Be(CoachOperationStatus.SessionNotFound,
            "a distinct refusal would turn this route into an existence oracle for message ids");

        (await harness.RowsAsync()).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the evidence a reviewer reads

    /// <summary>
    /// Revealing the evidence behind a reported notice returns the learner's request and the
    /// notice itself, to the learner's own scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the point of the whole feature: a reviewer opening the ledger row has to be able to
    /// read what was asked and what came back. If the reveal returned the request and an empty
    /// response — which is what it would do if the resolver were keyed on anything but the message
    /// id, since a notice is not the kind of row a "find the answer" heuristic looks for — the
    /// report would be a complaint about a message nobody can see.
    /// </para>
    /// <para>
    /// Both pointers come from the report row, which came from the turn correlation, so this
    /// closes the loop from <c>flag pressed</c> to <c>reviewer reads the exact pair</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RevealingAReportedNoticeReturnsTheRequestAndTheNoticeText()
    {
        using var harness = new CoachResponseReportHarness();

        const string learnerAsked = "Make today's plan shorter.";
        const string noticeSaid = "There is no plan for today yet, so there is nothing to change.";

        string conversationId = "c-evidence";
        string noticeId;

        await using (var setup = harness.NewContext())
        {
            var conversations = harness.NewConversationStore(setup);
            var messages = harness.NewMessageStore(setup);
            var owner = CoachOwner.ForUser("learner-a");

            await conversations.CreateAsync(owner, new CreateCoachConversationRequest(
                Title: "Plan", TitleSource: CoachConversationTitleSource.Generated,
                TargetLanguageCode: "ko", ConversationId: conversationId));

            await messages.AppendAsync(owner, new AppendCoachMessageRequest(
                conversationId, CoachMessageRole.Learner, CoachMessageKind.Text,
                Payload(learnerAsked), "op-1"));

            var notice = await messages.AppendAsync(owner, new AppendCoachMessageRequest(
                conversationId, CoachMessageRole.Coach, CoachMessageKind.Notice,
                Payload(noticeSaid), "op-1"));

            noticeId = notice.Message!.Id;
        }

        await using (var db = harness.NewContext())
        {
            var reported = await harness.NewService(db).ReportAsync(
                conversationId, noticeId, Request(CoachResponseReportReason.ExpectedAppAction));
            reported.IsOk.Should().BeTrue();
        }

        var opportunity = (await harness.OpportunitiesAsync()).Should().ContainSingle().Subject;

        await using var read = harness.NewContext();
        var result = await NewOperator(harness, read, messages: harness.NewMessageStore(read))
            .RevealEvidenceAsync(
                opportunity.Id,
                new CoachOpportunityEvidenceRequest(
                    CoachOpportunityLimits.EvidenceRevealAcknowledgement));

        result.IsOk.Should().BeTrue();
        result.Value!.EvidenceState.Should().Be(CoachOpportunityEvidenceState.Available);
        result.Value.LearnerMessageText.Should().Be(learnerAsked,
            "the request the notice answered, named by id rather than found by position");
        result.Value.PriorCoachMessageText.Should().Be(noticeSaid,
            "a notice is the response here, so it is what the reveal has to hand back");
        result.Value.CrossOwner.Should().BeFalse();
    }

    /// <summary>
    /// A reported notice's evidence is refused to another owner while cross-owner reveal is off.
    /// </summary>
    [Fact]
    public async Task AnotherOwnerCannotRevealAReportedNoticesEvidence()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync(responseKind: CoachMessageKind.Notice);

        await using (var db = harness.NewContext())
        {
            (await harness.NewService(db)
                .ReportAsync(turn.ConversationId, turn.ResponseMessageId, Request()))
                .IsOk.Should().BeTrue();
        }

        var opportunity = (await harness.OpportunitiesAsync()).Should().ContainSingle().Subject;

        await using var read = harness.NewContext();
        var result = await NewOperator(
                harness, read, callerId: "learner-b", messages: harness.NewMessageStore(read))
            .RevealEvidenceAsync(
                opportunity.Id,
                new CoachOpportunityEvidenceRequest(
                    CoachOpportunityLimits.EvidenceRevealAcknowledgement));

        result.Status.Should().Be(CoachOpportunityOperatorStatus.CrossOwnerRefused);
        result.Value.Should().BeNull("no learner text may leave the store on a refusal");
    }

    // ---------------------------------------------------------------- operator plumbing

    private static CoachOpportunityOperatorService NewOperator(
        CoachResponseReportHarness harness,
        CoachDbContext db,
        string callerId = "learner-a",
        ICoachMessageStore? messages = null) =>
        new(db,
            new TestUserScope(callerId),
            new TestOptionsMonitor<CoachOpportunityOptions>(new CoachOpportunityOptions
            {
                Enabled = true,
                OperatorSurface = new CoachOpportunityOperatorSurfaceOptions { Enabled = true }
            }),
            new TestOptionsMonitor<CoachOptions>(new CoachOptions
            {
                Enabled = true,
                AllowedUserProfileIds = ["learner-a", "learner-b"]
            }),
            harness.Time,
            NullLogger<CoachOpportunityOperatorService>.Instance,
            messages,
            new CoachKeyRingPlan
            {
                Mode = CoachKeyRingMode.AzureBlobConnectionString,
                ApplicationName = "SentenceStudio.Coach",
                ContainerName = "coach-keys",
                BlobName = "keyring.xml"
            });
}
