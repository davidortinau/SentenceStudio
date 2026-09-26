using FluentAssertions;
using SentenceStudio.Application.Capabilities;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.UnitTests.ApplicationCapabilities;

public sealed class ApplicationCapabilityValidationTests
{
    [Fact]
    public void Static_candidate_exposes_exact_declared_contract_types()
    {
        IApplicationCapabilityDescriptor descriptor =
            ApplicationCapabilityFixtures.StaticCandidateDefinition();

        descriptor.RequestType.Should().Be(typeof(ApplicationCapabilityQueryRequest));
        descriptor.ClientResultType.Should().Be(typeof(ApplicationStateVersion));
        descriptor.CoachObservationType.Should().Be(typeof(CoachObservationEnvelope));
        descriptor.CoachExposurePolicy.Should().Be(
            ApplicationCapabilityCoachExposurePolicy.Candidate);
        descriptor.ReleaseValidationStatus.Should().Be(
            ApplicationCapabilityReleaseValidationStatus.Passed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPPER.case")]
    [InlineData("a")]
    [InlineData("plan..read")]
    [InlineData("plan.read-")]
    [InlineData("plan_read")]
    public void Invalid_capability_codes_are_rejected(string code)
    {
        var descriptor = Define(
            ApplicationCapabilityFixtures.StaticCandidateMetadata() with
            {
                Code = code
            });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*Code*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("A-family")]
    [InlineData("ab")]
    [InlineData("family.name")]
    [InlineData("family--name")]
    public void Invalid_family_names_are_rejected(string family)
    {
        var descriptor = Define(
            ApplicationCapabilityFixtures.StaticCandidateMetadata() with
            {
                Family = family
            });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*Family*");
    }

    [Fact]
    public void Bounded_code_and_family_lengths_are_enforced()
    {
        var overlongCode = new string('a', ApplicationCapabilityDescriptorValidator.MaximumCodeLength + 1);
        var overlongFamily = new string('a', ApplicationCapabilityDescriptorValidator.MaximumFamilyLength + 1);

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(
                Define(ApplicationCapabilityFixtures.StaticCandidateMetadata() with
                {
                    Code = overlongCode
                })))
            .Should().Throw<ApplicationCapabilityValidationException>();
        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(
                Define(ApplicationCapabilityFixtures.StaticCandidateMetadata() with
                {
                    Family = overlongFamily
                })))
            .Should().Throw<ApplicationCapabilityValidationException>();
    }

    [Fact]
    public void Major_version_must_be_positive()
    {
        var descriptor = Define(
            ApplicationCapabilityFixtures.StaticCandidateMetadata() with
            {
                MajorVersion = 0
            });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*positive*");
    }

