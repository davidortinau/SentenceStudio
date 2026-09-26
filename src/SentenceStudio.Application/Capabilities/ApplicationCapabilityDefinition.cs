using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.Application.Capabilities;

public interface IApplicationCapabilityDescriptor
{
    string Code { get; }

    string Family { get; }

    int MajorVersion { get; }

    string SelectionDescription { get; }

    IReadOnlyList<string> PositiveExamples { get; }

    IReadOnlyList<string> NegativeExamples { get; }

    IReadOnlyList<string> Aliases { get; }

    Type RequestType { get; }

    Type ClientResultType { get; }

    Type CoachObservationType { get; }

    IReadOnlyList<ApplicationCapabilitySurface> Surfaces { get; }

    ApplicationExecutionAuthority ExecutionAuthority { get; }

    ApplicationEffectClass Effect { get; }

    ApplicationSensitivity Sensitivity { get; }

    ApplicationConfirmationPolicy Confirmation { get; }

    ApplicationIdempotencyPolicy Idempotency { get; }

    ApplicationSynchronizationPolicy Synchronization { get; }

    ApplicationContinuationPolicy Continuation { get; }

    ApplicationCapabilityPolicyState PolicyState { get; }

    /// <summary>
    /// Declared Coach exposure policy. Candidate is not authorization or proof of runtime
    /// qualification.
    /// </summary>
    ApplicationCapabilityCoachExposurePolicy CoachExposurePolicy { get; }

    ApplicationCapabilityBudgets? Budgets { get; }

    ApplicationCapabilityRolloutState Rollout { get; }

    ApplicationCapabilityAvailabilityState Availability { get; }

    ApplicationCapabilityDependencyState Dependencies { get; }

    /// <summary>
    /// Result of static release metadata validation. It is not runtime authorization.
    /// </summary>
    ApplicationCapabilityReleaseValidationStatus ReleaseValidationStatus { get; }

    string DescriptorFingerprint { get; }

    ApplicationCapabilityQualificationCandidate? QualificationCandidate { get; }

    ApplicationCapabilityImplementationIdentity? Handler { get; }

    ApplicationCapabilityImplementationIdentity? CoachProjector { get; }
}

public sealed class ApplicationCapabilityDefinition<TRequest, TClientResult, TCoachObservation>
    : IApplicationCapabilityDescriptor
{
    private readonly ApplicationCapabilityMetadata _metadata;

    public ApplicationCapabilityDefinition(ApplicationCapabilityMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata = metadata.Snapshot();
    }

    public string Code => _metadata.Code;

    public string Family => _metadata.Family;

    public int MajorVersion => _metadata.MajorVersion;

    public string SelectionDescription => _metadata.SelectionDescription;

    public IReadOnlyList<string> PositiveExamples => _metadata.PositiveExamples;

    public IReadOnlyList<string> NegativeExamples => _metadata.NegativeExamples;

    public IReadOnlyList<string> Aliases => _metadata.Aliases;

    public Type RequestType => typeof(TRequest);

    public Type ClientResultType => typeof(TClientResult);

    public Type CoachObservationType => typeof(TCoachObservation);

    public IReadOnlyList<ApplicationCapabilitySurface> Surfaces => _metadata.Surfaces;

    public ApplicationExecutionAuthority ExecutionAuthority => _metadata.ExecutionAuthority;

    public ApplicationEffectClass Effect => _metadata.Effect;

    public ApplicationSensitivity Sensitivity => _metadata.Sensitivity;

    public ApplicationConfirmationPolicy Confirmation => _metadata.Confirmation;

    public ApplicationIdempotencyPolicy Idempotency => _metadata.Idempotency;

    public ApplicationSynchronizationPolicy Synchronization => _metadata.Synchronization;

    public ApplicationContinuationPolicy Continuation => _metadata.Continuation;

    public ApplicationCapabilityPolicyState PolicyState => _metadata.PolicyState;

    public ApplicationCapabilityCoachExposurePolicy CoachExposurePolicy =>
        _metadata.CoachExposurePolicy;

    public ApplicationCapabilityBudgets? Budgets => _metadata.Budgets;

    public ApplicationCapabilityRolloutState Rollout => _metadata.Rollout;

    public ApplicationCapabilityAvailabilityState Availability => _metadata.Availability;

    public ApplicationCapabilityDependencyState Dependencies => _metadata.Dependencies;

    public ApplicationCapabilityReleaseValidationStatus ReleaseValidationStatus =>
        _metadata.ReleaseValidationStatus;

    public string DescriptorFingerprint => _metadata.DescriptorFingerprint;

    public ApplicationCapabilityQualificationCandidate? QualificationCandidate =>
        _metadata.QualificationCandidate;

    public ApplicationCapabilityImplementationIdentity? Handler => _metadata.Handler;

    public ApplicationCapabilityImplementationIdentity? CoachProjector => _metadata.CoachProjector;
}

internal static class ApplicationCapabilityStaticValidation
{
    internal static bool HasCompleteDeclaredPolicy(IApplicationCapabilityDescriptor descriptor)
    {
        var budgets = descriptor.Budgets;

        return descriptor.CoachExposurePolicy == ApplicationCapabilityCoachExposurePolicy.Candidate
            && descriptor.Surfaces.Count > 0
            && descriptor.Surfaces.All(surface =>
                Enum.IsDefined(surface) && surface != ApplicationCapabilitySurface.Unknown)
            && descriptor.Surfaces.Contains(ApplicationCapabilitySurface.Model)
            && Enum.IsDefined(descriptor.Sensitivity)
            && descriptor.Sensitivity is not ApplicationSensitivity.Unknown
                and not ApplicationSensitivity.Secret
                and not ApplicationSensitivity.Prohibited
            && Enum.IsDefined(descriptor.ExecutionAuthority)
            && descriptor.ExecutionAuthority != ApplicationExecutionAuthority.Unknown
            && Enum.IsDefined(descriptor.Effect)
            && descriptor.Effect != ApplicationEffectClass.Unknown
            && Enum.IsDefined(descriptor.Confirmation)
            && descriptor.Confirmation != ApplicationConfirmationPolicy.Unknown
            && Enum.IsDefined(descriptor.Idempotency)
            && descriptor.Idempotency != ApplicationIdempotencyPolicy.Unknown
            && Enum.IsDefined(descriptor.Synchronization)
            && descriptor.Synchronization != ApplicationSynchronizationPolicy.Unknown
            && Enum.IsDefined(descriptor.Continuation)
            && descriptor.Continuation != ApplicationContinuationPolicy.Unknown
            && descriptor.PolicyState == ApplicationCapabilityPolicyState.Approved
            && descriptor.Handler is not null
            && descriptor.CoachProjector is not null
            && descriptor.ClientResultType != descriptor.CoachObservationType
            && budgets is not null
            && budgets.MaxCoachObservationBytes > 0
            && budgets.SchemaTokenCost > 0
            && budgets.RiskCost > 0
            && budgets.ExposureCost > 0
            && descriptor.Rollout == ApplicationCapabilityRolloutState.Enabled
            && descriptor.Availability == ApplicationCapabilityAvailabilityState.Available
            && descriptor.Dependencies == ApplicationCapabilityDependencyState.Satisfied
            && descriptor.ReleaseValidationStatus
                == ApplicationCapabilityReleaseValidationStatus.Passed;
    }
}
