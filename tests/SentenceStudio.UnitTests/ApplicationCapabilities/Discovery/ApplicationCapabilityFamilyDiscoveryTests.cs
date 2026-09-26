using System.Reflection;
using FluentAssertions;
using SentenceStudio.Application.Capabilities;
using SentenceStudio.Application.Capabilities.Discovery;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.UnitTests.ApplicationCapabilities.Discovery;

public sealed class ApplicationCapabilityFamilyDiscoveryTests
{
    [Fact]
    public async Task Complete_family_with_more_than_eight_members_is_retained_when_budgets_allow()
    {
        var descriptors = Enumerable.Range(1, 10)
            .Select(index => ApplicationCapabilityDiscoveryFixtures.Descriptor(
                $"activity.review.member{index}.read",
                "review-activity",
                "Manage a complete review activity.",
                ["Open the complete review activity."],
                ["Open a different activity."]))
            .ToArray();
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(descriptors);

        var result = await Discover(
            index,
            "Open the complete review activity.",
            ApplicationCapabilityDiscoveryFixtures.Budgets(resultMembers: 10));

        result.State.Should().Be(ApplicationCapabilityDiscoveryOutcomeState.Complete);
        result.Families.Should().ContainSingle();
        result.Families[0].Members.Should().HaveCount(10);
        var memberCodes = result.Families[0].Members.Select(member => member.Code).ToArray();
        memberCodes.Should().Equal(memberCodes.OrderBy(code => code, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Complete_family_contains_only_admitted_model_static_candidates()
    {
        const string family = "today-plan";
        var builder = new ApplicationCapabilityDiscoveryIndexBuilder();
        builder.TryAdd(
                ApplicationCapabilityDiscoveryFixtures.Descriptor(
                    "plan.today.read",
                    family,
                    positiveExamples: ["Show my plan for today."]),
                ApplicationCapabilityDiscoveryFixtures.Schemas())
            .State.Should().Be(ApplicationCapabilityIndexAdmissionState.Added);
        builder.TryAdd(
                ApplicationCapabilityDiscoveryFixtures.Descriptor(
                    "plan.today.internal",
                    family,
                    positiveExamples: ["Show my plan for today."],
                    metadataMutation: metadata => metadata with
                    {
                        Surfaces = [ApplicationCapabilitySurface.UserInterface],
                        CoachExposurePolicy = ApplicationCapabilityCoachExposurePolicy.Denied
                    }),
                ApplicationCapabilityDiscoveryFixtures.Schemas())
            .State.Should().Be(ApplicationCapabilityIndexAdmissionState.DefaultDenied);
        var index = builder.Freeze(
            ApplicationCapabilityDiscoveryFixtures.Generation(
                builder.ComputeDescriptorSetHash()));

        var result = await Discover(
            index,
            "Show my plan for today.",
            ApplicationCapabilityDiscoveryFixtures.Budgets());

        result.Families.Should().ContainSingle();
        result.Families[0].Members.Should().ContainSingle()
            .Which.Code.Should().Be("plan.today.read");
    }

    [Fact]
    public async Task Budget_too_small_returns_typed_family_failure_without_truncating_members()
    {
        var descriptors = Enumerable.Range(1, 10)
            .Select(index => ApplicationCapabilityDiscoveryFixtures.Descriptor(
                $"activity.review.member{index}.read",
                "review-activity",
                positiveExamples: ["Open the review activity."],
                negativeExamples: ["Open another activity."]))
            .ToArray();
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(descriptors);

        var result = await Discover(
            index,
            "Open the review activity.",
            ApplicationCapabilityDiscoveryFixtures.Budgets(resultMembers: 9));

        result.State.Should().Be(ApplicationCapabilityDiscoveryOutcomeState.NeedsExpansion);
        result.Families.Should().BeEmpty();
        result.FamilyExceedsBudget.Should().NotBeNull();
        result.FamilyExceedsBudget!.Family.Should().Be("review-activity");
        result.FamilyExceedsBudget.Dimensions.Should().HaveFlag(
            ApplicationCapabilityBudgetDimension.ResultMembers);
        result.FamilyExceedsBudget.Required.ResultMembers.Should().Be(10);
        result.FamilyExceedsBudget.Available.ResultMembers.Should().Be(9);
    }

    [Fact]
    public async Task Stable_order_and_near_ties_are_preserved_without_a_similarity_threshold()
    {
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                "alpha.catalog.read",
                "alpha-family"),
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                "beta.catalog.read",
                "beta-family"),
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                "gamma.catalog.read",
                "gamma-family"));
        var provider = new ApplicationCapabilityDiscoveryFixtures.SemanticProvider(request =>
            ApplicationCapabilitySemanticScoringResult.Succeeded(
            [
                ApplicationCapabilityDiscoveryFixtures.Score(request, "gamma.catalog.read", 0.79),
                ApplicationCapabilityDiscoveryFixtures.Score(request, "beta.catalog.read", 0.80),
                ApplicationCapabilityDiscoveryFixtures.Score(request, "alpha.catalog.read", 0.80)
            ]));

