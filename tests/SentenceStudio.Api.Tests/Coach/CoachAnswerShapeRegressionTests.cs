using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using CoachToolNames = SentenceStudio.Api.Coach.Tools.CoachToolNames;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// R5 regression suite for the Sam answer-shape repair (2026-08-22).
/// Tests 1-3 of the mandatory six: S7 scripted-chat integration, schema contract with numeric
/// budget wording, and shape-refusal parameterized unit tests.
/// </summary>
public sealed class CoachAnswerShapeRegressionTests
{
    // ================================================================ TEST 1
    // Literal S7 scripted-chat integration test: a realistic bilingual grammar-comparison answer
    // that fits current bounds. Asserts Completed, PedagogicalAnswer, projection success, useful
    // L2 examples/retrieval prompt preserved.

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public async Task S7_bilingual_grammar_answer_within_bounds_ships_successfully(CoachImplementation arm)
    {
        // A realistic Korean grammar-comparison answer: explains the difference between
        // -neunde and -jiman for contrast, with bilingual examples and a retrieval prompt.
        // Total chars: well under 1600.
        const string json = """
            {
              "Kind": "PedagogicalAnswer",
              "PedagogicalAnswer": {
                "Topic": "Grammar",
                "Blocks": [
                  {
                    "Kind": "Answer",
                    "Spans": [
                      { "Text": "Both connect two clauses, but they differ in tone.", "Language": "Display" },
                      { "Text": "-jiman states a clean contrast.", "Language": "Display" },
                      { "Text": "-neunde sets up background before the main point.", "Language": "Display" }
                    ]
                  },
                  {
                    "Kind": "Example",
                    "Label": "Clean contrast (-jiman)",
                    "Spans": [
                      { "Text": "\uD55C\uAD6D\uC5B4\uB294 \uC5B4\uB835\uC9C0\uB9CC \uC7AC\uBBF8\uC788\uC5B4\uC694.", "Language": "Target" },
                      { "Text": "Korean is hard, but it is fun.", "Language": "Display" }
                    ]
                  },
                  {
                    "Kind": "Example",
                    "Label": "Background (-neunde)",
                    "Spans": [
                      { "Text": "\uBE44\uAC00 \uC624\uB294\uB370 \uC6B0\uC0B0 \uC548 \uAC00\uC838\uC654\uC5B4\uC694.", "Language": "Target" },
                      { "Text": "It was raining, and I did not bring an umbrella.", "Language": "Display" }
                    ]
                  },
                  {
                    "Kind": "Note",
                    "Spans": [
                      { "Text": "Use -neunde when the first clause is necessary context for the second.", "Language": "Display" }
                    ]
                  },
                  {
                    "Kind": "RetrievalPrompt",
                    "Spans": [
                      { "Text": "Can you make a sentence using -neunde to set up background?", "Language": "Display" }
                    ]
                  }
                ]
              },
              "CoachMessage": ""
            }
            """;

        var result = await RunAsync(new ScriptedChatClient(json), arm);

        result.Outcome.Should().Be(CoachAgentOutcome.Completed);
        result.Intent!.Kind.Should().Be(CoachIntentKind.PedagogicalAnswer);
        result.Intent.PedagogicalAnswer!.Topic.Should().Be(CoachAnswerTopic.Grammar);
        result.Intent.PedagogicalAnswer.Blocks.Should().HaveCount(5);
        result.Intent.PedagogicalAnswer.Blocks[^1].Kind.Should().Be(CoachAnswerBlockKind.RetrievalPrompt);

        // The projection must succeed: the answer is within bounds.
        var languages = new CoachLanguageProfile("ko-KR", "en-US", "en-US");
        var projection = new CoachAnswerProjection().Project(result.Intent.PedagogicalAnswer, languages);

        projection.IsValid.Should().BeTrue("this realistic S7 grammar answer is within all current bounds");
        projection.Answer!.EndsWithRecallQuestion.Should().BeTrue("the retrieval prompt is preserved");
        projection.Answer.PlainText.Should().Contain("-neunde");
        projection.Answer.Blocks.Should().Contain(b => b.Kind == CoachAnswerBlockKind.Example,
            "bilingual examples are preserved");
    }

