using FluentAssertions;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.Plans;
using Xunit;

namespace SentenceStudio.UnitTests.Services.Plans;

/// <summary>
/// Pins <see cref="PlanRevisionPreview.Merge"/> to the real
/// <see cref="PlanService.ApplyCoachConstraintsAsync"/> against real SQLite.
/// </summary>
/// <remarks>
/// The coach shows a suggestion preview before any write happens, so the preview has to be
/// built from <see cref="IPlanService.PreviewPlanAsync"/> — the planner's whole remainder,
/// with nothing completed and priorities starting from the top. If the merge that turns that
/// remainder into "the plan you would get" ever drifts from what the apply path actually
/// persists, a learner accepts one plan and receives another. These tests are the contract
/// that keeps the two in step.
/// </remarks>
public sealed class PlanRevisionPreviewParityTests : IDisposable
{
    private readonly CoachPlanRevisionHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly PlanConstraints ShortNoAudio = new() { AvailableMinutes = 20, AudioAllowed = false };

    private async Task<TodaysPlanDto> SeedPlanAsync()
    {
        _h.Generator.SetDefault(
            ("Reading", "resource-1", "skill-1", 10, 1),
            ("Listening", "resource-1", "skill-1", 10, 2),
            ("Writing", "resource-1", "skill-1", 10, 3));

        _h.Generator.SetConstrained(
            ("Cloze", "resource-2", "skill-1", 8, 1),
            ("Translation", "resource-2", "skill-1", 8, 2));

        return await _h.NewService().GenerateTodayAsync(new GenerateTodaysPlanRequest());
    }

    [Fact]
    public async Task MergedPreview_MatchesTheAppliedPlan_WhenWorkIsCompletedAndStarted()
    {
        var plan = await SeedPlanAsync();

        // One completed item and one started item — the state the preview used to discard.
        await _h.NewService().MarkCompleteAsync(_h.Date.UserLocalDate, plan.Items[0].Id, 10);
        await _h.NewService().UpdateProgressAsync(_h.Date.UserLocalDate, plan.Items[1].Id, 4);

        var before = await _h.NewService().GetTodaySnapshotAsync();
        var preview = await _h.NewService().PreviewPlanAsync(ShortNoAudio);
        preview.IsSuccess.Should().BeTrue();

        var projected = PlanRevisionPreview.Merge(before, preview.Snapshot!);

        var applied = await _h.NewService().ApplyCoachConstraintsAsync(
            new CoachPlanRevisionRequest { Constraints = ShortNoAudio, ExpectedPlanVersion = before.Version });

        applied.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        projected.Version.Should().Be(applied.After!.Version);
        projected.Hash.Should().Be(applied.After.Hash);

        projected.Items.Select(i => (i.PlanItemId, i.Priority, i.EstimatedMinutes, i.MinutesSpent, i.IsCompleted))
            .Should().BeEquivalentTo(
                applied.After.Items.Select(i => (i.PlanItemId, i.Priority, i.EstimatedMinutes, i.MinutesSpent, i.IsCompleted)));
    }

    [Fact]
    public async Task MergedPreview_MatchesTheAppliedPlan_WhenNothingHasBeenTouched()
    {
        await SeedPlanAsync();

        var before = await _h.NewService().GetTodaySnapshotAsync();
        var preview = await _h.NewService().PreviewPlanAsync(ShortNoAudio);

        var projected = PlanRevisionPreview.Merge(before, preview.Snapshot!);

        var applied = await _h.NewService().ApplyCoachConstraintsAsync(
            new CoachPlanRevisionRequest { Constraints = ShortNoAudio, ExpectedPlanVersion = before.Version });

        projected.Version.Should().Be(applied.After!.Version);
    }

