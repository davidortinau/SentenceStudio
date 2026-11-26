using FluentAssertions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Issue 289. A plain reply was shown exactly as the model wrote it, so a reply that said example
/// sentences were being given and then ended reached the learner with nothing in it (issues 268
/// and 281). The reply on issue 268 ended with a full stop, not a colon. These go through the
/// real conversation and session services with a scripted coach, and every test builds against
/// the code before the fix.
/// </summary>
public class CoachEmptyAnnouncementRetryTests
{
    private const string Answer = "1. 공을 차요. 2. 문을 발로 찼어요. 3. 축구공을 세게 찼어요.";

    private static CoachAgentTurnResult Reply(string text, long inputTokens = 0, long outputTokens = 0) =>
        CoachDurableTurnTests.Reply(text) with { Usage = new CoachRunUsage(inputTokens, outputTokens, 0m) };

    private static async Task<(CoachConversationHarness Harness, IReadOnlyList<string> Shown)> TurnAsync(
        params CoachAgentTurnResult[] script)
    {
        var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();
        foreach (var result in script)
        {
            harness.Coach.Script.Enqueue(result);
        }

        var turn = await harness.TurnAsync(conversationId, "give me three Korean only sentences for this word");

        turn.IsOk.Should().BeTrue(turn.Detail);
        return (harness, turn.Value!.Result!.Messages.Select(message => message.Text).ToList());
    }

    [Theory]
    [InlineData("Here are three Korean-only example sentences:")]
    [InlineData("차다를 쓴 예문 세 개입니다：")]
    // The reply on issue 268, in the session's own words: it ends with a full stop.
    [InlineData("You mean the word on screen. I will not give the answer away; here are three Korean-only "
        + "example sentences that use it without giving away the quiz choice.")]
    [InlineData("Sure! Here's a set of example sentences for you.")]
    [InlineData("다음은 예문 세 개입니다.")]
    // Issue 326: quotation marks near an announcement do not hide it. The phrase is outside them,
    // an apostrophe is not a quotation mark, and a quoted phrase does not cover an unquoted one.
    [InlineData("\"차다\" means to kick. Here are three example sentences that use it.")]
    [InlineData("For \"차다\", here are three example sentences.")]
    [InlineData("Don't worry, here's a set of example sentences for you.")]
    [InlineData("That is the learners' favourite, so here are three example sentences.")]
    [InlineData("Here are three example sentences that use \"here are\".")]
    [InlineData("\"차다\"는 이렇게 써요.\nHere are three example sentences.")]
    [InlineData("'차다'를 쓴 예문은 다음과 같습니다.")]
    [InlineData("‘차다’를 쓴 예문은 다음과 같습니다.")]
    public async Task An_announcement_with_nothing_after_it_is_asked_for_once_more_and_the_answer_is_shown(
        string announcement)
    {
        var (harness, shown) = await TurnAsync(Reply(announcement), Reply(Answer));
        using var _ = harness;

        shown.Should().Equal(Answer);
        harness.Coach.RunCount.Should().Be(2);
    }

    [Fact]
    public async Task The_second_call_is_for_the_same_message_and_says_what_was_wrong_with_the_first_reply()
    {
        var (harness, _) = await TurnAsync(Reply("Here are three example sentences:"), Reply(Answer));
        using var __ = harness;

        harness.Coach.Requests.Should().HaveCount(2);
        var first = harness.Coach.Requests[0];
        var second = harness.Coach.Requests[1];

        second.LearnerText.Should().Be(first.LearnerText);
        second.AgentSessionJson.Should().Be(first.AgentSessionJson,
            "the second call starts from the conversation as it was before the first reply");
        CoachInstructions.BuildTurnMessage(first).Should().NotContain("was not shown to the learner");
        CoachInstructions.BuildTurnMessage(second).Should().Contain(
            "Your first reply to this message was not shown to the learner: it said content was coming");
    }

    [Theory]
    [InlineData("차다 means to kick.")]
    [InlineData("Here are three: 공을 차요. 문을 찼어요. 세게 찼어요.")]
    [InlineData("Here's a tip: say it with 공.")]
    [InlineData("There are two readings of that word.")]
    [InlineData("I can show the following if you like. Want them?")]
    // Issue 326: a quoted sentence that itself begins with an announcing phrase is the content.
    [InlineData("In English you say \"Here is your coffee.\"")]
    [InlineData("In English you say \"Here is your coffee\".")]
    [InlineData("In English you say “Here is your coffee.”")]
    [InlineData("In English you say 'Here's your coffee.'")]
    [InlineData("In English you say ‘Here’s your coffee.’")]
    [InlineData("In English you say 'Don't worry, here is your coffee.'")]
    [InlineData("A waiter might say \"Thank you for waiting. Here is your coffee.\"")]
    [InlineData("한국어로는 \"다음은 제 차례예요\"라고 해요.")]
    [InlineData("한국어로는 「다음은 제 차례예요」라고 해요.")]
    [InlineData("한국어로는 『다음은 제 차례예요』라고 해요.")]
    public async Task A_reply_that_holds_what_it_says_is_shown_unchanged_and_costs_one_call(string reply)
    {
        var (harness, shown) = await TurnAsync(Reply(reply), Reply("This second reply must never be asked for."));
        using var _ = harness;

        shown.Should().Equal(reply);
        harness.Coach.RunCount.Should().Be(1);
    }

