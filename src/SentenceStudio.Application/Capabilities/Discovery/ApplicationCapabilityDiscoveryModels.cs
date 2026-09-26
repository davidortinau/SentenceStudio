using System.Collections.ObjectModel;
using SentenceStudio.Application.Capabilities;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.Application.Capabilities.Discovery;

public enum ApplicationCapabilityIndexPromotionState
{
    Created = 1,
    Promoted = 2
}

public enum ApplicationCapabilityIndexAdmissionState
{
    Added = 1,
    DefaultDenied = 2
}

public enum ApplicationCapabilityMatchReasonCode
{
    ExactCode = 1,
    ExactAlias = 2,
    ExactFamily = 3,
    NormalizedCodeTerms = 4,
    SelectionDescriptionTerms = 5,
    PositiveExample = 6,
    SemanticScore = 7,
    NegativeExamplePenalty = 8,
    HardNegativeExample = 9,
    NegatedPositiveExample = 10
}

public enum ApplicationCapabilitySemanticScoringState
{
    Succeeded = 1,
    Degraded = 2,
    Unavailable = 3,
    StaleGeneration = 4,
    DimensionMismatch = 5
}

public enum ApplicationCapabilitySemanticScoringReason
{
    None = 0,
    ProviderNotConfigured = 1,
    ProviderUnavailable = 2,
    ProviderFailure = 3,
    InvalidProviderResult = 4,
    StaleDescriptorSet = 5,
    StaleEmbeddingGeneration = 6,
    MixedOrUnexpectedDimensions = 7
}

public enum ApplicationCapabilityDiscoveryOutcomeState
{
    Complete = 1,
    CandidateLimitReached = 2,
    NeedsExpansion = 3
}

[Flags]
public enum ApplicationCapabilityBudgetDimension
{
    None = 0,
    SchemaTokens = 1,
    Risk = 2,
    Exposure = 4,
    ResultMembers = 8
}

public sealed class ApplicationCapabilitySchemaFingerprints
{
    public ApplicationCapabilitySchemaFingerprints(
        string request,
        string clientResult,
        string coachObservation)
    {
        Validate(request, nameof(request));
        Validate(clientResult, nameof(clientResult));
        Validate(coachObservation, nameof(coachObservation));

        Request = request;
        ClientResult = clientResult;
        CoachObservation = coachObservation;
    }

    public string Request { get; }

    public string ClientResult { get; }

    public string CoachObservation { get; }

    private static void Validate(string value, string name)
    {
        if (!ApplicationCapabilityDescriptorValidator.IsCanonicalFingerprint(value))
        {
            throw new ArgumentException(
                $"{name} must be a canonical lower-case SHA-256 fingerprint.",
                name);
        }
    }
}