    [Fact]
    public async Task MergedPreview_KeepsCompletedWorkAndLoggedMinutes()
    {
        var plan = await SeedPlanAsync();
        await _h.NewService().MarkCompleteAsync(_h.Date.UserLocalDate, plan.Items[0].Id, 10);
        await _h.NewService().UpdateProgressAsync(_h.Date.UserLocalDate, plan.Items[1].Id, 4);

        var before = await _h.NewService().GetTodaySnapshotAsync();
        var preview = await _h.NewService().PreviewPlanAsync(ShortNoAudio);

        var projected = PlanRevisionPreview.Merge(before, preview.Snapshot!);

        projected.CompletedItemCount.Should().Be(1);
        projected.InProgressItemCount.Should().Be(1);
        projected.TotalMinutesSpent.Should().Be(before.TotalMinutesSpent);
        projected.Items.Should().Contain(i => i.PlanItemId == plan.Items[0].Id && i.IsCompleted);
        projected.Items.Should().Contain(i => i.PlanItemId == plan.Items[1].Id && i.MinutesSpent == 4);
    }

    [Fact]
    public async Task MergedPreview_SlotsNewWorkBehindEverythingTheLearnerTouched()
    {
        var plan = await SeedPlanAsync();
        await _h.NewService().MarkCompleteAsync(_h.Date.UserLocalDate, plan.Items[0].Id, 10);
        await _h.NewService().UpdateProgressAsync(_h.Date.UserLocalDate, plan.Items[1].Id, 4);

        var before = await _h.NewService().GetTodaySnapshotAsync();
        var preview = await _h.NewService().PreviewPlanAsync(ShortNoAudio);

        var projected = PlanRevisionPreview.Merge(before, preview.Snapshot!);

        var preservedIds = before.Items.Where(PlanRevisionPreview.IsPreserved)
            .Select(i => i.PlanItemId).ToHashSet(StringComparer.Ordinal);
        var highestPreserved = projected.Items.Where(i => preservedIds.Contains(i.PlanItemId)).Max(i => i.Priority);

        projected.Items.Where(i => !preservedIds.Contains(i.PlanItemId))
            .Should().OnlyContain(i => i.Priority > highestPreserved);
    }

    [Fact]
    public async Task MergedPreview_IsPure_AndTheRawPreviewAloneWouldHaveDroppedFinishedWork()
    {
        var plan = await SeedPlanAsync();
        await _h.NewService().MarkCompleteAsync(_h.Date.UserLocalDate, plan.Items[0].Id, 10);

        var before = await _h.NewService().GetTodaySnapshotAsync();
        var preview = await _h.NewService().PreviewPlanAsync(ShortNoAudio);

        // The raw preview genuinely does not contain the completed row. That is correct for a
        // planner preview and is exactly why callers must merge rather than diff it directly.
        preview.Snapshot!.Items.Should().NotContain(i => i.PlanItemId == plan.Items[0].Id);
        preview.Snapshot.CompletedItemCount.Should().Be(0);

        PlanRevisionPreview.Merge(before, preview.Snapshot)
            .Items.Should().Contain(i => i.PlanItemId == plan.Items[0].Id && i.IsCompleted);

        // And nothing was written by any of it.
        var after = await _h.NewService().GetTodaySnapshotAsync();
        after.Version.Should().Be(before.Version);
    }

    [Fact]
    public async Task Remainder_ExcludesEveryPreservedRow()
    {
        var plan = await SeedPlanAsync();
        await _h.NewService().MarkCompleteAsync(_h.Date.UserLocalDate, plan.Items[0].Id, 10);
        await _h.NewService().UpdateProgressAsync(_h.Date.UserLocalDate, plan.Items[1].Id, 4);

        var before = await _h.NewService().GetTodaySnapshotAsync();
        var preview = await _h.NewService().PreviewPlanAsync(ShortNoAudio);
        var projected = PlanRevisionPreview.Merge(before, preview.Snapshot!);

        var remainder = PlanRevisionPreview.Remainder(projected);

        remainder.Should().NotContain(i => i.PlanItemId == plan.Items[0].Id);
        remainder.Should().NotContain(i => i.PlanItemId == plan.Items[1].Id);
        remainder.Should().OnlyContain(i => !i.IsCompleted && i.MinutesSpent == 0);
    }
}
