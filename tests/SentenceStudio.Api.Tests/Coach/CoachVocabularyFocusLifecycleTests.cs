using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Services.Progress;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Undo, receipts, and preview-to-accept parity for a vocabulary focus.
/// </summary>
/// <remarks>
/// A focus is a set of specific words, so every one of these paths has the same failure mode: the
/// learner is shown one set and given another. The defence is that the selection is resolved once
/// and then only ever replayed — never re-derived on reload, on acceptance, or on an unrelated
/// later change.
/// </remarks>
public class CoachVocabularyFocusLifecycleTests
{
    private static readonly string[] FirstSet = ["v-1", "v-2", "v-3", "v-4", "v-5"];

    // ---------------------------------------------------------------- undo

    [Fact]
    public async Task Undo_RestoresTheExactFocusThatProducedTheRestoredPlan()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        // Focus, then an unrelated change on top of it.
        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        // The fake planner returns the same remainder every time, so a second apply would be a
        // no-op and write no revision. Vary it so the minutes turn is a real second revision.
        NextTurnProducesADifferentPlan(harness);
        harness.Coach.NextResult = MinutesResult(30);
        await AskAsync(harness, sessionId, "make it 30 minutes");

        // Undoing the minutes must leave the focus exactly as it was, not drop it and not
        // re-resolve it.
        var undone = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        undone.IsOk.Should().BeTrue();

        // Guard against a vacuous pass: both turns must have written a revision, and the undo
        // must actually have rewritten the constraint state. Otherwise "the focus survived"
        // would only mean "nothing happened".
        // One accepted focus, one direct minutes change.
        harness.Db.CoachPlanRevisions.Count(r => r.Source != CoachRevisionSource.Undo)
            .Should().Be(2);

        // Asserted on stored state and on the next read, as the rest of the undo suite does: the
        // undo response itself still projects the pre-undo constraint set.
        var stored = CoachActiveStateEnvelope.TryRead(
            harness.Db.CoachSessions.Single().ActiveConstraintsJson)!;
        stored.Constraints.AvailableMinutes.Should().Be(20, "the session baseline is 20 minutes and the 30-minute revision was undone");
        stored.FocusSelection!.VocabularyWordIds.Should().Equal(
            FirstSet, "the focus that produced the restored plan came back with it");

