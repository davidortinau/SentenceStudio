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
    public async Task AGeneralAnswer_ThatNamesADueWord_IsShown()
    {
        // Issue 258: being due for review is a scheduling fact the learner can see in their own
        // app, not a secret. This pinned the refusal before; the reply is now shown.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [new CoachEmbargoedItem("SENTINELDUEWORD", "sentinel gloss")];
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = General("One of your words is SENTINELDUEWORD.");

        var result = await AskAsync(harness, sessionId, "Show me one of my words.");

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        string.Join(" ", result.Value.Messages.Select(m => m.Text)).Should().Contain("SENTINELDUEWORD");
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Show me 3 sentences with \uC0DD\uC120")]
    [InlineData("Can you use \uC0DD\uC120 in three sentences for me?")]
    [InlineData("\uC0DD\uC120 \uC608\uBB38 \uC138 \uAC1C \uBCF4\uC5EC \uC918")]
    public async Task ThreeSentencesWithADueWord_AreShown_WhenTheyContainOtherDueWordsAndTheirMeanings(string text)
    {
        // Issue 258, the Captain's test case. The word asked about is due, and the three
        // sentences use other due words (market, eat, like) and their meanings. Before, one such
        // match discarded the whole reply and the learner got the safe-failure notice.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed =
        [
            new CoachEmbargoedItem("\uC0DD\uC120", "fish", WordId: "word-fish"),
            new CoachEmbargoedItem("\uC2DC\uC7A5", "market", WordId: "word-market"),
            new CoachEmbargoedItem("\uBA39\uB2E4", "eat", Lemma: "\uBA39\uB2E4", WordId: "word-eat"),
            new CoachEmbargoedItem("\uC88B\uC544\uD558\uB2E4", "like", WordId: "word-like",
                Examples: ["\uC800\uB294 \uC0DD\uC120\uC744 \uC88B\uC544\uD574\uC694."])
        ];
        var sessionId = await harness.StartSessionAsync();

        const string reply =
            "1. \uC2DC\uC7A5\uC5D0\uC11C \uC0DD\uC120\uC744 \uC0C0\uC5B4\uC694. (I bought fish at the market.)\n"
            + "2. \uC800\uB141\uC5D0 \uC0DD\uC120\uC744 \uBA39\uC5B4\uC694. (I eat fish in the evening.)\n"
            + "3. \uC800\uB294 \uC0DD\uC120\uC744 \uC88B\uC544\uD574\uC694. (I like fish.)";
        harness.Coach.NextResult = General(reply);

        var result = await AskAsync(harness, sessionId, text);

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.StopReason.Should().Be(CoachStopReason.Completed);
        result.Value.Messages.Should().ContainSingle()
            .Which.Should().Match<CoachMessageDto>(m => m.Kind == CoachMessageKind.Text && m.Text == reply);
        result.Value.Messages.Select(m => m.Text).Should().NotContain(CoachDeterministicCopy.ValidationFailedNeutral);
    }

    [Fact]
    public async Task AGeneralAnswer_OfAboutAThousandCharacters_IsShown()
    {
        // Issue 258: the cap on a general reply is the 1,600 a teaching answer allows, not 400.
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var reply = string.Join(" ", Enumerable.Repeat("Here is one more plain sentence about your words.", 20));
        reply.Length.Should().BeInRange(950, 1050);

        harness.Coach.NextResult = General(reply);

        var result = await AskAsync(harness, sessionId, "Tell me about my words.");

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.Messages.Should().ContainSingle().Which.Text.Should().Be(reply);
    }

    [Fact]
    public void AGeneralAnswer_OverSixteenHundredCharacters_IsStillRefusedForItsLength()
    {
        new CoachIntentValidator().ValidateIntent(new CoachTurnIntent
        {
            Kind = CoachIntentKind.GeneralAnswer,
            CoachMessage = new string('a', 1601)
        }).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("Give me example sentences using \uC0DD\uC120.")]
    [InlineData("Give me example sentences using \uFFFC\uC0DD\uC120\uFFFC")]
    [InlineData("\uC0DD\uC120\uC73C\uB85C \uC608\uBB38 \uB9CC\uB4E4\uC5B4 \uC918")]
    public async Task AGeneralAnswer_ThatUsesADueWordTheLearnerWrote_IsShown(string text)
    {
        // Issue 252: Sam is told it may repeat a due word the learner wrote in their message, but
        // the scan refused the reply all the same, so a request about a word that happened to be
        // due could never be answered. The second message carries the placeholder characters
        // (U+FFFC) the reported requests had beside the word.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [new CoachEmbargoedItem("\uC0DD\uC120", "fish", WordId: "word-fish")];
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = General(
            "\uC2DC\uC7A5\uC5D0\uC11C \uC0DD\uC120\uC744 \uC0C0\uC5B4\uC694. (I bought fish at the market.)");

        var result = await AskAsync(harness, sessionId, text);

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.StopReason.Should().Be(CoachStopReason.Completed);
        result.Value.Messages.Should().ContainSingle()
            .Which.Should().Match<CoachMessageDto>(m =>
                m.Kind == CoachMessageKind.Text && m.Text!.Contains("\uC0DD\uC120"));
    }

    [Fact]
    public async Task AGeneralAnswer_ThatAlsoNamesADueWordTheLearnerDidNotWrite_IsShown()
    {
        // Issue 258: this pinned the refusal (writing one word released that word only). No reply
        // is refused for a due word now, whoever wrote it.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed =
        [
            new CoachEmbargoedItem("\uC0DD\uC120", "fish", WordId: "word-fish"),
            new CoachEmbargoedItem("SENTINELDUEWORD", "sentinel gloss", WordId: "word-sentinel")
        ];
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = General("\uC0DD\uC120 goes well with SENTINELDUEWORD.");

        var result = await AskAsync(harness, sessionId, "Give me example sentences using \uC0DD\uC120.");

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        string.Join(" ", result.Value.Messages.Select(m => m.Text)).Should().Contain("SENTINELDUEWORD");
    }

    [Fact]
    public async Task AGeneralAnswer_IsShown_WhenTheLearnerWroteTheMeaningOfADueWord()
    {
        // Captain, 2026-11-20: what the learner asks for directly is not refused. "How do you say
        // fish" asks for the word, due or not.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [new CoachEmbargoedItem("\uC0DD\uC120", "fish", WordId: "word-fish")];
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = General("Fish is \uC0DD\uC120.");

        var result = await AskAsync(harness, sessionId, "How do you say fish?");

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        string.Join(" ", result.Value.Messages.Select(m => m.Text)).Should().Contain("\uC0DD\uC120");
    }

    [Theory]
    [InlineData("Is it \uC0DD\uC120?")]
    [InlineData("What is the answer?")]
    [InlineData("\uC815\uB2F5 \uC54C\uB824\uC918")]
    public async Task AGeneralAnswer_IsShown_WhenTheLearnerAsksForTheAnswerTheirScreenIsHiding(string text)
    {
        // The learner is in control of their learning (Captain, 2026-11-20): in a quiz, a typed
        // guess or a request for the answer is answered, not refused.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [new CoachEmbargoedItem("\uC0DD\uC120", WordId: "word-fish")];
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = General("It is \uC0DD\uC120.");

        var result = await AskInAQuizAsync(harness, sessionId, text);

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        string.Join(" ", result.Value.Messages.Select(m => m.Text)).Should().Contain("\uC0DD\uC120");
    }

    [Theory]
    [InlineData("Can I get a hint?")]
    [InlineData("Help me, but don't tell me the answer.")]
    [InlineData("Help me with this one.")]
    public async Task AGeneralAnswer_ThatGivesTheHiddenAnswerUnasked_BecomesTheHintOrAnswerQuestion(string text)
    {
        // Issue 258, item 5. What stays guarded is Sam giving the answer away when the learner
        // did not ask for it, but the turn no longer ends in the safe-failure notice: the learner
        // is asked which they want.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [new CoachEmbargoedItem("\uC0DD\uC120", WordId: "word-fish")];
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = General("It is \uC0DD\uC120.");

        var result = await AskInAQuizAsync(harness, sessionId, text);

        result.Value!.StopReason.Should().Be(CoachStopReason.ClarificationRequested);
        result.Value.ClarifyingQuestion.Should().Be(HintOrAnswerQuestion);
        result.Value.Messages.Should().ContainSingle()
            .Which.Should().Match<CoachMessageDto>(m =>
                m.Kind == CoachMessageKind.Clarification
                && m.Text == HintOrAnswerQuestion);
        var shown = string.Join(" ", result.Value.Messages.Select(m => m.Text));
        shown.Should().NotContain("\uC0DD\uC120");
        shown.Should().NotContain(CoachDeterministicCopy.ValidationFailedNeutral);
    }

    [Theory]
    [InlineData("the answer", "It is \uC0DD\uC120.")]
    [InlineData("just tell me", "It is \uC0DD\uC120.")]
    [InlineData("a hint", "It lives in water and you might buy it at a market.")]
    public async Task TheReplyToTheHintOrAnswerQuestion_IsHonoured(string reply, string samsNextReply)
    {
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [new CoachEmbargoedItem("\uC0DD\uC120", WordId: "word-fish")];
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = General("It is \uC0DD\uC120.");
        var asked = await AskInAQuizAsync(harness, sessionId, "Help me with this one.");
        asked.Value!.ClarifyingQuestion.Should().Be(HintOrAnswerQuestion);

        harness.Coach.NextResult = General(samsNextReply);
        var result = await AskInAQuizAsync(harness, sessionId, reply);

        // Sam is told this message replies to the question, and what it writes is shown: the
        // question is never asked twice in a row, and no turn here ends in the notice.
        CoachInstructions.BuildTurnMessage(harness.Coach.LastRequest!)
            .Should().Contain("whether they want a hint or the answer, this message is their reply");
        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.Messages.Should().ContainSingle()
            .Which.Should().Match<CoachMessageDto>(m => m.Kind == CoachMessageKind.Text && m.Text == samsNextReply);
    }

    [Fact]
    public async Task TheHintOrAnswerQuestion_CountsTowardTheBudget_AndIsNotAskedWhenNoneIsLeft()
    {
        // docs/coach/sam-safe-failure-notice.md, "The clarification budget": the question counts
        // as one, and when none is left the reply is shown instead of the limit's notice.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [new CoachEmbargoedItem("\uC0DD\uC120", WordId: "word-fish")];
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = General("It is \uC0DD\uC120.");
        await AskInAQuizAsync(harness, sessionId, "Help me with this one.");
        harness.Db.ChangeTracker.Clear();
        harness.Db.CoachSessions.Single().ClarificationCount.Should().Be(1);

        var session = harness.Db.CoachSessions.Single();
        session.ClarificationCount = CoachConstraintLimits.MaxClarificationsPerSession;
        session.Status = CoachSessionStatus.Active;
        harness.Db.SaveChanges();
        harness.Db.ChangeTracker.Clear();

        harness.Coach.NextResult = General("It is \uC0DD\uC120.");
        var result = await AskInAQuizAsync(harness, sessionId, "Help me with this one.");

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        var shown = string.Join(" ", result.Value.Messages.Select(m => m.Text));
        shown.Should().NotContain(CoachDeterministicCopy.ClarificationLimitReached);
        shown.Should().NotContain(CoachDeterministicCopy.ValidationFailedNeutral);
    }

    [Fact]
    public async Task AnOffTopicReply_IsShownAsSamWroteIt()
    {
        // Issue 258, item 4: the deflection is Sam's own words and never the notice.
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        const string deflection = "Ha, good one. A calculator will do that faster than I can. I'm your language tutor: shall we get back to your words?";
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent { Kind = CoachIntentKind.OffTopic, CoachMessage = deflection }
        };

        var result = await AskAsync(harness, sessionId, "What is 5+5?");

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.Messages.Should().ContainSingle().Which.Text.Should().Be(deflection);
    }

    // Written out, not read from CoachDeterministicCopy: the exact words a learner sees are pinned
    // here, and this file still builds against the code before issue 258.
    private const string HintOrAnswerQuestion = "Would you like a hint, or the answer?";

    private static Task<CoachOperationResult<CoachTurnResponse>> AskInAQuizAsync(
        CoachApplicationHarness harness, string sessionId, string text) =>
        harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = text,
            ClientScreen = new CoachClientScreenContext
            {
                ScreenId = "native.vocabulary-quiz",
                Route = "vocabulary-quiz",
                Platform = "ios",
                AnswerHidden = true,
                Items =
                [
                    new CoachClientScreenItem
                    {
                        Kind = CoachClientScreenItemKinds.VocabularyWord,
                        Id = "word-fish",
                        Label = "fish",
                        Focus = true
                    }
                ]
            }
        });

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
