using FluentAssertions;
using SentenceStudio.Application.Capabilities.Discovery;

namespace SentenceStudio.UnitTests.ApplicationCapabilities.Discovery;

public sealed class ApplicationCapabilitySemanticScoringTests
{
    [Fact]
    public async Task Successful_provider_scores_rank_static_documents()
    {
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                "plan.today.read",
                "plans",
                "Read a study plan."),
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                "skill.catalog.read",
                "skills",
                "List language skills."));
        var provider = new ApplicationCapabilityDiscoveryFixtures.SemanticProvider(request =>
            ApplicationCapabilitySemanticScoringResult.Succeeded(
            [
                ApplicationCapabilityDiscoveryFixtures.Score(
                    request,
                    "skill.catalog.read",
                    0.9),
                ApplicationCapabilityDiscoveryFixtures.Score(
                    request,
                    "plan.today.read",
                    0.2)
            ]));

        var result = await Discover(index, provider, "unmatched semantic concept");

        result.Semantic.State.Should().Be(ApplicationCapabilitySemanticScoringState.Succeeded);
        result.Families.Select(family => family.Family).Should().Equal("skills", "plans");
        result.Families[0].Reasons.Should().Contain(
            ApplicationCapabilityMatchReasonCode.SemanticScore);
        var member = result.Families[0].Members.Should().ContainSingle().Which;
        member.SemanticScore.Should().Be(0.9);
        member.IsDiscoveryCandidate.Should().BeTrue();
        member.Reasons.Should().Contain(ApplicationCapabilityMatchReasonCode.SemanticScore);
    }

    [Theory]
    [InlineData(
        ApplicationCapabilitySemanticScoringState.Degraded,
        ApplicationCapabilitySemanticScoringReason.ProviderFailure)]
    [InlineData(
        ApplicationCapabilitySemanticScoringState.Unavailable,
        ApplicationCapabilitySemanticScoringReason.ProviderUnavailable)]
    [InlineData(
        ApplicationCapabilitySemanticScoringState.StaleGeneration,
        ApplicationCapabilitySemanticScoringReason.StaleEmbeddingGeneration)]
    [InlineData(
        ApplicationCapabilitySemanticScoringState.DimensionMismatch,
        ApplicationCapabilitySemanticScoringReason.MixedOrUnexpectedDimensions)]
    public async Task Typed_provider_failures_degrade_without_similarity(
        ApplicationCapabilitySemanticScoringState state,
        ApplicationCapabilitySemanticScoringReason reason)
    {
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor());
        var provider = new ApplicationCapabilityDiscoveryFixtures.SemanticProvider(_ =>
            state switch
            {
                ApplicationCapabilitySemanticScoringState.Degraded =>
                    ApplicationCapabilitySemanticScoringResult.Degraded(reason),
                ApplicationCapabilitySemanticScoringState.Unavailable =>
                    ApplicationCapabilitySemanticScoringResult.Unavailable(reason),
                ApplicationCapabilitySemanticScoringState.StaleGeneration =>
                    ApplicationCapabilitySemanticScoringResult.StaleGeneration(reason),
                ApplicationCapabilitySemanticScoringState.DimensionMismatch =>
                    ApplicationCapabilitySemanticScoringResult.DimensionMismatch(),
                _ => throw new InvalidOperationException()
            });

        var result = await Discover(index, provider, "no lexical overlap");

        result.Semantic.State.Should().Be(state);
        result.Semantic.Reason.Should().Be(reason);
        result.Families.Should().BeEmpty(
            "a failed semantic provider must not fabricate vector similarity");
    }

    [Fact]
    public async Task Missing_provider_is_typed_unavailable_and_lexical_retrieval_continues()
    {
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                positiveExamples: ["Show my study plan."]));

        var result = await Discover(index, provider: null, "Show my study plan.");

        result.Semantic.State.Should().Be(ApplicationCapabilitySemanticScoringState.Unavailable);
        result.Semantic.Reason.Should().Be(
            ApplicationCapabilitySemanticScoringReason.ProviderNotConfigured);
        result.Families.Should().ContainSingle();
        result.Families[0].SemanticScore.Should().Be(0);
    }

    [Fact]
    public async Task Stale_descriptor_or_embedding_generation_is_rejected()
    {
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor());
        var staleDescriptorProvider =
            new ApplicationCapabilityDiscoveryFixtures.SemanticProvider(request =>
                ApplicationCapabilitySemanticScoringResult.Succeeded(
                [
                    ApplicationCapabilityDiscoveryFixtures.Score(
                        request,
                        "plan.today.read",
                        0.9,
                        descriptorSetHash:
                            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
                ]));
        var staleEmbeddingProvider =
            new ApplicationCapabilityDiscoveryFixtures.SemanticProvider(request =>
                ApplicationCapabilitySemanticScoringResult.Succeeded(
                [
                    ApplicationCapabilityDiscoveryFixtures.Score(
                        request,
                        "plan.today.read",
                        0.9,
                        embeddingGeneration: "generation-2")
                ]));

        var staleDescriptor = await Discover(
            index,
            staleDescriptorProvider,
            "no lexical overlap");
        var staleEmbedding = await Discover(
            index,
            staleEmbeddingProvider,
            "no lexical overlap");

        staleDescriptor.Semantic.State.Should().Be(
            ApplicationCapabilitySemanticScoringState.StaleGeneration);
        staleDescriptor.Semantic.Reason.Should().Be(
            ApplicationCapabilitySemanticScoringReason.StaleDescriptorSet);
        staleEmbedding.Semantic.State.Should().Be(
            ApplicationCapabilitySemanticScoringState.StaleGeneration);
        staleEmbedding.Semantic.Reason.Should().Be(
            ApplicationCapabilitySemanticScoringReason.StaleEmbeddingGeneration);
        staleDescriptor.Families.Should().BeEmpty();
        staleEmbedding.Families.Should().BeEmpty();
    }

    [Fact]
    public async Task Unexpected_and_mixed_dimensions_are_rejected_as_typed_mismatch()
    {
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor("plan.today.read", "plans"),
            ApplicationCapabilityDiscoveryFixtures.Descriptor("skill.catalog.read", "skills"));
        var provider = new ApplicationCapabilityDiscoveryFixtures.SemanticProvider(request =>
            ApplicationCapabilitySemanticScoringResult.Succeeded(
            [
                ApplicationCapabilityDiscoveryFixtures.Score(
                    request,
                    "plan.today.read",
                    0.8),
                ApplicationCapabilityDiscoveryFixtures.Score(
                    request,
                    "skill.catalog.read",
                    0.8,
                    dimensions: request.Generation.EmbeddingDimensions + 1)
            ]));

        var result = await Discover(index, provider, "no lexical overlap");

        result.Semantic.State.Should().Be(
            ApplicationCapabilitySemanticScoringState.DimensionMismatch);
        result.Semantic.Reason.Should().Be(
            ApplicationCapabilitySemanticScoringReason.MixedOrUnexpectedDimensions);
        result.Families.Should().BeEmpty();
    }

    [Fact]
    public void Semantic_scores_are_bounded_at_the_provider_seam()
    {
        var index = ApplicationCapabilityDiscoveryFixtures.BuildIndex(
            ApplicationCapabilityDiscoveryFixtures.Descriptor());
        var generation = index.Generation;

        Invoking(() => new ApplicationCapabilitySemanticScore(
                "plan.today.read",
                1,
                1.01,
                generation.DescriptorSetHash,
                generation.EmbeddingProvider,
                generation.EmbeddingModel,
                generation.EmbeddingGeneration,
                generation.EmbeddingDimensions))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    private static async Task<ApplicationCapabilityDiscoveryResult> Discover(
        ApplicationCapabilityDiscoveryIndex index,
        IApplicationCapabilitySemanticScoreProvider? provider,
        string query)
    {
        var service = new ApplicationCapabilityDiscoveryService(
            index,
            new DeterministicApplicationCapabilityLexicalScorer(),
            provider);
        return await service.DiscoverAsync(new(
            query,
            ApplicationCapabilityDiscoveryFixtures.Budgets(),
            ApplicationCapabilityDiscoveryScoringPolicy.Balanced));
    }

    private static Action Invoking(Action action) => action;
}