    [Fact]
    public void Selection_examples_are_required_and_bounded()
    {
        var noPositiveExamples = Define(
            ApplicationCapabilityFixtures.StaticCandidateMetadata() with
            {
                PositiveExamples = []
            });
        var tooManyNegativeExamples = Define(
            ApplicationCapabilityFixtures.StaticCandidateMetadata() with
            {
                NegativeExamples = Enumerable.Range(1, 9).Select(i => $"negative {i}").ToArray()
            });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(noPositiveExamples))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*PositiveExamples*");
        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(tooManyNegativeExamples))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*NegativeExamples*");
    }

    [Fact]
    public void Handler_identity_is_required_and_must_be_closed()
    {
        var missing = Define(
            ApplicationCapabilityFixtures.DefaultDeniedMetadata() with { Handler = null });
        var open = Define(ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
        {
            Handler = ApplicationCapabilityImplementationIdentity.ExactContract(typeof(IHandler<>))
        });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(missing))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*Handler*required*");
        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(open))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*closed*");
    }

    [Fact]
    public void Exact_handler_identity_rejects_non_contract_types()
    {
        var descriptor = Define(ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
        {
            Handler = ApplicationCapabilityImplementationIdentity.ExactContract(typeof(string))
        });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*closed interface*");
    }

    [Fact]
    public void Exact_closed_handler_contract_identity_is_accepted()
    {
        var descriptor = Define(ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
        {
            Handler = ApplicationCapabilityImplementationIdentity.ExactContract(typeof(IHandler))
        });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().NotThrow();
        descriptor.Handler!.ContractType.Should().Be(typeof(IHandler));
        descriptor.Handler.DeferredIdentity.Should().BeNull();
    }

    [Fact]
    public void Duplicate_or_unknown_surfaces_are_fail_closed()
    {
        var duplicate = Define(ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
        {
            Surfaces = [ApplicationCapabilitySurface.UserInterface, ApplicationCapabilitySurface.UserInterface]
        });
        var unknown = Define(ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
        {
            Surfaces = [(ApplicationCapabilitySurface)999]
        });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(duplicate))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*more than once*");
        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(unknown))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*unsupported*");
    }

    [Fact]
    public void Declared_coach_exposure_candidate_requires_model_surface()
    {
        var descriptor = Define(ApplicationCapabilityFixtures.StaticCandidateMetadata() with
        {
            Surfaces = [ApplicationCapabilitySurface.UserInterface]
        });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*requires the Model surface*");
    }

    [Fact]
    public void Unknown_surface_fails_static_candidate_validation()
    {
        var descriptor = Define(ApplicationCapabilityFixtures.StaticCandidateMetadata() with
        {
            Surfaces = [ApplicationCapabilitySurface.Model, ApplicationCapabilitySurface.Unknown]
        });
        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*Coach exposure candidate*");
    }

    [Fact]
    public void Declared_coach_exposure_candidate_requires_a_distinct_observation_contract()
    {
        var descriptor = new ApplicationCapabilityDefinition<
            ApplicationCapabilityQueryRequest,
            ApplicationLimitation,
            ApplicationLimitation>(
                ApplicationCapabilityFixtures.StaticCandidateMetadata());

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationContractGraphException>()
            .WithMessage("*client-result contract node*");
    }

    [Fact]
    public void Coach_observation_rejects_a_nested_client_result_contract_node()
    {
        var descriptor = new ApplicationCapabilityDefinition<
            ApplicationCapabilityQueryRequest,
            ApplicationOperationReceipt,
            CoachObservationEnvelope>(
                ApplicationCapabilityFixtures.StaticCandidateMetadata());

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationContractGraphException>()
            .WithMessage("*ApplicationCapabilityReference*");
    }

    [Fact]
    public void Separated_recursive_client_and_coach_graphs_are_accepted()
    {
        var descriptor = new ApplicationCapabilityDefinition<
            ApplicationCapabilityQueryRequest,
            ApplicationStateVersion,
            CoachObservationEnvelope>(
                ApplicationCapabilityFixtures.StaticCandidateMetadata());

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().NotThrow();
        ApplicationContractGraphValidator
            .CollectReachableContractNodes(descriptor.ClientResultType, "Client result")
            .Should().NotIntersectWith(
                ApplicationContractGraphValidator.CollectReachableContractNodes(
                    descriptor.CoachObservationType,
                    "Coach observation"));
    }

    [Theory]
    [InlineData(ApplicationSensitivity.Secret)]
    [InlineData(ApplicationSensitivity.Prohibited)]
    public void Protected_sensitivity_cannot_become_a_coach_exposure_candidate(
        ApplicationSensitivity sensitivity)
    {
        var descriptor = Define(
            ApplicationCapabilityFixtures.StaticCandidateMetadata() with
            {
                Sensitivity = sensitivity
            });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*cannot become Coach exposure candidates*");
    }

    [Fact]
    public void Declared_coach_exposure_candidate_requires_a_projector()
    {
        var descriptor = Define(
            ApplicationCapabilityFixtures.StaticCandidateMetadata() with
            {
                CoachProjector = null
            });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*projector*");
    }

    [Fact]
    public void Passed_release_validation_requires_all_static_gates()
    {
        var descriptor = Define(ApplicationCapabilityFixtures.StaticCandidateMetadata() with
        {
            Dependencies = ApplicationCapabilityDependencyState.Unsatisfied
        });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*Passed release validation*");
    }

    [Theory]
    [InlineData(ApplicationEffectClass.Write)]
    [InlineData(ApplicationEffectClass.Launch)]
    [InlineData(ApplicationEffectClass.ExternalEffect)]
    [InlineData(ApplicationEffectClass.Composite)]
    public void Consequential_effects_require_confirmation_and_idempotency(ApplicationEffectClass effect)
    {
        var noConfirmation = Define(ConsequentialMetadata(effect) with
        {
            Confirmation = ApplicationConfirmationPolicy.None
        });
        var noIdempotency = Define(ConsequentialMetadata(effect) with
        {
            Idempotency = ApplicationIdempotencyPolicy.NotApplicable
        });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(noConfirmation))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*confirmation*");
        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(noIdempotency))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*idempotency*");
    }

    [Theory]
    [InlineData(ApplicationEffectClass.ExternalEffect)]
    [InlineData(ApplicationEffectClass.Composite)]
    public void Hard_effects_require_keyed_idempotency(ApplicationEffectClass effect)
    {
        var descriptor = Define(ConsequentialMetadata(effect) with
        {
            Idempotency = ApplicationIdempotencyPolicy.NaturallyIdempotent
        });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*keyed idempotency*");
    }

    [Theory]
    [InlineData(ApplicationEffectClass.Write)]
    [InlineData(ApplicationEffectClass.Launch)]
    [InlineData(ApplicationEffectClass.ExternalEffect)]
    [InlineData(ApplicationEffectClass.Composite)]
    public void Automatic_resume_is_rejected_for_unsafe_effects(ApplicationEffectClass effect)
    {
        var descriptor = Define(ConsequentialMetadata(effect) with
        {
            Continuation = ApplicationContinuationPolicy.OneAutomaticResume,
            Budgets = ApplicationCapabilityFixtures.StaticCandidateMetadata().Budgets! with
            {
                MaxContinuationDepth = 1
            }
        });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*only for read effects*");
    }

    [Theory]
    [InlineData(ApplicationContinuationPolicy.None, 1)]
    [InlineData(ApplicationContinuationPolicy.ClarificationOnly, 0)]
    [InlineData(ApplicationContinuationPolicy.OneAutomaticResume, 0)]
    [InlineData(ApplicationContinuationPolicy.OneAutomaticResume, 2)]
    public void Continuation_depth_is_explicitly_bounded(
        ApplicationContinuationPolicy continuation,
        int depth)
    {
        var descriptor = Define(ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
        {
            Continuation = continuation,
            Budgets = ApplicationCapabilityFixtures.StaticCandidateMetadata().Budgets! with
            {
                MaxContinuationDepth = depth
            }
        });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>();
    }

    [Fact]
    public void Continuation_without_a_budget_is_rejected_as_unbounded()
    {
        var descriptor = Define(ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
        {
            Continuation = ApplicationContinuationPolicy.ClarificationOnly,
            Budgets = null
        });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*bounded continuation budget*");
    }

    [Fact]
    public void Payload_schema_risk_exposure_and_latency_budgets_are_bounded()
    {
        var baseline = ApplicationCapabilityFixtures.StaticCandidateMetadata().Budgets!;
        var invalidBudgets = new[]
        {
            baseline with { MaxRequestBytes = 0 },
            baseline with { MaxClientResultBytes = 1_048_577 },
            baseline with { MaxCoachObservationBytes = -1 },
            baseline with { SchemaTokenCost = 0 },
            baseline with { RiskCost = 100_001 },
            baseline with { ExposureCost = 0 },
            baseline with { MaxExecutionMilliseconds = 120_001 }
        };

        foreach (var budget in invalidBudgets)
        {
            var descriptor = Define(
                ApplicationCapabilityFixtures.DefaultDeniedMetadata() with { Budgets = budget });
            Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
                .Should().Throw<ApplicationCapabilityValidationException>()
                .WithMessage("*budgets*");
        }
    }

    [Fact]
    public void Execution_authority_is_a_single_required_scalar()
    {
        typeof(IApplicationCapabilityDescriptor)
            .GetProperty(nameof(IApplicationCapabilityDescriptor.ExecutionAuthority))!
            .PropertyType.Should().Be(typeof(ApplicationExecutionAuthority));

        var unknown = Define(ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
        {
            ExecutionAuthority = ApplicationExecutionAuthority.Unknown
        });

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(unknown))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*ExecutionAuthority*");
    }

    [Fact]
    public void Contract_graph_accepts_the_approved_app_operation_boundary_recursively()
    {
        Invoking(() => ApplicationContractGraphValidator.Validate(
                typeof(ApplicationOperationReceipt),
                "Client result"))
            .Should().NotThrow();
    }

    [Fact]
    public void Every_approved_app_operation_object_contract_has_a_closed_recursive_graph()
    {
        var contractTypes = typeof(AppOperationWireSurface).Assembly
            .GetTypes()
            .Where(type => type.IsPublic
                && type.IsClass
                && type.IsSealed
                && !type.IsAbstract
                && string.Equals(type.Namespace, AppOperationWireSurface.Namespace, StringComparison.Ordinal))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        contractTypes.Select(type => type.Name).Should().Equal(
            "ApplicationCapabilityCommandRequest",
            "ApplicationCapabilityDefinition",
            "ApplicationCapabilityIdentity",
            "ApplicationCapabilityQueryRequest",
            "ApplicationCapabilityReference",
            "ApplicationCapabilityVersion",
            "ApplicationContinuation",
            "ApplicationLimitation",
            "ApplicationOperationConfirmationReference",
            "ApplicationOperationDecisionReference",
            "ApplicationOperationProposal",
            "ApplicationOperationReceipt",
            "ApplicationStateVersion",
            "ClientAction",
            "CoachObservationEnvelope",
            "CoachObservationReference",
            "LaunchActivityClientAction",
            "RefreshDomainViewClientAction",
            "ReloadLearnerContextClientAction");

        foreach (var contractType in contractTypes)
        {
            Invoking(() => ApplicationContractGraphValidator.Validate(contractType, "Approved"))
                .Should().NotThrow(contractType.Name);
        }
    }

    [Theory]
    [InlineData(typeof(object))]
    [InlineData(typeof(Type))]
    [InlineData(typeof(List<>))]
    [InlineData(typeof(Dictionary<string, object>))]
    [InlineData(typeof(UnsupportedContract))]
    public void Contract_graph_rejects_open_runtime_shaped_or_out_of_boundary_types(Type type)
    {
        Invoking(() => ApplicationContractGraphValidator.Validate(type, "Request"))
            .Should().Throw<ApplicationContractGraphException>();
    }

    [Fact]
    public void Definition_snapshots_all_caller_owned_collections()
    {
        var examples = new List<string> { "Show my plan." };
        var surfaces = new List<ApplicationCapabilitySurface>
        {
            ApplicationCapabilitySurface.UserInterface,
            ApplicationCapabilitySurface.Model
        };
        var metadata = ApplicationCapabilityFixtures.StaticCandidateMetadata() with
        {
            PositiveExamples = examples,
            Surfaces = surfaces
        };

        var descriptor = Define(metadata);
        examples.Add("Mutated later.");
        surfaces.Clear();

        descriptor.PositiveExamples.Should().Equal("Show my plan.");
        descriptor.Surfaces.Should().Equal(
            ApplicationCapabilitySurface.UserInterface,
            ApplicationCapabilitySurface.Model);
        Invoking(() => ((IList<string>)descriptor.PositiveExamples).Add("Cannot mutate"))
            .Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Definition_preserves_descriptor_and_qualification_report_fingerprints()
    {
        var descriptor = Define(ApplicationCapabilityFixtures.StaticCandidateMetadata());

        descriptor.DescriptorFingerprint.Should().Be(ApplicationCapabilityFixtures.DescriptorFingerprint);
        descriptor.QualificationCandidate.Should().BeEquivalentTo(
            ApplicationCapabilityFixtures.CreateQualificationCandidate());
    }

    [Fact]
    public void Mismatched_descriptor_fingerprint_is_rejected()
    {
        var metadata = ApplicationCapabilityFixtures.StaticCandidateMetadata() with
        {
            DescriptorFingerprint =
                "sha256:3333333333333333333333333333333333333333333333333333333333333333"
        };
        var descriptor = Define(metadata);

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*descriptor fingerprint does not match*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:not-a-real-fingerprint")]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sha512:2222222222222222222222222222222222222222222222222222222222222222")]
    public void Tampered_qualification_report_fingerprint_is_rejected(string fingerprint)
    {
        var metadata = ApplicationCapabilityFixtures.StaticCandidateMetadata() with
        {
            QualificationCandidate = ApplicationCapabilityFixtures.CreateQualificationCandidate(
                qualificationReportFingerprint: fingerprint)
        };
        var descriptor = Define(metadata);

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*QualificationReportFingerprint*canonical lower-case SHA-256*");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mismatched_qualification_candidate_contract_type_is_rejected(bool mismatchHandler)
    {
        var metadata = ApplicationCapabilityFixtures.StaticCandidateMetadata() with
        {
            QualificationCandidate = ApplicationCapabilityFixtures.CreateQualificationCandidate(
                handlerContractType: mismatchHandler
                    ? typeof(IMismatchedContract)
                    : typeof(ApplicationCapabilityFixtures.IVerifiedHandler),
                coachProjectorContractType: mismatchHandler
                    ? typeof(ApplicationCapabilityFixtures.IVerifiedCoachProjector)
                    : typeof(IMismatchedContract))
        };
        var descriptor = Define(metadata);

        Invoking(() => ApplicationCapabilityDescriptorValidator.Validate(descriptor))
            .Should().Throw<ApplicationCapabilityValidationException>()
            .WithMessage("*type does not match*");
    }

    [Fact]
    public void Exact_qualification_binding_rejects_mismatched_fingerprints_and_types()
    {
        const string otherDescriptorFingerprint =
            "sha256:3333333333333333333333333333333333333333333333333333333333333333";
        const string otherReportFingerprint =
            "sha256:4444444444444444444444444444444444444444444444444444444444444444";
        var candidate = ApplicationCapabilityFixtures.CreateQualificationCandidate();

        MatchesExactQualification(
            candidate,
            ApplicationCapabilityFixtures.DescriptorFingerprint,
            typeof(ApplicationCapabilityFixtures.IVerifiedHandler),
            typeof(ApplicationCapabilityFixtures.IVerifiedCoachProjector),
            ApplicationCapabilityFixtures.QualificationReportFingerprint).Should().BeTrue();
        MatchesExactQualification(
            candidate,
            otherDescriptorFingerprint,
            typeof(ApplicationCapabilityFixtures.IVerifiedHandler),
            typeof(ApplicationCapabilityFixtures.IVerifiedCoachProjector),
            ApplicationCapabilityFixtures.QualificationReportFingerprint).Should().BeFalse();
        MatchesExactQualification(
            candidate,
            ApplicationCapabilityFixtures.DescriptorFingerprint,
            typeof(ApplicationCapabilityFixtures.IVerifiedHandler),
            typeof(ApplicationCapabilityFixtures.IVerifiedCoachProjector),
            otherReportFingerprint).Should().BeFalse();
        MatchesExactQualification(
            candidate,
            ApplicationCapabilityFixtures.DescriptorFingerprint,
            typeof(IMismatchedContract),
            typeof(ApplicationCapabilityFixtures.IVerifiedCoachProjector),
            ApplicationCapabilityFixtures.QualificationReportFingerprint).Should().BeFalse();
    }

    private static ApplicationCapabilityMetadata ConsequentialMetadata(ApplicationEffectClass effect) =>
        ApplicationCapabilityFixtures.DefaultDeniedMetadata() with
        {
            Effect = effect,
            Confirmation = ApplicationConfirmationPolicy.Accept,
            Idempotency = ApplicationIdempotencyPolicy.RequiredByKey
        };

    private static ApplicationCapabilityDefinition<
        ApplicationCapabilityQueryRequest,
        ApplicationStateVersion,
        CoachObservationEnvelope> Define(ApplicationCapabilityMetadata metadata) => new(metadata);

    private static Action Invoking(Action action) => action;

    private static bool MatchesExactQualification(
        ApplicationCapabilityQualificationCandidate candidate,
        string descriptorFingerprint,
        Type handlerContractType,
        Type coachProjectorContractType,
        string qualificationReportFingerprint) =>
        (bool)typeof(ApplicationCapabilityQualificationCandidate)
            .GetMethod(
                "MatchesExactQualification",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(
                candidate,
                [
                    descriptorFingerprint,
                    handlerContractType,
                    coachProjectorContractType,
                    qualificationReportFingerprint
                ])!;

    private interface IHandler;

    private interface IHandler<T>;

    private interface IMismatchedContract;

    private sealed class UnsupportedContract
    {
        public string Value { get; init; } = string.Empty;
    }
}
