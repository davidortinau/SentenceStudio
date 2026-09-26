using System.Collections.ObjectModel;
using SentenceStudio.Application.Capabilities;

namespace SentenceStudio.Application.Capabilities.Discovery;

public interface IApplicationCapabilitySemanticScoreProvider
{
    ValueTask<ApplicationCapabilitySemanticScoringResult> ScoreAsync(
        ApplicationCapabilitySemanticScoringRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ApplicationCapabilitySemanticScoringRequest
{
    internal ApplicationCapabilitySemanticScoringRequest(
        string query,
        ApplicationCapabilityIndexGeneration generation,
        IReadOnlyList<ApplicationCapabilityDiscoveryDocument> documents)
    {
        Query = query;
        Generation = generation;
        Documents = documents;
    }

    public string Query { get; }

    public ApplicationCapabilityIndexGeneration Generation { get; }

    public IReadOnlyList<ApplicationCapabilityDiscoveryDocument> Documents { get; }
}

public sealed class ApplicationCapabilitySemanticScore
{
    public ApplicationCapabilitySemanticScore(
        string code,
        int majorVersion,
        double score,
        string descriptorSetHash,
        string embeddingProvider,
        string embeddingModel,
        string embeddingGeneration,
        int embeddingDimensions)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Capability code is required.", nameof(code));
        }

        if (majorVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(majorVersion));
        }

        if (!double.IsFinite(score) || score is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(score), "Semantic scores must be between zero and one.");
        }

        if (!ApplicationCapabilityDescriptorValidator.IsCanonicalFingerprint(descriptorSetHash))
        {
            throw new ArgumentException(
                "Descriptor set hash must be a canonical lower-case SHA-256 fingerprint.",
                nameof(descriptorSetHash));
        }

        ValidateIdentity(embeddingProvider, nameof(embeddingProvider));
        ValidateIdentity(embeddingModel, nameof(embeddingModel));
        ValidateIdentity(embeddingGeneration, nameof(embeddingGeneration));
        if (embeddingDimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(embeddingDimensions));
        }

        Code = code;
        MajorVersion = majorVersion;
        Score = score;
        DescriptorSetHash = descriptorSetHash;
        EmbeddingProvider = embeddingProvider;
        EmbeddingModel = embeddingModel;
        EmbeddingGeneration = embeddingGeneration;
        EmbeddingDimensions = embeddingDimensions;
    }

    public string Code { get; }

    public int MajorVersion { get; }

    public double Score { get; }

    public string DescriptorSetHash { get; }

    public string EmbeddingProvider { get; }

    public string EmbeddingModel { get; }

    public string EmbeddingGeneration { get; }

    public int EmbeddingDimensions { get; }

    private static void ValidateIdentity(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            throw new ArgumentException(
                $"{name} must be non-empty and no longer than 160 characters.",
                name);
        }
    }
}

public sealed class ApplicationCapabilitySemanticScoringResult
{
    private ApplicationCapabilitySemanticScoringResult(
        ApplicationCapabilitySemanticScoringState state,
        ApplicationCapabilitySemanticScoringReason reason,
        IReadOnlyList<ApplicationCapabilitySemanticScore> scores)
    {
        State = state;
        Reason = reason;
        Scores = scores;
    }

    public ApplicationCapabilitySemanticScoringState State { get; }

    public ApplicationCapabilitySemanticScoringReason Reason { get; }

    public IReadOnlyList<ApplicationCapabilitySemanticScore> Scores { get; }

    public static ApplicationCapabilitySemanticScoringResult Succeeded(
        IEnumerable<ApplicationCapabilitySemanticScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        return new(
            ApplicationCapabilitySemanticScoringState.Succeeded,
            ApplicationCapabilitySemanticScoringReason.None,
            new ReadOnlyCollection<ApplicationCapabilitySemanticScore>(scores.ToArray()));
    }

    public static ApplicationCapabilitySemanticScoringResult Degraded(
        ApplicationCapabilitySemanticScoringReason reason) =>
        Failure(ApplicationCapabilitySemanticScoringState.Degraded, reason);

    public static ApplicationCapabilitySemanticScoringResult Unavailable(
        ApplicationCapabilitySemanticScoringReason reason) =>
        Failure(ApplicationCapabilitySemanticScoringState.Unavailable, reason);

    public static ApplicationCapabilitySemanticScoringResult StaleGeneration(
        ApplicationCapabilitySemanticScoringReason reason) =>
        Failure(ApplicationCapabilitySemanticScoringState.StaleGeneration, reason);

    public static ApplicationCapabilitySemanticScoringResult DimensionMismatch() =>
        Failure(
            ApplicationCapabilitySemanticScoringState.DimensionMismatch,
            ApplicationCapabilitySemanticScoringReason.MixedOrUnexpectedDimensions);

    private static ApplicationCapabilitySemanticScoringResult Failure(
        ApplicationCapabilitySemanticScoringState state,
        ApplicationCapabilitySemanticScoringReason reason)
    {
        if (reason == ApplicationCapabilitySemanticScoringReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return new(
            state,
            reason,
            Array.Empty<ApplicationCapabilitySemanticScore>());
    }
}
