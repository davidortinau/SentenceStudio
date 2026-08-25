using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.Plans;
using Xunit;

namespace SentenceStudio.UnitTests.Services.Plans;

/// <summary>
/// Integration tests for the coach plan-revision lane on <see cref="PlanService"/>
/// against real SQLite: version/hash stability, the pure preview outcomes, the
/// transactional apply merge rules, undo, and rollback.
/// </summary>
public sealed class PlanServiceCoachRevisionTests : IDisposable
{
    private readonly CoachPlanRevisionHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly PlanConstraints ShortNoAudio = new() { AvailableMinutes = 20, AudioAllowed = false };

    /// <summary>Seeds today's plan from the generator's default activity set.</summary>
    private async Task<TodaysPlanDto> SeedPlanAsync()
    {
        _h.Generator.SetDefault(
            ("Reading", "resource-1", "skill-1", 10, 1),
            ("Listening", "resource-1", "skill-1", 10, 2),
            ("Writing", "resource-1", "skill-1", 10, 3));

        return await _h.NewService().GenerateTodayAsync(new GenerateTodaysPlanRequest());
    }

    private static void SetConstrainedPlan(CoachPlanRevisionHarness h) =>
        h.Generator.SetConstrained(
            ("Reading", "resource-2", "skill-1", 8, 1),
            ("Cloze", "resource-2", "skill-1", 8, 2));

    // ------------------------------------------------------------ version