internal sealed class ApplicationCapabilityDiscoveryDescriptorSnapshot
    : IApplicationCapabilityDescriptor
{
    private ApplicationCapabilityDiscoveryDescriptorSnapshot(
        IApplicationCapabilityDescriptor descriptor)
    {
        Code = descriptor.Code;
        Family = descriptor.Family;
        MajorVersion = descriptor.MajorVersion;
        SelectionDescription = descriptor.SelectionDescription;
        PositiveExamples = Copy(descriptor.PositiveExamples);
        NegativeExamples = Copy(descriptor.NegativeExamples);
        Aliases = Copy(descriptor.Aliases);
        RequestType = descriptor.RequestType;
        ClientResultType = descriptor.ClientResultType;
        CoachObservationType = descriptor.CoachObservationType;
        Surfaces = Copy(descriptor.Surfaces);
        ExecutionAuthority = descriptor.ExecutionAuthority;
        Effect = descriptor.Effect;
        Sensitivity = descriptor.Sensitivity;
        Confirmation = descriptor.Confirmation;
        Idempotency = descriptor.Idempotency;
        Synchronization = descriptor.Synchronization;
        Continuation = descriptor.Continuation;
        PolicyState = descriptor.PolicyState;
        CoachExposurePolicy = descriptor.CoachExposurePolicy;
        Budgets = descriptor.Budgets;
        Rollout = descriptor.Rollout;
        Availability = descriptor.Availability;
        Dependencies = descriptor.Dependencies;
        ReleaseValidationStatus = descriptor.ReleaseValidationStatus;
        DescriptorFingerprint = descriptor.DescriptorFingerprint;
        QualificationCandidate = descriptor.QualificationCandidate;
        Handler = descriptor.Handler;
        CoachProjector = descriptor.CoachProjector;
    }

    public string Code { get; }
    public string Family { get; }
    public int MajorVersion { get; }
    public string SelectionDescription { get; }
    public IReadOnlyList<string> PositiveExamples { get; }
    public IReadOnlyList<string> NegativeExamples { get; }
    public IReadOnlyList<string> Aliases { get; }
    public Type RequestType { get; }
    public Type ClientResultType { get; }
    public Type CoachObservationType { get; }
    public IReadOnlyList<ApplicationCapabilitySurface> Surfaces { get; }
    public ApplicationExecutionAuthority ExecutionAuthority { get; }
    public ApplicationEffectClass Effect { get; }
    public ApplicationSensitivity Sensitivity { get; }
    public ApplicationConfirmationPolicy Confirmation { get; }
    public ApplicationIdempotencyPolicy Idempotency { get; }
    public ApplicationSynchronizationPolicy Synchronization { get; }
    public ApplicationContinuationPolicy Continuation { get; }
    public ApplicationCapabilityPolicyState PolicyState { get; }
    public ApplicationCapabilityCoachExposurePolicy CoachExposurePolicy { get; }
    public ApplicationCapabilityBudgets? Budgets { get; }
    public ApplicationCapabilityRolloutState Rollout { get; }
    public ApplicationCapabilityAvailabilityState Availability { get; }
    public ApplicationCapabilityDependencyState Dependencies { get; }
    public ApplicationCapabilityReleaseValidationStatus ReleaseValidationStatus { get; }
    public string DescriptorFingerprint { get; }
    public ApplicationCapabilityQualificationCandidate? QualificationCandidate { get; }
    public ApplicationCapabilityImplementationIdentity? Handler { get; }
    public ApplicationCapabilityImplementationIdentity? CoachProjector { get; }

    internal static ApplicationCapabilityDiscoveryDescriptorSnapshot Create(
        IApplicationCapabilityDescriptor descriptor) =>
        descriptor as ApplicationCapabilityDiscoveryDescriptorSnapshot
        ?? new(descriptor);

    private static IReadOnlyList<T> Copy<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());
}

public sealed class ApplicationCapabilityDiscoveryCosts
{
    public ApplicationCapabilityDiscoveryCosts(
        long schemaTokens,
        long risk,
        long exposure,
        long resultMembers)
    {
        if (schemaTokens < 0 || risk < 0 || exposure < 0 || resultMembers < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaTokens),
                "Discovery costs cannot be negative.");
        }

        SchemaTokens = schemaTokens;
        Risk = risk;
        Exposure = exposure;
        ResultMembers = resultMembers;
    }

    public long SchemaTokens { get; }

    public long Risk { get; }

    public long Exposure { get; }

    public long ResultMembers { get; }

    internal bool FitsWithin(ApplicationCapabilityDiscoveryCosts available) =>
        SchemaTokens <= available.SchemaTokens
        && Risk <= available.Risk
        && Exposure <= available.Exposure
        && ResultMembers <= available.ResultMembers;

    internal ApplicationCapabilityDiscoveryCosts Subtract(
        ApplicationCapabilityDiscoveryCosts consumed) =>
        new(
            SchemaTokens - consumed.SchemaTokens,
            Risk - consumed.Risk,
            Exposure - consumed.Exposure,
            ResultMembers - consumed.ResultMembers);
}

public sealed class ApplicationCapabilityIndexGeneration
{
    public const string CurrentSchemaVersion = "application-capability-discovery/v2";

