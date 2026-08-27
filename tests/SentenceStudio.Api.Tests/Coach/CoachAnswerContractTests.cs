using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using CoachToolNames = SentenceStudio.Api.Coach.Tools.CoachToolNames;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The dual-purpose contract: the enums clients and the database depend on, the closed schema
/// the model answers into, and the guarantee that both arms produce the same intent.
/// </summary>
public class CoachAnswerContractTests
{
    // ---------------------------------------------------------------- appended, never inserted

    [Theory]
    [InlineData(CoachIntentKind.NoChange, 0)]
    [InlineData(CoachIntentKind.DirectConstraintChange, 1)]
    [InlineData(CoachIntentKind.SuggestConstraintChange, 2)]
    [InlineData(CoachIntentKind.AcceptPendingSuggestion, 3)]
    [InlineData(CoachIntentKind.RejectPendingSuggestion, 4)]
    [InlineData(CoachIntentKind.AskClarification, 5)]
    [InlineData(CoachIntentKind.OffTopic, 6)]
    [InlineData(CoachIntentKind.PedagogicalAnswer, 7)]
    public void EveryIntentKindKeepsItsStoredOrdinal(CoachIntentKind kind, int stored) =>
        // CoachPlanRevision.IntentKind is stored as an int, so renumbering silently re-labels
        // every revision already written.
        ((int)kind).Should().Be(stored);

    [Theory]
    [InlineData(CoachMessageKind.Text, 0)]
    [InlineData(CoachMessageKind.Clarification, 1)]
    [InlineData(CoachMessageKind.Suggestion, 2)]
    [InlineData(CoachMessageKind.Receipt, 3)]
    [InlineData(CoachMessageKind.Notice, 4)]
    [InlineData(CoachMessageKind.PedagogicalAnswer, 5)]
    public void EveryMessageKindKeepsItsOrdinal(CoachMessageKind kind, int stored) =>
        ((int)kind).Should().Be(stored);

    [Fact]
    public void TheNewEnumsSerializeAsNames()
    {
        // The wire format is names, so a client reading JSON is not coupled to ordinals.
        JsonSerializer.Serialize(CoachAnswerTopic.Grammar).Should().Be("\"Grammar\"");
        JsonSerializer.Serialize(CoachAnswerBlockKind.RetrievalPrompt).Should().Be("\"RetrievalPrompt\"");
        JsonSerializer.Serialize(CoachLanguageRole.Target).Should().Be("\"Target\"");
    }

    [Fact]
    public void TheAnswerBoundsAreTheApprovedOnes()
    {
        CoachAnswerLimits.MaxTotalCharacters.Should().Be(1600);
        CoachAnswerLimits.MinBlocks.Should().Be(1);
        CoachAnswerLimits.MaxBlocks.Should().Be(8);
        CoachAnswerLimits.MinSpansPerBlock.Should().Be(1);
        CoachAnswerLimits.MaxSpansPerBlock.Should().Be(6);
        CoachAnswerLimits.MaxSpanCharacters.Should().Be(320);

        // The plan/status message keeps its own, tighter bound: a receipt stays terse.
        CoachConstraintLimits.MaxTurnTextLength.Should().Be(500);
    }

    // ---------------------------------------------------------------- the closed schema

    [Fact]
    public async Task TheResponseSchemaCarriesTheAnswerAndItsClosedEnums()
    {
        var client = new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""");
        await RunAsync(client, CoachImplementation.Baseline);

        var schema = ((ChatResponseFormatJson)client.LastOptions!.ResponseFormat!).Schema!.Value.GetRawText();

        schema.Should().Contain("pedagogicalAnswer");
        schema.Should().Contain("blocks");
        schema.Should().Contain("spans");

        // The topic, block kind, and language role are closed sets in the schema, so the model
        // cannot invent one.
        foreach (var topic in Enum.GetNames<CoachAnswerTopic>())
        {
            schema.Should().Contain(topic);
        }

        foreach (var role in Enum.GetNames<CoachLanguageRole>())
        {
            schema.Should().Contain(role);
        }

        // And it cannot name a locale at all: the server resolves those.
        schema.Should().NotContain("languageTag");
        schema.Should().NotContain("locale");
    }

    [Fact]
    public async Task TheSchemaStillCarriesThePlanConstraints()
    {
        var client = new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}""");
        await RunAsync(client, CoachImplementation.Baseline);

        var schema = ((ChatResponseFormatJson)client.LastOptions!.ResponseFormat!).Schema!.Value.GetRawText();

