using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Api.Coach.Validation.Claims;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Contracts.Wire;
using Xunit;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// What a learner receives when the grounding ladder refuses at Enforce.
/// </summary>
/// <remarks>
/// <para>
/// <b>What was wrong.</b> The refusal shipped a hardcoded English sentence — "I could not answer
/// that one. Nothing changed." — straight past the client's resource file, so a learner reading the
/// app in Korean got English. It also emptied the evidence, so the one thing that could have made
/// the refusal useful was thrown away: the learner was told something went wrong and nothing about
/// what Sam had actually looked at, and the report path saw the same nothing.
/// </para>
/// <para>
/// The server now states a closed code and a typed destination, and the client writes the sentence.
/// </para>
/// </remarks>
public sealed class CoachRefusalContractTests
{
    private const string UnverifiedClaim = "You have reviewed these words plenty of times already.";

    // ─────────────────────────────────────────────────── the real turn

    [Fact]
    public async Task An_Enforce_refusal_carries_a_typed_limitation_and_no_server_prose()
    {
        var response = await RefusedTurnAsync();

        response.Status.Should().Be(CoachTurnStatus.Rejected);
        response.Answer.Should().BeNull();

        response.Limitation.Should().NotBeNull(
            "the reason is the server's to state, as a code the client can render");
        response.Limitation!.Code.Should().Be(CoachLimitationCode.UnverifiedClaimWithheld);

        response.Messages.Should().BeEmpty(
            "a server-authored English sentence bypasses the client's resource file entirely, and "
            + "a Korean learner would read it in English");
    }

    [Fact]
    public async Task An_Enforce_refusal_preserves_the_turns_real_evidence()
    {
        var response = await RefusedTurnAsync();

        response.Evidence.Should().NotBeEmpty(
            "a refusal carrying nothing says only that something went wrong; the same refusal "
            + "beside what Sam did look at is the difference between an apology and an answer");

        var item = response.Evidence[0];

        item.Coverage.Should().NotBeNull();
        item.AsOfUtc.Should().NotBeNull();
        item.DefinitionCode.Should().NotBeNull("the report path reads this");
    }

    [Fact]
    public async Task The_refusal_payload_carries_no_prose_in_any_language()
    {
        var response = await RefusedTurnAsync();
        var json = JsonSerializer.Serialize(response, WireJson.Client);

        foreach (var sentence in new[]
                 {
                     "I could not answer", "Nothing changed", "could not answer that one"
                 })
        {
            json.Should().NotContain(sentence, "server refusal copy is not learner-visible");
        }

        // And nothing from the model's own answer survived either.
        json.Should().NotContain(UnverifiedClaim);
    }

    [Fact]
    public async Task The_refusal_leaks_no_terms_or_learner_text()
    {
        var response = await RefusedTurnAsync();
        var json = JsonSerializer.Serialize(response, WireJson.Client);

        foreach (var forbidden in new[] { "gloss", "lemma", "example", "transcript" })
        {
            json.Should().NotContain(forbidden);
        }
    }

    [Fact]
    public async Task An_old_client_reading_the_new_code_degrades_to_unknown()
    {
        var response = await RefusedTurnAsync();

        // Round-tripped through a shape that predates the member, which is what a client built
        // before W9 does. The tolerant converter maps the unrecognised name to the zero member
        // rather than throwing, so the refusal still renders — unframed rather than mislabelled.
        var json = JsonSerializer.Serialize(response.Limitation, WireJson.Client);
        json.Should().Contain(nameof(CoachLimitationCode.UnverifiedClaimWithheld));

        var reread = JsonSerializer.Deserialize<CoachLimitationDto>(
            json.Replace(
                nameof(CoachLimitationCode.UnverifiedClaimWithheld),
                "SomethingThisBuildHasNeverHeardOf",
                StringComparison.Ordinal),
            WireJson.Client);

        reread!.Code.Should().Be(
            CoachLimitationCode.Unknown,
            "an old client shows a refusal it cannot categorise rather than one of the six it knows");
    }

