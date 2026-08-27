using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Opportunities;
using SentenceStudio.Api.Coach.Opportunities.Endpoints;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Security.DataProtection;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// The operator surface's four gates, its cross-owner refusal, its key-ring refusal, and its
/// acknowledgement requirement.
/// </summary>
/// <remarks>
/// The route-mapping gate (routes absent outside Development) is proven by
/// <see cref="CoachOpportunityRolloutTests"/>; this class proves the three gates the service
/// itself owns, plus the evidence path.
/// </remarks>
public class CoachOpportunityOperatorSurfaceTests
{
    private const string Owner = "learner-a";
    private const string Conversation = "conv-operator";

    private static CoachOptions Cohort(params string[] ids) => new()
    {
        Enabled = true,
        AllowedUserProfileIds = [.. ids]
    };

    private static CoachOpportunityOptions Surface(
        bool enabled = true, bool crossOwner = false) => new()
    {
        Enabled = true,
        OperatorSurface = new CoachOpportunityOperatorSurfaceOptions
        {
            Enabled = enabled,
            AllowCrossOwnerEvidence = crossOwner
        }
    };

    private static CoachOpportunityOperatorService NewService(
        CoachOpportunityHarness harness,
        Api.Coach.Persistence.CoachDbContext db,
        CoachOptions coachOptions,
        CoachOpportunityOptions? opportunityOptions = null,
        string? callerId = Owner,
        ICoachMessageStore? messages = null,
        CoachKeyRingPlan? keyRing = null) =>
        new(db,
            new TestUserScope(callerId),
            new TestOptionsMonitor<CoachOpportunityOptions>(opportunityOptions ?? Surface()),
            new TestOptionsMonitor<CoachOptions>(coachOptions),
            harness.Time,
            NullLogger<CoachOpportunityOperatorService>.Instance,
            messages,
            keyRing);

    // ---------------------------------------------------------------- gate 2: the flag

