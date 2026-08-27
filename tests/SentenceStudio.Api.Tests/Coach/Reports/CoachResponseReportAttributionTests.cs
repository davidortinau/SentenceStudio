using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Endpoints;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Tests.Coach.Opportunities;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Reports;

/// <summary>
/// Which report's turn facts a ledger row is allowed to show.
/// </summary>
/// <remarks>
/// <para>
/// A ledger row's identity is <em>(learner, problem, UTC day)</em>; a report's identity is a
/// response, forever. Several reports can therefore land on one row, and "the report behind this
/// row" is not defined by the row alone.
/// </para>
/// <para>
/// The original implementation took <c>ORDER BY ReportedAtUtc</c> and used the first. That reads
/// as authoritative and is not: the stop reason, attempt count, tool list, and write outcome it
/// renders describe one turn, while the row beside them summarises several — and once retention
/// prunes the report the row's evidence actually points at, the block silently starts describing a
/// different response than the one an operator would decrypt.
/// </para>
/// <para>
/// These tests pin the corrected rule: the facts follow the row's own evidence pointer, the
/// aggregate is always reported, and when the pointer has no surviving report the block is empty
/// rather than borrowed.
/// </para>
/// </remarks>
public class CoachResponseReportAttributionTests
{
    private const string Owner = "learner-a";

    private static CoachOptions Cohort(params string[] ids) => new()
    {
        Enabled = true,
        AllowedUserProfileIds = [.. ids]
    };

    private static CoachOpportunityOptions Surface() => new()
    {
        Enabled = true,
        OperatorSurface = new CoachOpportunityOperatorSurfaceOptions { Enabled = true }
    };

    private static CoachOpportunityOperatorService NewOperator(
        CoachResponseReportHarness harness,
        CoachDbContext db) =>
        new(db,
            new TestUserScope(Owner),
            new TestOptionsMonitor<CoachOpportunityOptions>(Surface()),
            new TestOptionsMonitor<CoachOptions>(Cohort(Owner)),
            harness.Time,
            NullLogger<CoachOpportunityOperatorService>.Instance,
            null,
            null);

    // ---------------------------------------------------------------- one report

    [Fact]
    public async Task OneReportRendersItsOwnFactsAndSaysTheyAreTheReportedResponse()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        await using (var db = harness.NewContext())
        {
            await harness.NewService(db).ReportAsync(
                turn.ConversationId,
                turn.ResponseMessageId,
                new CoachResponseReportRequest { Reason = CoachResponseReportReason.Confusing });
        }

        var ledger = await harness.OpportunitiesAsync();

        await using var read = harness.NewContext();
        var result = await NewOperator(harness, read).GetAsync(ledger.Single().Id);

        result.IsOk.Should().BeTrue();

        result.Value!.Report.Should().NotBeNull();
        result.Value.Report!.Reason.Should().Be(nameof(CoachResponseReportReason.Confusing));

        var rollup = result.Value.ReportRollup.Should().NotBeNull().And.Subject
            as CoachOpportunityReportRollupDto;

