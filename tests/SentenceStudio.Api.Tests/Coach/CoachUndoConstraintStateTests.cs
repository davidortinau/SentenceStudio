using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The constraint set behind Undo.
/// </summary>
/// <remarks>
/// <para>
/// Regression cover for a live defect. A session applied <c>AvailableMinutes=5</c> and
/// <c>AudioAllowed=false</c>, then Undo restored the earlier ten-minute, two-item plan — but the
/// session's active constraints stayed at 5 minutes / no audio. A later suggestion whose
/// normalized delta disclosed only <c>audio allowed</c> merged against those stale constraints,
/// so its trusted preview showed a five-minute plan with the second activity removed. Accepting
/// it would have silently re-applied a minutes constraint the learner had already undone and
/// which the delta never mentioned.
/// </para>
/// <para>
/// Undo now restores the constraint set that produced the plan it restores, read from the
/// revision's own audit envelope.
/// </para>
/// </remarks>
public class CoachUndoConstraintStateTests
{
    // A two-item, ten-minute plan: the state the reported session started from.
    private static readonly PlanSnapshotItem Reading =
        Item("plan-reading", PlanActivityType.Reading, priority: 1, minutes: 5);

    private static readonly PlanSnapshotItem Listening =
        Item("plan-listening", PlanActivityType.Listening, priority: 2, minutes: 5);

    // ---------------------------------------------------------------- the reported sequence

    [Fact]
    public async Task UndoingTheLatestRevision_RevertsOnlyThatRevisionsFields()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();
        var baseline = await ActiveConstraintsAsync(harness, sessionId);

        // Revision 1: five minutes.
        harness.PlanService.NextRemainder = [Item("r1", PlanActivityType.Cloze, 1, 5)];
        await DirectAsync(harness, sessionId, d => d.AvailableMinutes = 5);

        // Revision 2: no audio, as a separate revision.
        harness.PlanService.NextRemainder = [Item("r2", PlanActivityType.Writing, 1, 5)];
        await DirectAsync(harness, sessionId, d => d.AudioAllowed = false);

        var afterBoth = await ActiveConstraintsAsync(harness, sessionId);
        afterBoth.AvailableMinutes.Should().Be(5);
        afterBoth.AudioAllowed.Should().BeFalse();

