using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The suggestion preview, and the promise it makes to the learner.
/// </summary>
/// <remarks>
/// Regression cover for an end-to-end defect: the pending preview was built by diffing the
/// current plan against the planner's <b>raw</b> remainder, so completed and started rows came
/// back as <c>Removed</c> with both preserved counts at zero — a preview that appeared to throw
/// away finished work the apply path would never touch. The same turn also surfaced a
/// Speaking-emphasis suggestion whose preview contained no speaking activity at all.
/// </remarks>
public class CoachSuggestionPreviewTests
{
    // The state from the reported session: one completed Reading, one started VocabularyReview,
    // and two untouched remaining items after a 10-minute / no-audio direct revision.
    private static readonly PlanSnapshotItem CompletedReading =
        Item("done-reading", PlanActivityType.Reading, priority: 1, minutes: 5, spent: 5, completed: true);

    private static readonly PlanSnapshotItem StartedVocabulary =
        Item("started-vocab", PlanActivityType.VocabularyReview, priority: 2, minutes: 5, spent: 2, completed: false);

    private static readonly PlanSnapshotItem RemainingCloze =
        Item("remaining-cloze", PlanActivityType.Cloze, priority: 3, minutes: 4, spent: 0, completed: false);

    private static readonly PlanSnapshotItem RemainingTranslation =
        Item("remaining-translation", PlanActivityType.Translation, priority: 4, minutes: 4, spent: 0, completed: false);

    // ---------------------------------------------------------------- preservation

    [Fact]
    public async Task Preview_MarksCompletedAndStartedRowsAsPreservedNotRemoved()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();

        // The planner's remainder for the proposed constraints. It never contains the
        // learner's completed or started rows — that is exactly what tripped the defect.
        harness.PlanService.NextRemainder =
        [
            Item("preview-conversation", PlanActivityType.Conversation, priority: 1, minutes: 4, spent: 0, completed: false)
        ];

        var preview = (await SuggestAsync(harness, sessionId, CoachSkillEmphasis.Speaking))
            .Value!.PendingSuggestion!.Preview;

        var completed = preview.Items.Single(i => i.Id == CompletedReading.PlanItemId);
        var started = preview.Items.Single(i => i.Id == StartedVocabulary.PlanItemId);

        completed.ChangeKind.Should().Be(CoachPlanItemChangeKind.PreservedCompleted);
        started.ChangeKind.Should().Be(CoachPlanItemChangeKind.PreservedInProgress);

