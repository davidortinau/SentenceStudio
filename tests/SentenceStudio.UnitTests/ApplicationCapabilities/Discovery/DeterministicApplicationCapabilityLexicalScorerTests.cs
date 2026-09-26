using FluentAssertions;
using SentenceStudio.Application.Capabilities.Discovery;

namespace SentenceStudio.UnitTests.ApplicationCapabilities.Discovery;

public sealed class DeterministicApplicationCapabilityLexicalScorerTests
{
    private readonly DeterministicApplicationCapabilityLexicalScorer _scorer = new();

    [Theory]
    [InlineData("PLAN.TODAY.READ", ApplicationCapabilityMatchReasonCode.ExactCode)]
    [InlineData("today plan", ApplicationCapabilityMatchReasonCode.ExactAlias)]
    [InlineData("plan and practice history", ApplicationCapabilityMatchReasonCode.ExactFamily)]
    public void Exact_code_alias_and_family_are_normalized_and_ranked(
        string query,
        ApplicationCapabilityMatchReasonCode reason)
    {
        var document = Document(
            aliases: ["today-plan"],
            positiveExamples: ["Show today's plan."],
            negativeExamples: ["Replace today's plan."]);

        var score = _scorer.Score(query, document);

        score.Score.Should().BePositive();
        score.Reasons.Should().Contain(reason);
        score.HardNegative.Should().BeFalse();
    }

    [Fact]
    public void Positive_examples_add_deterministic_lexical_evidence()
    {
        var document = Document(
            positiveExamples: ["Show my current study plan."],
            negativeExamples: ["Replace my current study plan."]);

        var score = _scorer.Score("show my current study plan", document);

        score.Score.Should().Be(0.85);
        score.Reasons.Should().Contain(ApplicationCapabilityMatchReasonCode.PositiveExample);
        score.NegativePenalty.Should().Be(0);
    }

    [Fact]
    public void Negative_examples_penalize_and_exact_negative_examples_hard_suppress()
    {
        var document = Document(
            positiveExamples: ["Show my current study plan."],
            negativeExamples: ["Replace my current study plan."]);

        var partial = _scorer.Score("Could you replace the current plan?", document);
        var exact = _scorer.Score("Replace my current study plan.", document);

        partial.NegativePenalty.Should().BePositive();
        partial.Reasons.Should().Contain(
            ApplicationCapabilityMatchReasonCode.NegativeExamplePenalty);
        exact.HardNegative.Should().BeTrue();
        exact.NegativePenalty.Should().Be(1);
        exact.Reasons.Should().Contain(
            ApplicationCapabilityMatchReasonCode.HardNegativeExample);
    }

    [Fact]
    public void Homonyms_do_not_match_by_stemming_or_substring()
    {
        var document = Document(
            code: "skill.catalog.read",
            family: "skills",
            description: "List owned language learning skill profiles.",
            positiveExamples: ["Show my language skill profiles."],
            negativeExamples: ["Practice skillful writing."]);

        var score = _scorer.Score("A skillful technique", document);

        score.Score.Should().Be(0);
        score.NegativePenalty.Should().Be(0);
        score.Reasons.Should().BeEmpty();
    }

    [Fact]
    public void Negating_a_positive_outcome_is_a_hard_negative()
    {
        var document = Document(
            code: "activity.vocabulary-review.launch",
            family: "vocabulary-review-activity",
            description: "Launch vocabulary review.",
            positiveExamples: ["Start vocabulary review."],
            negativeExamples: ["Only show vocabulary review settings."]);

        var score = _scorer.Score("Don't start vocabulary review", document);

        score.HardNegative.Should().BeTrue();
        score.Reasons.Should().Contain(
            ApplicationCapabilityMatchReasonCode.NegatedPositiveExample);
    }

    [Fact]
    public void Negation_scopes_a_negative_action_without_erasing_a_positive_read()
    {
        var document = Document(
            positiveExamples: ["Show my plan for today."],
            negativeExamples: ["Replace my plan for today."]);

        var score = _scorer.Score(
            "Show my plan for today, and do not replace it.",
            document);

        score.HardNegative.Should().BeFalse();
        score.Score.Should().BePositive();
        score.NegativePenalty.Should().BePositive();
        score.Reasons.Should().Contain(ApplicationCapabilityMatchReasonCode.PositiveExample);
        score.Reasons.Should().Contain(
            ApplicationCapabilityMatchReasonCode.NegativeExamplePenalty);
    }

    private static ApplicationCapabilityDiscoveryDocument Document(
        string code = "plan.today.read",
        string family = "plan-and-practice-history",
        string description = "Read the current approved study plan.",
        IReadOnlyList<string>? positiveExamples = null,
        IReadOnlyList<string>? negativeExamples = null,
        IReadOnlyList<string>? aliases = null) =>
        ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                code,
                family,
                description,
                positiveExamples,
                negativeExamples,
                aliases))
            .Documents.Single();
}