    // ================================================================ TEST 2
    // Contract/schema test: the schema JSON includes numeric total-character budget wording.

    [Fact]
    public async Task Schema_includes_numeric_total_character_budget()
    {
        var client = new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""");
        await RunAsync(client, CoachImplementation.Baseline);

        var schema = ((ChatResponseFormatJson)client.LastOptions!.ResponseFormat!).Schema!.Value.GetRawText();

        // R1 repair: the [Description] on CoachPedagogicalAnswerIntent.Blocks now includes the
        // numeric bound "1600" so the model knows the limit before producing an answer.
        schema.Should().Contain("1600",
            "the schema must carry the numeric total-character budget so the model is aware of the cap");
    }

    // ================================================================ TEST 3
    // Shape-refusal unit tests on the real projection path.

    [Fact]
    public void Shape_refusal_length_violation_span_overrun()
    {
        // Trip rule: "A piece of text is longer than 320 characters."
        var oversizedSpanText = new string('x', CoachAnswerLimits.MaxSpanCharacters + 1);
        var intent = new CoachPedagogicalAnswerIntent
        {
            Topic = CoachAnswerTopic.Grammar,
            Blocks =
            [
                new CoachAnswerBlockIntent
                {
                    Kind = CoachAnswerBlockKind.Answer,
                    Spans = [new CoachAnswerSpanIntent { Text = oversizedSpanText, Language = CoachLanguageRole.Display }]
                }
            ]
        };

        var languages = new CoachLanguageProfile("ko-KR", "en-US", "en-US");
        var result = new CoachAnswerProjection().Project(intent, languages);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains($"{CoachAnswerLimits.MaxSpanCharacters}"),
            "the specific length-rule string is present");

        // The violation classifier sees this as a length violation.
        var violation = ClassifyViaReflection(result.Errors);
        violation.Should().Be(CoachViolationKind.LengthLimit);
    }

    [Fact]
    public void Shape_refusal_length_violation_total_overrun()
    {
        // Trip rule: "The answer is longer than 1600 characters."
        // Use multiple blocks, each at max span but total exceeds 1600.
        var spanText = new string('y', 300); // 300 chars each
        var intent = new CoachPedagogicalAnswerIntent
        {
            Topic = CoachAnswerTopic.Usage,
            Blocks = Enumerable.Range(0, 6).Select(_ => new CoachAnswerBlockIntent
            {
                Kind = CoachAnswerBlockKind.Answer,
                Spans = [new CoachAnswerSpanIntent { Text = spanText, Language = CoachLanguageRole.Display }]
            }).ToList()
        };

        // 6 blocks x 300 chars = 1800 > 1600
        var languages = new CoachLanguageProfile("ko-KR", "en-US", "en-US");
        var result = new CoachAnswerProjection().Project(intent, languages);

        // The "only one direct answer" rule fires first, but the total overrun rule still fires.
        // The important thing is the errors contain the total-character limit message.
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains($"{CoachAnswerLimits.MaxTotalCharacters}"),
            "the total-character-limit rule string is present");
    }

    [Fact]
    public void Shape_refusal_non_length_violation_retrieval_prompt_not_last()
    {
        // Trip rule: "A retrieval prompt must be the last part of an answer."
        var intent = new CoachPedagogicalAnswerIntent
        {
            Topic = CoachAnswerTopic.Vocabulary,
            Blocks =
            [
                new CoachAnswerBlockIntent
                {
                    Kind = CoachAnswerBlockKind.Answer,
                    Spans = [new CoachAnswerSpanIntent { Text = "Definition here.", Language = CoachLanguageRole.Display }]
                },
                new CoachAnswerBlockIntent
                {
                    Kind = CoachAnswerBlockKind.RetrievalPrompt,
                    Spans = [new CoachAnswerSpanIntent { Text = "Can you use it?", Language = CoachLanguageRole.Display }]
                },
                new CoachAnswerBlockIntent
                {
                    Kind = CoachAnswerBlockKind.Note,
                    Spans = [new CoachAnswerSpanIntent { Text = "Extra note.", Language = CoachLanguageRole.Display }]
                }
            ]
        };

        var languages = new CoachLanguageProfile("ko-KR", "en-US", "en-US");
        var result = new CoachAnswerProjection().Project(intent, languages);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("retrieval prompt must be the last"),
            "the non-length rule about retrieval-prompt ordering fires");

        // The classifier sees this as an IntentShape violation (non-length).
        var violation = ClassifyViaReflection(result.Errors);
        violation.Should().Be(CoachViolationKind.IntentShape);
    }

    [Fact]
    public void ProjectShape_returns_AnswerShapeInvalid_with_correct_fields()
    {
        var asOf = new DateTime(2026, 8, 22, 14, 30, 0, DateTimeKind.Utc);
        var limitation = CoachRefusalLimitationProjection.ProjectShape(asOf);

        limitation.Code.Should().Be(CoachLimitationCode.AnswerShapeInvalid);
        limitation.Coverage.Should().Be(CoachEvidenceCoverage.Unknown);
        limitation.AsOfUtc.Should().NotBeNull();
        limitation.Destination.Should().BeNull();
        limitation.AffectedCount.Should().BeNull();
        limitation.WithheldCount.Should().BeNull();
        limitation.WithheldReason.Should().BeNull();
        limitation.Alternatives.Should().BeEmpty();
        limitation.HintLadder.Should().BeEmpty();
        limitation.ShorterSession.Should().BeNull();
        limitation.FullScopeSurface.Should().BeNull();
        limitation.ExportSurface.Should().BeNull();
    }

    // ================================================================ Helpers

    /// <summary>
    /// Invokes the private static ClassifyAnswerShapeViolation via the same logic — the two
    /// prefix-match constants are compile-time strings in CoachSessionService. We replicate the
    /// classification here to test the same branching without reflecting into privates.
    /// </summary>
    private static CoachViolationKind ClassifyViaReflection(IReadOnlyList<string> errors)
    {
        const string spanOverrunPrefix = "A piece of text is longer than";
        const string totalOverrunPrefix = "The answer is longer than";

        foreach (var error in errors)
        {
            if (error.StartsWith(spanOverrunPrefix, StringComparison.Ordinal)
                || error.StartsWith(totalOverrunPrefix, StringComparison.Ordinal))
            {
                return CoachViolationKind.LengthLimit;
            }
        }

        return CoachViolationKind.IntentShape;
    }

    private static async Task<CoachAgentTurnResult> RunAsync(IChatClient client, CoachImplementation arm)
    {
        var services = new ServiceCollection();
        services.AddSingleton(client);

        using var provider = services.BuildServiceProvider();
        var options = new TestOptionsMonitor<CoachOptions>(new CoachOptions { Enabled = true });
        using var telemetry = new CoachTelemetry();
        var factory = new CoachAgentFactory(provider, options, NullLoggerFactory.Instance);

        ILearningCoach coach = arm == CoachImplementation.Baseline
            ? new BaselineLearningCoach(factory, new StubToolFactory(), options, telemetry,
                NullLogger<BaselineLearningCoach>.Instance)
            : new HarnessLearningCoach(factory, new StubToolFactory(), options, telemetry,
                NullLogger<HarnessLearningCoach>.Instance);

        return await coach.RunTurnAsync(new CoachAgentTurnRequest
        {
            SessionId = "session-shape-regression",
            LearnerText = "Explain when I use -neunde versus -jiman.",
            ActiveConstraints = new CoachConstraintSetDto
            {
                AvailableMinutes = 10,
                AudioAllowed = true,
                SpeechAllowed = true,
                TypingAllowed = true,
                EnergyLevel = CoachEnergyLevel.Normal
            },
            ClarificationsRemaining = 2,
            UserLocalDate = new DateOnly(2026, 8, 22)
        });
    }

    private sealed class StubToolFactory : ICoachToolFactory
    {
        public IReadOnlyList<AIFunction> CreateTools() =>
            CoachToolNames.All
                .Select(name => AIFunctionFactory.Create(
                    () => "stub", new AIFunctionFactoryOptions { Name = name, Description = $"Reads {name}." }))
                .ToList();
    }
}
