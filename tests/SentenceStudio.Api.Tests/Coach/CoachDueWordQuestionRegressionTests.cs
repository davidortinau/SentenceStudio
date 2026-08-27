using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The vocabulary question the coach kept refusing, and the boundary that replaced the refusal.
/// </summary>
/// <remarks>
/// <para>
/// Two live sessions failed on <c>What's the difference between 좋아하다 뭉 좋다?</c> First with
/// sixteen violations, then — after an exemption for words the learner had typed — with five, on a
/// profile where neither word was due at all. Both refusals had the same cause, and the exemption
/// was treating the symptom.
/// </para>
/// <para>
/// <b>Root cause.</b> The answer path matched the model's explanation against the learner's review
/// queue, and no part of that queue can reach the model. The five read-only tools return counts,
/// bands, tags and resource metadata; <c>ICoachValidationDataSource</c> is injected into
/// <see cref="CoachSessionService"/> alone, after the model has answered. So a shared word is the
/// model writing an ordinary word out of its own language knowledge that the queue happens to
/// contain — "like", "good", "verb". Matching on it blocks no exfiltration path, because there is
/// none, and it fails more often the more of a language the learner is actively reviewing.
/// </para>
/// </remarks>
public class CoachDueWordQuestionRegressionTests
{
    private const string Question = "What's the difference between \uC88B\uC544\uD558\uB2E4 \uBB49 \uC88B\uB2E4?";

    /// <summary>The second live profile: the asked-about words are not due, unrelated words are.</summary>
    private static readonly CoachEmbargoedItem[] UnrelatedDueRowsWithCommonGlosses =
    [
        new("\uC120\uD638\uD558\uB2E4", "like"),
        new("\uD6CC\uB96D\uD558\uB2E4", "good"),
        new("\uAD1C\uCC2E\uB2E4", "good"),
        new("\uB3D9\uC0AC", "verb"),
        new("\uB73B", "meaning")
    ];

    // ---------------------------------------------------------------- the reported failures

    [Fact]
    public async Task TheReportedQuestion_IsAnswered_WhenTheAskedAboutWordsAreNotDue()
    {
        // The exact second failure. AuthorizedRowCount was 0 because 좋아하다/좋다 are not due for
        // this profile, and every unrelated row's gloss is a word the explanation must contain.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = UnrelatedDueRowsWithCommonGlosses;

        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = AnswerResult(ContrastAnswer());

        var result = await AskAsync(harness, sessionId, Question);

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.StopReason.Should().Be(CoachStopReason.Completed);
        result.Value.Answer.Should().NotBeNull();

        var text = result.Value.Answer!.PlainText;
        text.Should().Contain("like", "the meaning is what the question asked for");
        text.Should().Contain("good");

        harness.Db.CoachSessions.Single().StopReason.Should().BeNull();
    }

    [Fact]
    public async Task TheReportedQuestion_IsAnswered_WhenTheAskedAboutWordsAreDue()
    {
        // The first failure. Same question, same answer, opposite queue.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed =
        [
            new CoachEmbargoedItem("\uC88B\uC544\uD558\uB2E4", "to like"),
            new CoachEmbargoedItem("\uC88B\uB2E4", "good"),
            .. UnrelatedDueRowsWithCommonGlosses
        ];

        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = AnswerResult(ContrastAnswer());

        var result = await AskAsync(harness, sessionId, Question);

        result.Value!.Answer.Should().NotBeNull();
        result.Value.Answer!.PlainText.Should().Contain("\uC88B\uC544\uD558\uB2E4");
    }

    [Fact]
    public async Task ADueTermItself_MayBeTaughtWhenTheLearnerAsks()
    {
        // The strongest form: the learner asks about a word that is due, and the answer both
        // names it and translates it. Being scheduled does not make a word unteachable.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [new CoachEmbargoedItem("\uC0AC\uACFC", "apple")];

        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = AnswerResult(
            AnswerWith("\uC0AC\uACFC means apple.", CoachLanguageRole.Display));

        var result = await AskAsync(harness, sessionId, "What does \uC0AC\uACFC mean?");

        result.Value!.Answer.Should().NotBeNull();
        result.Value.Answer!.PlainText.Should().Contain("apple");
    }

    // ---------------------------------------------------------------- the data-flow guarantee

    [Fact]
    public async Task TheAnswerPathNeverQueriesTheDueQueue()
    {
        // The real control, and the reason no matcher is needed: the answer path does not read
        // the review list. Asserted on the path that previously read it on every turn.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = UnrelatedDueRowsWithCommonGlosses;

        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = AnswerResult(ContrastAnswer());

        await AskAsync(harness, sessionId, Question);

        harness.ValidationData.EmbargoQueryCount.Should().Be(0);
    }

    [Fact]
    public void NoToolTheModelCanCall_CarriesATermGlossOrExample()
    {
        // The upstream half of the same guarantee, enforced by the start-up scanner: a coach tool
        // shape may not even name a term, a gloss, a mnemonic, or an example sentence.
        var result = new CoachEmbargoScanner().ScanTypes(
        [
            typeof(SentenceStudio.Api.Coach.Tools.VocabularyDueSummary),
            typeof(SentenceStudio.Api.Coach.Tools.PlanPreviewSummary),
            typeof(SentenceStudio.Api.Coach.Tools.ResourceCatalogSummary),
            typeof(SentenceStudio.Api.Coach.Tools.PracticeBalanceSummary),
            typeof(SentenceStudio.Api.Coach.Tools.LearnerProfileSummary)
        ]);

        result.IsValid.Should().BeTrue(
            "a collision between an answer and the queue is only a coincidence while this holds");
    }