    [Fact]
    public async Task The_appended_code_did_not_move_an_existing_ordinal()
    {
        await Task.CompletedTask;

        ((int)CoachLimitationCode.Unknown).Should().Be(0);
        ((int)CoachLimitationCode.NotBuilt).Should().Be(1);
        ((int)CoachLimitationCode.AvailableOnAnotherSurface).Should().Be(2);
        ((int)CoachLimitationCode.RefusedByDesign).Should().Be(3);
        ((int)CoachLimitationCode.WouldRemoveLearningValue).Should().Be(4);
        ((int)CoachLimitationCode.ExceedsSafeChangeScope).Should().Be(5);
        ((int)CoachLimitationCode.UnverifiedClaimWithheld).Should().Be(6, "appended, never inserted");
        ((int)CoachLimitationCode.AnswerShapeInvalid).Should().Be(7, "appended after UnverifiedClaimWithheld");

        Enum.GetValues<CoachLimitationCode>().Should().HaveCount(8);
    }

    [Fact]
    public async Task The_refusal_offers_no_hint_ladder_and_no_alternative()
    {
        var limitation = (await RefusedTurnAsync()).Limitation!;

        limitation.HintLadder.Should().BeEmpty(
            "the ladder is W7's answer to a request Sam declines by design; this is a turn Sam "
            + "tried to answer and could not stand behind");
        limitation.Alternatives.Should().BeEmpty();
        limitation.ShorterSession.Should().BeNull();
        limitation.FullScopeSurface.Should().BeNull();
    }

    [Fact]
    public async Task The_refusal_keeps_an_open_dispute_open()
    {
        // A refusal is not a resolution. The learner's correction stands until an answer satisfies
        // it, and a turn that produced no answer satisfied nothing.
        using var harness = new CoachApplicationHarness();
        harness.EnableCorrectionState();
        harness.SetGroundingStage(CoachGroundingStage.Enforce);
        harness.SeedWithheldVocabularyRead(
            reason: SentenceStudio.Api.Coach.Tools.CoachScopeWithheldReason.None);

        var response = await AskAsync(
            harness,
            new CoachTurnExecutionContext
            {
                ActiveDispute = new SentenceStudio.Api.Coach.Persistence.History.CoachTurnDisputeState(
                    CoachCorrectionSignal.WrongClaim,
                    "msg-coach-1",
                    new DateTime(2026, 8, 22, 6, 0, 0, DateTimeKind.Utc),
                    ResolvedAtUtc: null,
                    SentenceStudio.Api.Coach.Persistence.History.CoachDisputeResolution.Open,
                    []),
                PriorCoachMessageId = "msg-coach-1"
            });

        response.Status.Should().Be(CoachTurnStatus.Rejected);
        response.Dispute!.Status.Should().Be(
            CoachDisputeStatus.Open, "a refused turn resolved nothing");
    }

    // ─────────────────────────────────────────────── the destination matrix

    [Theory]
    [InlineData(CoachDefinitionCode.TrackedVocabularyDueSummary, CoachRouteName.Vocabulary)]
    [InlineData(CoachDefinitionCode.UndueVocabularySearch, CoachRouteName.Vocabulary)]
    [InlineData(CoachDefinitionCode.TrackedVocabularyDetail, CoachRouteName.Vocabulary)]
    [InlineData(CoachDefinitionCode.PracticeWindowBalance, CoachRouteName.ActivityLog)]
    [InlineData(CoachDefinitionCode.PlanDaySummary, CoachRouteName.ActivityLog)]
    [InlineData(CoachDefinitionCode.DeterministicPlanPreview, CoachRouteName.ActivityLog)]
    [InlineData(CoachDefinitionCode.ActiveSkillList, CoachRouteName.Skills)]
    [InlineData(CoachDefinitionCode.ActiveSkillDetail, CoachRouteName.Skills)]
    [InlineData(CoachDefinitionCode.LearnerSettingsSnapshot, CoachRouteName.Settings)]
    [InlineData(CoachDefinitionCode.LearnerOverviewSummary, CoachRouteName.Settings)]
    public void A_definition_with_a_real_screen_maps_to_it(
        CoachDefinitionCode definition,
        CoachRouteName expected)
    {
        var destination = CoachRefusalLimitationProjection.DestinationFor(definition);

        destination.Should().NotBeNull();
        destination!.Route.Should().Be(expected);

        destination.SideEffect.Should().Be(
            CoachRouteCatalog.All[expected].SideEffect,
            "the consequence comes from the catalogue, not from the projection's opinion");

        destination.Parameters.Should().BeEmpty("no composed query value ever leaves here");
    }

