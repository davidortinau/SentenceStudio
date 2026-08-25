using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Services.Plans;
using SentenceStudio.Services.Progress;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// A mixed turn keeps its answer whatever happens to the plan half.
/// </summary>
/// <remarks>
/// Review finding: the validated and scanned answer was returned only when the suggestion
/// succeeded. Every other exit from the suggestion branch — an offer already open, no plan to
/// edit, an invalid delta, an infeasible preview, an unowned resource, a change that would not
/// help — returned through a path that had no idea an answer existed. The learner lost the answer
/// they asked for because of the half they did not, and two of those exits returned a bare
/// problem response rather than a coach turn.
/// </remarks>
public class CoachMixedTurnAnswerRetentionTests
{
    // ---------------------------------------------------------------- every no-write exit

    [Fact]
    public async Task AnOfferAlreadyOpen_StillAnswersAndKeepsTheOffer()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var offered = await OfferSuggestionAsync(harness, sessionId);

        var result = await MixedAsync(harness, sessionId, d => d.AvailableMinutes = 5);

        AssertAnswerThenNotice(result, "already a suggestion");
        result.Value!.PendingSuggestion!.SuggestionId.Should().Be(offered.SuggestionId);
        harness.Db.CoachSessions.Single().PendingSuggestionId.Should().Be(offered.SuggestionId);
        AssertNoWrite(harness);
    }

    [Fact]
    public async Task NoPlanToEdit_StillAnswers()
    {
        using var harness = new CoachApplicationHarness();
        harness.PlanService.SetItems(Array.Empty<PlanSnapshotItem>());
        var sessionId = await harness.StartSessionAsync();

        var result = await MixedAsync(harness, sessionId, d => d.AvailableMinutes = 5);

        AssertAnswerThenNotice(result, "no plan for today yet");
        harness.PlanService.Current.Items.Should().BeEmpty("asking never creates a plan");
        AssertNoWrite(harness);
    }

    [Fact]
    public async Task AnInvalidTwoMinuteRequest_StillAnswersAndIsNotAProblemResponse()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        // Two minutes is below the allowed floor, so the plan half cannot proceed.
        var result = await MixedAsync(harness, sessionId, d => d.AvailableMinutes = 2);

        result.IsOk.Should().BeTrue("a bare RFC 7807 problem would throw the answer away");
        result.Status.Should().Be(CoachOperationStatus.Ok);
        AssertAnswerThenNotice(result, "could not make that change");
        AssertNoWrite(harness);
    }

    [Fact]
    public async Task ASuggestionWithNoDelta_IsRefusedUpstreamAndCarriesNoAnswer()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await MixedAsync(harness, sessionId, _ => { });

        // A SuggestConstraintChange with no delta is a malformed intent, and the intent
        // validator refuses it before the suggestion branch runs. The answer it carried was
        // never validated or leak-scanned at that point, so the whole turn is refused rather
        // than half of it delivered. Deliberately not threaded: shipping unscanned text out of
        // a malformed object is the trade this branch exists to avoid.
        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        result.Value.StopReason.Should().Be(CoachStopReason.ValidationFailed);
        result.Value.Answer.Should().BeNull();
        result.Value.Messages.Should().ContainSingle()
            .Which.Kind.Should().Be(CoachMessageKind.Notice);

        AssertNoWrite(harness);
    }

    [Fact]
    public async Task AnInfeasiblePreview_StillAnswers()
    {
        using var harness = new CoachApplicationHarness();
        harness.PlanService.PreviewOutcome = PlanPreviewOutcome.NoFeasiblePlan;
        var sessionId = await harness.StartSessionAsync();

        var result = await MixedAsync(harness, sessionId, d => d.AvailableMinutes = 5);

        result.IsOk.Should().BeTrue();
        AssertAnswerThenNotice(result, "could not build a plan");
        AssertNoWrite(harness);
    }

    [Fact]
    public async Task AnUnownedPreview_StillAnswers()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        // The preview names a resource the learner does not own.
        harness.ValidationData.OwnedProvider = () => Array.Empty<string>();

        var result = await MixedAsync(harness, sessionId, d => d.AvailableMinutes = 5);

        result.IsOk.Should().BeTrue();
        AssertAnswerThenNotice(result, "could not verify that change");
        AssertNoWrite(harness);
    }

    [Fact]
    public async Task AnIneffectiveSuggestion_StillAnswers()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        // The planner returns exactly the remaining work the learner already has, so the
        // suggestion would change nothing.
        harness.PlanService.NextRemainder = harness.PlanService.Current.Items
            .Where(i => !PlanRevisionPreview.IsPreserved(i))
            .Select((i, index) => i with { Priority = index + 1 })
            .ToList();

        var result = await MixedAsync(harness, sessionId, d => d.AvailableMinutes = 5);

        result.IsOk.Should().BeTrue();
        AssertAnswerThenNotice(result, "would help today");
        AssertNoWrite(harness);
    }

    [Fact]
    public async Task TheHappyPathStillAnswersAndOffers()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await MixedAsync(harness, sessionId, d => d.AvailableMinutes = 5);

        result.Value!.Answer.Should().NotBeNull();
        result.Value.PendingSuggestion.Should().NotBeNull();
        result.Value.Messages.Select(m => m.Kind).Should()
            .Equal(CoachMessageKind.PedagogicalAnswer, CoachMessageKind.Suggestion);
        AssertNoWrite(harness);
    }

    // ---------------------------------------------------------------- plan-only turns unchanged

    [Fact]
    public async Task APlanOnlySuggestionWithAnInvalidDelta_IsStillAProblemResponse()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        // No answer on the turn, so the existing plan-safety behaviour is untouched.
        harness.Coach.NextResult = SuggestResult(d => d.AvailableMinutes = 2, answer: null);

        var result = await AskAsync(harness, sessionId, "suggest something");

        result.IsOk.Should().BeFalse();
        result.Status.Should().Be(CoachOperationStatus.InvalidConstraint);
        AssertNoWrite(harness);
    }

    // ---------------------------------------------------------------- helpers

    private static void AssertAnswerThenNotice(
        CoachOperationResult<CoachTurnResponse> result, string noticeFragment)
    {
        result.IsOk.Should().BeTrue();

        var messages = result.Value!.Messages;
        messages.Should().HaveCount(2);
        messages[0].Kind.Should().Be(CoachMessageKind.PedagogicalAnswer, "the answer comes first");
        messages[1].Kind.Should().Be(CoachMessageKind.Notice, "the notice explains the plan half");
        messages[1].Text.Should().Contain(noticeFragment);

        result.Value.Answer.Should().NotBeNull();
        result.Value.Answer!.PlainText.Should().Contain("\uC88B\uC544\uD558\uB2E4");
        result.Value.ChangeReceipt.Should().BeNull();
    }

    private static void AssertNoWrite(CoachApplicationHarness harness)
    {
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    private static Task<CoachOperationResult<CoachTurnResponse>> MixedAsync(
        CoachApplicationHarness harness, string sessionId, Action<CoachConstraintDeltaIntent> configure)
    {
        harness.Coach.NextResult = SuggestResult(configure, Answer());
        return AskAsync(
            harness, sessionId,
            "What's the difference between \uC88B\uC544\uD558\uB2E4 and \uC88B\uB2E4? Also make today shorter.");
    }

    private static CoachAgentTurnResult SuggestResult(
        Action<CoachConstraintDeltaIntent> configure, CoachPedagogicalAnswerIntent? answer)
    {
        var delta = new CoachConstraintDeltaIntent();
        configure(delta);

        return new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                ConstraintDelta = delta,
                PedagogicalAnswer = answer,
                CoachMessage = "Answer and a suggestion."
            }
        };
    }

    private static CoachPedagogicalAnswerIntent Answer() => new()
    {
        Topic = CoachAnswerTopic.Vocabulary,
        Blocks =
        [
            new CoachAnswerBlockIntent
            {
                Kind = CoachAnswerBlockKind.Answer,
                Spans =
                [
                    new CoachAnswerSpanIntent
                    {
                        Text = "\uC88B\uC544\uD558\uB2E4 is a verb; \uC88B\uB2E4 is an adjective.",
                        Language = CoachLanguageRole.Display
                    }
                ]
            }
        ]
    };

    private static Task<CoachOperationResult<CoachTurnResponse>> AskAsync(
        CoachApplicationHarness harness, string sessionId, string text) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text
        });

    private static async Task<PendingCoachSuggestionDto> OfferSuggestionAsync(
        CoachApplicationHarness harness, string sessionId)
    {
        harness.Coach.NextResult = SuggestResult(
            d => d.SkillEmphasis = CoachSkillEmphasis.Writing, answer: null);

        return (await AskAsync(harness, sessionId, "what should I do today?")).Value!.PendingSuggestion!;
    }
}
