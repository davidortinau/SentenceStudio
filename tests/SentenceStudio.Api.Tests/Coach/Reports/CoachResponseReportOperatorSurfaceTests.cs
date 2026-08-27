using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Endpoints;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Reports;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Tests.Coach.Opportunities;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Reports;

/// <summary>
/// What a reviewer sees when they open a learner's report.
/// </summary>
/// <remarks>
/// The report is the one row on the ledger a person deliberately filed, so it carries more than
/// the automatic rows do — and every extra field is still a closed code. These prove both halves:
/// the facts arrive, and nothing free-text arrives with them.
/// </remarks>
public class CoachResponseReportOperatorSurfaceTests
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
        Api.Coach.Persistence.CoachDbContext db,
        ICoachMessageStore? messages = null) =>
        new(db,
            new TestUserScope(Owner),
            new TestOptionsMonitor<CoachOpportunityOptions>(Surface()),
            new TestOptionsMonitor<CoachOptions>(Cohort(Owner)),
            harness.Time,
            NullLogger<CoachOpportunityOperatorService>.Instance,
            messages,
            null);

    private static async Task<(CoachResponseReportHarness Harness, string OpportunityId)> ReportedAsync(
        CoachResponseReportReason reason = CoachResponseReportReason.IncorrectOrMisleading)
    {
        var harness = new CoachResponseReportHarness();
        var turn = await harness.SeedTurnAsync();

        await using (var db = harness.NewContext())
        {
            await harness.NewService(db).ReportAsync(
                turn.ConversationId,
                turn.ResponseMessageId,
                new CoachResponseReportRequest { Reason = reason });
        }

        var ledger = await harness.OpportunitiesAsync();
        return (harness, ledger.Single().Id);
    }

    // ---------------------------------------------------------------- the detail card

    [Fact]
    public async Task TheDetailCarriesTheReportReasonAndItsTurnFacts()
    {
        var (harness, opportunityId) = await ReportedAsync(CoachResponseReportReason.ExpectedAppAction);
        using var _ = harness;

        await using var db = harness.NewContext();
        var result = await NewOperator(harness, db).GetAsync(opportunityId);

        result.IsOk.Should().BeTrue();

        var report = result.Value!.Report.Should().NotBeNull().And.Subject as CoachOpportunityReportFactsDto;

        report!.Reason.Should().Be(nameof(CoachResponseReportReason.ExpectedAppAction));
        report.ResponseKind.Should().Be(nameof(CoachMessageKind.Text));
        report.ReportedAtUtc.Should().Be(harness.Time.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task TheDetailReportsEvidenceAvailabilityWithoutNamingAMessage()
    {
        var (harness, opportunityId) = await ReportedAsync();
        using var _ = harness;

        await using var db = harness.NewContext();
        var result = await NewOperator(harness, db).GetAsync(opportunityId);

        result.Value!.HasEvidence.Should().BeTrue();

        // A boolean, not an identifier: a reviewer's triage view has no use for a message id, and
        // a response carrying one would be a place for it to be copied somewhere less careful.
        var members = typeof(CoachOpportunityRowDto).GetProperties().Select(p => p.Name).ToList();
        members.Should().NotContain("EvidenceMessageId");
        members.Should().NotContain("EvidenceOfferMessageId");
    }

    [Fact]
    public async Task AnAutomaticRowCarriesNoReportBlock()
    {
        using var harness = new CoachResponseReportHarness();

        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.UnsupportedCapability,
            CoachOpportunityCapabilityCodes.WriteToolsDisabled,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.Product));

        var ledger = await harness.OpportunitiesAsync();

        await using var db = harness.NewContext();
        var result = await NewOperator(harness, db).GetAsync(ledger.Single().Id);

        result.Value!.Report.Should().BeNull(
            "a card that renders the report block is always rendering a report");
    }

    // ---------------------------------------------------------------- closed codes only

    [Fact]
    public void TheReportFactsShapeCarriesNoFreeTextMember()
    {
        var members = typeof(CoachOpportunityReportFactsDto)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        members.Should().NotContain("Note");
        members.Should().NotContain("Comment");
        members.Should().NotContain("Detail");
        members.Should().NotContain("Text");
        members.Should().NotContain("Summary");

        // Everything that is a string is a closed code, an enum name, or a bounded list of
        // registered tool names. Nothing here is prose, and there is nowhere to put any.
        typeof(CoachOpportunityReportFactsDto)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string) || p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .Should().BeSubsetOf(
            [
                nameof(CoachOpportunityReportFactsDto.Reason),
                nameof(CoachOpportunityReportFactsDto.ResponseKind),
                nameof(CoachOpportunityReportFactsDto.TurnStatus),
                nameof(CoachOpportunityReportFactsDto.TurnErrorCode),

                // W9 grounding. All three are enum member names rendered by NameOrNull, which
                // returns null for an ordinal the enum does not declare — so none of them can hold
                // a value that did not come out of a closed set.
                nameof(CoachOpportunityReportFactsDto.GroundingStage),
                nameof(CoachOpportunityReportFactsDto.GroundingRuleCodes),
                nameof(CoachOpportunityReportFactsDto.GroundingLimitationCode),
                nameof(CoachOpportunityReportFactsDto.InvokedToolNames),
                nameof(CoachOpportunityReportFactsDto.WriteStatus),
                nameof(CoachOpportunityReportFactsDto.WriteFailureCode)
            ]);
    }

    // ---------------------------------------------------------------- the rollup

    [Fact]
    public async Task TheRollupCountsReportsByReasonAndNamesNoLearner()
    {
        var (harness, _) = await ReportedAsync(CoachResponseReportReason.Confusing);
        using var _h = harness;

        await using var db = harness.NewContext();
        var rollup = await NewOperator(harness, db).RollupAsync(null);

        rollup.IsOk.Should().BeTrue();

        var line = rollup.Value!.Should().ContainSingle().Subject;

        line.Kind.Should().Be(nameof(CoachOpportunityKind.UserReportedResponse));
        line.CapabilityCode.Should().Be(CoachOpportunityCapabilityCodes.LearnerReportedConfusing);
        line.DistinctLearners.Should().Be(1);

        typeof(CoachOpportunityRollupDto).GetProperties().Select(p => p.Name)
            .Should().NotContain("Learners",
                "the cross-learner view a reviewer needs is 'how many people', and any response that answered 'which people' would be a cross-tenant read");
    }
}
