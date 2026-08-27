using System.Reflection;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Application;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The two agent arms must reach identical vocabulary-focus outcomes.
/// </summary>
/// <remarks>
/// Parity here is structural rather than statistical, and that is the point: neither arm decides
/// anything about a focus. Both may only fill one bounded description field; the registry maps it
/// and the resolver selects against it, both inside the application. So the corpus below pins the
/// mapping once, and the rest of these tests prove the arms cannot influence it.
/// </remarks>
public class CoachVocabularyFocusParityTests
{
    /// <summary>
    /// The alias corpus. One row per phrase either arm might plausibly emit, with the outcome the
    /// application must reach for it.
    /// </summary>
    public static TheoryData<string, string?> Corpus => new()
    {
        { "active verbs", CoachVocabularyFocusAliases.ActionVerbCode },
        { "action verbs", CoachVocabularyFocusAliases.ActionVerbCode },
        { "Action Verbs", CoachVocabularyFocusAliases.ActionVerbCode },
        { "the action verbs", CoachVocabularyFocusAliases.ActionVerbCode },
        { "verbs", CoachVocabularyFocusAliases.ActionVerbCode },
        { "\uB3D9\uC791 \uB3D9\uC0AC", CoachVocabularyFocusAliases.ActionVerbCode },
        { "\uD589\uB3D9 \uB3D9\uC0AC", CoachVocabularyFocusAliases.ActionVerbCode },
        { "\uB3D9\uC0AC", CoachVocabularyFocusAliases.ActionVerbCode },
        { "adjectives", CoachVocabularyFocusAliases.DescriptiveWordCode },
        { "descriptive words", CoachVocabularyFocusAliases.DescriptiveWordCode },
        { "\uD615\uC6A9\uC0AC", CoachVocabularyFocusAliases.DescriptiveWordCode },
        { "nouns", CoachVocabularyFocusAliases.NounCode },
        { "\uBA85\uC0AC", CoachVocabularyFocusAliases.NounCode },
        { "adverbs", CoachVocabularyFocusAliases.AdverbCode },
        { "expressions", CoachVocabularyFocusAliases.ExpressionCode },
        { "counters", CoachVocabularyFocusAliases.CounterCode },

        // Refused. Each of these would be a plausible model paraphrase and none of them is a
        // word class, so none may quietly become one.
        { "active voice", null },
        { "passive voice", null },
        { "hard words", null },
        { "words I keep forgetting", null },
        { "useful stuff", null },
        { "grammar", null }
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void TheRegistryReachesTheSameOutcomeForEveryPhrase(string phrase, string? expectedCode)
    {
        var mapped = CoachVocabularyFocusAliases.TryMap(phrase, out var alias);

        if (expectedCode is null)
        {
            mapped.Should().BeFalse($"'{phrase}' names no word class");
            return;
        }

        mapped.Should().BeTrue();
        alias.FocusCode.Should().Be(expectedCode);
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task BothArmsReachTheSameOfferAndTheSameAcceptedSet(string phrase, string? expectedCode)
    {
        // The arms differ only in which ILearningCoach the service holds, so running the same
        // scripted intent through each proves the decision is downstream of both. A recognized
        // phrase is offered and only writes once accepted; an unrecognized one never offers.
        var outcomes = new List<(bool Offered, string? Code, string Ids, bool Wrote)>();

        foreach (var _ in new[] { "baseline", "harness" })
        {
            using var harness = new CoachApplicationHarness();
            var sessionId = await harness.StartSessionAsync();

            harness.Coach.NextResult = FocusResult(phrase);
            var offered = await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
            {
                InputKind = CoachTurnInputKind.Text,
                Text = "focus today on that"
            });

            var pending = offered.Value?.PendingSuggestion;

            // Nothing is written by the offer itself, whatever the phrase was.
            harness.Db.CoachPlanRevisions.Should().BeEmpty();

            CoachOperationResult<CoachTurnResponse>? accepted = null;
            if (pending is not null)
            {
                accepted = await harness.Service.AcceptSuggestionAsync(
                    sessionId, pending.SuggestionId, new CoachSuggestionDecisionRequest());
            }

            outcomes.Add((
                pending is not null,
                accepted?.Value?.ActiveConstraints.VocabularyFocus?.FocusCode,
                string.Join(",", harness.PlanService.LastApplyFocusIds ?? []),
                accepted?.Value?.ChangeReceipt is not null));
        }

        outcomes[0].Should().Be(outcomes[1]);
        outcomes[0].Offered.Should().Be(expectedCode is not null, "an unmapped phrase never offers");
        outcomes[0].Code.Should().Be(expectedCode);
        outcomes[0].Wrote.Should().Be(expectedCode is not null, "only an accepted offer writes");

        if (expectedCode is not null)
        {
            outcomes[0].Ids.Should().Be("v-1,v-2,v-3,v-4,v-5", "identifiers and order are arm-independent");
        }
    }

    [Fact]
    public void NeitherArmCanReachTheRegistryOrTheResolver()
    {
        // The structural guarantee behind the parity above: an agent that cannot see the mapping
        // or the query cannot diverge from the other arm on either.
        foreach (var arm in new[] { typeof(BaselineLearningCoach), typeof(HarnessLearningCoach) })
        {
            var dependencies = arm
                .GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Select(p => p.ParameterType)
                .Concat(arm.GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Select(f => f.FieldType))
                .ToList();

            dependencies.Should().NotContain(typeof(CoachVocabularyFocusService));
            dependencies.Should().NotContain(typeof(IVocabularyFocusResolver));
        }
    }

    [Fact]
    public void TheIntentOffersBothArmsExactlyOneVocabularyField()
    {
        var vocabularyFields = typeof(CoachConstraintDeltaIntent)
            .GetProperties()
            .Where(p => p.Name.Contains("Vocabulary", StringComparison.Ordinal))
            .ToList();

        vocabularyFields.Select(p => p.Name).Should().BeEquivalentTo(
            ["VocabularyFocusDescription", "ClearVocabularyFocus"]);

        // A description and a flag. No identifier, no list, no count.
        vocabularyFields.Single(p => p.Name == "VocabularyFocusDescription").PropertyType
            .Should().Be<string>();
        vocabularyFields.Single(p => p.Name == "ClearVocabularyFocus").PropertyType
            .Should().Be<bool>();
    }

    [Fact]
    public async Task NeitherArmIsEverShownASelectedWord()
    {
        using var harness = new CoachApplicationHarness();
        var sessionId = await harness.StartSessionAsync();

        await harness.OfferAndAcceptFocusAsync(
            sessionId, FocusResult("active verbs"), "focus today on active verbs");

        harness.Coach.NextResult = new CoachAgentTurnResult
        {
            Outcome = CoachAgentOutcome.Completed,
            Intent = new CoachTurnIntent { Kind = CoachIntentKind.NoChange, CoachMessage = "Nothing to change." }
        };

        await harness.Service.SubmitTurnAsync(sessionId, new CoachTurnRequest
        {
            InputKind = CoachTurnInputKind.Text,
            Text = "anything else"
        });

        var context = System.Text.Json.JsonSerializer.Serialize(harness.Coach.LastRequest);

        foreach (var forbidden in new[]
                 {
                     "v-1", "v-5", "\uAC00\uB2E4", "\uC77D\uB2E4", "to go", "to read"
                 })
        {
            context.Should().NotContain(forbidden);
        }

        // What it may see: that a focus exists, and what kind.
        context.Should().Contain(CoachVocabularyFocusAliases.ActionVerbCode);
    }

    private static CoachAgentTurnResult FocusResult(string description) => new()
    {
        Outcome = CoachAgentOutcome.Completed,
        Intent = new CoachTurnIntent
        {
            Kind = CoachIntentKind.DirectConstraintChange,
            ConstraintDelta = new CoachConstraintDeltaIntent { VocabularyFocusDescription = description },
            CoachMessage = string.Empty
        }
    };
}
