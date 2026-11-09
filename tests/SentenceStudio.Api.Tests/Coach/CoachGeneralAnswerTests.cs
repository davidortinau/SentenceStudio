using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The general reply kind and the plan-free turn context (docs/coach/sam-single-runtime.md,
/// way-forward step 4).
/// </summary>
/// <remarks>
/// Before this, a result such as "you have 342 words" had no kind of its own: the only non-plan
/// answer kind was the language-teaching one, and Today's Plan constraints led every turn's
/// context. Both pulled Sam towards the plan.
/// </remarks>
public class CoachGeneralAnswerTests
{
    // ---------------------------------------------------------------- the turn

    [Fact]
    public async Task AGeneralAnswer_IsShownAsTextAndWritesNothing()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var planBefore = harness.PlanService.Current.Version;
        var constraintsBefore = harness.Db.CoachSessions.Single().ActiveConstraintsJson;

        harness.Coach.NextResult = General("You have 342 words in total, the same number your dashboard shows.");

        var result = await AskAsync(harness, sessionId, "How many words do I have?");

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.Messages.Should().ContainSingle()
            .Which.Should().Match<CoachMessageDto>(m =>
                m.Kind == CoachMessageKind.Text && m.Text!.Contains("342 words"));
        result.Value.ChangeReceipt.Should().BeNull();
        result.Value.PendingSuggestion.Should().BeNull();
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.PlanService.PreviewCallCount.Should().Be(0);
        harness.PlanService.Current.Version.Should().Be(planBefore);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
        harness.Db.CoachSessions.Single().ActiveConstraintsJson.Should().Be(constraintsBefore);
    }

    [Fact]
    public async Task AGeneralAnswer_ThatNamesADueWord_IsRefused()
    {
        // The general reply shows model prose as written, so it goes through the same leak scan
        // as NoChange: a due review answer never reaches the learner through it.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [new CoachEmbargoedItem("SENTINELDUEWORD", "sentinel gloss")];
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = General("One of your words is SENTINELDUEWORD.");

        var result = await AskAsync(harness, sessionId, "Show me one of my words.");

        string.Join(" ", result.Value!.Messages.Select(m => m.Text)).Should().NotContain("SENTINELDUEWORD");
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task APlanKindWithoutAPlanReference_StillFailsClosed()
    {
        // The plan bias guard is unchanged: a plan change from text needs the plan named.
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.DirectConstraintChange,
                ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 10 },
                CoachMessage = "Done."
            }
        };

        await AskAsync(harness, sessionId, "How many words do I have?");

        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- plan context

    [Theory]
    [InlineData("How many words do I have?")]
    [InlineData("Add an example sentence to 사과")]
    [InlineData("내 단어가 몇 개야?")]
    public async Task AMessageNotAboutThePlan_GetsNoPlanContext(string text)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = General("Sure.");

        await AskAsync(harness, sessionId, text);

        harness.Coach.LastRequest!.IncludePlanContext.Should().BeFalse();
        var rendered = CoachInstructions.BuildTurnMessage(harness.Coach.LastRequest);
        rendered.Should().NotContain("Today's Plan constraints:");
        rendered.Should().NotContain("- available minutes:");
        rendered.Should().Contain("Today's Plan: not part of this message.");
    }

    [Theory]
    [InlineData("make Today's Plan 10 minutes")]
    [InlineData("오늘 계획을 10분으로 바꿔줘")]
    public async Task AMessageAboutThePlan_GetsThePlanContext(string text)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = General("Sure.");

        await AskAsync(harness, sessionId, text);

        harness.Coach.LastRequest!.IncludePlanContext.Should().BeTrue();
        CoachInstructions.BuildTurnMessage(harness.Coach.LastRequest)
            .Should().Contain("Today's Plan constraints:")
            .And.Contain("- available minutes:");
    }

    [Fact]
    public void AnOpenSuggestion_AlwaysCarriesThePlanContext()
    {
        // The learner's "yes" names nothing, but it answers the open suggestion, whose id the
        // model must copy.
        var rendered = CoachInstructions.BuildTurnMessage(NewRequest("yes") with
        {
            IncludePlanContext = false,
            PendingSuggestionId = "suggestion-1"
        });

        rendered.Should().Contain("Open suggestion id: suggestion-1");
        rendered.Should().Contain("Today's Plan constraints:");
    }

    // ---------------------------------------------------------------- shape

    [Fact]
    public void AGeneralAnswer_WithAMessage_IsValid()
    {
        new CoachIntentValidator().ValidateIntent(new CoachTurnIntent
        {
            Kind = CoachIntentKind.GeneralAnswer,
            CoachMessage = "You have 12 words."
        }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void AGeneralAnswer_MustSaySomething_AndCarriesNoPlanChangeOrQuestion()
    {
        var validator = new CoachIntentValidator();

        validator.ValidateIntent(new CoachTurnIntent { Kind = CoachIntentKind.GeneralAnswer })
            .Violations.Should().Contain(v => v.Code == "message_required");

        validator.ValidateIntent(new CoachTurnIntent
        {
            Kind = CoachIntentKind.GeneralAnswer,
            CoachMessage = "Done.",
            ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 10 }
        }).Violations.Should().Contain(v => v.Code == "delta_forbidden");

        validator.ValidateIntent(new CoachTurnIntent
        {
            Kind = CoachIntentKind.GeneralAnswer,
            CoachMessage = "Hi.",
            ClarifyingQuestion = "Which word?"
        }).Violations.Should().Contain(v => v.Code == "question_forbidden");

        validator.ValidateIntent(new CoachTurnIntent
        {
            Kind = CoachIntentKind.GeneralAnswer,
            CoachMessage = "Hi.",
            VocabularySet = new CoachVocabularySetIntent { Topic = "food" }
        }).Violations.Should().Contain(v => v.Code == "vocabulary_set_requires_no_change");
    }

    [Fact]
    public void AGeneralAnswerWithParts_IsRepairedIntoTheAnswerPath()
    {
        // A word list sent as parts is still an answer that writes nothing; the parts go through
        // the answer bounds instead of the whole reply being refused.
        var intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.GeneralAnswer,
            CoachMessage = "Here are your food words.",
            PedagogicalAnswer = new CoachPedagogicalAnswerIntent
            {
                Topic = CoachAnswerTopic.Vocabulary,
                Blocks =
                [
                    new CoachAnswerBlockIntent
                    {
                        Kind = CoachAnswerBlockKind.Answer,
                        Spans = [new CoachAnswerSpanIntent { Text = "apple, bread", Language = CoachLanguageRole.Display }]
                    }
                ]
            }
        };

        var report = CoachAnswerShapeRepair.Repair(intent);

        intent.Kind.Should().Be(CoachIntentKind.PedagogicalAnswer);
        report.GeneralAnswerWithParts.Should().BeTrue("the rewrite is reported");
        report.Changed.Should().BeTrue();
        // The answer path shows only the parts, so the message must lead them, not be dropped.
        intent.PedagogicalAnswer!.Blocks.Should().HaveCount(2);
        intent.PedagogicalAnswer.Blocks[0].Kind.Should().Be(CoachAnswerBlockKind.Answer);
        string.Concat(intent.PedagogicalAnswer.Blocks[0].Spans.Select(s => s.Text))
            .Should().Contain("Here are your food words.");
        new CoachIntentValidator().ValidateIntent(intent).IsValid.Should().BeTrue();
    }

    [Fact]
    public void TheSchemaGuidance_DoesNotCallTheReplyThePlanMessage()
    {
        var message = typeof(CoachTurnIntent).GetProperty(nameof(CoachTurnIntent.CoachMessage))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
        var kind = typeof(CoachTurnIntent).GetProperty(nameof(CoachTurnIntent.Kind))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

        message.Should().NotContainEquivalentOf("plan");
        kind.Should().StartWith("What kind of reply this is. Use GeneralAnswer for most replies");
        CoachInstructions.Instructions.Should().NotContain("The plan message");
        CoachInstructions.Instructions.IndexOf("CHOOSING THE KIND OF REPLY", StringComparison.Ordinal)
            .Should().BeLessThan(CoachInstructions.Instructions.IndexOf("TODAY'S PLAN", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- helpers

    private static CoachAgentTurnResult General(string message) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent { Kind = CoachIntentKind.GeneralAnswer, CoachMessage = message }
    };

    private static CoachAgentTurnRequest NewRequest(string learnerText) => new()
    {
        SessionId = "session-1",
        LearnerText = learnerText,
        ActiveConstraints = new CoachConstraintSetDto
        {
            AvailableMinutes = 20,
            AudioAllowed = true,
            SpeechAllowed = true,
            TypingAllowed = true,
            EnergyLevel = CoachEnergyLevel.Normal
        },
        ClarificationsRemaining = 2,
        UserLocalDate = new DateOnly(2026, 11, 8)
    };

    private static Task<CoachOperationResult<CoachTurnResponse>> AskAsync(
        CoachApplicationHarness harness, string sessionId, string text) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text
        });
}
