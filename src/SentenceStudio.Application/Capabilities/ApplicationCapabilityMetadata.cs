using System.Collections.ObjectModel;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.Application.Capabilities;

public enum ApplicationCapabilityPolicyState
{
    Unknown = 0,
    Incomplete = 1,
    Approved = 2
}

public enum ApplicationCapabilityRolloutState
{
    Unknown = 0,
    Disabled = 1,
    Enabled = 2
}

public enum ApplicationCapabilityAvailabilityState
{
    Unknown = 0,
    Unavailable = 1,
    Available = 2
}

public enum ApplicationCapabilityDependencyState
{
    Unknown = 0,
    Unsatisfied = 1,
    Satisfied = 2
}

public enum ApplicationCapabilityCoachExposurePolicy
{
    Unknown = 0,
    Denied = 1,
    Candidate = 2
}

public enum ApplicationCapabilityReleaseValidationStatus
{
    Unknown = 0,
    Failed = 1,
    Passed = 2
}

public sealed record ApplicationCapabilityBudgets(
    int MaxRequestBytes,
    int MaxClientResultBytes,
    int MaxCoachObservationBytes,
    int SchemaTokenCost,
    int RiskCost,
    int ExposureCost,
    int MaxExecutionMilliseconds,
    int MaxContinuationDepth);

public sealed record ApplicationCapabilityImplementationIdentity
{
    private ApplicationCapabilityImplementationIdentity(Type? contractType, string? deferredIdentity)
    {
        ContractType = contractType;
        DeferredIdentity = deferredIdentity;
    }

    public Type? ContractType { get; }

    public string? DeferredIdentity { get; }

    public static ApplicationCapabilityImplementationIdentity ExactContract(Type contractType) =>
        new(contractType ?? throw new ArgumentNullException(nameof(contractType)), null);

    public static ApplicationCapabilityImplementationIdentity Deferred(string identity) =>
        new(null, identity ?? throw new ArgumentNullException(nameof(identity)));
}

public sealed record ApplicationCapabilityQualificationCandidate
{
    internal ApplicationCapabilityQualificationCandidate(
        string descriptorFingerprint,
        Type handlerContractType,
        Type coachProjectorContractType,
        string qualificationReportFingerprint)
    {
        DescriptorFingerprint = descriptorFingerprint
            ?? throw new ArgumentNullException(nameof(descriptorFingerprint));
        HandlerContractType = handlerContractType
            ?? throw new ArgumentNullException(nameof(handlerContractType));
        CoachProjectorContractType = coachProjectorContractType
            ?? throw new ArgumentNullException(nameof(coachProjectorContractType));
        QualificationReportFingerprint = qualificationReportFingerprint
            ?? throw new ArgumentNullException(nameof(qualificationReportFingerprint));
    }

    public string DescriptorFingerprint { get; }

    public Type HandlerContractType { get; }

    public Type CoachProjectorContractType { get; }

    public string QualificationReportFingerprint { get; }

    internal bool MatchesExactQualification(
        string descriptorFingerprint,
        Type handlerContractType,
        Type coachProjectorContractType,
        string qualificationReportFingerprint) =>
        string.Equals(DescriptorFingerprint, descriptorFingerprint, StringComparison.Ordinal)
        && HandlerContractType == handlerContractType
        && CoachProjectorContractType == coachProjectorContractType
        && string.Equals(
            QualificationReportFingerprint,
            qualificationReportFingerprint,
            StringComparison.Ordinal);
}

public sealed record ApplicationCapabilityMetadata
{
    public required string Code { get; init; }

    public required string Family { get; init; }

    public required int MajorVersion { get; init; }

    public required string SelectionDescription { get; init; }

    public IReadOnlyList<string> PositiveExamples { get; init; } = [];

    public IReadOnlyList<string> NegativeExamples { get; init; } = [];

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public IReadOnlyList<ApplicationCapabilitySurface> Surfaces { get; init; } = [];

    public required ApplicationExecutionAuthority ExecutionAuthority { get; init; }

    public required ApplicationEffectClass Effect { get; init; }

    public required ApplicationSensitivity Sensitivity { get; init; }

    public required ApplicationConfirmationPolicy Confirmation { get; init; }

    public required ApplicationIdempotencyPolicy Idempotency { get; init; }

    public required ApplicationSynchronizationPolicy Synchronization { get; init; }

    public required ApplicationContinuationPolicy Continuation { get; init; }

    public required ApplicationCapabilityPolicyState PolicyState { get; init; }

    /// <summary>
    /// Declared Coach exposure policy. Candidate is a static catalog declaration only and does
    /// not authorize model exposure.
    /// </summary>
    public required ApplicationCapabilityCoachExposurePolicy CoachExposurePolicy { get; init; }

    public ApplicationCapabilityBudgets? Budgets { get; init; }

    public required ApplicationCapabilityRolloutState Rollout { get; init; }

    public required ApplicationCapabilityAvailabilityState Availability { get; init; }

    public required ApplicationCapabilityDependencyState Dependencies { get; init; }

    /// <summary>
    /// Result of static release metadata validation only; trusted runtime verification remains
    /// outside this layer.
    /// </summary>
    public required ApplicationCapabilityReleaseValidationStatus ReleaseValidationStatus { get; init; }

    public required string DescriptorFingerprint { get; init; }

    public ApplicationCapabilityQualificationCandidate? QualificationCandidate { get; init; }

    public ApplicationCapabilityImplementationIdentity? Handler { get; init; }

    public ApplicationCapabilityImplementationIdentity? CoachProjector { get; init; }

    internal ApplicationCapabilityMetadata Snapshot() => this with
    {
        PositiveExamples = Copy(PositiveExamples),
        NegativeExamples = Copy(NegativeExamples),
        Aliases = Copy(Aliases),
        Surfaces = Copy(Surfaces)
    };

    private static IReadOnlyList<T> Copy<T>(IEnumerable<T>? values) =>
        new ReadOnlyCollection<T>((values ?? []).ToArray());
}