        rollup!.ReportCount.Should().Be(1);
        rollup.ReportedResponseCount.Should().Be(1);
        rollup.FactsAreForTheReportedResponse.Should().BeTrue();
        rollup.Reasons.Should().ContainSingle()
            .Which.Reason.Should().Be(nameof(CoachResponseReportReason.Confusing));
    }

    // ---------------------------------------------------------------- several reports, one row

    [Fact]
    public async Task SeveralReportsOnOneRowAreCountedRatherThanCollapsedIntoOne()
    {
        using var harness = new CoachResponseReportHarness();

        var first = await harness.SeedTurnAsync(conversationId: "c-1", operationId: "op-1");
        var second = await harness.SeedTurnAsync(conversationId: "c-2", operationId: "op-2");

        await using (var db = harness.NewContext())
        {
            var service = harness.NewService(db);

            await service.ReportAsync(
                first.ConversationId,
                first.ResponseMessageId,
                new CoachResponseReportRequest
                {
                    Reason = CoachResponseReportReason.IncorrectOrMisleading
                });
        }

        harness.Time.Advance(TimeSpan.FromHours(2));

        await using (var db = harness.NewContext())
        {
            await harness.NewService(db).ReportAsync(
                second.ConversationId,
                second.ResponseMessageId,
                new CoachResponseReportRequest
                {
                    Reason = CoachResponseReportReason.IncorrectOrMisleading
                });
        }

        var ledger = await harness.OpportunitiesAsync();

        // Same learner, same problem, same UTC day: one ledger row, two reports.
        var row = ledger.Should().ContainSingle().Subject;
        row.OccurrenceCount.Should().Be(2);

        await using var read = harness.NewContext();
        var result = await NewOperator(harness, read).GetAsync(row.Id);

        var rollup = result.Value!.ReportRollup!;

        rollup.ReportCount.Should().Be(2);
        rollup.ReportedResponseCount.Should().Be(2,
            "two different responses were reported, and a card that showed one turn's facts " +
            "without saying so would read as though the row described that turn");
        rollup.Reasons.Should().ContainSingle()
            .Which.ReportCount.Should().Be(2);
        rollup.FirstReportedAtUtc.Should().BeBefore(rollup.LastReportedAtUtc);

        // The facts shown are the ones for the response this row's evidence points at — the same
        // response an operator would decrypt if they revealed evidence on this row.
        result.Value.Report.Should().NotBeNull();
        rollup.FactsAreForTheReportedResponse.Should().BeTrue();

        var reports = await harness.RowsAsync();
        var pointed = reports.Single(report =>
            string.Equals(report.CoachMessageId, row.EvidenceOfferMessageId, StringComparison.Ordinal));

        result.Value.Report!.ReportedAtUtc.Should().Be(pointed.ReportedAtUtc);
    }

    // ---------------------------------------------------------------- the regression

    [Fact]
    public async Task TheFactsFollowTheEvidencePointerRatherThanTheEarliestReport()
    {
        using var harness = new CoachResponseReportHarness();

        var first = await harness.SeedTurnAsync(conversationId: "c-1", operationId: "op-1");
        var second = await harness.SeedTurnAsync(conversationId: "c-2", operationId: "op-2");

        await using (var db = harness.NewContext())
        {
            await harness.NewService(db).ReportAsync(
                first.ConversationId,
                first.ResponseMessageId,
                new CoachResponseReportRequest
                {
                    Reason = CoachResponseReportReason.IncorrectOrMisleading
                });
        }

        harness.Time.Advance(TimeSpan.FromHours(3));

        await using (var db = harness.NewContext())
        {
            await harness.NewService(db).ReportAsync(
                second.ConversationId,
                second.ResponseMessageId,
                new CoachResponseReportRequest
                {
                    Reason = CoachResponseReportReason.IncorrectOrMisleading
                });
        }

        var row = (await harness.OpportunitiesAsync()).Single();

        // Repoint the row at the LATER response. This is the state the ledger reaches whenever the
        // dedup upsert refreshes its pointers, and it is the case where "earliest report" and
        // "the report this row is about" are different rows — the divergence the old ORDER BY
        // could not see.
        await using (var mutate = harness.NewContext())
        {
            var tracked = await mutate.CoachOpportunities.SingleAsync(entity => entity.Id == row.Id);
            tracked.EvidenceOfferMessageId = second.ResponseMessageId;
            await mutate.SaveChangesAsync();
        }

        var reports = await harness.RowsAsync();
        var earliest = reports.OrderBy(report => report.ReportedAtUtc).First();
        var pointed = reports.Single(report =>
            string.Equals(report.CoachMessageId, second.ResponseMessageId, StringComparison.Ordinal));

        earliest.Id.Should().NotBe(pointed.Id, "the fixture must actually diverge");

        await using var read = harness.NewContext();
        var result = await NewOperator(harness, read).GetAsync(row.Id);

        result.Value!.Report!.ReportedAtUtc.Should().Be(pointed.ReportedAtUtc,
            "the evidence pointer and the turn facts must describe the same response, or the " +
            "detail card is quietly mixing two turns");
        result.Value.Report.ReportedAtUtc.Should().NotBe(earliest.ReportedAtUtc);
        result.Value.ReportRollup!.FactsAreForTheReportedResponse.Should().BeTrue();
    }

    [Fact]
    public async Task APrunedReportLeavesAnEmptyFactsBlockRatherThanABorrowedOne()
    {
        using var harness = new CoachResponseReportHarness();

        var first = await harness.SeedTurnAsync(conversationId: "c-1", operationId: "op-1");
        var second = await harness.SeedTurnAsync(conversationId: "c-2", operationId: "op-2");

        await using (var db = harness.NewContext())
        {
            await harness.NewService(db).ReportAsync(
                first.ConversationId,
                first.ResponseMessageId,
                new CoachResponseReportRequest
                {
                    Reason = CoachResponseReportReason.DidNotAnswer
                });
        }

        harness.Time.Advance(TimeSpan.FromHours(1));

        await using (var db = harness.NewContext())
        {
            await harness.NewService(db).ReportAsync(
                second.ConversationId,
                second.ResponseMessageId,
                new CoachResponseReportRequest
                {
                    Reason = CoachResponseReportReason.DidNotAnswer
                });
        }

        var row = (await harness.OpportunitiesAsync()).Single();

        // Retention prunes reports at 180 days; a Reviewed or Accepted ledger row is kept forever.
        // So a row can outlive the report its evidence points at, and this is what an operator
        // must see when it does.
        await using (var prune = harness.NewContext())
        {
            var pointed = await prune.CoachResponseReports.SingleAsync(report =>
                report.CoachMessageId == row.EvidenceOfferMessageId);

            prune.CoachResponseReports.Remove(pointed);
            await prune.SaveChangesAsync();
        }

        await using var read = harness.NewContext();
        var result = await NewOperator(harness, read).GetAsync(row.Id);

        result.Value!.Report.Should().BeNull(
            "an empty block is legible as missing; a block filled from another response's turn is " +
            "not, and it is the one a reviewer would act on");

        var rollup = result.Value.ReportRollup!;
        rollup.ReportCount.Should().Be(1, "the surviving report is still counted");
        rollup.FactsAreForTheReportedResponse.Should().BeFalse();
    }

    [Fact]
    public async Task ReportsUnderDifferentReasonsAreBrokenOutRatherThanTotalled()
    {
        using var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        // Reason drives the capability code and therefore the fingerprint, so two reasons are two
        // rows. Seeded directly so both land on one row: the projection has to hold up for a
        // future fingerprint that groups reasons, not only for today's one-reason-per-row shape.
        await using (var db = harness.NewContext())
        {
            await harness.NewService(db).ReportAsync(
                turn.ConversationId,
                turn.ResponseMessageId,
                new CoachResponseReportRequest { Reason = CoachResponseReportReason.Other });
        }

        var row = (await harness.OpportunitiesAsync()).Single();

        await using (var seed = harness.NewContext())
        {
            seed.CoachResponseReports.Add(new CoachResponseReport
            {
                Id = "report-extra",
                UserProfileId = Owner,
                ConversationId = turn.ConversationId,
                CoachMessageId = "message-other-response",
                CoachMessageSequence = 9,
                RequestMessageId = "message-other-request",
                RequestMessageSequence = 8,
                Reason = CoachResponseReportReason.ExpectedAppAction,
                ResponseKind = CoachMessageKind.Text,
                OpportunityId = row.Id,
                ReportedAtUtc = harness.Time.GetUtcNow().UtcDateTime.AddMinutes(5)
            });

            await seed.SaveChangesAsync();
        }

        await using var read = harness.NewContext();
        var result = await NewOperator(harness, read).GetAsync(row.Id);

        var rollup = result.Value!.ReportRollup!;

        rollup.ReportCount.Should().Be(2);
        rollup.Reasons.Should().HaveCount(2);
        rollup.Reasons.Select(reason => reason.Reason).Should().BeEquivalentTo(
        [
            nameof(CoachResponseReportReason.Other),
            nameof(CoachResponseReportReason.ExpectedAppAction)
        ]);

        // The facts still describe the response the row points at, not the one seeded beside it.
        result.Value.Report!.Reason.Should().Be(nameof(CoachResponseReportReason.Other));
        rollup.FactsAreForTheReportedResponse.Should().BeTrue();
    }

    // ---------------------------------------------------------------- automatic rows

    [Fact]
    public async Task AnAutomaticRowCarriesNeitherFactsNorARollup()
    {
        using var harness = new CoachResponseReportHarness();

        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.UnsupportedCapability,
            CoachOpportunityCapabilityCodes.WriteToolsDisabled,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.Product));

        var ledger = await harness.OpportunitiesAsync();

        await using var read = harness.NewContext();
        var result = await NewOperator(harness, read).GetAsync(ledger.Single().Id);

        result.Value!.Report.Should().BeNull();
        result.Value.ReportRollup.Should().BeNull(
            "a card that renders either block is always rendering a learner's report");
    }

    // ---------------------------------------------------------------- closed codes only

    [Fact]
    public void TheRollupShapeCarriesNoIdentifierOrProse()
    {
        var members = typeof(CoachOpportunityReportRollupDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        members.Should().NotContain("UserProfileId");
        members.Should().NotContain("ConversationId");
        members.Should().NotContain("CoachMessageId");
        members.Should().NotContain("Note");
        members.Should().NotContain("Text");

        typeof(CoachOpportunityReportRollupDto)
            .GetProperties()
            .Where(property => property.PropertyType == typeof(string))
            .Should().BeEmpty(
                "everything on this shape is a count, a timestamp, a boolean, or a list of closed " +
                "reason codes — there is no string member and nowhere to put prose");

        typeof(CoachOpportunityReportReasonCountDto)
            .GetProperties()
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .Should().BeEquivalentTo([nameof(CoachOpportunityReportReasonCountDto.Reason)]);
    }
}