    public ApplicationCapabilityIndexGeneration(
        string indexSchemaVersion,
        string descriptorSetHash,
        string embeddingProvider,
        string embeddingModel,
        string embeddingGeneration,
        int embeddingDimensions,
        DateTimeOffset createdAtUtc,
        ApplicationCapabilityIndexPromotionState promotionState)
    {
        ValidateTechnicalIdentity(indexSchemaVersion, nameof(indexSchemaVersion));
        ValidateTechnicalIdentity(embeddingProvider, nameof(embeddingProvider));
        ValidateTechnicalIdentity(embeddingModel, nameof(embeddingModel));
        ValidateTechnicalIdentity(embeddingGeneration, nameof(embeddingGeneration));

        if (!ApplicationCapabilityDescriptorValidator.IsCanonicalFingerprint(descriptorSetHash))
        {
            throw new ArgumentException(
                "Descriptor set hash must be a canonical lower-case SHA-256 fingerprint.",
                nameof(descriptorSetHash));
        }

        if (embeddingDimensions is <= 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(embeddingDimensions),
                "Embedding dimensions must be positive and bounded.");
        }

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Index generation time must use the UTC offset.",
                nameof(createdAtUtc));
        }

        if (!Enum.IsDefined(promotionState))
        {
            throw new ArgumentOutOfRangeException(nameof(promotionState));
        }

        IndexSchemaVersion = indexSchemaVersion;
        DescriptorSetHash = descriptorSetHash;
        EmbeddingProvider = embeddingProvider;
        EmbeddingModel = embeddingModel;
        EmbeddingGeneration = embeddingGeneration;
        EmbeddingDimensions = embeddingDimensions;
        CreatedAtUtc = createdAtUtc;
        PromotionState = promotionState;
    }

    public string IndexSchemaVersion { get; }

    public string DescriptorSetHash { get; }

    public string EmbeddingProvider { get; }

    public string EmbeddingModel { get; }

    public string EmbeddingGeneration { get; }

    public int EmbeddingDimensions { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public ApplicationCapabilityIndexPromotionState PromotionState { get; }

    private static void ValidateTechnicalIdentity(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            throw new ArgumentException(
                $"{name} must be non-empty and no longer than 160 characters.",
                name);
        }
    }
}

public sealed class ApplicationCapabilityDiscoveryDocument
{
    internal ApplicationCapabilityDiscoveryDocument(
        IApplicationCapabilityDescriptor descriptor,
        ApplicationCapabilitySchemaFingerprints schemaFingerprints)
    {
        var snapshot = ApplicationCapabilityDiscoveryDescriptorSnapshot.Create(descriptor);
        Code = snapshot.Code;
        MajorVersion = snapshot.MajorVersion;
        Family = snapshot.Family;
        SelectionDescription = snapshot.SelectionDescription;
        PositiveExamples = snapshot.PositiveExamples;
        NegativeExamples = snapshot.NegativeExamples;
        Aliases = snapshot.Aliases;
        ExecutionAuthority = snapshot.ExecutionAuthority;
        Effect = snapshot.Effect;
        Sensitivity = snapshot.Sensitivity;
        Confirmation = snapshot.Confirmation;
        Idempotency = snapshot.Idempotency;
        Synchronization = snapshot.Synchronization;
        Continuation = snapshot.Continuation;
        PolicyState = snapshot.PolicyState;
        CoachExposurePolicy = snapshot.CoachExposurePolicy;
        Rollout = snapshot.Rollout;
        Availability = snapshot.Availability;
        Dependencies = snapshot.Dependencies;
        ReleaseValidationStatus = snapshot.ReleaseValidationStatus;
        DescriptorFingerprint = snapshot.DescriptorFingerprint;
        SchemaFingerprints = schemaFingerprints;
        DiscoveryFingerprint = ApplicationCapabilityDiscoveryFingerprint.Compute(
            snapshot,
            schemaFingerprints);
        Costs = new ApplicationCapabilityDiscoveryCosts(
            snapshot.Budgets!.SchemaTokenCost,
            snapshot.Budgets.RiskCost,
            snapshot.Budgets.ExposureCost,
            resultMembers: 1);
    }