        var undo = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());
        undo.IsOk.Should().BeTrue();

        // The five minutes were never undone, so they survive. The audio switch was, so it
        // returns to what it was before revision 2.
        var afterUndo = await ActiveConstraintsAsync(harness, sessionId);
        afterUndo.AvailableMinutes.Should().Be(5);
        afterUndo.AudioAllowed.Should().Be(baseline.AudioAllowed);
        undo.Value!.ActiveConstraints.AvailableMinutes.Should().Be(5);
        undo.Value.ActiveConstraints.AudioAllowed.Should().Be(baseline.AudioAllowed);
    }

    [Fact]
    public async Task UndoingAgain_ReturnsToTheSessionBaseline()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();
        var baseline = await ActiveConstraintsAsync(harness, sessionId);

        harness.PlanService.NextRemainder = [Item("r1", PlanActivityType.Cloze, 1, 5)];
        await DirectAsync(harness, sessionId, d => d.AvailableMinutes = 5);

        harness.PlanService.NextRemainder = [Item("r2", PlanActivityType.Writing, 1, 5)];
        await DirectAsync(harness, sessionId, d => d.AudioAllowed = false);

        await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());
        var second = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        second.IsOk.Should().BeTrue();

        var restored = await ActiveConstraintsAsync(harness, sessionId);
        restored.AvailableMinutes.Should().Be(baseline.AvailableMinutes);
        restored.AudioAllowed.Should().Be(baseline.AudioAllowed);
        restored.SpeechAllowed.Should().Be(baseline.SpeechAllowed);
        restored.TypingAllowed.Should().Be(baseline.TypingAllowed);
        restored.EnergyLevel.Should().Be(baseline.EnergyLevel);
    }

    [Fact]
    public async Task AfterUndo_ASuggestionAppliesOnlyTheFieldItsDeltaDiscloses()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();
        var baseline = await ActiveConstraintsAsync(harness, sessionId);

        harness.PlanService.NextRemainder = [Item("r1", PlanActivityType.Cloze, 1, 5)];
        await DirectAsync(harness, sessionId, d => { d.AvailableMinutes = 5; d.AudioAllowed = false; });
        await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        // The exact follow-up from the report: a suggestion disclosing only "audio allowed".
        harness.PlanService.NextRemainder =
        [
            Item("suggested-listening", PlanActivityType.Listening, 1, 5),
            Item("suggested-reading", PlanActivityType.Reading, 2, 5)
        ];

        var suggestion = (await SuggestAsync(harness, sessionId, d => d.AudioAllowed = true))
            .Value!.PendingSuggestion!;

        suggestion.Delta.ChangedFields.Should().BeEquivalentTo(new[] { CoachConstraintField.AudioAllowed });
        suggestion.Rationale.Should().Be("I prepared a change for your review: audio allowed.");

        var accepted = await harness.Service.AcceptSuggestionAsync(
            sessionId, suggestion.SuggestionId, new CoachSuggestionDecisionRequest());

        accepted.IsOk.Should().BeTrue();

        // The only field that moved is the one the delta named. Minutes are back at the
        // baseline, not the undone 5.
        var applied = accepted.Value!.ActiveConstraints;
        applied.AudioAllowed.Should().BeTrue();
        applied.AvailableMinutes.Should().Be(
            baseline.AvailableMinutes,
            "an undone minutes constraint must not ride along inside a suggestion that never mentioned it");

        harness.PlanService.LastAppliedConstraints!.AvailableMinutes.Should().Be(baseline.AvailableMinutes);
        harness.PlanService.LastAppliedConstraints.AudioAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task AfterUndo_ASuggestionPreviewIsBuiltFromTheRestoredConstraints()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();
        var baseline = await ActiveConstraintsAsync(harness, sessionId);

        harness.PlanService.NextRemainder = [Item("r1", PlanActivityType.Cloze, 1, 5)];
        await DirectAsync(harness, sessionId, d => d.AvailableMinutes = 5);
        await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        harness.PlanService.NextRemainder =
        [
            Item("suggested-listening", PlanActivityType.Listening, 1, 5),
            Item("suggested-reading", PlanActivityType.Reading, 2, 5)
        ];

        await SuggestAsync(harness, sessionId, d => d.AudioAllowed = true);

        // The preview the learner is shown was planned against the restored set, so the plan
        // the preview promises is the plan acceptance produces.
        harness.PlanService.LastPreviewConstraints!.AvailableMinutes.Should().Be(baseline.AvailableMinutes);
    }

    // ---------------------------------------------------------------- persistence and audit

    [Fact]
    public async Task TheRestoredConstraintsSurviveASessionReload()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();
        var baseline = await ActiveConstraintsAsync(harness, sessionId);

        harness.PlanService.NextRemainder = [Item("r1", PlanActivityType.Cloze, 1, 5)];
        await DirectAsync(harness, sessionId, d => d.AvailableMinutes = 5);
        await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        var reloaded = (await harness.Service.GetSessionAsync(sessionId)).Value!.ActiveConstraints;

        reloaded.AvailableMinutes.Should().Be(baseline.AvailableMinutes);

        // And on the row itself, not only in the response projection.
        var stored = CoachActiveStateEnvelope.TryRead(
            harness.Db.CoachSessions.Single().ActiveConstraintsJson)!.Constraints;
        stored.AvailableMinutes.Should().Be(baseline.AvailableMinutes);
    }

    [Fact]
    public async Task EachRevisionRecordsTheConstraintsOnBothSidesOfItsChange()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.PlanService.NextRemainder = [Item("r1", PlanActivityType.Cloze, 1, 5)];
        await DirectAsync(harness, sessionId, d => d.AvailableMinutes = 5);

        var revision = harness.Db.CoachPlanRevisions.Single();
        var before = ReadConstraints(revision.BeforePlanSnapshotJson);
        var after = ReadConstraints(revision.AfterPlanSnapshotJson);

        // Both sides used to carry the post-apply set, which is what made the before-state
        // unrecoverable and Undo unable to restore it.
        after.AvailableMinutes.Should().Be(5);
        before.AvailableMinutes.Should().NotBe(5);
    }

    [Fact]
    public async Task TheUndoAuditRemainsAdditiveAndMarksItsTarget()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.PlanService.NextRemainder = [Item("r1", PlanActivityType.Cloze, 1, 5)];
        await DirectAsync(harness, sessionId, d => d.AvailableMinutes = 5);
        await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        var revisions = harness.Db.CoachPlanRevisions.OrderBy(r => r.RevisionNumber).ToList();

        revisions.Should().HaveCount(2, "undo appends a revision, it never deletes one");
        revisions[0].IsUndone.Should().BeTrue();
        revisions[0].UndoneByRevisionId.Should().Be(revisions[1].Id);
        revisions[1].Source.Should().Be(CoachRevisionSource.Undo);

        // The undo record itself states which set it moved away from and which it landed on.
        ReadConstraints(revisions[1].BeforePlanSnapshotJson).AvailableMinutes.Should().Be(5);
        ReadConstraints(revisions[1].AfterPlanSnapshotJson).AvailableMinutes.Should().NotBe(5);
    }

    [Fact]
    public async Task ALegacyRevisionWithNoRecoverableBeforeSet_LeavesConstraintsAlone()
    {
        using var harness = NewHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.PlanService.NextRemainder = [Item("r1", PlanActivityType.Cloze, 1, 5)];
        await DirectAsync(harness, sessionId, d => d.AvailableMinutes = 5);

        // Rewrite the row the way the old code wrote it: the same set on both sides.
        var revision = harness.Db.CoachPlanRevisions.Single();
        revision.BeforePlanSnapshotJson = revision.AfterPlanSnapshotJson;
        await harness.Db.SaveChangesAsync();

        var undo = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        // Restoring a value known to be wrong would be worse than leaving it: the plan is
        // still restored, and the constraints are simply not touched.
        undo.IsOk.Should().BeTrue();
        (await ActiveConstraintsAsync(harness, sessionId)).AvailableMinutes.Should().Be(5);
    }

    // ---------------------------------------------------------------- helpers

    private static CoachApplicationHarness NewHarness()
    {
        var harness = new CoachApplicationHarness();
        harness.PlanService.SetItems([Reading, Listening]);
        return harness;
    }

    private static async Task<CoachConstraintSetDto> ActiveConstraintsAsync(
        CoachApplicationHarness harness, string sessionId) =>
        (await harness.Service.GetSessionAsync(sessionId)).Value!.ActiveConstraints;

    private static CoachConstraintSetDto ReadConstraints(string envelopeJson) =>
        CoachNormalizedJson.Deserialize<CoachRevisionSnapshotEnvelope>(envelopeJson)!.State.AppliedConstraints;

    private static Task<CoachOperationResult<CoachTurnResponse>> DirectAsync(
        CoachApplicationHarness harness, string sessionId, Action<CoachConstraintDeltaIntent> configure)
    {
        var delta = new CoachConstraintDeltaIntent();
        configure(delta);

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.DirectConstraintChange,
                ConstraintDelta = delta,
                CoachMessage = "Done."
            }
        };

        return harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "change it"
        });
    }

    private static Task<CoachOperationResult<CoachTurnResponse>> SuggestAsync(
        CoachApplicationHarness harness, string sessionId, Action<CoachConstraintDeltaIntent> configure)
    {
        var delta = new CoachConstraintDeltaIntent();
        configure(delta);

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                ConstraintDelta = delta,
                CoachMessage = "A suggestion."
            }
        };

        return harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "suggest something"
        });
    }

    private static PlanSnapshotItem Item(string id, PlanActivityType type, int priority, int minutes) => new()
    {
        PlanItemId = id,
        ActivityType = type.ToString(),
        ResourceId = $"resource-{id}",
        Priority = priority,
        EstimatedMinutes = minutes
    };
}