    [Fact]
    public async Task Snapshot_VersionAndHash_AreStableAcrossReads()
    {
        await SeedPlanAsync();

        var first = await _h.NewService().GetTodaySnapshotAsync();
        var second = await _h.NewService().GetTodaySnapshotAsync();

        first.Version.Should().Be(second.Version);
        first.Hash.Should().Be(second.Hash);
        first.Version.Should().StartWith("v1:");
        first.Version.Should().EndWith(first.Hash);
        first.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task Snapshot_VersionChanges_WhenProgressChanges()
    {
        var plan = await SeedPlanAsync();
        var before = await _h.NewService().GetTodaySnapshotAsync();

        await _h.NewService().UpdateProgressAsync(_h.Date.UserLocalDate, plan.Items[0].Id, 5);

        var after = await _h.NewService().GetTodaySnapshotAsync();
        after.Version.Should().NotBe(before.Version);
        after.TotalMinutesSpent.Should().Be(5);
    }

    [Fact]
    public async Task Snapshot_IsUserScoped()
    {
        await SeedPlanAsync();
        var userA = await _h.NewService().GetTodaySnapshotAsync();

        _h.Scope.SetUser(CoachPlanRevisionHarness.UserB);
        var userB = await _h.NewService().GetTodaySnapshotAsync();

        userB.Items.Should().BeEmpty("user B has no plan");
        userB.Version.Should().NotBe(userA.Version);
    }

    [Fact]
    public void Snapshot_Empty_IsDeterministic()
    {
        var date = new DateOnly(2026, 8, 14);
        PlanSnapshot.Empty(date).Version.Should().Be(PlanSnapshot.Empty(date).Version);
        PlanSnapshot.Empty(date).Version.Should().NotBe(PlanSnapshot.Empty(date.AddDays(1)).Version);
    }

    // ------------------------------------------------------------ preview

    [Fact]
    public async Task Preview_WithInvalidConstraints_ReportsInvalidConstraintsAndDoesNotCallGenerator()
    {
        await SeedPlanAsync();
        _h.Generator.Requests.Clear();

        var result = await _h.NewService().PreviewPlanAsync(new PlanConstraints { AvailableMinutes = 500 });

        result.Outcome.Should().Be(PlanPreviewOutcome.InvalidConstraints);
        result.ValidationErrors.Should().ContainSingle();
        result.Snapshot.Should().BeNull();
        _h.Generator.Requests.Should().BeEmpty("invalid constraints are rejected before generation");
    }

    [Fact]
    public async Task Preview_WithNoFeasiblePlan_ReportsNoFeasiblePlanNotNull()
    {
        await SeedPlanAsync();
        _h.Generator.ReturnNullWhenConstrained();

        var result = await _h.NewService().PreviewPlanAsync(ShortNoAudio);

        result.Outcome.Should().Be(PlanPreviewOutcome.NoFeasiblePlan);
        result.Snapshot.Should().BeNull();
        result.ValidationErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_Succeeds_SuppressesWrites_AndIsStable()
    {
        await SeedPlanAsync();
        SetConstrainedPlan(_h);
        var rowsBefore = _h.Rows(CoachPlanRevisionHarness.UserA);
        _h.Generator.Requests.Clear();

        var first = await _h.NewService().PreviewPlanAsync(ShortNoAudio);
        var second = await _h.NewService().PreviewPlanAsync(ShortNoAudio);

        first.Outcome.Should().Be(PlanPreviewOutcome.Success);
        first.PreviewId.Should().NotBeNull();
        second.PreviewId.Should().Be(first.PreviewId, "identical constraints produce a stable preview id");
        first.Snapshot!.Items.Should().OnlyContain(i => i.MinutesSpent == 0 && !i.IsCompleted);

        _h.Generator.Requests.Should().OnlyContain(r => r.AllowWrites == false,
            "a preview must travel the zero-write path");
        _h.Rows(CoachPlanRevisionHarness.UserA).Select(r => r.PlanItemId)
            .Should().Equal(rowsBefore.Select(r => r.PlanItemId), "preview writes nothing");
    }

    // -------------------------------------------------------------- apply

    [Fact]
    public async Task Apply_ReplacesUntouchedItems_AndBumpsVersion()
    {
        await SeedPlanAsync();
        SetConstrainedPlan(_h);
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version,
            OperationKey = "op-1",
            SessionId = "session-1"
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        result.OperationKey.Should().Be("op-1");
        result.BeforePlanVersion.Should().Be(before.Version);
        result.AfterPlanVersion.Should().NotBe(before.Version);
        result.After!.Items.Select(i => i.ActivityType).Should().BeEquivalentTo(new[] { "Reading", "Cloze" });

        var persisted = await _h.NewService().GetTodaySnapshotAsync();
        persisted.Version.Should().Be(result.AfterPlanVersion, "the receipt matches what is on disk");
    }

    [Fact]
    public async Task Apply_WithNoPlan_ReportsPlanNotFoundAndWritesNothing()
    {
        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.PlanNotFound);
        result.After.Should().Be(result.Before);
        _h.Rows(CoachPlanRevisionHarness.UserA).Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_PreservesCompletedItemsUnchanged()
    {
        var plan = await SeedPlanAsync();
        var completedId = plan.Items[0].Id;
        await _h.NewService().MarkCompleteAsync(_h.Date.UserLocalDate, completedId, 12);

        var completedBefore = _h.Row(CoachPlanRevisionHarness.UserA, completedId);
        SetConstrainedPlan(_h);
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        result.PreservedCompletedCount.Should().Be(1);

        var completedAfter = _h.Row(CoachPlanRevisionHarness.UserA, completedId);
        completedAfter.Id.Should().Be(completedBefore.Id);
        completedAfter.IsCompleted.Should().BeTrue();
        completedAfter.MinutesSpent.Should().Be(12);
        completedAfter.CompletedAt.Should().Be(completedBefore.CompletedAt);
        completedAfter.Priority.Should().Be(completedBefore.Priority);
        completedAfter.EstimatedMinutes.Should().Be(completedBefore.EstimatedMinutes);
        completedAfter.ActivityType.Should().Be(completedBefore.ActivityType);
    }

    [Fact]
    public async Task Apply_PreservesStartedItemProgress()
    {
        var plan = await SeedPlanAsync();
        var startedId = plan.Items[1].Id;
        await _h.NewService().UpdateProgressAsync(_h.Date.UserLocalDate, startedId, 7);

        SetConstrainedPlan(_h);
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        result.PreservedInProgressCount.Should().Be(1);
        result.PreservedMinutesSpent.Should().BeGreaterThanOrEqualTo(7);

        var started = _h.Row(CoachPlanRevisionHarness.UserA, startedId);
        started.MinutesSpent.Should().Be(7, "a started item keeps its logged minutes");
        started.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Apply_PreservesMatchingUntouchedItemRow()
    {
        await SeedPlanAsync();
        // The revised plan reuses Reading on resource-1/skill-1, so the stable
        // plan item id matches an existing untouched row.
        _h.Generator.SetConstrained(
            ("Reading", "resource-1", "skill-1", 6, 1),
            ("Cloze", "resource-2", "skill-1", 8, 2));

        var readingBefore = _h.Rows(CoachPlanRevisionHarness.UserA)
            .Single(r => r.ActivityType == "Reading");
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        result.AdjustedItemCount.Should().Be(1);

        var readingAfter = _h.Row(CoachPlanRevisionHarness.UserA, readingBefore.PlanItemId);
        readingAfter.Id.Should().Be(readingBefore.Id, "a matching item keeps its row identity");
        readingAfter.CreatedAt.Should().Be(readingBefore.CreatedAt);
        readingAfter.EstimatedMinutes.Should().Be(6, "the matching item takes the revised estimate");
    }

    [Fact]
    public async Task Apply_WithStaleVersion_IsRejectedAndWritesNothing()
    {
        var plan = await SeedPlanAsync();
        var stale = await _h.NewService().GetTodaySnapshotAsync();

        // Somebody logs progress, invalidating the coach's view.
        await _h.NewService().UpdateProgressAsync(_h.Date.UserLocalDate, plan.Items[0].Id, 4);
        var rowsBefore = _h.Rows(CoachPlanRevisionHarness.UserA);

        SetConstrainedPlan(_h);
        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = stale.Version
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.StalePlanVersion);
        result.BeforePlanVersion.Should().Be(result.AfterPlanVersion);
        _h.Rows(CoachPlanRevisionHarness.UserA).Select(r => r.PlanItemId)
            .Should().Equal(rowsBefore.Select(r => r.PlanItemId));
    }

    [Fact]
    public async Task Apply_WithInvalidConstraints_IsRejectedAndWritesNothing()
    {
        await SeedPlanAsync();
        var rowsBefore = _h.Rows(CoachPlanRevisionHarness.UserA);
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = new PlanConstraints { GoalHorizonDays = 900 },
            ExpectedPlanVersion = before.Version
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.InvalidConstraints);
        result.ValidationErrors.Should().ContainSingle();
        _h.Rows(CoachPlanRevisionHarness.UserA).Select(r => r.PlanItemId)
            .Should().Equal(rowsBefore.Select(r => r.PlanItemId));
    }

    [Fact]
    public async Task Apply_WithNoFeasiblePlan_IsRejectedAndWritesNothing()
    {
        await SeedPlanAsync();
        _h.Generator.ReturnNullWhenConstrained();
        var rowsBefore = _h.Rows(CoachPlanRevisionHarness.UserA);
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.NoFeasiblePlan);
        result.AfterPlanVersion.Should().Be(before.Version);
        _h.Rows(CoachPlanRevisionHarness.UserA).Select(r => r.PlanItemId)
            .Should().Equal(rowsBefore.Select(r => r.PlanItemId));
    }

