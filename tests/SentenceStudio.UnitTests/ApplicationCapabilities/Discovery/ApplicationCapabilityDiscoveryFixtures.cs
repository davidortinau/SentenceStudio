using FluentAssertions;
using SentenceStudio.Application.Capabilities;
using SentenceStudio.Application.Capabilities.Discovery;
using SentenceStudio.Contracts.AppOperation;
using SentenceStudio.UnitTests.ApplicationCapabilities;

namespace SentenceStudio.UnitTests.ApplicationCapabilities.Discovery;

internal static class ApplicationCapabilityDiscoveryFixtures
{
    internal const string RequestSchemaFingerprint =
        "sha256:3333333333333333333333333333333333333333333333333333333333333333";
    internal const string ClientResultSchemaFingerprint =
        "sha256:4444444444444444444444444444444444444444444444444444444444444444";
    internal const string ObservationSchemaFingerprint =
        "sha256:5555555555555555555555555555555555555555555555555555555555555555";

    internal static ApplicationCapabilitySchemaFingerprints Schemas() =>
        new(
            RequestSchemaFingerprint,
            ClientResultSchemaFingerprint,
            ObservationSchemaFingerprint);

    internal static IApplicationCapabilityDescriptor Descriptor(
        string code = "plan.today.read",
        string family = "plan-and-practice-history",
        string? description = null,
        IReadOnlyList<string>? positiveExamples = null,
        IReadOnlyList<string>? negativeExamples = null,
        IReadOnlyList<string>? aliases = null,
        int majorVersion = 1,
        int schemaTokenCost = 10,
        int riskCost = 1,
        int exposureCost = 1,
        string? descriptorFingerprint = null,
        Func<ApplicationCapabilityMetadata, ApplicationCapabilityMetadata>? metadataMutation = null)
    {
        var metadata = ApplicationCapabilityFixtures.StaticCandidateMetadata(code) with
        {
            Family = family,
            MajorVersion = majorVersion,
            SelectionDescription = description ?? $"Select the {family} application outcome.",
            PositiveExamples = positiveExamples ?? [$"Use {code}."],
            NegativeExamples = negativeExamples ?? [$"Do not use {code}."],
            Aliases = aliases ?? [],
            Budgets = ApplicationCapabilityFixtures.StaticCandidateMetadata().Budgets! with
            {
                SchemaTokenCost = schemaTokenCost,
                RiskCost = riskCost,
                ExposureCost = exposureCost
            },
            DescriptorFingerprint =
                descriptorFingerprint ?? ApplicationCapabilityFixtures.DescriptorFingerprint,
            QualificationCandidate = ApplicationCapabilityFixtures.CreateQualificationCandidate(
                descriptorFingerprint
                    ?? ApplicationCapabilityFixtures.DescriptorFingerprint)
        };
        metadata = metadataMutation?.Invoke(metadata) ?? metadata;

        return new ApplicationCapabilityDefinition<
            ApplicationCapabilityQueryRequest,
            ApplicationStateVersion,
            CoachObservationEnvelope>(metadata);
    }

    internal static ApplicationCapabilityDiscoveryIndex BuildIndex(
        params IApplicationCapabilityDescriptor[] descriptors)
    {
        var builder = new ApplicationCapabilityDiscoveryIndexBuilder();
        foreach (var descriptor in descriptors)
        {
            builder.TryAdd(descriptor, Schemas()).State.Should().Be(
                ApplicationCapabilityIndexAdmissionState.Added);
        }

        return builder.Freeze(Generation(builder.ComputeDescriptorSetHash()));
    }

    internal static ApplicationCapabilityIndexGeneration Generation(
        string descriptorSetHash,
        string embeddingGeneration = "generation-1",
        int dimensions = 3,
        string provider = "test-provider",
        string model = "test-model") =>
        new(
            ApplicationCapabilityIndexGeneration.CurrentSchemaVersion,
            descriptorSetHash,
            provider,
            model,
            embeddingGeneration,
            dimensions,
            new DateTimeOffset(2026, 9, 3, 15, 0, 0, TimeSpan.Zero),
            ApplicationCapabilityIndexPromotionState.Created);

    internal static ApplicationCapabilityDiscoveryBudgets Budgets(
        long schemaTokens = 10_000,
        long risk = 10_000,
        long exposure = 10_000,
        long resultMembers = 10_000,
        int maximumFamilies = 100) =>
        new(schemaTokens, risk, exposure, resultMembers, maximumFamilies);

    internal static ApplicationCapabilitySemanticScore Score(
        ApplicationCapabilitySemanticScoringRequest request,
        string code,
        double score,
        int majorVersion = 1,
        string? descriptorSetHash = null,
        string? embeddingGeneration = null,
        int? dimensions = null,
        string? provider = null,
        string? model = null) =>
        new(
            code,
            majorVersion,
            score,
            descriptorSetHash ?? request.Generation.DescriptorSetHash,
            provider ?? request.Generation.EmbeddingProvider,
            model ?? request.Generation.EmbeddingModel,
            embeddingGeneration ?? request.Generation.EmbeddingGeneration,
            dimensions ?? request.Generation.EmbeddingDimensions);

    internal sealed class SemanticProvider(
        Func<ApplicationCapabilitySemanticScoringRequest, ApplicationCapabilitySemanticScoringResult> score)
        : IApplicationCapabilitySemanticScoreProvider
    {
        public ValueTask<ApplicationCapabilitySemanticScoringResult> ScoreAsync(
            ApplicationCapabilitySemanticScoringRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(score(request));
    }
}
