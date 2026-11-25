using FluentAssertions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Tools.SamTools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Issue 268. Over a quiz word, "for this word" was met with "do you mean the word on screen?",
/// and the "yes" that followed with a line that said three sentences were coming and held none.
/// These pin what the server tells the model on those two turns, and the standing rules behind
/// them. Every test builds against the code before the fix and fails there.
/// </summary>
public class CoachAssumesTheWordOnScreenTests
{
    private static CoachAgentTurnRequest Request(string learnerText, bool focus = true, bool answerHidden = true) => new()
    {
        SessionId = "session-1",
        LearnerText = learnerText,
        Screen = CurrentScreenTool.Read(new CoachClientScreenContext
        {
            ScreenId = "native.vocabulary-quiz",
            AnswerHidden = answerHidden,
            Items = focus
                ?
                [
                    new CoachClientScreenItem
                    {
                        Kind = CoachClientScreenItemKinds.VocabularyWord, Id = "w-1", Label = "생선", Focus = true
                    }
                ]
                : [],
            State = [new CoachClientScreenStateEntry { Key = "promptLanguage", Value = "target" }]
        }),
        ActiveConstraints = new CoachConstraintSetDto
        {
            AvailableMinutes = 20, AudioAllowed = true, SpeechAllowed = true, TypingAllowed = true,
            EnergyLevel = CoachEnergyLevel.Normal
        },
        ClarificationsRemaining = 2,
        UserLocalDate = new DateOnly(2026, 11, 25)
    };

    /// <summary>The application's own lines: everything before the fenced data blocks.</summary>
    private static string Context(CoachAgentTurnRequest request)
    {
        var message = CoachInstructions.BuildTurnMessage(request);
        return message[..message.IndexOf("Data blocks below are delimited", StringComparison.Ordinal)];
    }

    [Theory]
    [InlineData("give me three sentences for this word")]
    [InlineData("Is THIS  word common?")]
    [InlineData("what is on screen")]
    [InlineData("이 단어로 문장 세 개 만들어 주세요")]
    [InlineData("이거 어떻게 써요?")]
    public void A_message_that_points_at_the_screen_means_the_focus_item_and_is_not_met_with_a_question(string text)
    {
        var context = Context(Request(text));

        context.Should().Contain("they mean the focus item in ON SCREEN NOW");
        context.Should().Contain("Do not ask which one they mean");
    }

    [Theory]
    [InlineData("how much did I practice this week?")]
    [InlineData("give me example sentences")]
    [InlineData("what should I study this month")]
    public void A_message_that_points_at_nothing_on_screen_is_left_to_the_standing_rules(string text)
    {
        Context(Request(text)).Should().NotContain("Do not ask which one they mean");
    }

    [Fact]
    public void Pointing_at_a_screen_with_nothing_in_focus_does_not_name_a_focus_item()
    {
        // The standing rule for that screen is to say no word is visible and ask.
        Context(Request("give me three sentences for this word", focus: false))
            .Should().NotContain("they mean the focus item");
    }

    [Fact]
    public void A_reply_to_a_question_asked_over_a_hidden_answer_is_told_to_do_what_was_first_asked()
    {
        // The turn in the issue: a quiz word with its answer hidden, and "yes" to Sam's question.
        var context = Context(Request("yes") with { AnswersAQuestionWhileTheScreenHidesAnAnswer = true });

        context.Should().Contain("If this message answers it, do what they first asked, in full, in this reply");
        context.Should().Contain("A reply that only says what you understood, or that something is coming");
        context.Should().Contain("hint or the answer", "the hint-or-answer reply is still honoured (issue 258)");
    }

    [Fact]
    public void A_reply_to_a_question_asked_over_any_screen_is_told_the_same()
    {
        // Set by name, so this file also builds against the code before the fix and fails here.
        var flag = typeof(CoachAgentTurnRequest).GetProperty("AnswersAClarifyingQuestion");
        flag.Should().NotBeNull("the session service says when a message answers Sam's question");
        var request = Request("yes", answerHidden: false);
        flag!.SetValue(request, true);

        var context = Context(request);

        context.Should().Contain("If this message answers it, do what they first asked, in full, in this reply");
        context.Should().NotContain("hint or the answer", "no answer is hidden on this screen");
    }

    [Fact]
    public void A_turn_that_answers_no_question_is_not_told_that_it_does()
    {
        Context(Request("give me example sentences")).Should().NotContain("was just asked a clarifying question");
    }

    [Fact]
    public void The_standing_rules_assume_the_word_on_screen_and_check_while_answering()
    {
        var rules = CoachInstructions.Instructions;

        rules.Should().NotContain("you cannot tell which they mean, even if your last reply offered something",
            "a question first is no longer the rule when they name nothing");
        rules.Should().Contain("assume the focus item on screen");
        rules.Should().Contain("begin by naming the word you took");
        rules.Should().Contain("only when you would call a propose_ tool for it");
        rules.Should().Contain("do not ask \"or a different word?\"");
    }

    [Fact]
    public void The_standing_rules_say_a_reply_holds_what_it_announces()
    {
        var rules = CoachInstructions.Instructions;

        rules.Should().Contain("A reply holds what it says it holds");
        rules.Should().Contain("first asked, in full, in this same reply",
            "an answer to Sam's question gets the original request done");
    }
}
