using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The coach as a language-learning partner.
/// </summary>
/// <remarks>
/// Stage A of the dual-purpose coach: it stays a safe Today's Plan editor and gains the ability
/// to answer questions about vocabulary, grammar, usage, pronunciation, the learner's own
/// writing, conversation, and study strategy. The rule that makes both safe at once is that an
/// answer is a no-write turn — it may persist the encrypted conversation and its token usage,
/// and nothing else.
/// </remarks>
public class CoachPedagogicalAnswerTests
{
    // ---------------------------------------------------------------- answering

    [Fact]
    public async Task TheKoreanContrastQuestion_IsAnswered()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = AnswerResult(KoreanContrastAnswer());

        var result = await AskAsync(
            harness, sessionId, "What's the difference between \uC88B\uC544\uD558\uB2E4 and \uC88B\uB2E4?");

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);

        var answer = result.Value.Answer!;
        answer.Topic.Should().Be(CoachAnswerTopic.Vocabulary);
        answer.Blocks[0].Kind.Should().Be(CoachAnswerBlockKind.Answer, "the direct answer comes first");
        answer.PlainText.Should().Contain("\uC88B\uC544\uD558\uB2E4");

        // Server-resolved tags, so a client can pick the right script and voice.
        answer.TargetLanguageTag.Should().Be("ko-KR");
        answer.DisplayLanguageTag.Should().Be("en-US");
        answer.Blocks.SelectMany(b => b.Spans)
            .Where(s => s.Language == CoachLanguageRole.Target)
            .Should().OnlyContain(s => s.LanguageTag == "ko-KR");

        result.Value.Messages.Should().ContainSingle()
            .Which.Kind.Should().Be(CoachMessageKind.PedagogicalAnswer);
    }

    [Fact]
    public async Task AnAnswer_WritesNothingAtAll()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = AnswerResult(KoreanContrastAnswer());

        var planBefore = harness.PlanService.Current.Version;
        var constraintsBefore = harness.Db.CoachSessions.Single().ActiveConstraintsJson;

        var result = await AskAsync(harness, sessionId, "What does \uC88B\uB2E4 mean?");

        result.Value!.ChangeReceipt.Should().BeNull();
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.PlanService.PreviewCallCount.Should().Be(0, "answering previews nothing");
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
        harness.PlanService.Current.Version.Should().Be(planBefore);
        harness.Db.CoachSessions.Single().ActiveConstraintsJson.Should().Be(constraintsBefore);
    }

    [Fact]
    public async Task AFollowUpQuestion_ResumesTheEncryptedConversation()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = AnswerResult(KoreanContrastAnswer(), """{"turn":1}""");
        await AskAsync(harness, sessionId, "What's the difference between \uC88B\uC544\uD558\uB2E4 and \uC88B\uB2E4?");

        harness.Coach.NextResult = AnswerResult(KoreanContrastAnswer(), """{"turn":2}""");
        await AskAsync(harness, sessionId, "And with a person?");

        harness.Coach.LastRequest!.AgentSessionJson.Should().Be("""{"turn":1}""");
        harness.Db.CoachSessions.Single().ProtectedAgentSession.Should().NotContain("turn");
    }

    [Fact]
    public async Task AQuestionIsAnsweredWithNoPlanForToday()
    {
        using var harness = new CoachApplicationHarness();
        harness.PlanService.SetItems(Array.Empty<SentenceStudio.Services.Plans.PlanSnapshotItem>());

        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = AnswerResult(KoreanContrastAnswer());

        var result = await AskAsync(harness, sessionId, "What does \uC88B\uB2E4 mean?");

        result.IsOk.Should().BeTrue();
        result.Value!.Answer.Should().NotBeNull();
        harness.PlanService.Current.Items.Should().BeEmpty("asking a question never creates a plan");
    }

    [Fact]
    public async Task APlanCommandWithNoPlan_ExplainsInsteadOfWriting()
    {
        using var harness = new CoachApplicationHarness();
        harness.PlanService.SetItems(Array.Empty<SentenceStudio.Services.Plans.PlanSnapshotItem>());

        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = DirectResult(d => d.AvailableMinutes = 10);

        var result = await AskAsync(harness, sessionId, "make today 10 minutes");

        result.IsOk.Should().BeTrue();
        result.Value!.Messages.Single().Text.Should().Contain("no plan for today yet");
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.PlanService.Current.Items.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- pending offers

    [Fact]
    public async Task APureQuestion_LeavesAnOpenOfferExactlyWhereItWas()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        var offered = await OfferSuggestionAsync(harness, sessionId);

        harness.Coach.NextResult = AnswerResult(KoreanContrastAnswer());
        var result = await AskAsync(harness, sessionId, "What does \uC88B\uB2E4 mean?");

        result.Value!.Answer.Should().NotBeNull();
        result.Value.PendingSuggestion!.SuggestionId.Should().Be(offered.SuggestionId);
        result.Value.SessionStatus.Should().Be(CoachSessionStatus.SuggestionPending);

        var row = harness.Db.CoachSessions.Single();
        row.PendingSuggestionId.Should().Be(offered.SuggestionId);
        row.PendingSuggestionDeltaJson.Should().NotBeNullOrEmpty();
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task AMixedTurn_AnswersAndOffersWithoutWriting()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        // A language question and a plan request in one message.
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 5 },
                PedagogicalAnswer = KoreanContrastAnswer(),
                CoachMessage = "Here is the difference, and a shorter session."
            }
        };

        var result = await AskAsync(
            harness, sessionId,
            "What's the difference between \uC88B\uC544\uD558\uB2E4 and \uC88B\uB2E4? Also make today 5 minutes.");

        result.Value!.Answer.Should().NotBeNull("the question is answered on the same turn");
        result.Value.PendingSuggestion.Should().NotBeNull("the plan change waits for acceptance");
        result.Value.PendingSuggestion!.Delta.AvailableMinutes.Should().Be(5);
        result.Value.ChangeReceipt.Should().BeNull();

        harness.PlanService.ApplyCallCount.Should().Be(0, "zero write until an explicit acceptance");
        harness.Db.CoachPlanRevisions.Should().BeEmpty();

        result.Value.Messages.Select(m => m.Kind).Should()
            .Equal(CoachMessageKind.PedagogicalAnswer, CoachMessageKind.Suggestion);
    }

    [Fact]
    public async Task AMixedTurnsOffer_AppliesOnlyWhenAccepted()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 5 },
                PedagogicalAnswer = KoreanContrastAnswer(),
                CoachMessage = "Answer plus a suggestion."
            }
        };

        var suggestion = (await AskAsync(harness, sessionId, "What does \uC88B\uB2E4 mean? Also 5 minutes."))
            .Value!.PendingSuggestion!;

        var accepted = await harness.Service.AcceptSuggestionAsync(
            sessionId, suggestion.SuggestionId, new CoachSuggestionDecisionRequest());

        accepted.Value!.ChangeReceipt.Should().NotBeNull();
        accepted.Value.Answer.Should().BeNull("an applied change is not an answer");
        harness.Db.CoachPlanRevisions.Should().ContainSingle();
    }

    // ---------------------------------------------------------------- write authority

    [Theory]
    [InlineData("What's the difference between \uC88B\uC544\uD558\uB2E4 and \uC88B\uB2E4? Also make today 5 minutes.")]
    [InlineData("make it 10 minutes and what does \uC88B\uB2E4 mean")]
    [InlineData("set 10 minutes. also explain \uC88B\uB2E4")]
    [InlineData("change to \"10 minutes\"")]
    public async Task AMessageThatIsNotPurelyAPlanCommand_IsOfferedNotApplied(string text)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        // Even when the model calls it a direct change, the message decides.
        harness.Coach.NextResult = DirectResult(d => d.AvailableMinutes = 5);

        var result = await AskAsync(harness, sessionId, text);

        result.Value!.ChangeReceipt.Should().BeNull();
        result.Value.PendingSuggestion.Should().NotBeNull();
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("make it 10 minutes")]
    [InlineData("make today's plan 5 minutes and no audio")]
    [InlineData("10\uBD84\uC73C\uB85C \uBC14\uAF242")]
    public async Task AnExclusivePlanCommand_StillAppliesImmediately(string text)
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = DirectResult(d => d.AvailableMinutes = 10);

        var result = await AskAsync(harness, sessionId, text);

        result.Value!.ChangeReceipt.Should().NotBeNull("plan editing must not regress");
        harness.Db.CoachPlanRevisions.Should().ContainSingle();
    }

    [Fact]
    public async Task AStructuredConstraintAction_IsUnaffectedByTheTextGate()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var result = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.ConstraintAction,
            ConstraintAction = new CoachConstraintDeltaDto { AvailableMinutes = 10 }
        });

        result.Value!.ChangeReceipt.Should().NotBeNull("a tap is already unambiguous");
    }

    // ---------------------------------------------------------------- the due queue

    [Fact]
    public async Task AWordTheLearnerAskedAbout_IsExplainedWhetherOrNotItIsDue()
    {
        // Same question, same answer, both ways round. Review scheduling is not a gate on
        // teaching: a learner who is actively reviewing a word is the likeliest person to ask
        // about it.
        foreach (var due in new[] { true, false })
        {
            using var harness = new CoachApplicationHarness();
            harness.ValidationData.Embargoed = due
                ? [Due("\uC88B\uC544\uD558\uB2E4", "to like"), Due("\uC88B\uB2E4", "good")]
                : [];

            var sessionId = await harness.StartSessionAsync();
            harness.Coach.NextResult = AnswerResult(KoreanContrastAnswer());

            var result = await AskAsync(
                harness, sessionId, "What's the difference between \uC88B\uC544\uD558\uB2E4 and \uC88B\uB2E4?");

            result.Value!.Answer.Should().NotBeNull($"due={due} must not change whether a word can be taught");
            result.Value.Answer!.PlainText.Should().Contain("\uC88B\uC544\uD558\uB2E4");
            result.Value.StopReason.Should().Be(CoachStopReason.Completed);
        }
    }

    [Fact]
    public async Task AnAnswerNeverReadsTheDueQueueAtAll()
    {
        // The guarantee is upstream of any matcher: the answer path does not fetch the review
        // list, so there is nothing to match against and nothing to get wrong.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [Due("\uC0AC\uACFC", "apple")];

        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = AnswerResult(KoreanContrastAnswer());

        await AskAsync(harness, sessionId, "What does \uC88B\uB2E4 mean?");

        harness.ValidationData.EmbargoQueryCount.Should().Be(0);
    }

    [Fact]
    public async Task AWordThatHappensToBeDue_DoesNotBlockAnUnrelatedAnswer()
    {
        // The reported failure: 사과/apple is due, the learner asked about something else, and
        // the model wrote an ordinary word out of its own knowledge that collided with the queue.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [Due("\uC0AC\uACFC", "apple"), Due("\uC88B\uB2E4", "good")];

        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = AnswerResult(
            AnswerWith("It is good to eat an apple.", CoachLanguageRole.Display));

        var result = await AskAsync(harness, sessionId, "How do I talk about food?");

        result.Value!.Answer.Should().NotBeNull();
        result.Value.Status.Should().Be(CoachTurnStatus.Completed);
    }

    [Fact]
    public async Task TheModelNeverReceivesADueTermToRepeat()
    {
        // The reason a collision is a coincidence and not a leak: every tool answer the model can
        // see is counts and metadata. The start-up scanner refuses any shape that could name a
        // term, a gloss, or an example.
        var violations = new SentenceStudio.Api.Coach.Validation.CoachEmbargoScanner()
            .ScanTypes(
            [
                typeof(SentenceStudio.Api.Coach.Tools.VocabularyDueSummary),
                typeof(SentenceStudio.Api.Coach.Tools.PlanPreviewSummary),
                typeof(SentenceStudio.Api.Coach.Tools.ResourceCatalogSummary),
                typeof(SentenceStudio.Api.Coach.Tools.PracticeBalanceSummary),
                typeof(SentenceStudio.Api.Coach.Tools.LearnerProfileSummary)
            ]);

        violations.IsValid.Should().BeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EveryBlockAndTheFlattenedFallbackAreProjected()
    {
        // Block and span bounds still apply to every block, not just the first.
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var answer = KoreanContrastAnswer();
        answer.Blocks.Add(new CoachAnswerBlockIntent
        {
            Kind = CoachAnswerBlockKind.Note,
            Spans = [new CoachAnswerSpanIntent
            {
                Text = new string('x', CoachAnswerLimits.MaxSpanCharacters + 1),
                Language = CoachLanguageRole.Display
            }]
        });

        harness.Coach.NextResult = AnswerResult(answer);
        var result = await AskAsync(harness, sessionId, "What does \uC88B\uB2E4 mean?");

        result.Value!.Answer.Should().BeNull("an oversized span in any block fails the whole answer");
    }

    // ---------------------------------------------------------------- shape and bounds

    [Fact]
    public async Task AnOversizedAnswer_IsRefusedBeforeAnythingIsShownOrStored()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var answer = KoreanContrastAnswer();
        answer.Blocks[0].Spans[0].Text = new string('a', CoachAnswerLimits.MaxSpanCharacters + 1);
        harness.Coach.NextResult = AnswerResult(answer);

        var result = await AskAsync(harness, sessionId, "What does \uC88B\uB2E4 mean?");

        result.Value!.Answer.Should().BeNull();
        result.Value.Status.Should().Be(CoachTurnStatus.Rejected);
        // No learner-visible message any more. The refusal used to ship a hardcoded English
        // sentence past the client's resource file; the reason is now a closed code the client
        // renders in the learner's own language.
        result.Value.Messages.Should().BeEmpty();
        result.Value.Limitation!.Code.Should().Be(CoachLimitationCode.AnswerShapeInvalid);
    }

    [Fact]
    public async Task TooManyBlocks_AreRefused()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var answer = KoreanContrastAnswer();
        while (answer.Blocks.Count <= CoachAnswerLimits.MaxBlocks)
        {
            answer.Blocks.Add(new CoachAnswerBlockIntent
            {
                Kind = CoachAnswerBlockKind.Note,
                Spans = [new CoachAnswerSpanIntent { Text = "note", Language = CoachLanguageRole.Display }]
            });
        }

        harness.Coach.NextResult = AnswerResult(answer);

        (await AskAsync(harness, sessionId, "What does \uC88B\uB2E4 mean?")).Value!.Answer.Should().BeNull();
    }

    [Fact]
    public async Task AnAnswerThatDoesNotLeadWithTheAnswer_IsRefused()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        var answer = KoreanContrastAnswer();
        answer.Blocks[0].Kind = CoachAnswerBlockKind.Note;
        harness.Coach.NextResult = AnswerResult(answer);

        (await AskAsync(harness, sessionId, "What does \uC88B\uB2E4 mean?")).Value!.Answer.Should().BeNull();
    }

    [Fact]
    public async Task AnEmptyAnswer_IsRefused()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = AnswerResult(new CoachPedagogicalAnswerIntent
        {
            Topic = CoachAnswerTopic.Vocabulary
        });

        (await AskAsync(harness, sessionId, "What does \uC88B\uB2E4 mean?")).Value!.Answer.Should().BeNull();
    }

    [Fact]
    public async Task AnAnswerOnAWritingTurn_IsRefusedByTheIntentValidator()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        // An answer may never ride along with a direct write.
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.DirectConstraintChange,
                ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 10 },
                PedagogicalAnswer = KoreanContrastAnswer(),
                CoachMessage = "Done."
            }
        };

        var result = await AskAsync(harness, sessionId, "make it 10 minutes");

        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        result.Value.StopReason.Should().Be(CoachStopReason.ValidationFailed);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- assessment answers

    [Fact]
    public async Task AnExplicitRequestForAQuizAnswer_IsRefusedByInstruction()
    {
        // MVP position: the instructions forbid it, and the model reports OffTopic rather than
        // answering. No untrusted "an assessment is running" flag is invented to gate it.
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.OffTopic,
                CoachMessage = "I will not give the answer, but I can give you a hint."
            }
        };

        var result = await AskAsync(harness, sessionId, "just tell me the answer to this quiz question");

        result.Value!.Answer.Should().BeNull();
        harness.Db.CoachPlanRevisions.Should().BeEmpty();

        CoachInstructions.Instructions.Should().Contain("being tested on");
    }

    // ---------------------------------------------------------------- privacy

    [Fact]
    public async Task NoAnswerTextReachesTheAuditOrTheRow()
    {
        const string sentinel = "SENTINEL_ANSWER_9d21";

        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = AnswerResult(
            AnswerWith(sentinel, CoachLanguageRole.Display),
            $$"""{"learner":"{{sentinel}}"}""");

        await AskAsync(harness, sessionId, $"What does {sentinel} mean?");

        var row = harness.Db.CoachSessions.Single();
        row.ActiveConstraintsJson.Should().NotContain(sentinel);
        row.PendingSuggestionDeltaJson.Should().BeNull();
        row.ProtectedAgentSession.Should().NotContain(sentinel, "the conversation is encrypted at rest");
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- helpers

    private static CoachEmbargoedItem Due(string target, string native) => new(target, native);

    private static CoachAgentTurnResult AnswerResult(
        CoachPedagogicalAnswerIntent answer, string? agentSessionJson = null) => new()
        {
            Outcome = CoachAgentOutcome.Completed,
            AgentSessionJson = agentSessionJson,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.PedagogicalAnswer,
                PedagogicalAnswer = answer,
                CoachMessage = string.Empty
            }
        };

    private static CoachAgentTurnResult DirectResult(Action<CoachConstraintDeltaIntent> configure)
    {
        var delta = new CoachConstraintDeltaIntent();
        configure(delta);

        return new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.DirectConstraintChange,
                ConstraintDelta = delta,
                CoachMessage = "Done."
            }
        };
    }

    private static CoachPedagogicalAnswerIntent KoreanContrastAnswer() => new()
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
                        Text = "\uC88B\uC544\uD558\uB2E4 is a verb, \"to like\". \uC88B\uB2E4 is an adjective, \"to be good\".",
                        Language = CoachLanguageRole.Display
                    }
                ]
            },
            new CoachAnswerBlockIntent
            {
                Kind = CoachAnswerBlockKind.Example,
                Label = "Example",
                Spans =
                [
                    new CoachAnswerSpanIntent
                    {
                        Text = "\uC800\uB294 \uCEE4\uD53C\uB97C \uC88B\uC544\uD574\uC694.",
                        Language = CoachLanguageRole.Target
                    },
                    new CoachAnswerSpanIntent { Text = "I like coffee.", Language = CoachLanguageRole.Display }
                ]
            }
        ]
    };

    private static CoachPedagogicalAnswerIntent AnswerWith(string text, CoachLanguageRole role) => new()
    {
        Topic = CoachAnswerTopic.Vocabulary,
        Blocks =
        [
            new CoachAnswerBlockIntent
            {
                Kind = CoachAnswerBlockKind.Answer,
                Spans = [new CoachAnswerSpanIntent { Text = text, Language = role }]
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
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                ConstraintDelta = new CoachConstraintDeltaIntent { SkillEmphasis = CoachSkillEmphasis.Writing },
                CoachMessage = "A suggestion."
            }
        };

        return (await AskAsync(harness, sessionId, "what should I do today?")).Value!.PendingSuggestion!;
    }
}