    // ---------------------------------------------------------------- what an answer still cannot do

    [Fact]
    public async Task AnAnswerStillWritesNothing()
    {
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = UnrelatedDueRowsWithCommonGlosses;

        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = AnswerResult(ContrastAnswer());

        var planBefore = harness.PlanService.Current.Version;
        var result = await AskAsync(harness, sessionId, Question);

        result.Value!.ChangeReceipt.Should().BeNull();
        result.Value.PendingSuggestion.Should().BeNull();
        harness.PlanService.Current.Version.Should().Be(planBefore);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task AMalformedAnswerIsStillRefusedBeforeAnythingIsStored()
    {
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = UnrelatedDueRowsWithCommonGlosses;

        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = AnswerResult(new CoachPedagogicalAnswerIntent
        {
            Topic = CoachAnswerTopic.Vocabulary,
            Blocks = []
        });

        var result = await AskAsync(harness, sessionId, Question);

        result.Value!.Answer.Should().BeNull();
        result.Value.Status.Should().Be(CoachTurnStatus.Rejected);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task AMixedTurn_IsNotRefusedForASentinelInItsDiscardedPlanMessage()
    {
        // A mixed turn surfaces the validated answer and deterministic rationale. Its
        // CoachMessage is thrown away, so a sentinel sitting in it can reach no one, and
        // refusing the turn over it would only lose a good answer and a good suggestion.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [new CoachEmbargoedItem("SENTINELDUEWORD", "sentinel gloss")];

        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.SuggestConstraintChange,
                ConstraintDelta = new CoachConstraintDeltaIntent { AvailableMinutes = 5 },
                PedagogicalAnswer = ContrastAnswer(),
                CoachMessage = "SENTINELDUEWORD would help here."
            }
        };

        var result = await AskAsync(
            harness, sessionId, $"{Question} Also make today shorter.");

        result.Value!.Status.Should().Be(CoachTurnStatus.Completed);
        result.Value.Answer.Should().NotBeNull("the answer is validated and surfaced on its own");
        result.Value.PendingSuggestion.Should().NotBeNull();

        // Nothing the model wrote reached the learner, so the sentinel did not either.
        result.Value.Messages.Should().NotContain(m => m.Text.Contains("SENTINELDUEWORD"));
        result.Value.PendingSuggestion!.Rationale.Should().NotContain("SENTINELDUEWORD");

        // Still a suggestion: no write until the learner accepts.
        harness.PlanService.ApplyCallCount.Should().Be(0);
        harness.Db.CoachPlanRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task ASurfacedNoChangeMessage_IsStillRefusedForASentinel()
    {
        // The other half of the same rule: NoChange falls through to the branch that shows the
        // model's CoachMessage verbatim, so that string is scanned.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [new CoachEmbargoedItem("SENTINELDUEWORD", "sentinel gloss")];

        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.NoChange,
                CoachMessage = "Nothing to change, but try SENTINELDUEWORD."
            }
        };

        var result = await AskAsync(harness, sessionId, "anything to change?");

        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        result.Value.Messages.Should().NotContain(m => m.Text.Contains("SENTINELDUEWORD"));
        harness.ValidationData.EmbargoQueryCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ThePlanPathStillScansModelAuthoredPlanText()
    {
        // Scope check: a clarifying question is model-authored and surfaced verbatim, so it is
        // scanned.
        using var harness = new CoachApplicationHarness();
        harness.ValidationData.Embargoed = [new CoachEmbargoedItem("SENTINELDUEWORD", "sentinel gloss")];

        var sessionId = await harness.StartSessionAsync();
        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.AskClarification,
                ClarifyingQuestion = "Did you mean SENTINELDUEWORD?",
                CoachMessage = string.Empty
            }
        };

        var result = await AskAsync(harness, sessionId, "change my plan somehow");

        result.Value!.Status.Should().Be(CoachTurnStatus.Rejected);
        harness.ValidationData.EmbargoQueryCount.Should().BeGreaterThan(0);
    }

    // ---------------------------------------------------------------- helpers

    private static CoachAgentTurnResult AnswerResult(CoachPedagogicalAnswerIntent answer) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.PedagogicalAnswer,
            PedagogicalAnswer = answer,
            CoachMessage = string.Empty
        }
    };

    /// <summary>The answer a real model gives here. It cannot avoid "like" or "good".</summary>
    private static CoachPedagogicalAnswerIntent ContrastAnswer() => new()
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
                        Text = "\uC88B\uC544\uD558\uB2E4 means to like something. \uC88B\uB2E4 means to be good.",
                        Language = CoachLanguageRole.Display
                    }
                ]
            },
            new CoachAnswerBlockIntent
            {
                Kind = CoachAnswerBlockKind.Use,
                Label = "Use",
                Spans =
                [
                    new CoachAnswerSpanIntent
                    {
                        Text = "Use \uC88B\uC544\uD558\uB2E4 for a person who likes something.",
                        Language = CoachLanguageRole.Display
                    }
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
}