        var first = await Discover(
            index,
            "otherwise unmatched",
            ApplicationCapabilityDiscoveryFixtures.Budgets(),
            provider);
        var second = await Discover(
            index,
            "otherwise unmatched",
            ApplicationCapabilityDiscoveryFixtures.Budgets(),
            provider);

        first.Families.Select(family => family.Family).Should().Equal(
            "alpha-family",
            "beta-family",
            "gamma-family");
        second.Families.Select(family => family.Family)
            .Should().Equal(first.Families.Select(family => family.Family));
        first.Families.Select(family => family.NearTieGroup).Should().OnlyContain(group => group == 1);
    }

    [Fact]
    public async Task Exact_evidence_precedes_a_higher_vector_only_score_when_policy_requires_it()
    {
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                "plan.today.read",
                "plans"),
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                "skill.catalog.read",
                "skills"));
        var provider = new ApplicationCapabilityDiscoveryFixtures.SemanticProvider(request =>
            ApplicationCapabilitySemanticScoringResult.Succeeded(
            [
                ApplicationCapabilityDiscoveryFixtures.Score(request, "plan.today.read", 0.01),
                ApplicationCapabilityDiscoveryFixtures.Score(request, "skill.catalog.read", 1)
            ]));

        var result = await Discover(
            index,
            "plan.today.read",
            ApplicationCapabilityDiscoveryFixtures.Budgets(),
            provider,
            new ApplicationCapabilityDiscoveryScoringPolicy(
                lexicalWeight: 0.1,
                semanticWeight: 1,
                exactAndAliasEvidencePrecedesVectorOnly: true,
                nearTieTolerance: 0.02));

        result.Families[0].Family.Should().Be("plans");
        result.Families[0].Reasons.Should().Contain(
            ApplicationCapabilityMatchReasonCode.ExactCode);
    }

    [Fact]
    public async Task Hard_negative_suppresses_a_dangerous_near_neighbor_family()
    {
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                "activity.vocabulary-review.launch",
                "vocabulary-review-launch",
                "Launch vocabulary review.",
                ["Start vocabulary review."],
                ["Only show vocabulary review settings."]),
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                "preference.vocabulary-review.read",
                "vocabulary-review-settings",
                "Read vocabulary review settings.",
                ["Show vocabulary review settings."],
                ["Delete every saved learning resource."]));

        var result = await Discover(
            index,
            "Don't start vocabulary review; show vocabulary review settings.",
            ApplicationCapabilityDiscoveryFixtures.Budgets());

        result.Suppressions.Should().ContainSingle()
            .Which.Family.Should().Be("vocabulary-review-launch");
        result.Suppressions[0].Reasons.Should().Contain(
            ApplicationCapabilityMatchReasonCode.NegatedPositiveExample);
        result.Suppressions[0].Members.Should().ContainSingle();
        result.Suppressions[0].Members[0].HardNegative.Should().BeTrue();
        result.Suppressions[0].Members[0].IsDiscoveryCandidate.Should().BeFalse();
        result.Suppressions[0].Members[0].Reasons.Should().Contain(
            ApplicationCapabilityMatchReasonCode.NegatedPositiveExample);
        result.Families.Select(family => family.Family)
            .Should().Contain("vocabulary-review-settings")
            .And.NotContain("vocabulary-review-launch");
    }

    [Fact]
    public async Task Exact_plan_read_negative_does_not_suppress_positive_replace_sibling()
    {
        const string family = "today-plan";
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                "plan.today.read",
                family,
                "Read today's approved plan.",
                ["Show my plan for today."],
                ["Replace my plan for today."]),
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                "plan.today.replace",
                family,
                "Replace today's approved plan.",
                ["Replace my plan for today."],
                ["Only show my plan for today."]));

        var result = await Discover(
            index,
            "Replace my plan for today.",
            ApplicationCapabilityDiscoveryFixtures.Budgets());

        result.Suppressions.Should().BeEmpty();
        var match = result.Families.Should().ContainSingle().Which;
        match.Members.Should().HaveCount(2);
        var read = match.Members.Single(member => member.Code == "plan.today.read");
        read.HardNegative.Should().BeTrue();
        read.IsDiscoveryCandidate.Should().BeFalse();
        read.Reasons.Should().Contain(ApplicationCapabilityMatchReasonCode.HardNegativeExample);
        var replace = match.Members.Single(member => member.Code == "plan.today.replace");
        replace.HardNegative.Should().BeFalse();
        replace.IsDiscoveryCandidate.Should().BeTrue();
        replace.Reasons.Should().Contain(ApplicationCapabilityMatchReasonCode.PositiveExample);
        match.Reasons.Should().Contain(ApplicationCapabilityMatchReasonCode.PositiveExample)
            .And.NotContain(ApplicationCapabilityMatchReasonCode.HardNegativeExample);
    }

    [Theory]
    [InlineData(
        "Delete every saved learning resource.",
        "resource.catalog.read",
        "resource.catalog.delete")]
    [InlineData(
        "Reset my vocabulary review settings.",
        "preference.vocabulary-review.read",
        "preference.vocabulary-review.reset")]
    [InlineData(
        "Archive the completed practice session.",
        "practice.session.read",
        "practice.session.archive")]
    public async Task Exact_negative_on_one_member_preserves_positive_sibling(
        string query,
        string negativeMemberCode,
        string positiveMemberCode)
    {
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                negativeMemberCode,
                "sibling-negative-family",
                positiveExamples: [$"Use {negativeMemberCode}."],
                negativeExamples: [query]),
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                positiveMemberCode,
                "sibling-negative-family",
                positiveExamples: [query],
                negativeExamples: [$"Use {negativeMemberCode}."]));

        var result = await Discover(
            index,
            query,
            ApplicationCapabilityDiscoveryFixtures.Budgets());

        result.Suppressions.Should().BeEmpty();
        var match = result.Families.Should().ContainSingle().Which;
        match.Members.Should().HaveCount(2);
        match.Members.Single(member => member.Code == negativeMemberCode)
            .HardNegative.Should().BeTrue();
        match.Members.Single(member => member.Code == positiveMemberCode)
            .IsDiscoveryCandidate.Should().BeTrue();
    }

    [Fact]
    public async Task Negated_positive_on_one_member_preserves_positive_sibling()
    {
        const string family = "vocabulary-review";
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                "activity.vocabulary-review.launch",
                family,
                positiveExamples: ["Start vocabulary review."],
                negativeExamples: ["Only show vocabulary review settings."]),
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                "preference.vocabulary-review.read",
                family,
                positiveExamples: ["Show vocabulary review settings."],
                negativeExamples: ["Delete every saved learning resource."]));

        var result = await Discover(
            index,
            "Don't start vocabulary review; show vocabulary review settings.",
            ApplicationCapabilityDiscoveryFixtures.Budgets());

        result.Suppressions.Should().BeEmpty();
        var match = result.Families.Should().ContainSingle().Which;
        match.Members.Single(member => member.Code == "activity.vocabulary-review.launch")
            .Reasons.Should().Contain(ApplicationCapabilityMatchReasonCode.NegatedPositiveExample);
        match.Members.Single(member => member.Code == "preference.vocabulary-review.read")
            .IsDiscoveryCandidate.Should().BeTrue();
    }

    [Fact]
    public async Task Candidate_limit_is_explicit_and_reports_a_near_tie_boundary()
    {
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor("alpha.catalog.read", "alpha-family"),
            ApplicationCapabilityDiscoveryFixtures.Descriptor("beta.catalog.read", "beta-family"));
        var provider = new ApplicationCapabilityDiscoveryFixtures.SemanticProvider(request =>
            ApplicationCapabilitySemanticScoringResult.Succeeded(
            [
                ApplicationCapabilityDiscoveryFixtures.Score(request, "alpha.catalog.read", 0.8),
                ApplicationCapabilityDiscoveryFixtures.Score(request, "beta.catalog.read", 0.8)
            ]));

        var result = await Discover(
            index,
            "unmatched",
            ApplicationCapabilityDiscoveryFixtures.Budgets(maximumFamilies: 1),
            provider);

        result.State.Should().Be(
            ApplicationCapabilityDiscoveryOutcomeState.CandidateLimitReached);
        result.Families.Should().ContainSingle();
        result.FamiliesBeyondCandidateLimit.Should().Be(1);
        result.CandidateBoundaryHasNearTie.Should().BeTrue();
    }

    [Fact]
    public async Task External_scores_can_rank_only_static_candidates_and_cannot_grant_execution()
    {
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor());
        var provider = new ApplicationCapabilityDiscoveryFixtures.SemanticProvider(request =>
            ApplicationCapabilitySemanticScoringResult.Succeeded(
            [
                ApplicationCapabilityDiscoveryFixtures.Score(request, "plan.today.read", 1)
            ]));

        var result = await Discover(
            index,
            "unmatched",
            ApplicationCapabilityDiscoveryFixtures.Budgets(),
            provider);

        result.Families.Should().ContainSingle();
        typeof(ApplicationCapabilityDiscoveryResult)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Should().NotContain(name =>
                name.Contains("Eligible", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Execute", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Authorize", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Invoke", StringComparison.OrdinalIgnoreCase));

        var unknownProvider = new ApplicationCapabilityDiscoveryFixtures.SemanticProvider(request =>
            ApplicationCapabilitySemanticScoringResult.Succeeded(
            [
                ApplicationCapabilityDiscoveryFixtures.Score(
                    request,
                    "uncatalogued.capability",
                    1)
            ]));
        var unknown = await Discover(
            index,
            "unmatched",
            ApplicationCapabilityDiscoveryFixtures.Budgets(),
            unknownProvider);
        unknown.Semantic.State.Should().Be(ApplicationCapabilitySemanticScoringState.Degraded);
        unknown.Families.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 1, 1, 1, 1)]
    [InlineData(1, 0, 1, 1, 1)]
    [InlineData(1, 1, 0, 1, 1)]
    [InlineData(1, 1, 1, 0, 1)]
    [InlineData(1, 1, 1, 1, 0)]
    public void Discovery_operational_budget_bounds_must_be_positive(
        long schema,
        long risk,
        long exposure,
        long resultMembers,
        int maximumFamilies)
    {
        Invoking(() => new ApplicationCapabilityDiscoveryBudgets(
                schema,
                risk,
                exposure,
                resultMembers,
                maximumFamilies))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    private static async Task<ApplicationCapabilityDiscoveryResult> Discover(
        ApplicationCapabilityDiscoveryIndex index,
        string query,
        ApplicationCapabilityDiscoveryBudgets budgets,
        IApplicationCapabilitySemanticScoreProvider? provider = null,
        ApplicationCapabilityDiscoveryScoringPolicy? scoringPolicy = null)
    {
        var service = new ApplicationCapabilityDiscoveryService(
            index,
            new DeterministicApplicationCapabilityLexicalScorer(),
            provider);
        return await service.DiscoverAsync(new(
            query,
            budgets,
            scoringPolicy ?? ApplicationCapabilityDiscoveryScoringPolicy.Balanced));
    }

    private static Action Invoking(Action action) => action;
}
