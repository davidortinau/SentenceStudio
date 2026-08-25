using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Groundedness: what the coach tells a learner about their plan.
/// </summary>
/// <remarks>
/// Regression cover for a live defect. "15 minutes, no audio" applied correctly, and the
/// receipt then read "fits 12 minutes total, with a 5-word vocabulary review and a 10-minute
/// reading activity" — 5 + 10 is not 12, none of it matched the plan, and it said nothing
/// about the work it had preserved. The validated plan and receipt data were correct
/// throughout; only the model's sentence was wrong.
///
/// Any claim about Today's Plan is now written by the application from the validated delta.
/// The numbers live in the receipt and diff, which the server derives itself.
/// </remarks>
public class CoachGroundednessTests
{
    /// <summary>
    /// The kind of fluent, internally inconsistent narration that caused the defect. Every
    /// fragment here must be unreachable from a response, a stored session, and a revision.
    /// </summary>
    private const string FabricatedNarration =
        "Today\u2019s Plan now fits 12 minutes total, with a 5-word vocabulary review and a " +
        "10-minute reading activity. You are on track to reach B2 by December.";

    private static readonly string[] FabricatedFragments =
    [
        "12 minutes total", "5-word", "10-minute reading", "B2 by December"
    ];

    // ---------------------------------------------------------------- direct change

    [Fact]
    public async Task AnAppliedDirectChange_NeverSurfacesTheModelsNarration()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await DirectChangeAsync(harness, sessionId, FabricatedNarration);

        result.IsOk.Should().BeTrue();
        result.Value!.ChangeReceipt.Should().NotBeNull();