    [Fact]
    public async Task Two_announcements_in_a_row_end_the_turn_with_the_second_shown()
    {
        var (harness, shown) = await TurnAsync(
            Reply("Here are three example sentences:"),
            Reply("Here are the three sentences you asked for:"),
            Reply("A third reply must never be asked for."));
        using var _ = harness;

        shown.Should().Equal("Here are the three sentences you asked for:");
        harness.Coach.RunCount.Should().Be(2);
    }

    [Fact]
    public async Task A_second_call_that_fails_leaves_the_first_reply_shown()
    {
        var (harness, shown) = await TurnAsync(
            Reply("Here are three example sentences:"),
            new CoachAgentTurnResult { Outcome = CoachAgentOutcome.Timeout });
        using var _ = harness;

        shown.Should().Equal("Here are three example sentences:");
        harness.Coach.RunCount.Should().Be(2);
    }

    [Fact]
    public async Task An_announcement_that_leads_answer_parts_costs_one_call()
    {
        var withParts = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.GeneralAnswer,
                CoachMessage = "Here are three example sentences:",
                PedagogicalAnswer = new CoachPedagogicalAnswerIntent
                {
                    Topic = CoachAnswerTopic.Other,
                    Blocks =
                    [
                        new CoachAnswerBlockIntent
                        {
                            Kind = CoachAnswerBlockKind.Example,
                            Spans = [new CoachAnswerSpanIntent { Text = "공을 차요.", Language = CoachLanguageRole.Target }]
                        }
                    ]
                }
            }
        };

        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();
        harness.Coach.Script.Enqueue(withParts);
        harness.Coach.Script.Enqueue(Reply("This second reply must never be asked for."));

        await harness.TurnAsync(conversationId, "give me three sentences for this word");

        harness.Coach.RunCount.Should().Be(1);
    }

    // Issue 305: the same fault through the answer path. The answer path shows the parts and not
    // the message, so an answer whose one part is the announcement is an empty announcement too.

    private static CoachAnswerBlockIntent Part(CoachAnswerBlockKind kind, params (string Text, CoachLanguageRole Language)[] pieces) =>
        new()
        {
            Kind = kind,
            Spans = pieces.Select(piece => new CoachAnswerSpanIntent { Text = piece.Text, Language = piece.Language }).ToList()
        };

    private static CoachAgentTurnResult AnswerReply(
        CoachIntentKind kind, string message, params CoachAnswerBlockIntent[] parts) => new()
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = kind,
                CoachMessage = message,
                PedagogicalAnswer = new CoachPedagogicalAnswerIntent { Topic = CoachAnswerTopic.Other, Blocks = [.. parts] }
            }
        };

    [Theory]
    [InlineData("Here are three example sentences:", "")]
    // The message is not shown on the answer path, so what it holds does not fill the announcement.
    [InlineData("Here are three example sentences:", "Here are three example sentences:")]
    [InlineData("Here are three Korean-only example sentences that use it without giving away the quiz choice.", "")]
    [InlineData("다음은 예문 세 개입니다.", "")]
    public async Task An_answer_whose_only_part_is_an_announcement_is_asked_for_once_more_and_the_answer_is_shown(
        string announcement, string message)
    {
        var (harness, shown) = await TurnAsync(
            AnswerReply(CoachIntentKind.PedagogicalAnswer, message,
                Part(CoachAnswerBlockKind.Answer, (announcement, CoachLanguageRole.Display))),
            Reply(Answer));
        using var _ = harness;

        shown.Should().Equal(Answer);
        harness.Coach.RunCount.Should().Be(2);
    }

    [Fact]
    public async Task A_general_answer_with_no_message_whose_only_part_is_an_announcement_is_asked_for_once_more()
    {
        var (harness, shown) = await TurnAsync(
            AnswerReply(CoachIntentKind.GeneralAnswer, "",
                Part(CoachAnswerBlockKind.Answer, ("Here are three example sentences:", CoachLanguageRole.Display))),
            Reply(Answer));
        using var _ = harness;

        shown.Should().Equal(Answer);
        harness.Coach.RunCount.Should().Be(2);
    }

    [Fact]
    public async Task An_answer_part_that_announces_and_empty_parts_after_it_are_asked_for_once_more()
    {
        var (harness, shown) = await TurnAsync(
            AnswerReply(CoachIntentKind.PedagogicalAnswer, "",
                Part(CoachAnswerBlockKind.Answer, ("Here are three example sentences:", CoachLanguageRole.Display)),
                Part(CoachAnswerBlockKind.Example, ("  ", CoachLanguageRole.Target)),
                Part(CoachAnswerBlockKind.Example)),
            Reply(Answer));
        using var _ = harness;

        shown.Should().Equal(Answer);
        harness.Coach.RunCount.Should().Be(2);
    }

    public static TheoryData<string, CoachAgentTurnResult> AnswersThatHoldWhatTheySay => new()
    {
        {
            "one answer part that answers",
            AnswerReply(CoachIntentKind.PedagogicalAnswer, "",
                Part(CoachAnswerBlockKind.Answer, ("차다 means to kick.", CoachLanguageRole.Display)))
        },
        {
            "one answer part whose later pieces are the content",
            AnswerReply(CoachIntentKind.PedagogicalAnswer, "",
                Part(CoachAnswerBlockKind.Answer,
                    ("Here are three example sentences:", CoachLanguageRole.Display),
                    ("공을 차요. 문을 찼어요. 세게 찼어요.", CoachLanguageRole.Target)))
        },
        {
            // The sentence the learner asked for, in the language studied, reads like an announcement.
            "one answer part that is a sentence in the language studied",
            AnswerReply(CoachIntentKind.PedagogicalAnswer, "",
                Part(CoachAnswerBlockKind.Answer, ("Here is your coffee.", CoachLanguageRole.Target)))
        },
        {
            "one answer part that ends in the language studied",
            AnswerReply(CoachIntentKind.PedagogicalAnswer, "",
                Part(CoachAnswerBlockKind.Answer,
                    ("You can say it like this.", CoachLanguageRole.Display),
                    ("다음은 제 차례예요.", CoachLanguageRole.Target)))
        },
        {
            // Issue 326: the sentence is quoted inside one piece in the display language.
            "one answer part that quotes a sentence beginning with an announcing phrase",
            AnswerReply(CoachIntentKind.PedagogicalAnswer, "",
                Part(CoachAnswerBlockKind.Answer, ("In English you say \"Here is your coffee.\"", CoachLanguageRole.Display)))
        },
        {
            "an announcing answer part and an example part",
            AnswerReply(CoachIntentKind.PedagogicalAnswer, "",
                Part(CoachAnswerBlockKind.Answer, ("Here are three example sentences:", CoachLanguageRole.Display)),
                Part(CoachAnswerBlockKind.Example, ("공을 차요.", CoachLanguageRole.Target)))
        },
        {
            "one part that is not the direct answer",
            AnswerReply(CoachIntentKind.PedagogicalAnswer, "",
                Part(CoachAnswerBlockKind.Example, ("공을 차요:", CoachLanguageRole.Target)))
        },
        {
            // The shape repair puts a general answer's message in front as a part of its own.
            "a general answer with a message and one announcing part",
            AnswerReply(CoachIntentKind.GeneralAnswer, "차다 means to kick.",
                Part(CoachAnswerBlockKind.Answer, ("Here are three example sentences:", CoachLanguageRole.Display)))
        }
    };

    [Theory]
    [MemberData(nameof(AnswersThatHoldWhatTheySay))]
    public async Task An_answer_with_content_or_more_than_one_part_costs_one_call(string because, CoachAgentTurnResult answer)
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();
        harness.Coach.Script.Enqueue(answer);
        harness.Coach.Script.Enqueue(Reply("This second reply must never be asked for."));

        await harness.TurnAsync(conversationId, "give me three sentences for this word");

        harness.Coach.RunCount.Should().Be(1, because);
    }

    [Fact]
    public async Task Both_calls_are_charged_to_the_turns_one_run()
    {
        var (harness, _) = await TurnAsync(
            Reply("Here are three example sentences:", inputTokens: 100, outputTokens: 10),
            Reply(Answer, inputTokens: 120, outputTokens: 40));
        using var __ = harness;

        var totals = await harness.App.Usage.GetDailyTotalsAsync(
            CoachConversationHarness.OwnerUserId, harness.App.DateContext.UserLocalDate);

        totals.RunCount.Should().Be(1, "the second call is part of the same run, not a second one");
        totals.InputTokens.Should().Be(220);
        totals.OutputTokens.Should().Be(50);
    }
}