    public string Code { get; }

    public int MajorVersion { get; }

    public string Family { get; }

    public string SelectionDescription { get; }

    public IReadOnlyList<string> PositiveExamples { get; }

    public IReadOnlyList<string> NegativeExamples { get; }

    public IReadOnlyList<string> Aliases { get; }

    public ApplicationExecutionAuthority ExecutionAuthority { get; }

    public ApplicationEffectClass Effect { get; }

    public ApplicationSensitivity Sensitivity { get; }

    public ApplicationConfirmationPolicy Confirmation { get; }

    public ApplicationIdempotencyPolicy Idempotency { get; }

    public ApplicationSynchronizationPolicy Synchronization { get; }

    public ApplicationContinuationPolicy Continuation { get; }

    public ApplicationCapabilityPolicyState PolicyState { get; }

    public ApplicationCapabilityCoachExposurePolicy CoachExposurePolicy { get; }

    public ApplicationCapabilityRolloutState Rollout { get; }

    public ApplicationCapabilityAvailabilityState Availability { get; }

    public ApplicationCapabilityDependencyState Dependencies { get; }

    public ApplicationCapabilityReleaseValidationStatus ReleaseValidationStatus { get; }

    public string DescriptorFingerprint { get; }

    public ApplicationCapabilitySchemaFingerprints SchemaFingerprints { get; }

    public string DiscoveryFingerprint { get; }

    public ApplicationCapabilityDiscoveryCosts Costs { get; }

}

public sealed class ApplicationCapabilityDiscoveryBudgets
{
    public ApplicationCapabilityDiscoveryBudgets(
        long schemaTokenBudget,
        long riskBudget,
        long exposureBudget,
        long resultMemberBudget,
        int maximumFamilyCandidates)
    {
        if (schemaTokenBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaTokenBudget));
        }

        if (riskBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(riskBudget));
        }

        if (exposureBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exposureBudget));
        }

        if (resultMemberBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resultMemberBudget));
        }

        if (maximumFamilyCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFamilyCandidates));
        }

        SchemaTokenBudget = schemaTokenBudget;
        RiskBudget = riskBudget;
        ExposureBudget = exposureBudget;
        ResultMemberBudget = resultMemberBudget;
        MaximumFamilyCandidates = maximumFamilyCandidates;
    }

    public long SchemaTokenBudget { get; }

    public long RiskBudget { get; }

    public long ExposureBudget { get; }

    public long ResultMemberBudget { get; }

    public int MaximumFamilyCandidates { get; }

    internal ApplicationCapabilityDiscoveryCosts ToCosts() =>
        new(SchemaTokenBudget, RiskBudget, ExposureBudget, ResultMemberBudget);
}

public sealed class ApplicationCapabilityDiscoveryScoringPolicy
{
    public ApplicationCapabilityDiscoveryScoringPolicy(
        double lexicalWeight,
        double semanticWeight,
        bool exactAndAliasEvidencePrecedesVectorOnly,
        double nearTieTolerance)
    {
        ValidateWeight(lexicalWeight, nameof(lexicalWeight), allowZero: false);
        ValidateWeight(semanticWeight, nameof(semanticWeight), allowZero: true);

        if (!double.IsFinite(nearTieTolerance) || nearTieTolerance is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(nearTieTolerance));
        }