        AssertNoFabrication(result.Value);
        result.Value.Messages.Should().ContainSingle()
            .Which.Text.Should().Be(CoachDeterministicCopy.AppliedDirectChange);
    }

    [Fact]
    public async Task AnAppliedDirectChange_KeepsTheTrustedNumbersInTheReceipt()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var receipt = (await DirectChangeAsync(harness, sessionId, FabricatedNarration)).Value!.ChangeReceipt!;

        // The receipt still carries everything a learner needs; it just carries it as data the
        // server derived, not as a sentence the model wrote.
        receipt.PreservedCompletedItemCount.Should().Be(1);
        receipt.PreservedInProgressItemCount.Should().Be(1);
        receipt.PreservedMinutesSpent.Should().Be(8);
        receipt.Diff.AfterPlanVersion.Should().Be(harness.PlanService.Current.Version);
        receipt.Diff.EstimatedMinutesAfter.Should().Be(harness.PlanService.Current.TotalEstimatedMinutes);
    }

    [Fact]
    public async Task AnAppliedDirectChange_NeverPersistsTheModelsNarrationInTheAudit()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await DirectChangeAsync(harness, sessionId, FabricatedNarration);

        var revision = harness.Db.CoachPlanRevisions.Single();
        var audit = string.Join(
            '|',
            revision.AcceptedConstraintDeltaJson,
            revision.BeforePlanSnapshotJson,
            revision.AfterPlanSnapshotJson,
            revision.BeforePlanVersion,
            revision.AfterPlanVersion);

        foreach (var fragment in FabricatedFragments)
        {
            audit.Should().NotContain(fragment);
        }
    }

    [Fact]
    public async Task ADirectRequestThatChangesNothing_StatesTheOutcomeItself()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent(),
            CoachMessage = FabricatedNarration
        });

        var result = await SubmitTextAsync(harness, sessionId, "leave it as it is");

        // An empty delta fails the intent-shape rule before it can be narrated at all.
        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        AssertNoFabrication(result.Value);
    }

    // ---------------------------------------------------------------- suggestions

    [Fact]
    public async Task APendingSuggestion_DescribesTheValidatedDeltaNotTheModelsSentence()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var suggestion = (await SuggestAsync(harness, sessionId, FabricatedNarration))
            .Value!.PendingSuggestion!;

        suggestion.Rationale.Should().Be("I prepared a change for your review: a focus on writing.");
        AssertNoFabrication((await harness.Service.GetSessionAsync(sessionId)).Value!.PendingSuggestion!.Rationale);
    }

    [Fact]
    public async Task APendingSuggestion_StoresNoModelProseAtAll()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await SuggestAsync(harness, sessionId, FabricatedNarration);

        var session = harness.Db.CoachSessions.Single();

        // Only the normalized delta is persisted for a pending offer. The learner-visible
        // rationale is regenerated from it on every read.
        session.PendingSuggestionDeltaJson.Should().NotBeNullOrEmpty();
        foreach (var fragment in FabricatedFragments)
        {
            session.PendingSuggestionDeltaJson!.Should().NotContain(fragment);
            session.ActiveConstraintsJson.Should().NotContain(fragment);
        }
    }

    [Fact]
    public async Task ARereadSuggestion_ReadsExactlyAsItWasFirstOffered()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var offered = (await SuggestAsync(harness, sessionId, FabricatedNarration)).Value!.PendingSuggestion!;
        var reread = (await harness.Service.GetSessionAsync(sessionId)).Value!.PendingSuggestion!;

        reread.Rationale.Should().Be(offered.Rationale);
    }

    [Fact]
    public async Task AnAcceptedSuggestion_UsesTheApplicationsReceiptText()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = (await SuggestAsync(harness, sessionId, FabricatedNarration)).Value!.PendingSuggestion!;

        var tapped = await harness.Service.AcceptSuggestionAsync(
            sessionId, suggestion.SuggestionId, new CoachSuggestionDecisionRequest());

        tapped.Value!.Messages.Should().ContainSingle()
            .Which.Text.Should().Be(CoachDeterministicCopy.AppliedSuggestion);
        AssertNoFabrication(tapped.Value);
    }

    [Fact]
    public async Task ATypedAcceptance_UsesTheSameReceiptTextAsATap()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var suggestion = (await SuggestAsync(harness, sessionId, FabricatedNarration)).Value!.PendingSuggestion!;

        var typed = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "Yes, update it",
            PendingSuggestionId = suggestion.SuggestionId
        });

        typed.Value!.Messages.Should().ContainSingle()
            .Which.Text.Should().Be(CoachDeterministicCopy.AppliedSuggestion);
        AssertNoFabrication(typed.Value);
    }

    // ---------------------------------------------------------------- what the model keeps

    [Fact]
    public async Task AClarifyingQuestion_IsStillTheModels()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "How many minutes do you have?",
            CoachMessage = "I need one detail."
        });

        var result = await SubmitTextAsync(harness, sessionId, "not much time");

        // It asserts nothing about the plan, and it has already passed the answer-leak and
        // banned-claim validators.
        result.Value!.ClarifyingQuestion.Should().Be("How many minutes do you have?");
    }

    [Fact]
    public async Task AnOffTopicReply_IsStillTheModels()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.OffTopic,
            CoachMessage = "I can only adjust today\u2019s study settings."
        });

        var result = await SubmitTextAsync(harness, sessionId, "how do I say hello?");

        result.Value!.Messages.Should().ContainSingle()
            .Which.Text.Should().Be("I can only adjust today\u2019s study settings.");
    }

    [Fact]
    public async Task AProficiencyClaim_IsRefusedBeforeItCanBeSurfacedAtAll()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.OffTopic,
            CoachMessage = "You are a B2 learner now."
        });

        var result = await SubmitTextAsync(harness, sessionId, "how am I doing?");

        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        result.Value.Messages.Should().NotContain(m => m.Text.Contains("B2", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- arm parity

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public async Task BothArmsProduceIdenticalApplicationOwnedText(CoachImplementation implementation)
    {
        using var harness = new CoachApplicationHarness();
        harness.Coach.Implementation = implementation;
        var sessionId = await harness.StartSessionAsync();

        var applied = await DirectChangeAsync(harness, sessionId, FabricatedNarration);
        applied.Value!.Messages.Single().Text.Should().Be(CoachDeterministicCopy.AppliedDirectChange);

        using var second = new CoachApplicationHarness();
        second.Coach.Implementation = implementation;
        var secondSession = await second.StartSessionAsync();
        var suggestion = (await SuggestAsync(second, secondSession, FabricatedNarration))
            .Value!.PendingSuggestion!;

        suggestion.Rationale.Should().Be("I prepared a change for your review: a focus on writing.");
    }

    // ---------------------------------------------------------------- the copy itself

    [Fact]
    public void TheSuggestionRationale_ReadsFromTheValidatedDeltaOnly()
    {
        var delta = new CoachConstraintDeltaDto
        {
            AvailableMinutes = 15,
            AudioAllowed = false,
            EnergyLevel = CoachEnergyLevel.Low,
            ChangedFields =
            [
                CoachConstraintField.AvailableMinutes,
                CoachConstraintField.AudioAllowed,
                CoachConstraintField.EnergyLevel
            ]
        };

        CoachDeterministicCopy.SuggestionRationale(delta)
            .Should().Be("I prepared a change for your review: 15 minutes, no audio, and lower energy.");
    }

    [Fact]
    public void AValueNotListedInChangedFields_IsNeverDescribed()
    {
        // ChangedFields is what the mapper validated. A stray value outside it — however it
        // arrived — cannot reach the learner.
        var delta = new CoachConstraintDeltaDto
        {
            AvailableMinutes = 999,
            GoalTag = "become fluent by friday",
            ChangedFields = [CoachConstraintField.AudioAllowed],
            AudioAllowed = false
        };

        var rationale = CoachDeterministicCopy.SuggestionRationale(delta);

        rationale.Should().Be("I prepared a change for your review: no audio.");
        rationale.Should().NotContain("999");
        rationale.Should().NotContain("fluent");
    }

    [Fact]
    public void AGoalTagIsNeverEchoedBackAsProse()
    {
        var delta = new CoachConstraintDeltaDto
        {
            GoalTag = "meeting my partner\u2019s family",
            ChangedFields = [CoachConstraintField.GoalTag]
        };

        CoachDeterministicCopy.SuggestionRationale(delta)
            .Should().Be("I prepared a change for your review: a different goal.");
    }

    [Fact]
    public void AnEmptyDeltaStillProducesAReadableSentence()
    {
        CoachDeterministicCopy.SuggestionRationale(new CoachConstraintDeltaDto())
            .Should().Be("I prepared a change for your review.");
    }

    [Fact]
    public void TheCopyStatesNoCountsOfItemsMinutesOrWords()
    {
        // The receipt and the diff are the numeric authority. A sentence that repeats them can
        // only ever drift out of step with them.
        foreach (var text in new[]
                 {
                     CoachDeterministicCopy.AppliedDirectChange,
                     CoachDeterministicCopy.AppliedSuggestion,
                     CoachDeterministicCopy.RejectedSuggestion,
                     CoachDeterministicCopy.NoChange
                 })
        {
            text.Should().NotMatchRegex(@"\d");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void AssertNoFabrication(CoachTurnResponse response)
    {
        var surfaced = string.Join(
            '|',
            string.Join('|', response.Messages.Select(m => m.Text)),
            response.ClarifyingQuestion ?? string.Empty,
            response.ChangeReceipt?.Summary ?? string.Empty,
            response.ChangeReceipt?.Revision.Summary ?? string.Empty,
            response.PendingSuggestion?.Rationale ?? string.Empty,
            string.Join('|', response.PlanState.Items.Select(i => $"{i.Title} {i.Description}")));

        AssertNoFabrication(surfaced);
    }

    private static void AssertNoFabrication(string surfaced)
    {
        foreach (var fragment in FabricatedFragments)
        {
            surfaced.Should().NotContain(fragment);
        }
    }

    private static CoachAgentTurnResult Completed(CoachTurnIntent intent) =>
        new() { Outcome = CoachAgentOutcome.Completed, Intent = intent };

    private static Task<CoachOperationResult<CoachTurnResponse>> SubmitTextAsync(
        CoachApplicationHarness harness, string sessionId, string text) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text
        });

    private static Task<CoachOperationResult<CoachTurnResponse>> DirectChangeAsync(
        CoachApplicationHarness harness, string sessionId, string narration)
    {
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 15, AudioAllowed = false },
            CoachMessage = narration
        });

        return SubmitTextAsync(harness, sessionId, "15 minutes, no audio");
    }

    private static Task<CoachOperationResult<CoachTurnResponse>> SuggestAsync(
        CoachApplicationHarness harness, string sessionId, string narration)
    {
        harness.Coach.NextResult = Completed(new CoachTurnIntent
        {
            Kind = CoachIntentKind.SuggestConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { SkillEmphasis = CoachSkillEmphasis.Writing },
            CoachMessage = narration
        });

        return SubmitTextAsync(harness, sessionId, "what should I do today?");
    }
}