    [Theory]
    [InlineData(CoachDefinitionCode.OwnedResourceCatalog)]
    [InlineData(CoachDefinitionCode.OwnedResourceList)]
    [InlineData(CoachDefinitionCode.OwnedResourceDetail)]
    [InlineData(CoachDefinitionCode.Unknown)]
    [InlineData(null)]
    [InlineData((CoachDefinitionCode)99)]
    public void A_definition_with_no_screen_among_the_six_maps_to_null(CoachDefinitionCode? definition)
    {
        CoachRefusalLimitationProjection.DestinationFor(definition).Should().BeNull(
            "resources live on /resources, which is not one of the six the plan binds; pointing at "
            + "the nearest thing is the fake screen this exists to prevent");
    }

    [Fact]
    public void Every_definition_code_has_a_decided_mapping()
    {
        // Non-vacuity: a fourteenth definition added later falls into the null arm, and this count
        // is what makes somebody notice rather than shipping a silently unlinked refusal.
        var decided = Enum.GetValues<CoachDefinitionCode>()
            .Select(definition => CoachRefusalLimitationProjection.DestinationFor(definition))
            .ToList();

        decided.Should().HaveCount(15);
        decided.Count(destination => destination is not null).Should().Be(11);
        decided.Count(destination => destination is null).Should().Be(4);
    }

    [Fact]
    public void No_destination_names_a_route_outside_the_six()
    {
        var routes = Enum.GetValues<CoachDefinitionCode>()
            .Select(definition => CoachRefusalLimitationProjection.DestinationFor(definition))
            .Where(destination => destination is not null)
            .Select(destination => destination!.Route)
            .Distinct();

        routes.Should().OnlyContain(route => CoachRouteCatalog.All.ContainsKey(route));
        routes.Should().NotContain(CoachRouteName.Unknown);
    }

    // ─────────────────────────────────────────────────────────── helpers

    private static async Task<CoachTurnResponse> RefusedTurnAsync()
    {
        using var harness = new CoachApplicationHarness();
        harness.SetGroundingStage(CoachGroundingStage.Enforce);
        harness.SeedWithheldVocabularyRead(
            reason: SentenceStudio.Api.Coach.Tools.CoachScopeWithheldReason.None);

        return await AskAsync(harness, CoachTurnExecutionContext.Default);
    }

    private static async Task<CoachTurnResponse> AskAsync(
        CoachApplicationHarness harness,
        CoachTurnExecutionContext context)
    {
        var sessionId = await harness.StartSessionAsync();

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent
            {
                Kind = CoachIntentKind.PedagogicalAnswer,
                PedagogicalAnswer = new CoachPedagogicalAnswerIntent
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
                                    Text = UnverifiedClaim,
                                    Language = CoachLanguageRole.Display
                                }
                            ]
                        }
                    ]
                },
                CoachMessage = string.Empty
            }
        };

        var result = await harness.Service.SubmitTurnAsync(
            sessionId,
            new CoachTurnRequest { InputKind = CoachTurnInputKind.Text, Text = "How am I doing?" },
            context);

        result.IsOk.Should().BeTrue();
        return result.Value!;
    }
}
