using System.Reflection;
using FluentAssertions;
using SentenceStudio.Application.Capabilities;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.UnitTests.ApplicationCapabilities;

public sealed class ApplicationCapabilityDefaultDenyTests
{
    public static TheoryData<string, ApplicationCapabilityMetadata> DeniedMutants => new()
    {
        {
            "unknown Coach exposure policy",
            ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
            {
                CoachExposurePolicy = ApplicationCapabilityCoachExposurePolicy.Unknown
            }
        },
        {
            "missing Coach surface",
            ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
            {
                Surfaces = [ApplicationCapabilitySurface.UserInterface]
            }
        },
        {
            "unknown surface",
            ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
            {
                Surfaces = [ApplicationCapabilitySurface.Unknown]
            }
        },
        {
            "unknown sensitivity",
            ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
            {
                Sensitivity = ApplicationSensitivity.Unknown
            }
        },
        {
            "unknown policy",
            ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
            {
                PolicyState = ApplicationCapabilityPolicyState.Unknown
            }
        },
        {
            "missing projector",
            ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
            {
                CoachProjector = null
            }
        },
        {
            "unknown release validation",
            ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
            {
                ReleaseValidationStatus = ApplicationCapabilityReleaseValidationStatus.Unknown
            }
        },
        {
            "missing budgets",
            ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
            {
                Budgets = null
            }
        },
        {
            "disabled rollout",
            ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
            {
                Rollout = ApplicationCapabilityRolloutState.Disabled,
                ReleaseValidationStatus = ApplicationCapabilityReleaseValidationStatus.Failed
            }
        },
        {
            "unavailable dependency",
            ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
            {
                Dependencies = ApplicationCapabilityDependencyState.Unsatisfied,
                ReleaseValidationStatus = ApplicationCapabilityReleaseValidationStatus.Failed
            }
        }
    };

    [Theory]
    [MemberData(nameof(DeniedMutants))]
    public void Default_denied_policy_accepts_incomplete_static_metadata(
        string reason,
        ApplicationCapabilityMetadata metadata)
    {
        var descriptor = Define(metadata);

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().NotThrow(reason);
        descriptor.CoachExposurePolicy.Should().NotBe(
            ApplicationCapabilityCoachExposurePolicy.Candidate,
            reason);
    }

    [Fact]
    public void Model_surface_does_not_change_default_denied_policy()
    {
        var descriptor = Define(ApplicationCapabilityFixtures.DefaultDeniedMetadata());

        descriptor.Surfaces.Should().Contain(ApplicationCapabilitySurface.Model);
        descriptor.CoachExposurePolicy.Should().Be(
            ApplicationCapabilityCoachExposurePolicy.Denied);
    }

    [Fact]
    public void Deferred_identity_strings_do_not_satisfy_static_candidate_validation()
    {
        var metadata = ApplicationCapabilityFixtures.StaticCandidateMetadata() with
        {
            Handler = ApplicationCapabilityImplementationIdentity.Deferred(
                "application.plan-today-read.handler"),
            CoachProjector = ApplicationCapabilityImplementationIdentity.Deferred(
                "application.plan-today-read.coach-projector"),
            QualificationCandidate = null
        };
        var descriptor = Define(metadata);

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*static qualification candidate*");
    }

    [Fact]
    public void Static_candidate_exposes_only_declared_policy_and_validation_metadata()
    {
        var descriptor = Define(ApplicationCapabilityFixtures.StaticCandidateMetadata());

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().NotThrow();
        descriptor.CoachExposurePolicy.Should().Be(
            ApplicationCapabilityCoachExposurePolicy.Candidate);
        descriptor.ReleaseValidationStatus.Should().Be(
            ApplicationCapabilityReleaseValidationStatus.Passed);
        descriptor.QualificationCandidate.Should().NotBeNull();
    }