        var focus = (await harness.Service.GetSessionAsync(sessionId)).Value!
            .ActiveConstraints.VocabularyFocus;
        focus.Should().NotBeNull();
        focus!.FocusCode.Should().Be(CoachVocabularyFocusAliases.ActionVerbCode);
        harness.FocusResolver.ResolveCount.Should().Be(1, "an undo never resolves");
    }

    [Fact]
    public async Task Undo_OfTheFocusItself_ReturnsToNoFocus()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        var undone = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        undone.Value!.ActiveConstraints.VocabularyFocus
            .Should().BeNull("the revision that introduced the focus recorded no focus before it");
    }

    [Fact]
    public async Task TheRestoredFocusSurvivesASessionReload()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        // The fake planner returns the same remainder every time, so a second apply would be a
        // no-op and write no revision. Vary it so the minutes turn is a real second revision.
        NextTurnProducesADifferentPlan(harness);
        harness.Coach.NextResult = MinutesResult(30);
        await AskAsync(harness, sessionId, "make it 30 minutes");

        await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        var reloaded = (await harness.Service.GetSessionAsync(sessionId)).Value!.ActiveConstraints;
        reloaded.VocabularyFocus.Should().NotBeNull();
        reloaded.VocabularyFocus!.FocusCode.Should().Be(CoachVocabularyFocusAliases.ActionVerbCode);

        // The stored artifact, not just the response projection.
        var stored = CoachActiveStateEnvelope.TryRead(
            harness.Db.CoachSessions.Single().ActiveConstraintsJson)!;
        stored.FocusSelection!.VocabularyWordIds.Should().Equal(FirstSet);
    }

    [Fact]
    public async Task TheUndoAuditHoldsIdentifiersAndCountsButNoWord()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");
        await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        foreach (var revision in harness.Db.CoachPlanRevisions.ToList())
        {
            foreach (var json in new[] { revision.BeforePlanSnapshotJson, revision.AfterPlanSnapshotJson })
            {
                // The revision row is permanent. It records which words, never what they are.
                json.Should().NotContain("\uAC00\uB2E4");
                json.Should().NotContain("to go");
                json.Should().NotContain("active verbs", "not even the learner's own wording");
            }
        }

        var applied = harness.Db.CoachPlanRevisions
            .First(r => r.Source == CoachRevisionSource.AcceptedSuggestion);
        CoachNormalizedJson.Deserialize<CoachRevisionSnapshotEnvelope>(applied.AfterPlanSnapshotJson)!
            .FocusSelection!.VocabularyWordIds.Should().Equal(FirstSet);
    }

    [Fact]
    public async Task ARevisionWithoutTheArtifact_KeepsTheCurrentFocusRatherThanGuessing()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        // The fake planner returns the same remainder every time, so a second apply would be a
        // no-op and write no revision. Vary it so the minutes turn is a real second revision.
        NextTurnProducesADifferentPlan(harness);
        harness.Coach.NextResult = MinutesResult(30);
        await AskAsync(harness, sessionId, "make it 30 minutes");

        // Simulate a row written before the focus artifact existed.
        var latest = harness.Db.CoachPlanRevisions.OrderBy(r => r.RevisionNumber).Last();
        var envelope = CoachNormalizedJson.Deserialize<CoachRevisionSnapshotEnvelope>(latest.BeforePlanSnapshotJson)!;
        latest.BeforePlanSnapshotJson = CoachNormalizedJson.Serialize(envelope with { FocusSelection = null });
        harness.Db.SaveChanges();

        var undone = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        // Guessing which words a past plan used would be worse than leaving it alone.
        undone.Value!.ActiveConstraints.VocabularyFocus.Should().NotBeNull();
        harness.FocusResolver.ResolveCount.Should().Be(1);
    }

    [Fact]
    public async Task ARevisionThatChangedNoConstraint_IsStillRestorable()
    {
        // The defect that produced all of the above. A current revision whose constraints did not
        // change looks exactly like a pre-stamping row under an equality test, so Undo used to
        // read it as legacy, restore nothing, and report success — the learner undid a change and
        // kept its constraints. The schema version is what separates the two now.
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        var revision = harness.Db.CoachPlanRevisions.Single();
        var before = CoachNormalizedJson
            .Deserialize<CoachRevisionSnapshotEnvelope>(revision.BeforePlanSnapshotJson)!;
        var after = CoachNormalizedJson
            .Deserialize<CoachRevisionSnapshotEnvelope>(revision.AfterPlanSnapshotJson)!;

        // Force the two sides to identical constraints, which is what a plan-only change produces.
        revision.AfterPlanSnapshotJson = CoachNormalizedJson.Serialize(
            after with { State = before.State });
        harness.Db.SaveChanges();

        var undone = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        undone.IsOk.Should().BeTrue();
        CoachActiveStateEnvelope.TryRead(harness.Db.CoachSessions.Single().ActiveConstraintsJson)!
            .Constraints.AvailableMinutes.Should().Be(before.State.AppliedConstraints.AvailableMinutes);
    }

    [Fact]
    public async Task ALegacyRevisionWithoutAVersion_KeepsWhatIsInForce()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        // A row written before the schema carried a version, with both sides equal — the only
        // shape that is genuinely unrecoverable.
        var revision = harness.Db.CoachPlanRevisions.Single();
        var before = CoachNormalizedJson
            .Deserialize<CoachRevisionSnapshotEnvelope>(revision.BeforePlanSnapshotJson)!;
        revision.BeforePlanSnapshotJson = StripVersion(before);
        revision.AfterPlanSnapshotJson = StripVersion(before);
        harness.Db.SaveChanges();

        await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        var stored = CoachActiveStateEnvelope.TryRead(
            harness.Db.CoachSessions.Single().ActiveConstraintsJson)!;
        stored.FocusSelection!.VocabularyWordIds.Should().Equal(
            FirstSet, "an unrecoverable row keeps the focus in force rather than guessing");
    }

    [Fact]
    public async Task ARevisionFromANewerSchema_IsRefusedNotTreatedAsANoOp()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        var revision = harness.Db.CoachPlanRevisions.Single();
        var before = CoachNormalizedJson
            .Deserialize<CoachRevisionSnapshotEnvelope>(revision.BeforePlanSnapshotJson)!;
        revision.BeforePlanSnapshotJson = CoachNormalizedJson.Serialize(
            before with { Version = CoachRevisionSnapshotEnvelope.CurrentVersion + 5 });
        harness.Db.SaveChanges();

        var undone = await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        // A typed refusal, not "nothing to restore" dressed up as success.
        undone.IsOk.Should().BeFalse();
        undone.Status.Should().Be(CoachOperationStatus.InvalidConstraint);
        harness.Db.CoachSessions.Single().StopReason.Should().Be(CoachStopReason.ValidationFailed);
    }

    [Fact]
    public async Task Undo_OfAClear_BringsTheFocusBack()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        NextTurnProducesADifferentPlan(harness);
        harness.Coach.NextResult = ClearFocusResult();
        await AskAsync(harness, sessionId, "clear vocabulary focus");

        (await harness.Service.GetSessionAsync(sessionId)).Value!
            .ActiveConstraints.VocabularyFocus.Should().BeNull();

        await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        var stored = CoachActiveStateEnvelope.TryRead(
            harness.Db.CoachSessions.Single().ActiveConstraintsJson)!;
        stored.Constraints.VocabularyFocus!.FocusCode
            .Should().Be(CoachVocabularyFocusAliases.ActionVerbCode);
        stored.FocusSelection!.VocabularyWordIds.Should().Equal(FirstSet);
    }

    [Fact]
    public async Task Undo_OfAReplacedFocus_RestoresTheExactOldSet()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        // A second, different focus with a different set.
        harness.FocusResolver.NextResult = harness.FocusResolver.NextResult with
        {
            Items =
            [
                FakeVocabularyFocusResolver.Word("a-1", "크다", "to be big"),
                FakeVocabularyFocusResolver.Word("a-2", "작다", "to be small")
            ]
        };

        NextTurnProducesADifferentPlan(harness);
        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("adjectives"), "focus today on adjectives");

        await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        var stored = CoachActiveStateEnvelope.TryRead(
            harness.Db.CoachSessions.Single().ActiveConstraintsJson)!;
        stored.FocusSelection!.VocabularyWordIds.Should().Equal(FirstSet);
        stored.Constraints.VocabularyFocus!.FocusCode
            .Should().Be(CoachVocabularyFocusAliases.ActionVerbCode);
        stored.Constraints.VocabularyFocus.Words.Select(w => w.TargetText)
            .Should().Equal("\uAC00\uB2E4", "\uBA39\uB2E4", "\uBCF4\uB2E4", "\uD558\uB2E4", "\uC77D\uB2E4");
    }

    [Fact]
    public async Task Undo_PreservesCompletedAndStartedWork()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var completedBefore = harness.PlanService.Current.Items.Count(i => i.IsCompleted);
        var startedBefore = harness.PlanService.Current.Items.Count(i => !i.IsCompleted && i.MinutesSpent > 0);

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");
        await harness.Service.UndoAsync(sessionId, new CoachUndoRequest());

        harness.PlanService.Current.Items.Count(i => i.IsCompleted).Should().Be(completedBefore);
        harness.PlanService.Current.Items.Count(i => !i.IsCompleted && i.MinutesSpent > 0)
            .Should().Be(startedBefore);
    }

    // ---------------------------------------------------------------- envelope round-trip

    [Fact]
    public void TheEnvelopeRoundTripsWithAndWithoutAFocus()
    {
        var state = new CoachPlanStateDto
        {
            PlanDate = new DateOnly(2026, 8, 15),
            PlanVersion = "v1:abc",
            Items = [],
            AppliedConstraints = CoachConstraintMapper.Default(20),
            EstimatedTotalMinutes = 20,
            CompletedCount = 0,
            TotalCount = 0,
            CompletionPercentage = 0
        };

        var withoutFocus = new CoachRevisionSnapshotEnvelope
        {
            Version = CoachRevisionSnapshotEnvelope.CurrentVersion,
            State = state,
            Restore = PlanSnapshot.Empty(new DateOnly(2026, 8, 15))
        };

        var withFocus = withoutFocus with
        {
            FocusSelection = new CoachFocusSelection
            {
                FocusCode = CoachVocabularyFocusAliases.ActionVerbCode,
                VocabularyWordIds = FirstSet,
                ResolvedForPlanVersion = "v1:abc",
                EligibleCount = 12
            }
        };

        foreach (var envelope in new[] { withoutFocus, withFocus })
        {
            var round = CoachNormalizedJson.Deserialize<CoachRevisionSnapshotEnvelope>(
                CoachNormalizedJson.Serialize(envelope))!;

            round.Version.Should().Be(CoachRevisionSnapshotEnvelope.CurrentVersion);
            round.State.AppliedConstraints.AvailableMinutes.Should().Be(20);
            round.FocusSelection?.VocabularyWordIds.Should().BeEquivalentTo(envelope.FocusSelection?.VocabularyWordIds);
        }
    }

    [Fact]
    public void AVersionlessEnvelopeReadsAsLegacy()
    {
        var json = """{"State":null,"Restore":null}""";

        CoachNormalizedJson.Deserialize<CoachRevisionSnapshotEnvelope>(json)!
            .Version.Should().Be(CoachRevisionSnapshotEnvelope.LegacyVersion);
    }

    /// <summary>Re-serializes an envelope with its version member removed.</summary>
    private static string StripVersion(CoachRevisionSnapshotEnvelope envelope)
    {
        var json = CoachNormalizedJson.Serialize(envelope);
        using var document = System.Text.Json.JsonDocument.Parse(json);

        var buffer = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals(nameof(CoachRevisionSnapshotEnvelope.Version)))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ---------------------------------------------------------------- deterministic copy

    [Fact]
    public void ThePendingRationale_ReportsTheResolvedCountAndLabel()
    {
        var delta = new CoachConstraintDeltaDto
        {
            VocabularyFocusDescription = "active verbs",
            ChangedFields = [CoachConstraintField.VocabularyFocus]
        };

        var rationale = CoachDeterministicCopy.SuggestionRationale(delta, Projection(5));

        rationale.Should().Contain("I found 5 matching action verbs for this plan");
        rationale.Should().NotContain("active verbs", "the raw learner wording is never surfaced");
    }

    [Fact]
    public void ThePendingRationale_NamesNoFieldTheDeltaDidNotChange()
    {
        var delta = new CoachConstraintDeltaDto
        {
            VocabularyFocusDescription = "active verbs",
            // A model that also set a goal tag it was told not to set: the mapper did not declare
            // it as changed, so the sentence cannot mention it.
            GoalTag = "other",
            ChangedFields = [CoachConstraintField.VocabularyFocus]
        };

        var rationale = CoachDeterministicCopy.SuggestionRationale(delta, Projection(5));

        rationale.Should().NotContain("other");
        rationale.Should().NotContain("goal");
        rationale.Should().NotContain("emphasis");
    }

    [Fact]
    public async Task TheAppliedReceiptUsesTheSameCountAndLabelAsTheOffer()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        result.Value!.Messages.Should().ContainSingle()
            .Which.Text.Should().Be(CoachDeterministicCopy.FocusApplied(5, "action verbs"));
    }

    [Fact]
    public async Task ClearingTheFocusHasItsOwnReceiptSentence()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        harness.Coach.NextResult = ClearFocusResult();
        var result = await AskAsync(harness, sessionId, "clear vocabulary focus");

        result.Value!.Messages.Should().ContainSingle()
            .Which.Text.Should().Be(CoachDeterministicCopy.FocusCleared);
        result.Value.ActiveConstraints.VocabularyFocus.Should().BeNull();
    }

    // ---------------------------------------------------------------- preview to accept

    [Fact]
    public async Task AcceptingAnOffer_ReplaysTheStoredIdentifiersAndResolvesNothing()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = SuggestFocusResult("active verbs");
        var offered = await AskAsync(harness, sessionId, "could you suggest a vocabulary focus");

        offered.Value!.PendingSuggestion.Should().NotBeNull();
        harness.FocusResolver.ResolveCount.Should().Be(1);

        // The offer is stored with its selection, so acceptance needs no resolver at all.
        var stored = CoachPendingSuggestionEnvelope.TryRead(
            harness.Db.CoachSessions.Single().PendingSuggestionDeltaJson)!;
        stored.FocusSelection!.VocabularyWordIds.Should().Equal(FirstSet);

        // Change the resolver's answer. A re-resolve would now produce different words, so if
        // acceptance re-ran it the plan would silently differ from the preview.
        harness.FocusResolver.NextResult = harness.FocusResolver.NextResult with
        {
            Items = [FakeVocabularyFocusResolver.Word("OTHER-1", "\uB2EC\uB9AC\uB2E4", "to run")]
        };

        var accepted = await harness.Service.AcceptSuggestionAsync(
            sessionId, offered.Value.PendingSuggestion!.SuggestionId, new CoachSuggestionDecisionRequest());

        accepted.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        harness.FocusResolver.ResolveCount.Should().Be(1, "acceptance never resolves again");
        harness.PlanService.LastApplyFocusIds.Should().Equal(
            FirstSet, "the stored identifiers are the only apply input");
    }

    [Fact]
    public async Task ThePreviewAndTheAcceptedPlanCarryTheSameIdentifiersInTheSameOrder()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = SuggestFocusResult("active verbs");
        var offered = await AskAsync(harness, sessionId, "could you suggest a vocabulary focus");

        var previewIds = harness.PlanService.LastPreviewFocusIds!.ToArray();

        var accepted = await harness.Service.AcceptSuggestionAsync(
            sessionId, offered.Value!.PendingSuggestion!.SuggestionId, new CoachSuggestionDecisionRequest());

        harness.PlanService.LastApplyFocusIds.Should().Equal(previewIds, "order is part of the artifact");
        accepted.Value!.ChangeReceipt.Should().NotBeNull();
        accepted.Value.ActiveConstraints.VocabularyFocus!.FocusCode
            .Should().Be(CoachVocabularyFocusAliases.ActionVerbCode);
    }

    [Fact]
    public async Task RereadingAnOffer_ShowsTheSameSentenceAndTheSameSet()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = SuggestFocusResult("active verbs");
        var offered = await AskAsync(harness, sessionId, "could you suggest a vocabulary focus");

        var reread = (await harness.Service.GetSessionAsync(sessionId)).Value!.PendingSuggestion;

        reread!.Rationale.Should().Be(offered.Value!.PendingSuggestion!.Rationale);
        reread.Rationale.Should().Contain("I found 5 matching action verbs");
        harness.FocusResolver.ResolveCount.Should().Be(1, "a re-read never resolves");
    }

    // ---------------------------------------------------------------- write authority

    [Theory]
    [InlineData("clear vocabulary focus")]
    [InlineData("stop focusing on verbs")]
    [InlineData("no vocabulary focus today")]
    [InlineData("focus today on active verbs")]
    public void AFocusCommand_MayWriteOnItsOwn(string text)
    {
        new CoachWriteAuthority().Evaluate(text).Should().Be(CoachWriteAuthority.Denial.None);
    }

    [Theory]
    [InlineData("should I stop focusing on verbs?")]
    [InlineData("what does \uC88B\uB2E4 mean, and clear my vocabulary focus")]
    [InlineData("clear my focus and also tell me what \uC88B\uB2E4 means")]
    [InlineData("does \"focus\" mean the same as \uC9D1\uC911")]
    public void AQuestionOrMixedMessage_StillMayNotWrite(string text)
    {
        new CoachWriteAuthority().Evaluate(text).Should().NotBe(CoachWriteAuthority.Denial.None);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Makes the next apply a real change rather than a no-op.</summary>
    private static void NextTurnProducesADifferentPlan(CoachApplicationHarness harness) =>
        harness.PlanService.NextRemainder =
        [
            FakePlanService.Item(
                $"fresh-{Guid.NewGuid():N}", PlanActivityType.Writing,
                priority: 1, minutes: 4, spent: 0, completed: false)
        ];

    private static CoachVocabularyFocusDto Projection(int selected) => new()
    {
        FocusCode = CoachVocabularyFocusAliases.ActionVerbCode,
        DisplayLabel = "action verbs",
        EligibleCount = 12,
        SelectedCount = selected
    };

    private static CoachAgentTurnResult FocusResult(string description) =>
        Result(CoachIntentKind.DirectConstraintChange,
            new CoachConstraintDeltaIntent { VocabularyFocusDescription = description });

    private static CoachAgentTurnResult SuggestFocusResult(string description) =>
        Result(CoachIntentKind.SuggestConstraintChange,
            new CoachConstraintDeltaIntent { VocabularyFocusDescription = description });

    private static CoachAgentTurnResult ClearFocusResult() =>
        Result(CoachIntentKind.DirectConstraintChange,
            new CoachConstraintDeltaIntent { ClearVocabularyFocus = true });

    private static CoachAgentTurnResult MinutesResult(int minutes) =>
        Result(CoachIntentKind.DirectConstraintChange,
            new CoachConstraintDeltaIntent { AvailableMinutes = minutes });

    private static CoachAgentTurnResult Result(CoachIntentKind kind, CoachConstraintDeltaIntent delta) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent { Kind = kind, ConstraintDelta = delta, CoachMessage = string.Empty }
    };

    private static Task<CoachOperationResult<CoachTurnResponse>> AskAsync(
        CoachApplicationHarness harness, string sessionId, string text) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text
        });
}