        schema.Should().Contain("availableMinutes");
        schema.Should().Contain("audioAllowed");
    }

    // ---------------------------------------------------------------- arm parity

    [Theory]
    [InlineData(CoachImplementation.Baseline)]
    [InlineData(CoachImplementation.Harness)]
    public async Task BothArmsReadTheSameAnswerIntent(CoachImplementation arm)
    {
        const string json = """
            {
              "Kind": "PedagogicalAnswer",
              "PedagogicalAnswer": {
                "Topic": "Grammar",
                "Blocks": [
                  {
                    "Kind": "Answer",
                    "Spans": [ { "Text": "It marks the topic.", "Language": "Display" } ]
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
        result.Intent.PedagogicalAnswer.Blocks.Should().ContainSingle()
            .Which.Spans.Should().ContainSingle()
            .Which.Language.Should().Be(CoachLanguageRole.Display);
    }

    // ---------------------------------------------------------------- language resolution

    [Theory]
    [InlineData("Korean", "ko-KR")]
    [InlineData("korean", "ko-KR")]
    [InlineData("English", "en-US")]
    [InlineData("Japanese", "ja-JP")]
    [InlineData("pt-BR", "pt-BR")]
    public void KnownLanguagesResolveToTags(string stored, string expected) =>
        CoachLanguageResolver.ToTag(stored, "en").Should().Be(expected);

    [Theory]
    [InlineData("Klingon")]
    [InlineData("!!!")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("this is not a language tag at all")]
    public void AnUnknownLanguageFallsBackRatherThanInventingALocale(string? stored) =>
        // A made-up tag would reach a client that uses it to choose a font and a voice.
        CoachLanguageResolver.ToTag(stored, "en").Should().Be("en");

    // ---------------------------------------------------------------- projection

    [Fact]
    public void TheFlattenedFallbackIsBuiltFromTheSameSpans()
    {
        var result = new CoachAnswerProjection().Project(
            new CoachPedagogicalAnswerIntent
            {
                Topic = CoachAnswerTopic.Usage,
                Blocks =
                [
                    new CoachAnswerBlockIntent
                    {
                        Kind = CoachAnswerBlockKind.Answer,
                        Spans = [new CoachAnswerSpanIntent { Text = "Use it with friends.", Language = CoachLanguageRole.Display }]
                    },
                    new CoachAnswerBlockIntent
                    {
                        Kind = CoachAnswerBlockKind.Example,
                        Label = "Example",
                        Spans = [new CoachAnswerSpanIntent { Text = "\uBC25 \uBA39\uC5C8\uC5B4?", Language = CoachLanguageRole.Target }]
                    }
                ]
            },
            new CoachLanguageProfile("ko-KR", "en-US", "en-US"));

        result.IsValid.Should().BeTrue();
        result.Answer!.PlainText.Should().Contain("Use it with friends.");
        result.Answer.PlainText.Should().Contain("Example:");
        result.Answer.PlainText.Should().Contain("\uBC25 \uBA39\uC5C8\uC5B4?");

        // Everything the client can see is scanned, including the fallback.
        CoachAnswerProjection.TextsToScan(result.Answer).Should().Contain(result.Answer.PlainText);
    }

    [Fact]
    public void ARetrievalPromptMustComeLast()
    {
        var projection = new CoachAnswerProjection();
        var languages = new CoachLanguageProfile("ko-KR", "en-US", "en-US");

        var misplaced = projection.Project(
            new CoachPedagogicalAnswerIntent
            {
                Topic = CoachAnswerTopic.Vocabulary,
                Blocks =
                [
                    new CoachAnswerBlockIntent
                    {
                        Kind = CoachAnswerBlockKind.Answer,
                        Spans = [new CoachAnswerSpanIntent { Text = "A.", Language = CoachLanguageRole.Display }]
                    },
                    new CoachAnswerBlockIntent
                    {
                        Kind = CoachAnswerBlockKind.RetrievalPrompt,
                        Spans = [new CoachAnswerSpanIntent { Text = "Can you use it?", Language = CoachLanguageRole.Display }]
                    },
                    new CoachAnswerBlockIntent
                    {
                        Kind = CoachAnswerBlockKind.Note,
                        Spans = [new CoachAnswerSpanIntent { Text = "Note.", Language = CoachLanguageRole.Display }]
                    }
                ]
            },
            languages);

        misplaced.IsValid.Should().BeFalse();
    }

    // ---------------------------------------------------------------- write authority

    [Theory]
    [InlineData("make it 10 minutes", CoachWriteAuthority.Denial.None)]
    [InlineData("make today's plan 5 minutes and no audio", CoachWriteAuthority.Denial.None)]
    [InlineData("what does \uC88B\uB2E4 mean", CoachWriteAuthority.Denial.AsksAQuestion)]
    [InlineData("make it 10 minutes?", CoachWriteAuthority.Denial.AsksAQuestion)]
    [InlineData("set 10 minutes and explain \uC88B\uB2E4", CoachWriteAuthority.Denial.AsksAQuestion)]
    [InlineData("set 10 minutes. also tell me about \uC88B\uB2E4", CoachWriteAuthority.Denial.CarriesASecondRequest)]
    [InlineData("change to \"10 minutes\"", CoachWriteAuthority.Denial.QuotesText)]
    [InlineData("hello there", CoachWriteAuthority.Denial.NamesNoPlanChange)]
    public void OnlyAnExclusivePlanCommandMayWrite(string text, CoachWriteAuthority.Denial expected) =>
        new CoachWriteAuthority().Evaluate(text).Should().Be(expected);

    [Fact]
    public void AVeryLongMessageIsProseNotACommand() =>
        new CoachWriteAuthority()
            .Evaluate("make it 10 minutes " + new string('x', CoachWriteAuthority.MaxCommandLength))
            .Should().Be(CoachWriteAuthority.Denial.TooLongToBeACommand);

    // ---------------------------------------------------------------- helpers

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
            SessionId = "session-1",
            LearnerText = "What's the difference between \uC88B\uC544\uD558\uB2E4 and \uC88B\uB2E4?",
            ActiveConstraints = new CoachConstraintSetDto
            {
                AvailableMinutes = 10,
                AudioAllowed = true,
                SpeechAllowed = true,
                TypingAllowed = true,
                EnergyLevel = CoachEnergyLevel.Normal
            },
            ClarificationsRemaining = 2,
            UserLocalDate = new DateOnly(2026, 8, 15)
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