        LexicalWeight = lexicalWeight;
        SemanticWeight = semanticWeight;
        ExactAndAliasEvidencePrecedesVectorOnly = exactAndAliasEvidencePrecedesVectorOnly;
        NearTieTolerance = nearTieTolerance;
    }

    public double LexicalWeight { get; }

    public double SemanticWeight { get; }

    public bool ExactAndAliasEvidencePrecedesVectorOnly { get; }

    public double NearTieTolerance { get; }

    public static ApplicationCapabilityDiscoveryScoringPolicy Balanced { get; } =
        new(lexicalWeight: 1, semanticWeight: 1, exactAndAliasEvidencePrecedesVectorOnly: true, nearTieTolerance: 0.02);

    private static void ValidateWeight(double value, string name, bool allowZero)
    {
        if (!double.IsFinite(value) || value < 0 || (!allowZero && value == 0) || value > 100)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

public sealed class ApplicationCapabilityDiscoveryRequest
{
    public const int MaximumQueryLength = 8_192;

    public ApplicationCapabilityDiscoveryRequest(
        string query,
        ApplicationCapabilityDiscoveryBudgets budgets,
        ApplicationCapabilityDiscoveryScoringPolicy scoringPolicy)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaximumQueryLength)
        {
            throw new ArgumentException(
                $"Discovery query must be non-empty and no longer than {MaximumQueryLength} characters.",
                nameof(query));
        }

        Query = query;
        Budgets = budgets ?? throw new ArgumentNullException(nameof(budgets));
        ScoringPolicy = scoringPolicy ?? throw new ArgumentNullException(nameof(scoringPolicy));
    }

    public string Query { get; }

    public ApplicationCapabilityDiscoveryBudgets Budgets { get; }

    public ApplicationCapabilityDiscoveryScoringPolicy ScoringPolicy { get; }
}

public sealed class ApplicationCapabilityIndexAdmission
{
    internal ApplicationCapabilityIndexAdmission(
        ApplicationCapabilityIndexAdmissionState state,
        string code,
        int majorVersion)
    {
        State = state;
        Code = code;
        MajorVersion = majorVersion;
    }

    public ApplicationCapabilityIndexAdmissionState State { get; }

    public string Code { get; }

    public int MajorVersion { get; }
}

public sealed class ApplicationCapabilityDiscoveryMember
{
    internal ApplicationCapabilityDiscoveryMember(
        ApplicationCapabilityDiscoveryDocument document,
        double lexicalScore,
        double negativePenalty,
        double semanticScore,
        double combinedScore,
        bool hardNegative,
        bool isDiscoveryCandidate,
        IReadOnlyList<ApplicationCapabilityMatchReasonCode> reasons)
    {
        Code = document.Code;
        MajorVersion = document.MajorVersion;
        Family = document.Family;
        DescriptorFingerprint = document.DescriptorFingerprint;
        SchemaFingerprints = document.SchemaFingerprints;
        DiscoveryFingerprint = document.DiscoveryFingerprint;
        Costs = document.Costs;
        LexicalScore = lexicalScore;
        NegativePenalty = negativePenalty;
        SemanticScore = semanticScore;
        CombinedScore = combinedScore;
        HardNegative = hardNegative;
        IsDiscoveryCandidate = isDiscoveryCandidate;
        Reasons = reasons;
    }

    public string Code { get; }

    public int MajorVersion { get; }

    public string Family { get; }

    public string DescriptorFingerprint { get; }

    public ApplicationCapabilitySchemaFingerprints SchemaFingerprints { get; }

    public string DiscoveryFingerprint { get; }

    public ApplicationCapabilityDiscoveryCosts Costs { get; }

    public double LexicalScore { get; }

    public double NegativePenalty { get; }

    public double SemanticScore { get; }

    public double CombinedScore { get; }

    public bool HardNegative { get; }

    public bool IsDiscoveryCandidate { get; }

    public IReadOnlyList<ApplicationCapabilityMatchReasonCode> Reasons { get; }
}

public sealed class ApplicationCapabilityFamilyMatch
{
    internal ApplicationCapabilityFamilyMatch(
        string family,
        int rank,
        int nearTieGroup,
        double lexicalScore,
        double negativePenalty,
        double semanticScore,
        double combinedScore,
        IReadOnlyList<ApplicationCapabilityMatchReasonCode> reasons,
        IReadOnlyList<ApplicationCapabilityDiscoveryMember> members,
        ApplicationCapabilityDiscoveryCosts costs)
    {
        Family = family;
        Rank = rank;
        NearTieGroup = nearTieGroup;
        LexicalScore = lexicalScore;
        NegativePenalty = negativePenalty;
        SemanticScore = semanticScore;
        CombinedScore = combinedScore;
        Reasons = reasons;
        Members = members;
        Costs = costs;
    }

