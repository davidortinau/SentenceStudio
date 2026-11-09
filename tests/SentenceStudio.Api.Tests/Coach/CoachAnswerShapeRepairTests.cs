using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Regression for 2026-10-31: "How much did I practice this week?" and a plan follow-up were
/// refused with "An answer must have between 1 and 8 parts" because the model sent an empty
/// placeholder answer, or one part per list item.
/// </summary>
public sealed class CoachAnswerShapeRepairTests
{
    private static readonly CoachLanguageProfile Languages = new("ko-KR", "en-US", "en-US");

    // ---------------------------------------------------------------- unit

    [Fact]
    public void AnEmptyPlaceholderAnswer_IsTreatedAsAbsent()
    {
        var intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.SuggestConstraintChange,
            PedagogicalAnswer = new CoachPedagogicalAnswerIntent { Topic = CoachAnswerTopic.Other, Blocks = [] }
        };

        var report = CoachAnswerShapeRepair.Repair(intent);

        intent.PedagogicalAnswer.Should().BeNull();
        report.DroppedEmptyAnswer.Should().BeTrue();
    }

    [Fact]
    public void WhitespaceOnlyPiecesAndParts_AreDropped()
    {
        var intent = AnswerIntent(
            Block(CoachAnswerBlockKind.Answer, "You practised on 3 days in the last 7 days.", " "),
            Block(CoachAnswerBlockKind.Note, "  "));

        var report = CoachAnswerShapeRepair.Repair(intent);

        report.DroppedEmptySpans.Should().Be(2);
        report.DroppedEmptyBlocks.Should().Be(1);
        new CoachAnswerProjection().Project(intent.PedagogicalAnswer, Languages).IsValid.Should().BeTrue();
    }

    [Fact]
    public void OnePartPerDay_IsFoldedWithHeadingsKeptAndThePromptLast()
    {
        var blocks = new List<CoachAnswerBlockIntent>
        {
            Block(CoachAnswerBlockKind.Answer, "In the last 7 days you practised on 5 days.")
        };
        foreach (var day in new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" })
        {
            var block = Block(CoachAnswerBlockKind.Note, "practised");
            block.Label = day;
            blocks.Add(block);
        }
        blocks.Add(Block(CoachAnswerBlockKind.RetrievalPrompt, "Which day will you practise next?"));
        var intent = AnswerIntent([.. blocks]);

        var report = CoachAnswerShapeRepair.Repair(intent);
        var projection = new CoachAnswerProjection().Project(intent.PedagogicalAnswer, Languages);

        report.FoldedBlocks.Should().BeGreaterThan(0);
        projection.IsValid.Should().BeTrue(string.Join(" | ", projection.Errors));
        projection.Answer!.Blocks.Should().HaveCount(CoachAnswerLimits.MaxBlocks);
        projection.Answer.Blocks[^1].Kind.Should().Be(CoachAnswerBlockKind.RetrievalPrompt);
        foreach (var day in new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" })
        {
            projection.Answer.PlainText.Should().Contain(day, "no heading is lost in the fold");
        }
    }

    [Fact]
    public void AnAnswerTurnWithItsReplyInCoachMessage_GetsADirectAnswerFromIt()
    {
        var intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.PedagogicalAnswer,
            PedagogicalAnswer = new CoachPedagogicalAnswerIntent { Blocks = [] },
            CoachMessage = "In the last 7 days you practised on 3 days."
        };

        var report = CoachAnswerShapeRepair.Repair(intent);
        var projection = new CoachAnswerProjection().Project(intent.PedagogicalAnswer, Languages);

        report.AnswerFromMessage.Should().BeTrue();
        projection.IsValid.Should().BeTrue();
        projection.Answer!.PlainText.Should().Be("In the last 7 days you practised on 3 days.");
    }

    [Fact]
    public void AMessageIsNeverPromotedToAnAnswerOnAnotherKind()
    {
        var intent = new CoachTurnIntent { Kind = CoachIntentKind.NoChange, CoachMessage = "Hello." };

        CoachAnswerShapeRepair.Repair(intent).Changed.Should().BeFalse();
        intent.PedagogicalAnswer.Should().BeNull();
    }

    [Fact]
    public void AnAnswerWithinBounds_IsUntouched()
    {
        var intent = AnswerIntent(Block(CoachAnswerBlockKind.Answer, "Yes."));
        CoachAnswerShapeRepair.Repair(intent).Changed.Should().BeFalse();
    }

    // ---------------------------------------------------------------- turn path

    [Fact]
    public async Task APlanFollowUpWithAnEmptyPlaceholderAnswer_IsOfferedNotRefused()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                ConstraintDelta = new CoachConstraintDeltaIntent { SkillEmphasis = CoachSkillEmphasis.Writing },
                PedagogicalAnswer = new CoachPedagogicalAnswerIntent { Topic = CoachAnswerTopic.Other, Blocks = [] },
                CoachMessage = "I suggest more writing."
            }
        };

        var result = await AskAsync(harness, sessionId, "Suggest a change to Today's Plan.");

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().NotBe(CoachTurnStatus.Rejected);
        result.Value.StopReason.Should().NotBe(CoachStopReason.ValidationFailed);
        result.Value.PendingSuggestion.Should().NotBeNull();
        result.Value.Answer.Should().BeNull();
        harness.PlanService.ApplyCallCount.Should().Be(0, "a suggestion still waits for acceptance");
    }

    [Fact]
    public async Task APracticeQuestionAnsweredInCoachMessage_IsShownAsTheAnswer()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.PedagogicalAnswer,
                PedagogicalAnswer = new CoachPedagogicalAnswerIntent { Topic = CoachAnswerTopic.StudyStrategy, Blocks = [] },
                CoachMessage = "In the last 7 days you practised on 3 days."
            }
        };

        var result = await AskAsync(harness, sessionId, "How much did I practice this week?");

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.Answer!.PlainText.Should().Be("In the last 7 days you practised on 3 days.");
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- helpers

    private static CoachTurnIntent AnswerIntent(params CoachAnswerBlockIntent[] blocks) => new()
    {
        Kind = CoachIntentKind.PedagogicalAnswer,
        PedagogicalAnswer = new CoachPedagogicalAnswerIntent { Topic = CoachAnswerTopic.StudyStrategy, Blocks = [.. blocks] }
    };

    private static CoachAnswerBlockIntent Block(CoachAnswerBlockKind kind, params string[] texts) => new()
    {
        Kind = kind,
        Spans = texts.Select(t => new CoachAnswerSpanIntent { Text = t, Language = CoachLanguageRole.Display }).ToList()
    };

    private static Task<CoachOperationResult<CoachTurnResponse>> AskAsync(
        CoachApplicationHarness harness, string sessionId, string text) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text
        });

    [Fact]
    public void A_general_answer_with_a_follow_up_question_keeps_the_answer_and_the_offer()
    {
        var intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.GeneralAnswer,
            CoachMessage = "I found 7 words tagged food.",
            ClarifyingQuestion = "Want me to show their example sentences?"
        };

        var report = CoachAnswerShapeRepair.Repair(intent);

        report.FoldedFollowUpQuestion.Should().BeTrue();
        intent.ClarifyingQuestion.Should().BeNull("a general answer never carries a separate question");
        intent.CoachMessage.Should().Be("I found 7 words tagged food. Want me to show their example sentences?");
        new CoachIntentValidator().ValidateIntent(intent).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_follow_up_that_would_overflow_the_message_is_dropped_not_truncated()
    {
        var message = new string('a', 390);
        var intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.GeneralAnswer,
            CoachMessage = message,
            ClarifyingQuestion = "Want me to show more?"
        };

        CoachAnswerShapeRepair.Repair(intent);

        intent.CoachMessage.Should().Be(message);
        intent.ClarifyingQuestion.Should().BeNull();
    }

    [Fact]
    public void A_real_clarification_is_left_alone()
    {
        var intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.AskClarification,
            ClarifyingQuestion = "Which topic?"
        };

        CoachAnswerShapeRepair.Repair(intent).FoldedFollowUpQuestion.Should().BeFalse();
        intent.ClarifyingQuestion.Should().Be("Which topic?");
    }
}
