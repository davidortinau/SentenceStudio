using Microsoft.EntityFrameworkCore;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// One row per (learner, problem, UTC day), and the counters that make a trend visible.
/// </summary>
/// <remarks>
/// Bounds growth to at most one row per learner per problem per day while preserving a real
/// frequency-over-time curve. The alternative — one row per occurrence — would make the table
/// grow with traffic rather than with the number of distinct problems, and would make
/// "how often" a scan rather than a column.
/// </remarks>
public class CoachOpportunityDedupTests
{
    private static CoachOpportunitySignal Signal(
        string capability = CoachOpportunityCapabilityCodes.EntityLookupByName) =>
        new(CoachOpportunityKind.UnsupportedCapability,
            capability,
            CoachOpportunitySurface.WriteLedger,
            CoachOpportunityDisposition.Product,
            ToolName: CoachToolNames.ProposeVocabularyRemoval,
            FailureCode: Api.Coach.Operations.CoachWriteFailureCodes.EntityNotOwned,
            Evidence: new CoachOpportunityEvidencePointer("conv-1"));

    [Fact]
    public async Task TheSameProblemTwiceInADayIsOneRow()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(Signal());
        await harness.Recorder.RecordAsync(Signal());

        var rows = await harness.RowsAsync();
        rows.Should().ContainSingle();
        rows[0].OccurrenceCount.Should().Be(2);
        rows[0].FirstObservedAtUtc.Should().Be(rows[0].LastObservedAtUtc);
    }

    [Fact]
    public async Task TheNextDayIsANewRow()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(Signal());
        harness.Time.Advance(TimeSpan.FromDays(1));
        await harness.Recorder.RecordAsync(Signal());

        var rows = await harness.RowsAsync();
        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(row => row.OccurrenceCount.Should().Be(1));
        rows.Select(row => row.DedupBucketDate).Distinct().Should().HaveCount(2);
        rows.Select(row => row.Fingerprint).Distinct().Should().ContainSingle(
            "it is the same problem on a different day, so the fingerprint must not change");
    }

    [Fact]
    public async Task ADifferentProblemIsADifferentRow()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(Signal());
        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.WriteToolsDisabled));

        var rows = await harness.RowsAsync();
        rows.Should().HaveCount(2);
        rows.Select(row => row.Fingerprint).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task TwoLearnersHittingTheSameGapAreTwoRowsWithOneFingerprint()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(Signal());
        await harness.RecorderFor("learner-b").RecordAsync(Signal());

        var rows = await harness.RowsAsync();
        rows.Should().HaveCount(2);
        rows.Select(row => row.UserProfileId).Should().BeEquivalentTo(["learner-a", "learner-b"]);
        rows.Select(row => row.Fingerprint).Distinct().Should().ContainSingle(
            "the fingerprint is a problem identity, not an owner identity — that is what makes " +
            "the cross-learner rollup possible without ever naming a learner");
    }

    [Fact]
    public async Task ConcurrentRecordersStillProduceOneRow()
    {
        using var harness = new CoachOpportunityHarness();

        // The whole point of the single ON CONFLICT statement: two workers observing the same
        // refusal at the same moment must produce a count of two, not two rows or a crash.
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => harness.Recorder.RecordAsync(Signal()).AsTask()));

        var rows = await harness.RowsAsync();
        rows.Should().ContainSingle();
        rows[0].OccurrenceCount.Should().Be(8);
    }

    [Fact]
    public async Task AnAggregateOnlySignalIsStrippedOfEveryPointer()
    {
        using var harness = new CoachOpportunityHarness();

        // Deliberately hostile input: a mapper that forgot to strip. The recorder strips
        // unconditionally, so a future mapper written by somebody who had not read the design
        // still cannot produce an inspectable row for a refusal.
        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.HarmfulOrUnsafeRequest,
            CoachOpportunityCapabilityCodes.DestructiveRequestRefused,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.AggregateOnly,
            Evidence: new CoachOpportunityEvidencePointer("conv-secret", "msg-secret", 7, "msg-2", 6),
            TurnId: "turn-secret",
            TurnOperationId: "op-secret",
            WriteOperationId: "write-secret",
            RelatedOpportunityId: "related-secret"));

        var rows = await harness.RowsAsync();
        var row = rows.Should().ContainSingle().Subject;

        row.ConversationId.Should().BeNull();
        row.TurnId.Should().BeNull();
        row.TurnOperationId.Should().BeNull();
        row.EvidenceMessageId.Should().BeNull();
        row.EvidenceMessageSequence.Should().BeNull();
        row.EvidenceOfferMessageId.Should().BeNull();
        row.EvidenceOfferMessageSequence.Should().BeNull();
        row.WriteOperationId.Should().BeNull();
        row.RelatedOpportunityId.Should().BeNull();
    }

    [Fact]
    public async Task NoTrustedOwnerRecordsNothing()
    {
        using var harness = new CoachOpportunityHarness(userProfileId: null);

        await harness.Recorder.RecordAsync(Signal());

        (await harness.RowsAsync()).Should().BeEmpty(
            "an unowned row cannot be scoped, cannot be deleted on erasure, and cannot be " +
            "reviewed — the recorder fails closed rather than guessing an owner");
    }

    [Fact]
    public async Task CaptureOffRecordsNothing()
    {
        using var harness = new CoachOpportunityHarness(
            options: new CoachOpportunityOptions { Enabled = false });

        await harness.Recorder.RecordAsync(Signal());

        (await harness.RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnknownCapabilityCodeIsDropped()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.UnsupportedCapability,
            "whatever the learner typed",
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.Product));

        (await harness.RowsAsync()).Should().BeEmpty(
            "an unvalidated code column is a free-text column wearing a different name");
    }

    [Fact]
    public async Task AnUnregisteredToolNameIsDroppedWithoutDroppingTheRow()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(Signal() with { ToolName = "delete_everything" });

        var row = (await harness.RowsAsync()).Should().ContainSingle().Subject;
        row.ToolName.Should().BeNull(
            "the registry is the authority for what a tool is called; a name it does not know " +
            "is model-supplied and never reaches the column");
        row.CapabilityCode.Should().Be(CoachOpportunityCapabilityCodes.EntityLookupByName);
    }

    [Fact]
    public async Task AnUnknownFailureCodeIsDroppedWithoutDroppingTheRow()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(Signal() with { FailureCode = "because the learner said 사과" });

        var row = (await harness.RowsAsync()).Should().ContainSingle().Subject;
        row.FailureCode.Should().BeNull();
    }

    [Fact]
    public async Task ARegisteredToolStampsItsRiskClass()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(Signal());

        var row = (await harness.RowsAsync()).Should().ContainSingle().Subject;
        row.ToolName.Should().Be(CoachToolNames.ProposeVocabularyRemoval);
        row.RiskClass.Should().NotBeNull(
            "the risk class comes from the registration, so a reviewer can tell a read refusal " +
            "from a destructive-write refusal without joining another table");
    }

    [Fact]
    public async Task AReferentLossChainsToTheCapabilityRefusalThatPrecededIt()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.ProposalRefusedByPolicy,
            "preference_setting_session_minutes",
            CoachOpportunitySurface.ToolInvocation,
            CoachOpportunityDisposition.Product,
            ToolName: CoachToolNames.ProposePreferenceChange,
            Evidence: new CoachOpportunityEvidencePointer("conv-1")));

        harness.Time.Advance(TimeSpan.FromMinutes(2));

        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.AmbiguousFollowUp,
            CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.Product,
            OfferLink: CoachOpportunityOfferLink.PriorCoachQuestion,
            StopReason: CoachStopReason.ClarificationRequested,
            Evidence: new CoachOpportunityEvidencePointer("conv-1", "msg-2", 2, "msg-1", 1)));

        var rows = await harness.RowsAsync();
        var loss = rows.Single(row => row.Kind == CoachOpportunityKind.AmbiguousFollowUp);
        var refusal = rows.Single(row => row.Kind == CoachOpportunityKind.ProposalRefusedByPolicy);

        loss.RelatedOpportunityId.Should().Be(refusal.Id,
            "'the model offered a change it is not allowed to make' followed by 'the learner " +
            "said yes and nothing bound to it' is one product problem, and the chain says so " +
            "without either row carrying a word of what was said");
    }

    [Fact]
    public async Task AChainNeverReachesAcrossLearners()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.RecorderFor("learner-b").RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.UnsupportedCapability,
            "preference_setting_session_minutes",
            CoachOpportunitySurface.ToolInvocation,
            CoachOpportunityDisposition.Product,
            Evidence: new CoachOpportunityEvidencePointer("conv-1")));

        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.AmbiguousFollowUp,
            CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.Product,
            OfferLink: CoachOpportunityOfferLink.PriorCoachQuestion,
            Evidence: new CoachOpportunityEvidencePointer("conv-1", "msg-2", 2, "msg-1", 1)));

        var rows = await harness.RowsAsync();
        rows.Single(row => row.UserProfileId == "learner-a").RelatedOpportunityId
            .Should().BeNull("a chain is owner-scoped, so it cannot link two learners' rows");
    }

    [Fact]
    public async Task AStaleRefusalIsNotChained()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.UnsupportedCapability,
            "preference_setting_session_minutes",
            CoachOpportunitySurface.ToolInvocation,
            CoachOpportunityDisposition.Product,
            Evidence: new CoachOpportunityEvidencePointer("conv-1")));

        harness.Time.Advance(CoachOpportunityLimits.RelatedOpportunityWindow + TimeSpan.FromMinutes(1));

        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.AmbiguousFollowUp,
            CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.Product,
            OfferLink: CoachOpportunityOfferLink.PriorCoachQuestion,
            Evidence: new CoachOpportunityEvidencePointer("conv-1", "msg-2", 2, "msg-1", 1)));

        var rows = await harness.RowsAsync();
        rows.Single(row => row.Kind == CoachOpportunityKind.AmbiguousFollowUp)
            .RelatedOpportunityId.Should().BeNull();
    }

    [Fact]
    public async Task EveryRowStampsTheSchemaVersion()
    {
        using var harness = new CoachOpportunityHarness();
        await harness.Recorder.RecordAsync(Signal());

        (await harness.RowsAsync()).Should().AllSatisfy(row =>
            row.SchemaVersion.Should().Be(CoachOpportunityLimits.SchemaVersion));
    }
}