    [Fact]
    public async Task TheSurfaceIsUnavailableWhenTheFlagIsOff()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(Owner), Surface(enabled: false));

        service.IsCallerAuthorized().Should().BeFalse();
        (await service.RollupAsync(null)).Status
            .Should().Be(CoachOpportunityOperatorStatus.NotAvailable);
        (await service.ListAsync(null, null, null, null, 0, 0)).Status
            .Should().Be(CoachOpportunityOperatorStatus.NotAvailable);
    }

    // ---------------------------------------------------------------- gate 4: the cohort

    [Fact]
    public async Task ANonCohortCallerSeesNothing()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort("somebody-else"));

        service.IsCallerAuthorized().Should().BeFalse();
        (await service.RollupAsync(null)).Status
            .Should().Be(CoachOpportunityOperatorStatus.NotAvailable);
    }

    [Fact]
    public async Task TheDevelopmentSentinelDoesNotOpenTheOperatorSurface()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(CoachOptions.DevAllSentinel));

        service.IsCallerAuthorized().Should().BeFalse(
            "the sentinel exists so a developer can use the coach product without enumerating " +
            "themselves; it must not also open a screen that can decrypt learner messages");
    }

    [Fact]
    public async Task AnUnauthenticatedCallerSeesNothing()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(Owner), callerId: null);

        service.IsCallerAuthorized().Should().BeFalse();
    }

    // ---------------------------------------------------------------- reads

    [Fact]
    public async Task TheListExcludesAggregateOnlyRows()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedRowAsync(harness);

        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.HarmfulOrUnsafeRequest,
            CoachOpportunityCapabilityCodes.DestructiveRequestRefused,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.AggregateOnly));

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(Owner));

        var page = await service.ListAsync(null, null, null, null, 0, 0);

        page.IsOk.Should().BeTrue();
        page.Value!.Items.Should().ContainSingle();
        page.Value.Items[0].Disposition.Should().Be(nameof(CoachOpportunityDisposition.Product),
            "an aggregate-only row carries nothing a reviewer could act on; its signal lives in " +
            "the rollup");
    }

    [Fact]
    public async Task TheRollupCountsLearnersWithoutNamingThem()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedRowAsync(harness);
        await SeedRowAsync(harness, "learner-b");
        await SeedRowAsync(harness, "learner-c");

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(Owner));

        var rollup = await service.RollupAsync(null);

        rollup.IsOk.Should().BeTrue();
        var line = rollup.Value!.Should().ContainSingle().Subject;

        line.DistinctLearners.Should().Be(3);
        line.RowCount.Should().Be(3);
        line.TotalOccurrences.Should().Be(3);

        // The shape itself is the guarantee: there is no member that could carry an owner.
        typeof(CoachOpportunityRollupDto).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(name =>
                name.Contains("User", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Owner", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Tenant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheRollupIncludesAggregateOnlyRows()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.OutOfScopeRequest,
            CoachOpportunityCapabilityCodes.OffTopic,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.AggregateOnly));

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(Owner));

        var rollup = await service.RollupAsync(null);
        rollup.Value.Should().ContainSingle();
    }

    [Fact]
    public async Task AnUnknownCapabilityFilterIsRefused()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(Owner));

        var page = await service.ListAsync(null, null, "'; DROP TABLE CoachOpportunity; --", null, 0, 0);

        page.Status.Should().Be(CoachOpportunityOperatorStatus.InvalidRequest,
            "a filter value outside the closed set can only be a typo or a probe");
    }

    // ---------------------------------------------------------------- review

    [Fact]
    public async Task AReviewRecordsTheDecisionAndRendersAMarkdownBlock()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(Owner));
        var id = (await harness.RowsAsync())[0].Id;

        var result = await service.ReviewAsync(id, new CoachOpportunityReviewRequest(
            CoachOpportunityStatus.Accepted,
            CoachOpportunityReviewerNoteCode.NeedsCaptainDecision,
            "docs/sam-future-opportunities.md"));

        result.IsOk.Should().BeTrue();
        result.Value!.Row.Status.Should().Be(nameof(CoachOpportunityStatus.Accepted));
        result.Value.Row.ReviewerNoteCode.Should()
            .Be(nameof(CoachOpportunityReviewerNoteCode.NeedsCaptainDecision));
        result.Value.MarkdownBlock.Should().Contain("coach-opportunity://");
        result.Value.MarkdownBlock.Should().Contain("distinct learner(s)");
    }

    [Theory]
    [InlineData("docs/specs/sam-referent.md", true)]
    [InlineData("docs/sam-future-opportunities.md", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("docs/../../secret.md", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("docs/specs/../../x.md", false)]
    [InlineData("the learner said 사과", false)]
    [InlineData("https://example.com", false)]
    public void OnlyAnAllowedSpecPathIsAccepted(string? path, bool expected) =>
        new CoachOpportunityReviewRequest(CoachOpportunityStatus.Reviewed, null, path)
            .IsLinkedSpecPathValid.Should().Be(expected);

    [Fact]
    public async Task AnInvalidSpecPathIsRefused()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(Owner));
        var id = (await harness.RowsAsync())[0].Id;

        var result = await service.ReviewAsync(id, new CoachOpportunityReviewRequest(
            CoachOpportunityStatus.Accepted, null, "the learner asked about 사과"));

        result.Status.Should().Be(CoachOpportunityOperatorStatus.InvalidRequest,
            "the linked path is a reference into this repository, not a second free-text column");
    }

    [Fact]
    public async Task AReviewNeverDeletesTheRow()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(Owner));
        var id = (await harness.RowsAsync())[0].Id;

        await service.ReviewAsync(id, new CoachOpportunityReviewRequest(
            CoachOpportunityStatus.Dismissed, CoachOpportunityReviewerNoteCode.NotAProblem));

        var rows = await harness.RowsAsync();
        rows.Should().ContainSingle();
        rows[0].Status.Should().Be(CoachOpportunityStatus.Dismissed);
    }

    // ---------------------------------------------------------------- review transitions

    /// <summary>
    /// An accepted row cannot be walked back into a status the retention sweep would delete.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Status is not only a label: it decides retention. <c>New</c> and <c>Dismissed</c> age out,
    /// so moving an accepted row to either is a delete with extra steps — the row disappears at
    /// the next sweep and the decision it recorded goes with it, silently.
    /// </para>
    /// <para>
    /// <c>Reviewed</c> is refused too, even though the sweep spares it. Accepted means something
    /// downstream — a spec, a backlog entry, a branch — now points at this row, and that claim
    /// cannot be un-made by an edit here, because the artifacts pointing at it do not go away.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(CoachOpportunityStatus.New)]
    [InlineData(CoachOpportunityStatus.Reviewed)]
    [InlineData(CoachOpportunityStatus.Dismissed)]
    [InlineData(CoachOpportunityStatus.Deferred)]
    public async Task AnAcceptedRowCannotBeWalkedBack(CoachOpportunityStatus requested)
    {
        using var harness = new CoachOpportunityHarness();
        await SeedRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(Owner));
        var id = (await harness.RowsAsync())[0].Id;

        var accepted = await service.ReviewAsync(id, new CoachOpportunityReviewRequest(
            CoachOpportunityStatus.Accepted,
            CoachOpportunityReviewerNoteCode.SpecWritten,
            "docs/specs/sam-referent.md"));

        accepted.IsOk.Should().BeTrue();

        var walkBack = await service.ReviewAsync(id, new CoachOpportunityReviewRequest(
            requested, CoachOpportunityReviewerNoteCode.NotAProblem));

        walkBack.Status.Should().Be(CoachOpportunityOperatorStatus.TransitionRefused);

        // And nothing about the row moved — not the status, not the note, not the spec path.
        var row = (await harness.RowsAsync())[0];
        row.Status.Should().Be(CoachOpportunityStatus.Accepted);
        row.ReviewerNoteCode.Should().Be(CoachOpportunityReviewerNoteCode.SpecWritten);
        row.LinkedSpecPath.Should().Be("docs/specs/sam-referent.md");
    }

    /// <summary>
    /// Re-recording the same status is an edit, not a transition, and is allowed.
    /// </summary>
    /// <remarks>
    /// A reviewer refining a note code or attaching a spec path to an already-accepted row is
    /// ordinary work. A policy that refused it would make accepted rows uneditable, which is a
    /// different kind of wrong.
    /// </remarks>
    [Fact]
    public async Task AnAcceptedRowCanStillBeReLabelledAsAccepted()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(Owner));
        var id = (await harness.RowsAsync())[0].Id;

        await service.ReviewAsync(id, new CoachOpportunityReviewRequest(
            CoachOpportunityStatus.Accepted, CoachOpportunityReviewerNoteCode.NeedsCaptainDecision));

        var refined = await service.ReviewAsync(id, new CoachOpportunityReviewRequest(
            CoachOpportunityStatus.Accepted,
            CoachOpportunityReviewerNoteCode.SpecWritten,
            "docs/specs/sam-referent.md"));

        refined.IsOk.Should().BeTrue();
        refined.Value!.Row.ReviewerNoteCode.Should()
            .Be(nameof(CoachOpportunityReviewerNoteCode.SpecWritten));
        refined.Value.Row.LinkedSpecPath.Should().Be("docs/specs/sam-referent.md");
    }

    /// <summary>
    /// Deferred and dismissed rows may be reopened, to any status.
    /// </summary>
    /// <remarks>
    /// <c>Deferred</c> means "real, but not now" — coming back is the entire point.
    /// <c>Dismissed</c> means "not worth carrying", and a dismissed problem that keeps recurring
    /// is precisely the case a reviewer has to be able to reconsider. Neither has anything
    /// downstream pointing at it, so allowing the move costs nothing and forbidding it would make
    /// a wrong dismissal permanent.
    /// </remarks>
    [Theory]
    [InlineData(CoachOpportunityStatus.Deferred, CoachOpportunityStatus.New)]
    [InlineData(CoachOpportunityStatus.Deferred, CoachOpportunityStatus.Reviewed)]
    [InlineData(CoachOpportunityStatus.Deferred, CoachOpportunityStatus.Accepted)]
    [InlineData(CoachOpportunityStatus.Deferred, CoachOpportunityStatus.Dismissed)]
    [InlineData(CoachOpportunityStatus.Dismissed, CoachOpportunityStatus.New)]
    [InlineData(CoachOpportunityStatus.Dismissed, CoachOpportunityStatus.Reviewed)]
    [InlineData(CoachOpportunityStatus.Dismissed, CoachOpportunityStatus.Accepted)]
    [InlineData(CoachOpportunityStatus.Dismissed, CoachOpportunityStatus.Deferred)]
    [InlineData(CoachOpportunityStatus.Reviewed, CoachOpportunityStatus.Accepted)]
    [InlineData(CoachOpportunityStatus.Reviewed, CoachOpportunityStatus.Deferred)]
    [InlineData(CoachOpportunityStatus.Reviewed, CoachOpportunityStatus.Dismissed)]
    [InlineData(CoachOpportunityStatus.New, CoachOpportunityStatus.Accepted)]
    public async Task AnUndecidedOrReopenableRowMayMoveOn(
        CoachOpportunityStatus from,
        CoachOpportunityStatus to)
    {
        using var harness = new CoachOpportunityHarness();
        await SeedRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(Owner));
        var id = (await harness.RowsAsync())[0].Id;

        if (from != CoachOpportunityStatus.New)
        {
            (await service.ReviewAsync(id, new CoachOpportunityReviewRequest(from, null)))
                .IsOk.Should().BeTrue($"the fixture must reach {from} before the move under test");
        }

        var moved = await service.ReviewAsync(id, new CoachOpportunityReviewRequest(to, null));

        moved.IsOk.Should().BeTrue();
        moved.Value!.Row.Status.Should().Be(to.ToString());
    }

    /// <summary>
    /// The whole transition matrix, so a new status member cannot slip past unexamined.
    /// </summary>
    /// <remarks>
    /// Enumerated rather than listed, so adding a status to
    /// <see cref="CoachOpportunityStatus"/> without deciding its rules widens this test's input
    /// and fails it if the rule it inherits is wrong.
    /// </remarks>
    [Fact]
    public void TheTransitionMatrixIsMonotonicOutOfAcceptedOnly()
    {
        foreach (var current in Enum.GetValues<CoachOpportunityStatus>())
        {
            foreach (var requested in Enum.GetValues<CoachOpportunityStatus>())
            {
                var expected = current == requested
                               || current != CoachOpportunityStatus.Accepted;

                CoachOpportunityReviewTransitions.IsAllowed(current, requested)
                    .Should().Be(expected, $"{current} -> {requested}");
            }
        }

        // The refusal that matters most is the one that would have handed a decision to the
        // retention sweep, and the policy can say which those are.
        CoachOpportunityReviewTransitions.WouldRestoreRetentionEligibility(
            CoachOpportunityStatus.Accepted, CoachOpportunityStatus.New).Should().BeTrue();
        CoachOpportunityReviewTransitions.WouldRestoreRetentionEligibility(
            CoachOpportunityStatus.Accepted, CoachOpportunityStatus.Dismissed).Should().BeTrue();
        CoachOpportunityReviewTransitions.WouldRestoreRetentionEligibility(
            CoachOpportunityStatus.Accepted, CoachOpportunityStatus.Reviewed).Should().BeFalse();
        CoachOpportunityReviewTransitions.WouldRestoreRetentionEligibility(
            CoachOpportunityStatus.Deferred, CoachOpportunityStatus.New).Should().BeFalse(
            "that move is allowed, so nothing was refused");
    }

    /// <summary>
    /// A refused transition never leaves an accepted row exposed to the sweep.
    /// </summary>
    /// <remarks>
    /// The point of the whole policy, stated as an outcome rather than a status code: the row is
    /// still there after a retention pass that would have deleted it under the requested status.
    /// </remarks>
    [Fact]
    public async Task ARefusedWalkBackSurvivesTheRetentionSweep()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(Owner));
        var id = (await harness.RowsAsync())[0].Id;

        await service.ReviewAsync(id, new CoachOpportunityReviewRequest(
            CoachOpportunityStatus.Accepted, CoachOpportunityReviewerNoteCode.SpecWritten));

        await service.ReviewAsync(id, new CoachOpportunityReviewRequest(
            CoachOpportunityStatus.Dismissed, CoachOpportunityReviewerNoteCode.NotAProblem));

        harness.Time.Advance(CoachOpportunityLimits.Retention + TimeSpan.FromDays(30));

        await using var sweepContext = harness.NewContext();
        (await harness.NewRetentionSweep(sweepContext).RunAsync()).RowsDeleted.Should().Be(0);

        (await harness.RowsAsync()).Should().ContainSingle()
            .Which.Status.Should().Be(CoachOpportunityStatus.Accepted);
    }

    // ---------------------------------------------------------------- evidence

    [Fact]
    public async Task EvidenceRequiresTheAcknowledgement()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedEvidenceRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(
            harness, db, Cohort(Owner),
            messages: harness.NewMessageStore(db),
            keyRing: DurableKeyRing());

        var id = (await harness.RowsAsync())[0].Id;

        (await service.RevealEvidenceAsync(id, null)).Status
            .Should().Be(CoachOpportunityOperatorStatus.InvalidRequest);

        (await service.RevealEvidenceAsync(id, new CoachOpportunityEvidenceRequest("yes"))).Status
            .Should().Be(CoachOpportunityOperatorStatus.InvalidRequest);

        (await service.RevealEvidenceAsync(id, new CoachOpportunityEvidenceRequest(""))).Status
            .Should().Be(CoachOpportunityOperatorStatus.InvalidRequest);
    }

    [Fact]
    public async Task AnAcknowledgedRevealReturnsBothMessagesAndAuditsItself()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedEvidenceRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(
            harness, db, Cohort(Owner),
            messages: harness.NewMessageStore(db),
            keyRing: DurableKeyRing());

        var id = (await harness.RowsAsync())[0].Id;

        var result = await service.RevealEvidenceAsync(
            id,
            new CoachOpportunityEvidenceRequest(
                CoachOpportunityLimits.EvidenceRevealAcknowledgement));

        result.IsOk.Should().BeTrue();
        result.Value!.EvidenceState.Should().Be(CoachOpportunityEvidenceState.Available);
        result.Value.PriorCoachMessageText.Should().Contain("45 minutes");
        result.Value.LearnerMessageText.Should().Be("yes");
        result.Value.CrossOwner.Should().BeFalse();
        result.Value.EvidenceRevealCount.Should().Be(1);

        var row = (await harness.RowsAsync())[0];
        row.EvidenceRevealCount.Should().Be(1,
            "the counter lives on the row that was read, so the audit and the thing audited " +
            "cannot drift apart");
        row.EvidenceLastRevealedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ACrossOwnerRevealIsRefusedByDefault()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedEvidenceRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(
            harness, db,
            Cohort("operator-b"),
            callerId: "operator-b",
            messages: harness.NewMessageStore(db),
            keyRing: DurableKeyRing());

        var id = (await harness.RowsAsync())[0].Id;

        var result = await service.RevealEvidenceAsync(
            id,
            new CoachOpportunityEvidenceRequest(
                CoachOpportunityLimits.EvidenceRevealAcknowledgement));

        result.Status.Should().Be(CoachOpportunityOperatorStatus.CrossOwnerRefused);

        (await harness.RowsAsync())[0].EvidenceRevealCount.Should().Be(0,
            "a refused reveal read nothing, so it must not be counted as one");
    }

    [Fact]
    public async Task ACrossOwnerRevealIsPossibleOnlyWhenExplicitlyEnabled()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedEvidenceRowAsync(harness);

        await using var db = harness.NewContext();
        var service = NewService(
            harness, db,
            Cohort("operator-b"),
            Surface(crossOwner: true),
            callerId: "operator-b",
            messages: harness.NewMessageStore(db),
            keyRing: DurableKeyRing());

        var id = (await harness.RowsAsync())[0].Id;

        var result = await service.RevealEvidenceAsync(
            id,
            new CoachOpportunityEvidenceRequest(
                CoachOpportunityLimits.EvidenceRevealAcknowledgement));

        result.IsOk.Should().BeTrue();
        result.Value!.CrossOwner.Should().BeTrue(
            "a cross-owner read is reported as one, so it cannot happen quietly");
    }

    [Fact]
    public async Task AnEphemeralKeyRingRefusesEvidence()
    {
        using var harness = new CoachOpportunityHarness();
        await SeedEvidenceRowAsync(harness);

        await using var db = harness.NewContext();

        // No key ring plan at all, and a HostDefault plan, both mean "ephemeral".
        var noPlan = NewService(
            harness, db, Cohort(Owner), messages: harness.NewMessageStore(db), keyRing: null);

        var id = (await harness.RowsAsync())[0].Id;

        (await noPlan.RevealEvidenceAsync(id, new CoachOpportunityEvidenceRequest(
                CoachOpportunityLimits.EvidenceRevealAcknowledgement)))
            .Status.Should().Be(CoachOpportunityOperatorStatus.EphemeralKeyRing);

        (await harness.RowsAsync())[0].EvidenceRevealCount.Should().Be(0);
    }

    [Fact]
    public async Task AnAggregateOnlyRowHasNoEvidenceToReveal()
    {
        using var harness = new CoachOpportunityHarness();

        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.HarmfulOrUnsafeRequest,
            CoachOpportunityCapabilityCodes.DestructiveRequestRefused,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.AggregateOnly,
            Evidence: new CoachOpportunityEvidencePointer(Conversation, "msg-2", 2, "msg-1", 1)));

        await using var db = harness.NewContext();
        var service = NewService(
            harness, db, Cohort(Owner),
            messages: harness.NewMessageStore(db),
            keyRing: DurableKeyRing());

        var id = (await harness.RowsAsync())[0].Id;

        (await service.RevealEvidenceAsync(id, new CoachOpportunityEvidenceRequest(
                CoachOpportunityLimits.EvidenceRevealAcknowledgement)))
            .Status.Should().Be(CoachOpportunityOperatorStatus.NotAvailable,
                "a refusal never becomes an inspectable dossier, whatever an operator asks for");
    }

    [Fact]
    public async Task AnUnresolvableEvidencePointerReadsAsUnavailableNotAsAnError()
    {
        using var harness = new CoachOpportunityHarness();

        // Pointers to a conversation that was never created — the shape a learner's own
        // conversation deletion leaves behind.
        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.AmbiguousFollowUp,
            CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.Product,
            OfferLink: CoachOpportunityOfferLink.PriorCoachQuestion,
            Evidence: new CoachOpportunityEvidencePointer("conv-deleted", "msg-2", 2, "msg-1", 1)));

        await using var db = harness.NewContext();
        var service = NewService(
            harness, db, Cohort(Owner),
            messages: harness.NewMessageStore(db),
            keyRing: DurableKeyRing());

        var id = (await harness.RowsAsync())[0].Id;

        var result = await service.RevealEvidenceAsync(
            id,
            new CoachOpportunityEvidenceRequest(
                CoachOpportunityLimits.EvidenceRevealAcknowledgement));

        result.IsOk.Should().BeTrue();
        result.Value!.EvidenceState.Should().Be(CoachOpportunityEvidenceState.Unavailable,
            "the ledger row is still a valid product signal without its evidence");
        result.Value.LearnerMessageText.Should().BeNull();
    }

    [Fact]
    public async Task AMissingRowIsIndistinguishableFromAnUnavailableSurface()
    {
        using var harness = new CoachOpportunityHarness();

        await using var db = harness.NewContext();
        var service = NewService(harness, db, Cohort(Owner));

        (await service.GetAsync("does-not-exist")).Status
            .Should().Be(CoachOpportunityOperatorStatus.NotAvailable,
                "telling a caller that a row exists but is off-limits is an oracle for which " +
                "opportunity identifiers exist");
    }

    private static CoachKeyRingPlan DurableKeyRing() =>
        new()
        {
            Mode = CoachKeyRingMode.AzureBlobConnectionString,
            ApplicationName = "SentenceStudio.Coach",
            ContainerName = "coach-keys",
            BlobName = "keyring.xml"
        };

    private static async Task SeedRowAsync(
        CoachOpportunityHarness harness,
        string userProfileId = Owner)
    {
        var recorder = userProfileId == Owner ? harness.Recorder : harness.RecorderFor(userProfileId);

        await recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.UnsupportedCapability,
            CoachOpportunityCapabilityCodes.EntityLookupByName,
            CoachOpportunitySurface.WriteLedger,
            CoachOpportunityDisposition.Product,
            ToolName: CoachToolNames.ProposeVocabularyRemoval,
            Evidence: new CoachOpportunityEvidencePointer(Conversation)));
    }

    private static async Task SeedEvidenceRowAsync(CoachOpportunityHarness harness)
    {
        await using var db = harness.NewContext();
        var conversations = harness.NewConversationStore(db);
        var messages = harness.NewMessageStore(db);
        var owner = CoachOwner.ForUser(Owner);

        await conversations.CreateAsync(
            owner,
            new CreateCoachConversationRequest(
                "Operator", CoachConversationTitleSource.Generated, null, Conversation));

        var offer = await messages.AppendAsync(owner, new AppendCoachMessageRequest(
            Conversation, CoachMessageRole.Coach, CoachMessageKind.Text,
            new CoachMessagePayload
            {
                Kind = CoachMessagePayloadKind.CoachText,
                Text = "Your study time is 10 minutes. Shall I change it to 45 minutes?",
                CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
            }));

        var answer = await messages.AppendAsync(owner, new AppendCoachMessageRequest(
            Conversation, CoachMessageRole.Learner, CoachMessageKind.Text,
            new CoachMessagePayload
            {
                Kind = CoachMessagePayloadKind.LearnerText,
                Text = "yes",
                CreatedAtUtc = harness.Time.GetUtcNow().UtcDateTime
            }));

        await harness.Recorder.RecordAsync(new CoachOpportunitySignal(
            CoachOpportunityKind.AmbiguousFollowUp,
            CoachOpportunityCapabilityCodes.ReferentLostAfterOffer,
            CoachOpportunitySurface.TurnOutcome,
            CoachOpportunityDisposition.Product,
            OfferLink: CoachOpportunityOfferLink.PriorCoachQuestion,
            StopReason: CoachStopReason.ClarificationRequested,
            Evidence: new CoachOpportunityEvidencePointer(
                Conversation,
                answer.Message!.Id, answer.Message.Sequence,
                offer.Message!.Id, offer.Message.Sequence)));
    }
}