        preview.PreservedCompletedItemCount.Should().Be(1);
        preview.PreservedInProgressItemCount.Should().Be(1);
        preview.Items.Should().NotContain(
            i => i.Id == CompletedReading.PlanItemId && i.ChangeKind == CoachPlanItemChangeKind.Removed);
    }

    [Fact]
    public async Task Preview_KeepsLoggedMinutesOnPreservedRows()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.PlanService.NextRemainder =
        [
            Item("preview-conversation", PlanActivityType.Conversation, priority: 1, minutes: 4, spent: 0, completed: false)
        ];

        var preview = (await SuggestAsync(harness, sessionId, CoachSkillEmphasis.Speaking))
            .Value!.PendingSuggestion!.Preview;

        preview.Items.Single(i => i.Id == CompletedReading.PlanItemId).MinutesSpent.Should().Be(5);
        preview.Items.Single(i => i.Id == StartedVocabulary.PlanItemId).MinutesSpent.Should().Be(2);
        preview.Items.Single(i => i.Id == CompletedReading.PlanItemId).IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Preview_ReplacesOnlyUntouchedRemainingWork()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.PlanService.NextRemainder =
        [
            Item("preview-conversation", PlanActivityType.Conversation, priority: 1, minutes: 4, spent: 0, completed: false)
        ];

        var preview = (await SuggestAsync(harness, sessionId, CoachSkillEmphasis.Speaking))
            .Value!.PendingSuggestion!.Preview;

        preview.Items.Single(i => i.Id == "preview-conversation").ChangeKind
            .Should().Be(CoachPlanItemChangeKind.Added);
        preview.Items.Single(i => i.Id == RemainingCloze.PlanItemId).ChangeKind
            .Should().Be(CoachPlanItemChangeKind.Removed);
        preview.Items.Single(i => i.Id == RemainingTranslation.PlanItemId).ChangeKind
            .Should().Be(CoachPlanItemChangeKind.Removed);

        preview.RemovedItemCount.Should().Be(2);
        preview.AddedItemCount.Should().Be(1);
    }

    [Fact]
    public async Task Preview_EstimatedMinutesUseTheSameDefinitionAsTheApply()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.PlanService.NextRemainder =
        [
            Item("preview-conversation", PlanActivityType.Conversation, priority: 1, minutes: 4, spent: 0, completed: false)
        ];

        var suggestion = (await SuggestAsync(harness, sessionId, CoachSkillEmphasis.Speaking))
            .Value!.PendingSuggestion!;

        // Before: the whole current plan (5 + 5 + 4 + 4). After: preserved (5 + 5) plus the
        // new remainder (4) — not the bare remainder the raw preview would have reported.
        suggestion.Preview.EstimatedMinutesBefore.Should().Be(18);
        suggestion.Preview.EstimatedMinutesAfter.Should().Be(14);

        var accepted = await harness.Service.AcceptSuggestionAsync(
            sessionId, suggestion.SuggestionId, new CoachSuggestionDecisionRequest());

        accepted.Value!.ChangeReceipt!.Diff.EstimatedMinutesBefore
            .Should().Be(suggestion.Preview.EstimatedMinutesBefore);
        accepted.Value.ChangeReceipt.Diff.EstimatedMinutesAfter
            .Should().Be(suggestion.Preview.EstimatedMinutesAfter);
    }

    // ---------------------------------------------------------------- preview / accept parity

    [Fact]
    public async Task AcceptingASuggestion_ProducesTheSamePlanVersionAndDiffAsThePreview()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.PlanService.NextRemainder =
        [
            Item("preview-conversation", PlanActivityType.Conversation, priority: 1, minutes: 4, spent: 0, completed: false)
        ];

        var suggestion = (await SuggestAsync(harness, sessionId, CoachSkillEmphasis.Speaking))
            .Value!.PendingSuggestion!;

        var accepted = await harness.Service.AcceptSuggestionAsync(
            sessionId, suggestion.SuggestionId, new CoachSuggestionDecisionRequest());

        var previewDiff = suggestion.Preview;
        var appliedDiff = accepted.Value!.ChangeReceipt!.Diff;

        appliedDiff.BeforePlanVersion.Should().Be(previewDiff.BeforePlanVersion);
        appliedDiff.AfterPlanVersion.Should().Be(
            previewDiff.AfterPlanVersion,
            "a learner who accepts must get exactly the plan the preview showed them");

        appliedDiff.AddedItemCount.Should().Be(previewDiff.AddedItemCount);
        appliedDiff.RemovedItemCount.Should().Be(previewDiff.RemovedItemCount);
        appliedDiff.AdjustedItemCount.Should().Be(previewDiff.AdjustedItemCount);
        appliedDiff.PreservedCompletedItemCount.Should().Be(previewDiff.PreservedCompletedItemCount);
        appliedDiff.PreservedInProgressItemCount.Should().Be(previewDiff.PreservedInProgressItemCount);

        appliedDiff.Items.Select(i => (i.Id, i.ChangeKind))
            .Should().BeEquivalentTo(previewDiff.Items.Select(i => (i.Id, i.ChangeKind)));

        // And the plan really is what the preview promised.
        harness.PlanService.Current.Version.Should().Be(previewDiff.AfterPlanVersion);
    }

    [Fact]
    public async Task ReReadingTheSession_ShowsTheSamePreviewItWasOffered()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.PlanService.NextRemainder =
        [
            Item("preview-conversation", PlanActivityType.Conversation, priority: 1, minutes: 4, spent: 0, completed: false)
        ];

        var offered = (await SuggestAsync(harness, sessionId, CoachSkillEmphasis.Speaking))
            .Value!.PendingSuggestion!;

        var reread = (await harness.Service.GetSessionAsync(sessionId)).Value!.PendingSuggestion!;

        reread.SuggestionId.Should().Be(offered.SuggestionId);
        reread.Preview.AfterPlanVersion.Should().Be(offered.Preview.AfterPlanVersion);
        reread.Preview.PreservedCompletedItemCount.Should().Be(1);
        reread.Preview.PreservedInProgressItemCount.Should().Be(1);
    }

    // ---------------------------------------------------------------- effectiveness

    [Fact]
    public async Task ASpeakingEmphasisTheRemainderCannotDeliver_IsNotOffered()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();

        // Precisely the reported defect: the rationale claims better active-skill balance,
        // but the plan the planner would build is vocabulary plus reading.
        harness.PlanService.NextRemainder =
        [
            Item("preview-vocab", PlanActivityType.VocabularyReview, priority: 1, minutes: 4, spent: 0, completed: false),
            Item("preview-reading", PlanActivityType.Reading, priority: 2, minutes: 4, spent: 0, completed: false)
        ];

        var result = await SuggestAsync(harness, sessionId, CoachSkillEmphasis.Speaking);

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        result.Value.StopReason.Should().Be(CoachStopReason.ValidationFailed);
        result.Value.PendingSuggestion.Should().BeNull();

        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().BeNull();
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task ASpeakingEmphasisTheRemainderDoesDeliver_IsOffered()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.PlanService.NextRemainder =
        [
            Item("preview-conversation", PlanActivityType.Conversation, priority: 1, minutes: 4, spent: 0, completed: false)
        ];

        var result = await SuggestAsync(harness, sessionId, CoachSkillEmphasis.Speaking);

        result.Value!.PendingSuggestion.Should().NotBeNull();
        result.Value.PendingSuggestion!.Preview.Items
            .Should().Contain(i => i.ActivityType == CoachPlanActivityType.Conversation
                                   && i.ChangeKind == CoachPlanItemChangeKind.Added);
        harness.PlanService.ApplyCallCount.Should().Be(0, "a suggestion previews and never writes");
    }

    [Fact]
    public async Task ASuggestionThatChangesNothing_IsNotOffered()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();

        // The planner returns exactly the remaining work the learner already has.
        harness.PlanService.NextRemainder =
        [
            RemainingCloze with { Priority = 1 },
            RemainingTranslation with { Priority = 2 }
        ];

        var result = await SuggestAsync(harness, sessionId, emphasis: null);

        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        result.Value.PendingSuggestion.Should().BeNull();
        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().BeNull();
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task ASuggestionWhoseRemainderBreaksItsOwnModalitySwitch_IsNotOffered()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();

        // Audio is turned off, yet the remainder the planner returned needs audio.
        harness.PlanService.NextRemainder =
        [
            Item("preview-listening", PlanActivityType.Listening, priority: 1, minutes: 4, spent: 0, completed: false)
        ];

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                ConstraintDelta = new CoachConstraintDeltaIntent { AudioAllowed = false },
                CoachMessage = "This keeps today quiet."
            }
        };

        var result = await SubmitTextAsync(harness, sessionId, "suggest something");

        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        result.Value.PendingSuggestion.Should().BeNull();
        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().BeNull();
    }

    [Fact]
    public async Task RejectingAnOfferedSuggestion_StillWritesNothing()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.PlanService.NextRemainder =
        [
            Item("preview-conversation", PlanActivityType.Conversation, priority: 1, minutes: 4, spent: 0, completed: false)
        ];

        var suggestion = (await SuggestAsync(harness, sessionId, CoachSkillEmphasis.Speaking))
            .Value!.PendingSuggestion!;

        var planBefore = harness.PlanService.Current.Version;

        await harness.Service.RejectSuggestionAsync(
            sessionId, suggestion.SuggestionId, new CoachSuggestionDecisionRequest());

        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.PlanService.Current.Version.Should().Be(planBefore);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().BeNull();
    }

    // ---------------------------------------------------------------- helpers

    private static CoachApplicationHarness NewHarness()
    {
        var harness = new CoachApplicationHarness();
        harness.PlanService.SetItems([CompletedReading, StartedVocabulary, RemainingCloze, RemainingTranslation]);
        return harness;
    }

    private static Task<CoachOperationResult<CoachTurnResponse>> SubmitTextAsync(
        CoachApplicationHarness harness, string sessionId, string text) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text
        });

    private static Task<CoachOperationResult<CoachTurnResponse>> SuggestAsync(
        CoachApplicationHarness harness, string sessionId, CoachSkillEmphasis? emphasis)
    {
        // The claim below is now checked against what the turn read, so the read has to happen.
        harness.SeedPracticeBalanceRead();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                ConstraintDelta = emphasis is null
                    ? new CoachConstraintDeltaIntent { AvailableMinutes = 14 }
                    : new CoachConstraintDeltaIntent { SkillEmphasis = emphasis },
                CoachMessage = "This would balance your active skills.",
                EvidenceReferences =
                [
                    new CoachEvidenceReferenceIntent { Kind = CoachEvidenceKind.PracticeBalance, WindowDays = 14 }
                ]
            }
        };

        return SubmitTextAsync(
            harness, sessionId, "Suggest one useful change for better skill balance, but do not apply it yet.");
    }

    private static PlanSnapshotItem Item(
        string id, PlanActivityType type, int priority, int minutes, int spent, bool completed) => new()
        {
            PlanItemId = id,
            ActivityType = type.ToString(),
            ResourceId = $"resource-{id}",
            SkillId = null,
            Priority = priority,
            EstimatedMinutes = minutes,
            MinutesSpent = spent,
            IsCompleted = completed
        };
}
