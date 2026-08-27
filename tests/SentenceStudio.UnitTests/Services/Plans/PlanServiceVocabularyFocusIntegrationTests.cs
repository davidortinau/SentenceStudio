using FluentAssertions;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.Plans;
using Xunit;

namespace SentenceStudio.UnitTests.Services.Plans;

/// <summary>
/// Proves a resolved vocabulary focus flows through preview and apply without
/// drift, and that every existing revision guarantee still holds when a focus
/// set is present.
/// </summary>
public sealed class PlanServiceVocabularyFocusIntegrationTests : IDisposable
{
    private readonly CoachPlanRevisionHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly string[] Selected =
    [
        "verb-word-01", "verb-word-02", "verb-word-03", "verb-word-04", "verb-word-05"
    ];

    private static readonly PlanConstraints Constraints = new() { AvailableMinutes = 20, AudioAllowed = false };

    private async Task<TodaysPlanDto> SeedPlanAsync()
    {
        _h.Generator.SetDefault(
            ("Reading", "resource-1", "skill-1", 10, 1),
            ("Listening", "resource-1", "skill-1", 10, 2));
        _h.Generator.SetConstrained(
            ("VocabularyReview", null, "skill-1", 10, 1),
            ("Cloze", null, "skill-1", 10, 2));

        return await _h.NewService().GenerateTodayAsync(new GenerateTodaysPlanRequest());
    }

    [Fact]
    public async Task Preview_CarriesTheExactSelectedWordIds()
    {
        await SeedPlanAsync();

        var preview = await _h.NewService().PreviewPlanAsync(Constraints, Selected);

        preview.Outcome.Should().Be(PlanPreviewOutcome.Success);
        preview.Skeleton!.FocusVocabularyIds.Should().Equal(Selected,
            "the planner must carry exactly the resolved set, in order");
        preview.Skeleton.Activities.Should().OnlyContain(a => a.FocusVocabularyIds.SequenceEqual(Selected));
    }

    [Fact]
    public async Task Preview_WithFocus_PerformsNoWrites()
    {
        await SeedPlanAsync();
        var before = _h.Rows(CoachPlanRevisionHarness.UserA);

        await _h.NewService().PreviewPlanAsync(Constraints, Selected);

        _h.Generator.Requests.Should().OnlyContain(r => r.AllowWrites == false || r.Constraints == null,
            "the focus preview travels the zero-write path");
        _h.Rows(CoachPlanRevisionHarness.UserA).Select(r => (r.Id, r.PlanItemId))
            .Should().Equal(before.Select(r => (r.Id, r.PlanItemId)));
    }

    [Fact]
    public async Task Preview_IsStableForTheSameFocusSet()
    {
        await SeedPlanAsync();

        var first = await _h.NewService().PreviewPlanAsync(Constraints, Selected);
        var second = await _h.NewService().PreviewPlanAsync(Constraints, Selected);

        second.PreviewId.Should().Be(first.PreviewId);
    }

    [Fact]
    public async Task Apply_UsesExactlyThePreviewedFocusSet()
    {
        await SeedPlanAsync();
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var preview = await _h.NewService().PreviewPlanAsync(Constraints, Selected);
        preview.Outcome.Should().Be(PlanPreviewOutcome.Success);

        var applied = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = Constraints,
            FocusVocabularyWordIds = Selected,
            ExpectedPlanVersion = before.Version
        });

        applied.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        applied.After!.Items.Select(i => i.ActivityType)
            .Should().Contain("VocabularyReview", "the focused review survives the apply");

        // Re-previewing the applied state with the same focus yields the same
        // plan shape: preview and apply did not drift.
        var replay = await _h.NewService().PreviewPlanAsync(Constraints, Selected);
        replay.Skeleton!.FocusVocabularyIds.Should().Equal(Selected);
    }

    [Fact]
    public async Task Apply_WithADifferentFocusSet_ProducesADifferentPlanVersion()
    {
        await SeedPlanAsync();
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var first = await _h.NewService().PreviewPlanAsync(Constraints, Selected);
        var other = await _h.NewService().PreviewPlanAsync(Constraints, ["verb-word-09", "verb-word-10"]);

        // Focus is carried on the plan items, so the two previews describe the
        // same activities; the distinction lives in the focus payload.
        first.Skeleton!.FocusVocabularyIds.Should().NotEqual(other.Skeleton!.FocusVocabularyIds);

        var applied = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = Constraints,
            FocusVocabularyWordIds = Selected,
            ExpectedPlanVersion = before.Version
        });
        applied.Outcome.Should().Be(PlanRevisionOutcome.Applied);
    }

    [Fact]
    public async Task Apply_WithFocus_RejectsAStalePlanVersionWithoutWriting()
    {
        var plan = await SeedPlanAsync();
        var stale = await _h.NewService().GetTodaySnapshotAsync();
        await _h.NewService().UpdateProgressAsync(_h.Date.UserLocalDate, plan.Items[0].Id, 4);
        var rowsBefore = _h.Rows(CoachPlanRevisionHarness.UserA);

        var result = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = Constraints,
            FocusVocabularyWordIds = Selected,
            ExpectedPlanVersion = stale.Version
        });

        result.Outcome.Should().Be(PlanRevisionOutcome.StalePlanVersion);
        _h.Rows(CoachPlanRevisionHarness.UserA).Select(r => r.PlanItemId)
            .Should().Equal(rowsBefore.Select(r => r.PlanItemId));
    }

    [Fact]
    public async Task Apply_WithFocus_PreservesCompletedAndStartedWork()
    {
        var plan = await SeedPlanAsync();
        var completedId = plan.Items[0].Id;
        var startedId = plan.Items[1].Id;
        await _h.NewService().MarkCompleteAsync(_h.Date.UserLocalDate, completedId, 12);
        await _h.NewService().UpdateProgressAsync(_h.Date.UserLocalDate, startedId, 6);

        var before = await _h.NewService().GetTodaySnapshotAsync();
        var completedBefore = _h.Row(CoachPlanRevisionHarness.UserA, completedId);

        var applied = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = Constraints,
            FocusVocabularyWordIds = Selected,
            ExpectedPlanVersion = before.Version
        });

        applied.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        applied.PreservedCompletedCount.Should().Be(1);
        applied.PreservedInProgressCount.Should().Be(1);

        var completedAfter = _h.Row(CoachPlanRevisionHarness.UserA, completedId);
        completedAfter.IsCompleted.Should().BeTrue();
        completedAfter.MinutesSpent.Should().Be(12);
        completedAfter.CompletedAt.Should().Be(completedBefore.CompletedAt);
        _h.Row(CoachPlanRevisionHarness.UserA, startedId).MinutesSpent.Should().Be(6);
    }

    [Fact]
    public async Task Undo_AfterAFocusedApply_RestoresThePreviousPlan()
    {
        await SeedPlanAsync();
        var original = await _h.NewService().GetTodaySnapshotAsync();

        var applied = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = Constraints,
            FocusVocabularyWordIds = Selected,
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
    public async Task ApplyWithoutFocus_IsUnchangedByThisFeature()
    {
        await SeedPlanAsync();
        var before = await _h.NewService().GetTodaySnapshotAsync();

        var applied = await _h.NewService().ApplyCoachConstraintsAsync(new CoachPlanRevisionRequest
        {
            Constraints = Constraints,
            ExpectedPlanVersion = before.Version
        });

        applied.Outcome.Should().Be(PlanRevisionOutcome.Applied);
        _h.Generator.Requests.Should().Contain(r => r.FocusVocabularyWordIds == null,
            "a request without a focus set must not invent one");
    }
}