    [Fact]
    public void Public_interfaces_contain_no_final_eligibility_semantics()
    {
        var applicationAssembly = typeof(IApplicationCapabilityCatalog).Assembly;
        var definitionType = typeof(ApplicationCapabilityDefinition<
            ApplicationCapabilityQueryRequest,
            ApplicationStateVersion,
            CoachObservationEnvelope>);
        var interfaceMembers = new[]
            {
                typeof(IApplicationCapabilityDescriptor),
                typeof(IApplicationCapabilityCatalog)
            }
            .SelectMany(type => type.GetMembers())
            .ToArray();

        applicationAssembly.GetExportedTypes()
            .Select(type => type.Name)
            .Should().NotContain("ApplicationCapabilityVerificationContext");
        applicationAssembly.GetExportedTypes()
            .Select(type => type.Name)
            .Should().NotContain("ApplicationCapabilityQualificationEvidence");
        applicationAssembly.GetExportedTypes()
            .Select(type => type.Name)
            .Should().NotContain("ApplicationCapabilityRuntimeEligibilityDecision");
        applicationAssembly.GetExportedTypes()
            .Select(type => type.Name)
            .Should().NotContain(
                name => name.Contains("Eligibility", StringComparison.OrdinalIgnoreCase),
                "the Application assembly must not export a final eligibility state");
        interfaceMembers
            .Select(member => member.Name)
            .Should().NotContain(
                name => name.Contains("Eligible", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Eligibility", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("ModelQualified", StringComparison.OrdinalIgnoreCase),
                "catalog contracts must not publish a forgeable final decision");
        typeof(IApplicationCapabilityDescriptor)
            .GetProperties()
            .Should().NotContain(property =>
                property.PropertyType == typeof(ApplicationModelExposureState));
        typeof(IApplicationCapabilityDescriptor)
            .GetProperties()
            .Where(property => property.PropertyType == typeof(bool))
            .Should().BeEmpty(
                "a public descriptor boolean could be mistaken for a final runtime decision");
        definitionType
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(member => member.Name)
            .Should().NotContain(
                name => name.Contains("Eligible", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Eligibility", StringComparison.OrdinalIgnoreCase),
                "the concrete descriptor must not add a final decision outside its interface");
        typeof(ApplicationCapabilityQualificationCandidate).GetConstructors()
            .Should().BeEmpty(
                "qualification candidates are catalog-owned metadata, not caller assertions");
        typeof(ApplicationCapabilityQualificationCandidate)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.ReturnType == typeof(ApplicationCapabilityQualificationCandidate))
            .Should().BeEmpty(
                "there must be no public qualification factory accepting caller-supplied evidence");
        definitionType.GetConstructors()
            .Should().ContainSingle()
            .Which.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Should().Equal(typeof(ApplicationCapabilityMetadata));
    }

    [Fact]
    public void Frozen_catalog_returns_static_metadata_without_a_final_decision()
    {
        IApplicationCapabilityCatalog catalog = new ApplicationCapabilityCatalogBuilder()
            .Add(ApplicationCapabilityFixtures.StaticCandidateDefinition())
            .Freeze();

        catalog.All.Should().ContainSingle();
        catalog.TryGet("plan.today.read", 1, out var descriptor).Should().BeTrue();
        descriptor.Should().NotBeNull();
        descriptor!.CoachExposurePolicy.Should().Be(
            ApplicationCapabilityCoachExposurePolicy.Candidate);
        descriptor.ReleaseValidationStatus.Should().Be(
            ApplicationCapabilityReleaseValidationStatus.Passed);
        typeof(IApplicationCapabilityCatalog)
            .GetProperties()
            .Select(property => property.Name)
            .Should().Equal(nameof(IApplicationCapabilityCatalog.All));
    }

    [Fact]
    public void External_fake_catalog_cannot_express_a_final_eligibility_decision()
    {
        IApplicationCapabilityDescriptor descriptor = new ExternalFakeDescriptor(
            ApplicationCapabilityFixtures.StaticCandidateDefinition());
        IApplicationCapabilityCatalog catalog = new ExternalFakeCatalog(descriptor);

        catalog.All.Should().ContainSingle();
        catalog.TryGet(descriptor.Code, descriptor.MajorVersion, out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(descriptor);
        descriptor.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Should().NotContain(property =>
                property.PropertyType == typeof(bool)
                || property.PropertyType == typeof(ApplicationModelExposureState)
                || property.Name.Contains("Eligible", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Eligibility", StringComparison.OrdinalIgnoreCase));
        typeof(IApplicationCapabilityDescriptor)
            .GetMethods()
            .Where(method => !method.IsSpecialName)
            .Should().BeEmpty("a static descriptor cannot execute or expose a capability");
        typeof(IApplicationCapabilityCatalog)
            .GetMethods()
            .Where(method => !method.IsSpecialName
                && method.Name != nameof(IApplicationCapabilityCatalog.TryGet))
            .Should().BeEmpty("a static catalog can only perform descriptor lookup");
    }

    private sealed class ExternalFakeDescriptor(IApplicationCapabilityDescriptor source)
        : IApplicationCapabilityDescriptor
    {
        public string Code => source.Code;
        public string Family => source.Family;
        public int MajorVersion => source.MajorVersion;
        public string SelectionDescription => source.SelectionDescription;
        public IReadOnlyList<string> PositiveExamples => source.PositiveExamples;
        public IReadOnlyList<string> NegativeExamples => source.NegativeExamples;
        public IReadOnlyList<string> Aliases => source.Aliases;
        public Type RequestType => source.RequestType;
        public Type ClientResultType => source.ClientResultType;
        public Type CoachObservationType => source.CoachObservationType;
        public IReadOnlyList<ApplicationCapabilitySurface> Surfaces => source.Surfaces;
        public ApplicationExecutionAuthority ExecutionAuthority => source.ExecutionAuthority;
        public ApplicationEffectClass Effect => source.Effect;
        public ApplicationSensitivity Sensitivity => source.Sensitivity;
        public ApplicationConfirmationPolicy Confirmation => source.Confirmation;
        public ApplicationIdempotencyPolicy Idempotency => source.Idempotency;
        public ApplicationSynchronizationPolicy Synchronization => source.Synchronization;
        public ApplicationContinuationPolicy Continuation => source.Continuation;
        public ApplicationCapabilityPolicyState PolicyState => source.PolicyState;
        public ApplicationCapabilityCoachExposurePolicy CoachExposurePolicy =>
            source.CoachExposurePolicy;
        public ApplicationCapabilityBudgets? Budgets => source.Budgets;
        public ApplicationCapabilityRolloutState Rollout => source.Rollout;
        public ApplicationCapabilityAvailabilityState Availability => source.Availability;
        public ApplicationCapabilityDependencyState Dependencies => source.Dependencies;
        public ApplicationCapabilityReleaseValidationStatus ReleaseValidationStatus =>
            source.ReleaseValidationStatus;
        public string DescriptorFingerprint => source.DescriptorFingerprint;
        public ApplicationCapabilityQualificationCandidate? QualificationCandidate =>
            source.QualificationCandidate;
        public ApplicationCapabilityImplementationIdentity? Handler => source.Handler;
        public ApplicationCapabilityImplementationIdentity? CoachProjector => source.CoachProjector;
    }

    private sealed class ExternalFakeCatalog(IApplicationCapabilityDescriptor descriptor)
        : IApplicationCapabilityCatalog
    {
        public IReadOnlyList<IApplicationCapabilityDescriptor> All { get; } = [descriptor];

        public bool TryGet(
            string codeOrAlias,
            int majorVersion,
            out IApplicationCapabilityDescriptor? resolved)
        {
            var found = string.Equals(codeOrAlias, descriptor.Code, StringComparison.Ordinal)
                && majorVersion == descriptor.MajorVersion;
            resolved = found ? descriptor : null;
            return found;
        }
    }

    private static ApplicationCapabilityDefinition<
        ApplicationCapabilityQueryRequest,
        ApplicationStateVersion,
        CoachObservationEnvelope> Define(ApplicationCapabilityMetadata metadata) => new(metadata);

    private static Action Invoking(Action action) => action;
}