    [Fact]
    public async Task Apply_Twice_IsIdempotent()
    {
        await SeedPlanAsync();
        SetConstrainedPlan(_h);
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var first = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version,
            OperationKey = "op-dup"
        });
        first.Outcome.Should().Be(PlanRevisionOutcome.Applied);

        var rowsAfterFirst = _h.Rows(CoachPlanRevisionHarness.UserA);

        var second = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = first.AfterPlanVersion,
            OperationKey = "op-dup"
        });

        second.Outcome.Should().Be(PlanRevisionOutcome.NoChange,
            "repeating an applied revision must not write a second time");
        second.BeforePlanVersion.Should().Be(second.AfterPlanVersion);
        second.AfterPlanVersion.Should().Be(first.AfterPlanVersion);

        _h.Rows(CoachPlanRevisionHarness.UserA).Select(r => (r.Id, r.PlanItemId, r.UpdatedAt))
            .Should().Equal(rowsAfterFirst.Select(r => (r.Id, r.PlanItemId, r.UpdatedAt)));
    }

    [Fact]
    public async Task Apply_IsCrossUserIsolated()
    {
        // User B gets a plan first.
        _h.Scope.SetUser(CoachPlanRevisionHarness.UserB);
        await SeedPlanAsync();
        var userBRowsBefore = _h.Rows(CoachPlanRevisionHarness.UserB);
        userBRowsBefore.Should().NotBeEmpty();

        // User A gets their own plan and revises it.
        _h.Scope.SetUser(CoachPlanRevisionHarness.UserA);
        await SeedPlanAsync();
        SetConstrainedPlan(_h);
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.Applied);

        _h.Rows(CoachPlanRevisionHarness.UserB)
            .Select(r => (r.Id, r.PlanItemId, r.EstimatedMinutes, r.Priority))
            .Should().Equal(userBRowsBefore.Select(r => (r.Id, r.PlanItemId, r.EstimatedMinutes, r.Priority)),
                "one learner's coach revision never touches another learner's plan");
    }

    [Fact]
    public async Task Apply_WithAnotherUsersPlanVersion_IsStale()
    {
        _h.Scope.SetUser(CoachPlanRevisionHarness.UserB);
        await SeedPlanAsync();
        var userBVersion = (await _h.NewService().GetTodaySnapshotAsync()).Version;

        _h.Scope.SetUser(CoachPlanRevisionHarness.UserA);
        await SeedPlanAsync();
        // User A's plan happens to be structurally identical, so force a
        // difference before asserting the version gate.
        var plan = _h.Rows(CoachPlanRevisionHarness.UserA);
        await _h.NewService().UpdateProgressAsync(_h.Date.UserLocalDate, plan[0].PlanItemId, 3);

        SetConstrainedPlan(_h);
        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = userBVersion
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.StalePlanVersion);
    }

    // --------------------------------------------------------------- undo

    [Fact]
    public async Task Undo_RestoresThePreviousRemainingPlan()
    {
        await SeedPlanAsync();
        SetConstrainedPlan(_h);
        var original = await _h.NewService().GetTodaySnapshotAsync();

        var applied = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = original.Version
        });
        applied.Outcome.Should().Be(PlanRevisionOutcome.Applied);

        var undo = await _h.NewService().UndoCoachRevisionAsync(new CoachPlanUndoRequest
        {
            TargetSnapshot = applied.Before!,
            ExpectedPlanVersion = applied.AfterPlanVersion,
            OperationKey = "undo-1"
        });

        undo.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        undo.OperationKey.Should().Be("undo-1");
        undo.AfterPlanVersion.Should().Be(original.Version,
            "restoring the before-snapshot restores the original plan version");

        _h.Rows(CoachPlanRevisionHarness.UserA).Select(r => r.PlanItemId)
            .Should().Equal(original.Items.Select(i => i.PlanItemId));
    }

    [Fact]
    public async Task Undo_WithStaleVersion_IsRejectedAndWritesNothing()
    {
        await SeedPlanAsync();
        SetConstrainedPlan(_h);
        var original = await _h.NewService().GetTodaySnapshotAsync();

        var applied = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = original.Version
        });

        var rowsBefore = _h.Rows(CoachPlanRevisionHarness.UserA);

        var undo = await _h.NewService().UndoCoachRevisionAsync(new CoachPlanUndoRequest
        {
            TargetSnapshot = applied.Before!,
            ExpectedPlanVersion = original.Version // already superseded
        });

        undo.Outcome.Should().Be(PlanRevisionOutcome.StalePlanVersion);
        _h.Rows(CoachPlanRevisionHarness.UserA).Select(r => r.PlanItemId)
            .Should().Equal(rowsBefore.Select(r => r.PlanItemId));
    }

    [Fact]
    public async Task Undo_AfterAdditionalCompletion_KeepsCompletedWorkAndMinutes()
    {
        var plan = await SeedPlanAsync();
        SetConstrainedPlan(_h);
        var original = await _h.NewService().GetTodaySnapshotAsync();

        var applied = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = original.Version
        });
        applied.Outcome.Should().Be(PlanRevisionOutcome.Applied);

        // The learner finishes one of the revised items before undoing.
        var revisedItemId = applied.After!.Items.First().PlanItemId;
        await _h.NewService().MarkCompleteAsync(_h.Date.UserLocalDate, revisedItemId, 9);
        var currentVersion = (await _h.NewService().GetTodaySnapshotAsync()).Version;
        var minutesBeforeUndo = (await _h.NewService().GetTodaySnapshotAsync()).TotalMinutesSpent;

        var undo = await _h.NewService().UndoCoachRevisionAsync(new CoachPlanUndoRequest
        {
            TargetSnapshot = applied.Before!,
            ExpectedPlanVersion = currentVersion
        });

        undo.Outcome.Should().Be(PlanRevisionOutcome.Applied);

        var after = await _h.NewService().GetTodaySnapshotAsync();
        after.TotalMinutesSpent.Should().BeGreaterThanOrEqualTo(minutesBeforeUndo,
            "undo never lowers logged minutes");

        var completed = _h.Row(CoachPlanRevisionHarness.UserA, revisedItemId);
        completed.IsCompleted.Should().BeTrue("undo never un-completes work");
        completed.MinutesSpent.Should().Be(9);

        // The original remaining items are back alongside the completed one.
        after.Items.Select(i => i.PlanItemId).Should().Contain(revisedItemId);
        after.Items.Select(i => i.PlanItemId).Should()
            .Contain(original.Items.Select(i => i.PlanItemId));
        _ = plan;
    }

    [Fact]
    public async Task Undo_ToCurrentState_IsNoChange()
    {
        await SeedPlanAsync();
        var current = await _h.NewService().GetTodaySnapshotAsync();

        var undo = await _h.NewService().UndoCoachRevisionAsync(new CoachPlanUndoRequest
        {
            TargetSnapshot = current,
            ExpectedPlanVersion = current.Version
        });

        undo.Outcome.Should().Be(PlanRevisionOutcome.NoChange);
        undo.AfterPlanVersion.Should().Be(current.Version);
    }

    [Fact]
    public async Task Undo_WithSnapshotFromAnotherDate_IsRejected()
    {
        await SeedPlanAsync();
        var current = await _h.NewService().GetTodaySnapshotAsync();
        var wrongDate = PlanSnapshot.FromItems(_h.Date.UserLocalDate.AddDays(-1), current.Items);

        var undo = await _h.NewService().UndoCoachRevisionAsync(new CoachPlanUndoRequest
        {
            TargetSnapshot = wrongDate,
            ExpectedPlanVersion = current.Version
        });

        undo.Outcome.Should().Be(PlanRevisionOutcome.ValidationFailed);
        undo.ValidationErrors.Should().ContainSingle();
    }

    // ---------------------------------------------------- ambient transaction

    [Fact]
    public async Task Apply_InsideAmbientTransaction_JoinsItWithoutCommittingIt()
    {
        await SeedPlanAsync();
        SetConstrainedPlan(_h);

        var (service, db) = _h.NewServiceWithContext();
        await using var ambient = await db.Database.BeginTransactionAsync();

        var result = await service.ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        db.Database.CurrentTransaction.Should().NotBeNull(
            "the revision must join the caller's transaction, never commit or dispose it");

        // The caller still owns the decision: rolling back discards the revision.
        await ambient.RollbackAsync();
        db.ChangeTracker.Clear();

        _h.Rows(CoachPlanRevisionHarness.UserA).Should().NotContain(r => r.ActivityType == "Cloze",
            "the caller's rollback discards the joined revision");
    }

    [Fact]
    public async Task Apply_InsideAmbientTransaction_RollsBackOnlyItsOwnWorkOnFailure()
    {
        var plan = await SeedPlanAsync();
        var completedId = plan.Items[0].Id;
        await _h.NewService().MarkCompleteAsync(_h.Date.UserLocalDate, completedId, 12);
        SetConstrainedPlan(_h);

        var (service, db) = _h.NewServiceWithContext();
        await using var ambient = await db.Database.BeginTransactionAsync();

        // The caller does its own write first; the revision must not undo it.
        var callerRow = db.DailyPlans.Single(p => p.UserProfileId == CoachPlanRevisionHarness.UserA);
        callerRow.Strategy = "caller-owned";
        await db.SaveChangesAsync();

        _h.Sabotage.ArmDeleteOf(_h.Row(CoachPlanRevisionHarness.UserA, completedId).Id);

        var result = await service.ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.ValidationFailed);
        db.Database.CurrentTransaction.Should().NotBeNull();

        await ambient.CommitAsync();
        db.ChangeTracker.Clear();

        using var verify = _h.NewDbContext();
        verify.DailyPlans.Single(p => p.UserProfileId == CoachPlanRevisionHarness.UserA)
            .Strategy.Should().Be("caller-owned", "the savepoint rollback kept the caller's own write");
        _h.Rows(CoachPlanRevisionHarness.UserA).Should().NotContain(r => r.ActivityType == "Cloze",
            "the failed revision's writes were undone");
        _h.Row(CoachPlanRevisionHarness.UserA, completedId).IsCompleted.Should().BeTrue(
            "the injected delete was undone with the savepoint");
    }

    [Fact]
    public async Task Undo_InsideAmbientTransaction_JoinsItWithoutCommittingIt()
    {
        await SeedPlanAsync();
        SetConstrainedPlan(_h);
        var original = await _h.NewService().GetTodaySnapshotAsync();

        var applied = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = original.Version
        });
        applied.Outcome.Should().Be(PlanRevisionOutcome.Applied);

        var (service, db) = _h.NewServiceWithContext();
        await using var ambient = await db.Database.BeginTransactionAsync();

        var undo = await service.UndoCoachRevisionAsync(new CoachPlanUndoRequest
        {
            TargetSnapshot = applied.Before!,
            ExpectedPlanVersion = applied.AfterPlanVersion
        });

        undo.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        db.Database.CurrentTransaction.Should().NotBeNull();

        await ambient.CommitAsync();
        db.ChangeTracker.Clear();

        _h.Rows(CoachPlanRevisionHarness.UserA).Select(r => r.PlanItemId)
            .Should().Equal(original.Items.Select(i => i.PlanItemId));
    }

    // ----------------------------------------------------------- rollback

    [Fact]
    public async Task Apply_RollsBackTheWholeTransaction_WhenAPostWriteInvariantFails()
    {
        var plan = await SeedPlanAsync();
        var completedId = plan.Items[0].Id;
        await _h.NewService().MarkCompleteAsync(_h.Date.UserLocalDate, completedId, 12);

        SetConstrainedPlan(_h);
        var rowsBefore = _h.Rows(CoachPlanRevisionHarness.UserA);
        var before = await _h.NewService().GetTodaySnapshotAsync();

        // Make the completed row vanish inside the transaction, immediately
        // after the revision's own write succeeds. The post-write invariant
        // check must catch it and roll the whole transaction back.
        var completedRowId = _h.Row(CoachPlanRevisionHarness.UserA, completedId).Id;
        _h.Sabotage.ArmDeleteOf(completedRowId);

        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.ValidationFailed);
        result.ValidationErrors.Should().Contain(e => e.Contains("Completed item"));
        result.AfterPlanVersion.Should().Be(before.Version, "a rolled-back revision reports the plan unchanged");

        var rowsAfter = _h.Rows(CoachPlanRevisionHarness.UserA);
        rowsAfter.Select(r => (r.Id, r.PlanItemId, r.EstimatedMinutes, r.Priority, r.MinutesSpent, r.IsCompleted))
            .Should().Equal(rowsBefore.Select(r => (r.Id, r.PlanItemId, r.EstimatedMinutes, r.Priority, r.MinutesSpent, r.IsCompleted)),
                "the rollback undid both the revision's writes and the injected delete");
        rowsAfter.Should().NotContain(r => r.ActivityType == "Cloze", "no new item survived the rollback");

        var snapshotAfter = await _h.NewService().GetTodaySnapshotAsync();
        snapshotAfter.Version.Should().Be(before.Version);
    }

    // ----------------------------------------------------- invariant rules

    [Fact]
    public void ValidateRevisedPlan_RejectsLostCompletedWorkAndLostMinutes()
    {
        var date = new DateOnly(2026, 8, 14);
        var before = PlanSnapshot.FromItems(date, new[]
        {
            new PlanSnapshotItem
            {
                PlanItemId = "done", ActivityType = "Reading", Priority = 1,
                EstimatedMinutes = 10, MinutesSpent = 10, IsCompleted = true
            },
            new PlanSnapshotItem
            {
                PlanItemId = "started", ActivityType = "Cloze", Priority = 2,
                EstimatedMinutes = 10, MinutesSpent = 4, IsCompleted = false
            }
        });

        PlanService.ValidateRevisedPlan(before, PlanSnapshot.Empty(date))
            .Should().HaveCountGreaterThanOrEqualTo(3, "removing completed and started work is a violation");

        var uncompleted = PlanSnapshot.FromItems(date, before.Items
            .Select(i => i.PlanItemId == "done" ? i with { IsCompleted = false } : i));
        PlanService.ValidateRevisedPlan(before, uncompleted)
            .Should().Contain(e => e.Contains("lost its completed state"));

        var lowered = PlanSnapshot.FromItems(date, before.Items
            .Select(i => i.PlanItemId == "started" ? i with { MinutesSpent = 1 } : i));
        PlanService.ValidateRevisedPlan(before, lowered)
            .Should().Contain(e => e.Contains("lost logged minutes"));

        PlanService.ValidateRevisedPlan(before, before).Should().BeEmpty();
    }

    [Fact]
    public void ValidateRevisedPlan_RejectsDuplicatePlanItemIds()
    {
        var date = new DateOnly(2026, 8, 14);
        var item = new PlanSnapshotItem
        {
            PlanItemId = "dupe", ActivityType = "Reading", Priority = 1,
            EstimatedMinutes = 10, MinutesSpent = 0, IsCompleted = false
        };
        var after = PlanSnapshot.FromItems(date, new[] { item, item with { Priority = 2 } });

        PlanService.ValidateRevisedPlan(PlanSnapshot.Empty(date), after)
            .Should().Contain(e => e.Contains("Duplicate plan item id"));
    }
}
