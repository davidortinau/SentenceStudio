using FluentAssertions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Tools.SamTools;
using SentenceStudio.Api.Tests.Coach.History;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Issue 281. During a quiz Sam offered something, the learner answered with a one-word yes, and
/// the reply said three example sentences were being given and held none. The fix for issue 268
/// tells the model to carry a request out only on a turn that answers a clarifying question; an
/// offer made in an ordinary reply leaves no question open, so the yes that accepts it was sent
/// with nothing. These pin what the server tells the model on that turn, and the standing rule
/// behind it. Every test builds against the code before the fix.
/// </summary>
public class CoachCarriesOutAnAcceptedOfferTests
{
    private static CoachAgentTurnRequest Request(string learnerText) => new()
    {
        SessionId = "session-1",
        LearnerText = learnerText,
        Screen = CurrentScreenTool.Read(new CoachClientScreenContext
        {
            ScreenId = "native.vocabulary-quiz",
            AnswerHidden = true,
            Items =
            [
                new CoachClientScreenItem
                {
                    Kind = CoachClientScreenItemKinds.VocabularyWord, Id = "w-1", Label = "생선", Focus = true
                }
            ],
            State = [new CoachClientScreenStateEntry { Key = "promptLanguage", Value = "target" }]
        }),
        ActiveConstraints = new CoachConstraintSetDto
        {
            AvailableMinutes = 20, AudioAllowed = true, SpeechAllowed = true, TypingAllowed = true,
            EnergyLevel = CoachEnergyLevel.Normal
        },
        // No plan talk in these turns, as the session service decides for a message like "yes".
        IncludePlanContext = false,
        ClarificationsRemaining = 2,
        UserLocalDate = new DateOnly(2026, 11, 25)
    };

    /// <summary>The application's own lines: everything before the fenced data blocks.</summary>
    private static string Context(CoachAgentTurnRequest request) => ApplicationLines(CoachInstructions.BuildTurnMessage(request));

    private static string ApplicationLines(string message) =>
        message[..message.IndexOf("Data blocks below are delimited", StringComparison.Ordinal)];

    [Theory]
    [InlineData("yes")]
    [InlineData("Yes!")]
    [InlineData("sure")]
    [InlineData("go ahead")]
    [InlineData("네")]
    [InlineData("좋아요")]
    [InlineData("해주세요")]
    public void A_plain_yes_is_met_with_what_was_offered_in_the_same_reply(string text)
    {
        var context = Context(Request(text));

        context.Should().Contain("If your last reply offered or asked something, they have accepted it");
        context.Should().Contain("do it, in full, in this reply");
        context.Should().Contain("as parts of PedagogicalAnswer.Blocks when it is a language answer");
        context.Should().Contain("A reply that only says what you will do, or that something is coming");
    }

    [Theory]
    [InlineData("yes, but only two of them")]
    [InlineData("no")]
    [InlineData("아니요")]
    [InlineData("maybe")]
    [InlineData("좋아요?")]
    [InlineData("give me example sentences")]
    [InlineData("yes I would like you to explain the grammar of the second sentence instead")]
    public void A_message_that_is_not_a_plain_yes_is_left_to_the_standing_rules(string text)
    {
        Context(Request(text)).Should().NotContain("they have accepted it");
    }

    [Fact]
    public void A_yes_that_answers_a_clarifying_question_keeps_the_line_it_already_had()
    {
        // Issue 268's line covers this turn; the two are not sent together.
        var context = Context(Request("yes") with { AnswersAClarifyingQuestion = true });

        context.Should().Contain("If this message answers it, do what they first asked, in full, in this reply");
        context.Should().NotContain("they have accepted it");
    }

    [Fact]
    public void A_yes_to_an_open_plan_suggestion_is_not_told_to_do_something_else()
    {
        // The server accepts an open plan suggestion on a typed yes before the model is asked;
        // if the model does see such a turn, the suggestion is what the yes is about.
        var context = Context(Request("yes") with { IncludePlanContext = true, PendingSuggestionId = "suggestion-1" });

        context.Should().Contain("Open suggestion id: suggestion-1");
        context.Should().NotContain("they have accepted it");
    }

    [Fact]
    public void A_proposal_that_is_waiting_is_not_prepared_again_by_a_yes()
    {
        Context(Request("yes")).Should().Contain(
            "If a proposal is already waiting for their approval, say it is still waiting; do not prepare it again");
    }

    [Fact]
    public void The_standing_rules_say_an_accepted_offer_is_carried_out()
    {
        var rules = CoachInstructions.Instructions;

        rules.Should().Contain("When they accept something you offered");
        rules.Should().Contain("your offer is now their request: do it, in full,");
        rules.Should().Contain("Do not answer that you will, and do not offer it again");
    }

    [Fact]
    public async Task A_yes_after_an_offer_made_in_an_ordinary_reply_reaches_the_model_with_that_line()
    {
        // Through the real conversation and session services: the first reply is an ordinary
        // answer that ends with an offer, so no clarifying question is open when the yes arrives.
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Here is a hint. Want three example sentences?");
        await harness.TurnAsync(conversationId, "help me with this word");

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Sentences.");
        await harness.TurnAsync(conversationId, "yes");

        var request = harness.Coach.Requests[^1];
        request.LearnerText.Should().Be("yes");
        ApplicationLines(CoachInstructions.BuildTurnMessage(request))
            .Should().Contain("If your last reply offered or asked something, they have accepted it");
    }

    [Fact]
    public async Task A_yes_after_a_clarifying_question_reaches_the_model_with_the_line_for_that()
    {
        using var harness = new CoachConversationHarness();
        var conversationId = await harness.CreateConversationAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.AskClarification,
                ClarifyingQuestion = "Do you mean the word on screen?"
            }
        };
        await harness.TurnAsync(conversationId, "give me three sentences");

        harness.Coach.NextResult = CoachDurableTurnTests.Reply("Sentences.");
        await harness.TurnAsync(conversationId, "yes");

        var lines = ApplicationLines(CoachInstructions.BuildTurnMessage(harness.Coach.Requests[^1]));
        lines.Should().Contain("If this message answers it, do what they first asked, in full, in this reply");
        lines.Should().NotContain("they have accepted it");
    }
}
