using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.Deletion;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// Erasure removes every opportunity row a learner owns, and the retention sweep spares
/// decisions.
/// </summary>
public class CoachOpportunityLifecycleTests
{
    private static CoachOpportunitySignal Signal(string capability) =>
        new(CoachOpportunityKind.UnsupportedCapability,
            capability,
            CoachOpportunitySurface.WriteLedger,
            CoachOpportunityDisposition.Product,
            ToolName: CoachToolNames.ProposeVocabularyRemoval,
            Evidence: new CoachOpportunityEvidencePointer("conv-1"));

    // ---------------------------------------------------------------- erasure

    [Fact]
    public async Task ErasureRemovesEveryRowTheLearnerOwns()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));
        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.WriteToolsDisabled));
        await harness.RecorderFor("learner-b").RecordAsync(
            Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));

        await using var db = harness.NewContext();
        var contributor = harness.NewDeletionContributor(db);

        var deleted = await contributor.DeleteAllAsync(CoachOwner.ForUser("learner-a"));

        deleted.Should().Be(2);

        var remaining = await harness.RowsAsync();
        remaining.Should().ContainSingle();
        remaining[0].UserProfileId.Should().Be("learner-b",
            "erasure is owner-scoped; another learner's rows are not this learner's to delete");
    }

    [Fact]
    public async Task ErasureIsIdempotent()
    {
        using var harness = new CoachOpportunityHarness();
        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));

        await using var db = harness.NewContext();
        var contributor = harness.NewDeletionContributor(db);

        (await contributor.DeleteAllAsync(CoachOwner.ForUser("learner-a"))).Should().Be(1);

        // The coordinator's verification pass runs the contributor a second time and requires a
        // count of zero. A contributor that failed here would report a partially completed
        // erasure and roll the whole transaction back.
        (await contributor.DeleteAllAsync(CoachOwner.ForUser("learner-a"))).Should().Be(0);
    }

    [Fact]
    public async Task ErasureWithNoOwnerDeletesNothing()
    {
        using var harness = new CoachOpportunityHarness();
        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));

        await using var db = harness.NewContext();
        var contributor = harness.NewDeletionContributor(db);

        (await contributor.DeleteAllAsync(default)).Should().Be(0,
            "an empty owner would make the filter empty and take every learner's rows");

        (await harness.RowsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task TheCoordinatorDiscoversTheContributor()
    {
        using var harness = new CoachOpportunityHarness();
        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));

        await using var db = harness.NewContext();

        // Registered via TryAddEnumerable in production, so the coordinator finds it without a
        // hand-maintained table list. Here the enumerable is supplied directly, which is the
        // same shape DI produces.
        var service = new CoachDataDeletionService(
            db,
            [harness.NewDeletionContributor(db)],
            NullLogger<CoachDataDeletionService>.Instance);

        var report = await service.DeleteAllForOwnerAsync(CoachOwner.ForUser("learner-a"));

        report.Succeeded.Should().BeTrue();
        report.RowsDeleted.Should().Be(1);
        report.DeletesByContributor.Should().ContainKey("CoachOpportunity");
        (await harness.RowsAsync()).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- retention

    [Fact]
    public async Task RetentionRemovesUndecidedRowsPastTheWindow()
    {
        using var harness = new CoachOpportunityHarness();
        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));

        harness.Time.Advance(CoachOpportunityLimits.Retention + TimeSpan.FromDays(1));

        await using var db = harness.NewContext();
        var result = await harness.NewRetentionSweep(db).RunAsync();

        result.RowsDeleted.Should().Be(1);
        (await harness.RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RetentionSparesRowsInsideTheWindow()
    {
        using var harness = new CoachOpportunityHarness();
        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));

        harness.Time.Advance(CoachOpportunityLimits.Retention - TimeSpan.FromDays(1));

        await using var db = harness.NewContext();
        (await harness.NewRetentionSweep(db).RunAsync()).RowsDeleted.Should().Be(0);
    }

    /// <summary>
    /// Every status a reviewer touched survives the sweep.
    /// </summary>
    /// <remarks>
    /// <c>Reviewed</c> belongs here with <c>Accepted</c> and <c>Deferred</c>: a reviewer who read
    /// a row and has not decided yet has done work, and deleting it silently returns the problem
    /// to the pool as though nobody had looked, so the same review happens again on a fresh row.
    /// The approved design ages out <c>New</c> and <c>Dismissed</c> only.
    /// </remarks>
    [Theory]
    [InlineData(CoachOpportunityStatus.Reviewed)]
    [InlineData(CoachOpportunityStatus.Accepted)]
    [InlineData(CoachOpportunityStatus.Deferred)]
    public async Task RetentionSparesADecision(CoachOpportunityStatus status)
    {
        using var harness = new CoachOpportunityHarness();
        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));

        await using (var setup = harness.NewContext())
        {
            var row = await setup.CoachOpportunities.SingleAsync();
            row.Status = status;
            await setup.SaveChangesAsync();
        }

        harness.Time.Advance(CoachOpportunityLimits.Retention + TimeSpan.FromDays(30));

        await using var db = harness.NewContext();
        (await harness.NewRetentionSweep(db).RunAsync()).RowsDeleted.Should().Be(0,
            "a reviewed, accepted, or deferred row records work somebody did; deleting it would " +
            "erase the reason a spec exists, or silently re-open a triage decision");
    }

    /// <summary>
    /// The sweep's set and the transition policy's set are the same set.
    /// </summary>
    /// <remarks>
    /// Stated as a test rather than trusted to a comment because they protect each other: the
    /// transition policy refuses to walk a decided row into a retention-eligible status, and it
    /// can only do that correctly if its idea of "retention-eligible" matches what the sweep
    /// actually deletes. A status added to one and not the other re-opens a silent delete.
    /// </remarks>
    [Fact]
    public void TheRetentionSetAndTheTransitionPolicyAgree()
    {
        CoachOpportunityReviewTransitions.RetentionEligible.Should().BeEquivalentTo(
            [CoachOpportunityStatus.New, CoachOpportunityStatus.Dismissed]);

        CoachOpportunityReviewTransitions.Retained.Should().BeEquivalentTo(
            [CoachOpportunityStatus.Reviewed,
             CoachOpportunityStatus.Accepted,
             CoachOpportunityStatus.Deferred]);

        // Total and disjoint, so no status is silently in neither set.
        CoachOpportunityReviewTransitions.RetentionEligible
            .Concat(CoachOpportunityReviewTransitions.Retained)
            .Should().BeEquivalentTo(Enum.GetValues<CoachOpportunityStatus>());

        foreach (var status in Enum.GetValues<CoachOpportunityStatus>())
        {
            CoachOpportunityReviewTransitions.IsRetentionEligible(status)
                .Should().Be(CoachOpportunityReviewTransitions.RetentionEligible.Contains(status));
        }
    }

    [Theory]
    [InlineData(CoachOpportunityStatus.New)]
    [InlineData(CoachOpportunityStatus.Dismissed)]
    public async Task RetentionRemovesAnUndecidedOrDismissedRow(CoachOpportunityStatus status)
    {
        using var harness = new CoachOpportunityHarness();
        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));

        await using (var setup = harness.NewContext())
        {
            var row = await setup.CoachOpportunities.SingleAsync();
            row.Status = status;
            await setup.SaveChangesAsync();
        }

        harness.Time.Advance(CoachOpportunityLimits.Retention + TimeSpan.FromDays(1));

        await using var db = harness.NewContext();
        (await harness.NewRetentionSweep(db).RunAsync()).RowsDeleted.Should().Be(1);
    }

    [Fact]
    public async Task ARecurringDismissedProblemKeepsRefreshingItsOwnWindow()
    {
        using var harness = new CoachOpportunityHarness();
        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));

        await using (var setup = harness.NewContext())
        {
            var row = await setup.CoachOpportunities.SingleAsync();
            row.Status = CoachOpportunityStatus.Dismissed;
            await setup.SaveChangesAsync();
        }

        harness.Time.Advance(CoachOpportunityLimits.Retention - TimeSpan.FromDays(1));

        // Same problem, same day bucket? No — a year later it is a new bucket, so this is a new
        // row. The dismissed one is now inside the window again relative to nothing, so the
        // point of this test is the status: recurrence after a dismissal is still recorded.
        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));

        var rows = await harness.RowsAsync();
        rows.Should().HaveCount(2);
        rows.Should().Contain(row => row.Status == CoachOpportunityStatus.Dismissed,
            "'we dismissed this and it kept happening' has to stay visible");
    }

    [Fact]
    public async Task RetentionOffRemovesNothing()
    {
        using var harness = new CoachOpportunityHarness(
            options: new CoachOpportunityOptions { Enabled = true, RetentionSweepEnabled = false });

        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));
        harness.Time.Advance(CoachOpportunityLimits.Retention + TimeSpan.FromDays(1));

        await using var db = harness.NewContext();
        (await harness.NewRetentionSweep(db).RunAsync()).RowsDeleted.Should().Be(0);
    }

    [Fact]
    public async Task RetentionIsBounded()
    {
        using var harness = new CoachOpportunityHarness();

        // One row per problem per day; walking the clock produces distinct buckets.
        for (var day = 0; day < CoachOpportunityLimits.RetentionBatchSize + 10; day++)
        {
            await harness.Recorder.RecordAsync(
                Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));
            harness.Time.Advance(TimeSpan.FromDays(1));
        }

        harness.Time.Advance(CoachOpportunityLimits.Retention + TimeSpan.FromDays(1));

        await using var db = harness.NewContext();
        var result = await harness.NewRetentionSweep(db).RunAsync();

        result.RowsDeleted.Should().Be(CoachOpportunityLimits.RetentionBatchSize,
            "a first pass over a long-lived table must not hold the cleanup lease — and " +
            "therefore the whole cleanup pass — for an unbounded time");
    }

    [Fact]
    public async Task TheCleanupPassRunsTheOpportunitySweep()
    {
        using var harness = new CoachOpportunityHarness();
        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));

        harness.Time.Advance(CoachOpportunityLimits.Retention + TimeSpan.FromDays(1));

        await using var db = harness.NewContext();

        var cleanup = new CoachExpiryCleanupService(
            db,
            Microsoft.Extensions.Options.Options.Create(new CoachPersistenceOptions()),
            harness.Time,
            NullLogger<CoachExpiryCleanupService>.Instance,
            expiredSessionFilter: null,
            opportunityRetention: harness.NewRetentionSweep(db));

        var result = await cleanup.RunAsync();

        result.OpportunitiesDeleted.Should().Be(1,
            "the sweep joins the existing lease-protected pass rather than adding a second " +
            "background service and a second lease");
        (await harness.RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ACleanupPassWithoutTheSweepStillWorks()
    {
        using var harness = new CoachOpportunityHarness();
        await harness.Recorder.RecordAsync(Signal(CoachOpportunityCapabilityCodes.EntityLookupByName));

        await using var db = harness.NewContext();

        var cleanup = new CoachExpiryCleanupService(
            db,
            Microsoft.Extensions.Options.Options.Create(new CoachPersistenceOptions()),
            harness.Time,
            NullLogger<CoachExpiryCleanupService>.Instance);

        var result = await cleanup.RunAsync();

        result.OpportunitiesDeleted.Should().Be(0);
        (await harness.RowsAsync()).Should().ContainSingle(
            "the sweep is optional, so every existing hand-constructed call site keeps working");
    }
}
