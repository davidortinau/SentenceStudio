using SentenceStudio.Application.Capabilities;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.UnitTests.ApplicationCapabilities;

internal static class ApplicationCapabilityFixtures
{
    internal const string DescriptorFingerprint =
        "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    internal const string QualificationReportFingerprint =
        "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    internal interface IVerifiedHandler;

    internal interface IVerifiedCoachProjector;

    internal static ApplicationCapabilityMetadata StaticCandidateMetadata(string code = "plan.today.read") => new()
    {
        Code = code,
        Family = "plan-and-practice-history",
        MajorVersion = 1,
        SelectionDescription = "Read the learner's approved aggregate plan state.",
        PositiveExamples = ["Show my plan for today."],
        NegativeExamples = ["Replace my plan for today."],
        Surfaces = [ApplicationCapabilitySurface.UserInterface, ApplicationCapabilitySurface.Model],
        ExecutionAuthority = ApplicationExecutionAuthority.Server,
        Effect = ApplicationEffectClass.Read,
        Sensitivity = ApplicationSensitivity.Aggregate,
        Confirmation = ApplicationConfirmationPolicy.None,
        Idempotency = ApplicationIdempotencyPolicy.NotApplicable,
        Synchronization = ApplicationSynchronizationPolicy.OnlineOnly,
        Continuation = ApplicationContinuationPolicy.None,
        PolicyState = ApplicationCapabilityPolicyState.Approved,
        CoachExposurePolicy = ApplicationCapabilityCoachExposurePolicy.Candidate,
        Budgets = new(
            MaxRequestBytes: 4_096,
            MaxClientResultBytes: 32_768,
            MaxCoachObservationBytes: 8_192,
            SchemaTokenCost: 512,
            RiskCost: 1,
            ExposureCost: 1,
            MaxExecutionMilliseconds: 5_000,
            MaxContinuationDepth: 0),
        Rollout = ApplicationCapabilityRolloutState.Enabled,
        Availability = ApplicationCapabilityAvailabilityState.Available,
        Dependencies = ApplicationCapabilityDependencyState.Satisfied,
        ReleaseValidationStatus = ApplicationCapabilityReleaseValidationStatus.Passed,
        DescriptorFingerprint = DescriptorFingerprint,
        QualificationCandidate = CreateQualificationCandidate(
            DescriptorFingerprint,
            typeof(IVerifiedHandler),
            typeof(IVerifiedCoachProjector),
            QualificationReportFingerprint),
        Handler = ApplicationCapabilityImplementationIdentity.ExactContract(typeof(IVerifiedHandler)),
        CoachProjector = ApplicationCapabilityImplementationIdentity.ExactContract(
            typeof(IVerifiedCoachProjector))
    };

    internal static ApplicationCapabilityDefinition<
        ApplicationCapabilityQueryRequest,
        ApplicationStateVersion,
        CoachObservationEnvelope> StaticCandidateDefinition(
            string code = "plan.today.read") =>
        new(StaticCandidateMetadata(code));

    internal static ApplicationCapabilityMetadata DefaultDeniedMetadata(string code = "plan.today.read") =>
        StaticCandidateMetadata(code) with
        {
            CoachExposurePolicy = ApplicationCapabilityCoachExposurePolicy.Denied,
            QualificationCandidate = null,
            CoachProjector = null
        };

    internal static ApplicationCapabilityQualificationCandidate CreateQualificationCandidate(
        string descriptorFingerprint = DescriptorFingerprint,
        Type? handlerContractType = null,
        Type? coachProjectorContractType = null,
        string qualificationReportFingerprint = QualificationReportFingerprint) =>
        (ApplicationCapabilityQualificationCandidate)Activator.CreateInstance(
            typeof(ApplicationCapabilityQualificationCandidate),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [
                descriptorFingerprint,
                handlerContractType ?? typeof(IVerifiedHandler),
                coachProjectorContractType ?? typeof(IVerifiedCoachProjector),
                qualificationReportFingerprint
            ],
            culture: null)!;
}