    public string Family { get; }

    public int Rank { get; }

    public int NearTieGroup { get; }

    public double LexicalScore { get; }

    public double NegativePenalty { get; }

    public double SemanticScore { get; }

    public double CombinedScore { get; }

    public IReadOnlyList<ApplicationCapabilityMatchReasonCode> Reasons { get; }

    public IReadOnlyList<ApplicationCapabilityDiscoveryMember> Members { get; }

    public ApplicationCapabilityDiscoveryCosts Costs { get; }
}

public sealed class ApplicationCapabilityFamilySuppression
{
    internal ApplicationCapabilityFamilySuppression(
        string family,
        IReadOnlyList<ApplicationCapabilityMatchReasonCode> reasons,
        IReadOnlyList<ApplicationCapabilityDiscoveryMember> members)
    {
        Family = family;
        Reasons = reasons;
        Members = members;
    }

    public string Family { get; }

    public IReadOnlyList<ApplicationCapabilityMatchReasonCode> Reasons { get; }

    public IReadOnlyList<ApplicationCapabilityDiscoveryMember> Members { get; }
}

public sealed class ApplicationCapabilityFamilyExceedsBudget
{
    internal ApplicationCapabilityFamilyExceedsBudget(
        string family,
        int rank,
        ApplicationCapabilityBudgetDimension dimensions,
        ApplicationCapabilityDiscoveryCosts required,
        ApplicationCapabilityDiscoveryCosts available)
    {
        Family = family;
        Rank = rank;
        Dimensions = dimensions;
        Required = required;
        Available = available;
    }

    public string Family { get; }

    public int Rank { get; }

    public ApplicationCapabilityBudgetDimension Dimensions { get; }

    public ApplicationCapabilityDiscoveryCosts Required { get; }

    public ApplicationCapabilityDiscoveryCosts Available { get; }
}

public sealed class ApplicationCapabilitySemanticOutcome
{
    internal ApplicationCapabilitySemanticOutcome(
        ApplicationCapabilitySemanticScoringState state,
        ApplicationCapabilitySemanticScoringReason reason)
    {
        State = state;
        Reason = reason;
    }

    public ApplicationCapabilitySemanticScoringState State { get; }

    public ApplicationCapabilitySemanticScoringReason Reason { get; }
}

public sealed class ApplicationCapabilityDiscoveryResult
{
    internal ApplicationCapabilityDiscoveryResult(
        ApplicationCapabilityDiscoveryOutcomeState state,
        ApplicationCapabilityIndexGeneration generation,
        ApplicationCapabilitySemanticOutcome semantic,
        IReadOnlyList<ApplicationCapabilityFamilyMatch> families,
        IReadOnlyList<ApplicationCapabilityFamilySuppression> suppressions,
        ApplicationCapabilityFamilyExceedsBudget? familyExceedsBudget,
        int familiesBeyondCandidateLimit,
        bool candidateBoundaryHasNearTie)
    {
        State = state;
        Generation = generation;
        Semantic = semantic;
        Families = families;
        Suppressions = suppressions;
        FamilyExceedsBudget = familyExceedsBudget;
        FamiliesBeyondCandidateLimit = familiesBeyondCandidateLimit;
        CandidateBoundaryHasNearTie = candidateBoundaryHasNearTie;
    }

    public ApplicationCapabilityDiscoveryOutcomeState State { get; }

    public ApplicationCapabilityIndexGeneration Generation { get; }

    public ApplicationCapabilitySemanticOutcome Semantic { get; }

    public IReadOnlyList<ApplicationCapabilityFamilyMatch> Families { get; }

    public IReadOnlyList<ApplicationCapabilityFamilySuppression> Suppressions { get; }

    public ApplicationCapabilityFamilyExceedsBudget? FamilyExceedsBudget { get; }

    public int FamiliesBeyondCandidateLimit { get; }

    public bool CandidateBoundaryHasNearTie { get; }
}
