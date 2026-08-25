using FluentAssertions;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.Plans;
using Xunit;

namespace SentenceStudio.UnitTests.Services.Plans;

/// <summary>
/// Regression tests for the production 500 seen on PostgreSQL:
/// <c>NpgsqlRetryingExecutionStrategy does not support user-initiated
/// transactions</c>. The harness configures a SQLite execution strategy whose
/// <c>RetriesOnFailure</c> is true, which triggers the identical EF guard, so a
/// hand-rolled <c>BeginTransactionAsync</c> throws here exactly as it did on
/// Azure PostgreSQL.
/// </summary>
public sealed class PlanServiceCoachRevisionRetryStrategyTests : IDisposable
{
    private readonly CoachPlanRevisionHarness _h = CoachPlanRevisionHarness.CreateWithRetryingExecutionStrategy();

    public void Dispose() => _h.Dispose();

    private static readonly PlanConstraints ShortNoAudio = new() { AvailableMinutes = 10, AudioAllowed = false };

    private async Task<TodaysPlanDto> SeedPlanAsync()
    {
        _h.Generator.SetDefault(
            ("Reading", "resource-1", "skill-1", 10, 1),
            ("Listening", "resource-1", "skill-1", 10, 2),
            ("Writing", "resource-1", "skill-1", 10, 3));

        return await _h.NewService().GenerateTodayAsync(new GenerateTodaysPlanRequest());
    }

    private void SetConstrainedPlan() =>
        _h.Generator.SetConstrained(
            ("Reading", "resource-2", "skill-1", 5, 1),
            ("Cloze", "resource-2", "skill-1", 5, 2));

    [Fact]
    public async Task Apply_UnderRetryingExecutionStrategy_Succeeds()
    {
        await SeedPlanAsync();
        SetConstrainedPlan();
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version,
            OperationKey = "retry-op-1"
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.Applied,
            "the revision must run as a retriable unit instead of a hand-rolled transaction");
        result.AfterPlanVersion.Should().NotBe(before.Version);
        result.After!.Items.Select(i => i.ActivityType).Should().BeEquivalentTo(new[] { "Reading", "Cloze" });

        var persisted = await _h.NewService().GetTodaySnapshotAsync();
        persisted.Version.Should().Be(result.AfterPlanVersion);
    }

    [Fact]
    public async Task Apply_UnderRetryingExecutionStrategy_PreservesCompletedWork()
    {
        var plan = await SeedPlanAsync();
        var completedId = plan.Items[0].Id;
        await _h.NewService().MarkCompleteAsync(_h.Date.UserLocalDate, completedId, 11);
        var completedBefore = _h.Row(CoachPlanRevisionHarness.UserA, completedId);

        SetConstrainedPlan();
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        result.PreservedCompletedCount.Should().Be(1);

        var completedAfter = _h.Row(CoachPlanRevisionHarness.UserA, completedId);
        completedAfter.IsCompleted.Should().BeTrue();
        completedAfter.MinutesSpent.Should().Be(11);
        completedAfter.CompletedAt.Should().Be(completedBefore.CompletedAt);
    }

    [Fact]
    public async Task Apply_UnderRetryingExecutionStrategy_StillRejectsStaleVersionWithoutWriting()
    {
        var plan = await SeedPlanAsync();
        var stale = await _h.NewService().GetTodaySnapshotAsync();
        await _h.NewService().UpdateProgressAsync(_h.Date.UserLocalDate, plan.Items[0].Id, 4);
        var rowsBefore = _h.Rows(CoachPlanRevisionHarness.UserA);

        SetConstrainedPlan();
        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = stale.Version
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.StalePlanVersion);
        _h.Rows(CoachPlanRevisionHarness.UserA).Select(r => r.PlanItemId)
            .Should().Equal(rowsBefore.Select(r => r.PlanItemId));
    }

    [Fact]
    public async Task Apply_UnderRetryingExecutionStrategy_RollsBackOnInvariantFailure()
    {
        var plan = await SeedPlanAsync();
        var completedId = plan.Items[0].Id;
        await _h.NewService().MarkCompleteAsync(_h.Date.UserLocalDate, completedId, 12);

        SetConstrainedPlan();
        var rowsBefore = _h.Rows(CoachPlanRevisionHarness.UserA);
        var before = await _h.NewService().GetTodaySnapshotAsync();

        _h.Sabotage.ArmDeleteOf(_h.Row(CoachPlanRevisionHarness.UserA, completedId).Id);

        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.ValidationFailed);
        _h.Rows(CoachPlanRevisionHarness.UserA)
            .Select(r => (r.Id, r.PlanItemId, r.MinutesSpent, r.IsCompleted))
            .Should().Equal(rowsBefore.Select(r => (r.Id, r.PlanItemId, r.MinutesSpent, r.IsCompleted)),
                "rollback must still work when the unit runs inside an execution strategy");
    }

    [Fact]
    public async Task Apply_UnderRetryingExecutionStrategy_IsIdempotent()
    {
        await SeedPlanAsync();
        SetConstrainedPlan();
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var first = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = before.Version
        });
        first.Outcome.Should().Be(PlanRevisionOutcome.Applied);

        var second = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = ShortNoAudio,
            ExpectedPlanVersion = first.AfterPlanVersion
        });

        second.Outcome.Should().Be(PlanRevisionOutcome.NoChange);
        second.AfterPlanVersion.Should().Be(first.AfterPlanVersion);
    }

    [Fact]
    public async Task Undo_UnderRetryingExecutionStrategy_Succeeds()
    {
        await SeedPlanAsync();
        SetConstrainedPlan();
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
            ExpectedPlanVersion = applied.AfterPlanVersion
        });

        undo.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        undo.AfterPlanVersion.Should().Be(original.Version);
        _h.Rows(CoachPlanRevisionHarness.UserA).Select(r => r.PlanItemId)
            .Should().Equal(original.Items.Select(i => i.PlanItemId));
    }

    [Fact]
    public async Task Undo_UnderRetryingExecutionStrategy_NoChangeDoesNotThrow()
    {
        await SeedPlanAsync();
        var current = await _h.NewService().GetTodaySnapshotAsync();

        var undo = await _h.NewService().UndoCoachRevisionAsync(new CoachPlanUndoRequest
        {
            TargetSnapshot = current,
            ExpectedPlanVersion = current.Version
        });

        undo.Outcome.Should().Be(PlanRevisionOutcome.NoChange);
    }
}
