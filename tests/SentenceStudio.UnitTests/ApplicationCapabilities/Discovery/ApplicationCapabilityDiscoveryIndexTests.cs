using System.Reflection;
using FluentAssertions;
using SentenceStudio.Application.Capabilities;
using SentenceStudio.Application.Capabilities.Discovery;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.UnitTests.ApplicationCapabilities.Discovery;

public sealed class ApplicationCapabilityDiscoveryIndexTests
{
    [Fact]
    public void Index_admits_only_complete_static_candidates_and_default_denies_the_rest()
    {
        var builder = new ApplicationCapabilityDiscoveryIndexBuilder();
        var admitted = builder.TryAdd(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(),
            ApplicationCapabilityDiscoveryFixtures.Schemas());
        var deniedDefinition = new ApplicationCapabilityDefinition<
            SentenceStudio.Contracts.AppOperation.ApplicationCapabilityQueryRequest,
            SentenceStudio.Contracts.AppOperation.ApplicationStateVersion,
            SentenceStudio.Contracts.AppOperation.CoachObservationEnvelope>(
                ApplicationCapabilityFixtures.DefaultDeniedMetadata("diary.entry.read"));
        var denied = builder.TryAdd(
            deniedDefinition,
            ApplicationCapabilityDiscoveryFixtures.Schemas());

        admitted.State.Should().Be(ApplicationCapabilityIndexAdmissionState.Added);
        denied.State.Should().Be(ApplicationCapabilityIndexAdmissionState.DefaultDenied);

        var index = builder.Freeze(
            ApplicationCapabilityDiscoveryFixtures.Generation(
                builder.ComputeDescriptorSetHash()));
        index.Documents.Should().ContainSingle()
            .Which.Code.Should().Be("plan.today.read");
    }

    [Fact]
    public void Duplicate_identity_alias_collisions_and_cross_version_family_changes_are_rejected()
    {
        var duplicateBuilder = new ApplicationCapabilityDiscoveryIndexBuilder();
        duplicateBuilder.TryAdd(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(),
            ApplicationCapabilityDiscoveryFixtures.Schemas());

        Invoking(() => duplicateBuilder.TryAdd(
                ApplicationCapabilityDiscoveryFixtures.Descriptor(),
                ApplicationCapabilityDiscoveryFixtures.Schemas()))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*plan.today.read@1*collides*");

        var aliasBuilder = new ApplicationCapabilityDiscoveryIndexBuilder();
        aliasBuilder.TryAdd(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                aliases: ["plan.current.read"]),
            ApplicationCapabilityDiscoveryFixtures.Schemas());
        Invoking(() => aliasBuilder.TryAdd(
                ApplicationCapabilityDiscoveryFixtures.Descriptor("plan.current.read"),
                ApplicationCapabilityDiscoveryFixtures.Schemas()))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*plan.current.read@1*collides*");

        var familyBuilder = new ApplicationCapabilityDiscoveryIndexBuilder();
        familyBuilder.TryAdd(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(),
            ApplicationCapabilityDiscoveryFixtures.Schemas());
        Invoking(() => familyBuilder.TryAdd(
                ApplicationCapabilityDiscoveryFixtures.Descriptor(
                    family: "different-family",
                    majorVersion: 2),
                ApplicationCapabilityDiscoveryFixtures.Schemas()))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*changes family*");
    }

    [Fact]
    public void Frozen_index_and_documents_expose_no_mutable_collections()
    {
        var descriptor = ApplicationCapabilityDiscoveryFixtures.Descriptor(
            positiveExamples: ["Show today's plan."],
            negativeExamples: ["Replace today's plan."]);
        var builder = new ApplicationCapabilityDiscoveryIndexBuilder();
        builder.TryAdd(descriptor, ApplicationCapabilityDiscoveryFixtures.Schemas());
        var index = builder.Freeze(
            ApplicationCapabilityDiscoveryFixtures.Generation(
                builder.ComputeDescriptorSetHash()));

        builder.IsFrozen.Should().BeTrue();
        Invoking(() => builder.TryAdd(
                ApplicationCapabilityDiscoveryFixtures.Descriptor("progress.summary.read"),
                ApplicationCapabilityDiscoveryFixtures.Schemas()))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*frozen*");
        Invoking(() => ((IList<ApplicationCapabilityDiscoveryDocument>)index.Documents)
                .Add(index.Documents[0]))
            .Should().Throw<NotSupportedException>();
        Invoking(() => ((IList<string>)index.Documents[0].PositiveExamples).Add("mutate"))
            .Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Descriptor_schema_and_generation_fingerprints_invalidate_stale_index_builds()
    {
        Invoking(() => new ApplicationCapabilitySchemaFingerprints(
                "not-a-fingerprint",
                ApplicationCapabilityDiscoveryFixtures.ClientResultSchemaFingerprint,
                ApplicationCapabilityDiscoveryFixtures.ObservationSchemaFingerprint))
            .Should().Throw<ArgumentException>();

        var invalidDescriptor = ApplicationCapabilityDiscoveryFixtures.Descriptor(
            descriptorFingerprint: "sha256:invalid");
        Invoking(() => new ApplicationCapabilityDiscoveryIndexBuilder().TryAdd(
                invalidDescriptor,
                ApplicationCapabilityDiscoveryFixtures.Schemas()))
            .Should().Throw<ApplicationCapabilityValidationException>();

        var builder = new ApplicationCapabilityDiscoveryIndexBuilder();
        builder.TryAdd(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(),
            ApplicationCapabilityDiscoveryFixtures.Schemas());
        var staleHash = builder.ComputeDescriptorSetHash();
        builder.TryAdd(
            ApplicationCapabilityDiscoveryFixtures.Descriptor("progress.summary.read"),
            ApplicationCapabilityDiscoveryFixtures.Schemas());

        Invoking(() => builder.Freeze(
                ApplicationCapabilityDiscoveryFixtures.Generation(staleHash)))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*stale*");

        var originalBuilder = new ApplicationCapabilityDiscoveryIndexBuilder();
        originalBuilder.TryAdd(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(),
            ApplicationCapabilityDiscoveryFixtures.Schemas());
        var originalHash = originalBuilder.ComputeDescriptorSetHash();

        var changedSchemaBuilder = new ApplicationCapabilityDiscoveryIndexBuilder();
        changedSchemaBuilder.TryAdd(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(),
            new ApplicationCapabilitySchemaFingerprints(
                "sha256:6666666666666666666666666666666666666666666666666666666666666666",
                ApplicationCapabilityDiscoveryFixtures.ClientResultSchemaFingerprint,
                ApplicationCapabilityDiscoveryFixtures.ObservationSchemaFingerprint));
        Invoking(() => changedSchemaBuilder.Freeze(
                ApplicationCapabilityDiscoveryFixtures.Generation(originalHash)))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*does not match*");

        var changedDescriptorBuilder = new ApplicationCapabilityDiscoveryIndexBuilder();
        changedDescriptorBuilder.TryAdd(
            ApplicationCapabilityDiscoveryFixtures.Descriptor(
                descriptorFingerprint:
                    "sha256:7777777777777777777777777777777777777777777777777777777777777777"),
            ApplicationCapabilityDiscoveryFixtures.Schemas());
        Invoking(() => changedDescriptorBuilder.Freeze(
                ApplicationCapabilityDiscoveryFixtures.Generation(originalHash)))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*does not match*");
    }

    [Fact]
    public void Every_discovery_affecting_descriptor_field_changes_the_generation_or_rejects_stale_input()
    {
        var mutations = DescriptorMutations();

        foreach (var mutation in mutations)
        {
            var originalBuilder = BuilderFor(mutation.Original());
            var originalHash = originalBuilder.ComputeDescriptorSetHash();
            var changedBuilder = BuilderFor(mutation.Changed(), allowValidationRejection: true);
            var changedHash = changedBuilder.ComputeDescriptorSetHash();

            changedHash.Should().NotBe(
                originalHash,
                because: $"the {mutation.Name} mutation affects discovery behavior");
            Invoking(() => changedBuilder.Freeze(
                    ApplicationCapabilityDiscoveryFixtures.Generation(originalHash)))
                .Should().Throw<InvalidOperationException>(
                    because: $"the caller hash must be rejected after the {mutation.Name} mutation")
                .WithMessage("*does not match*");
        }
    }

    [Fact]
    public void Canonical_hash_changes_when_behavior_changes_but_caller_descriptor_fingerprint_does_not()
    {
        var original = ApplicationCapabilityDiscoveryFixtures.Descriptor(
            description: "Read today's approved plan.");
        var changed = ApplicationCapabilityDiscoveryFixtures.Descriptor(
            description: "Read the learner's historical practice summary.");
        original.DescriptorFingerprint.Should().Be(changed.DescriptorFingerprint);
        var originalBuilder = BuilderFor(Input(original));
        var changedBuilder = BuilderFor(Input(changed));

        changedBuilder.ComputeDescriptorSetHash().Should().NotBe(
            originalBuilder.ComputeDescriptorSetHash());
    }

    [Fact]
    public void Set_like_metadata_and_descriptor_registration_order_have_canonical_hash_order()
    {
        var firstDescriptor = ApplicationCapabilityDiscoveryFixtures.Descriptor(
            positiveExamples: ["Show today's plan.", "Read the approved plan."],
            negativeExamples: ["Replace today's plan.", "Delete today's plan."],
            aliases: ["plan.current.read", "plan.approved.read"],
            metadataMutation: metadata => metadata with
            {
                Surfaces =
                [
                    ApplicationCapabilitySurface.UserInterface,
                    ApplicationCapabilitySurface.Model,
                    ApplicationCapabilitySurface.Automation
                ]
            });
        var reorderedDescriptor = ApplicationCapabilityDiscoveryFixtures.Descriptor(
            positiveExamples: ["Read the approved plan.", "Show today's plan."],
            negativeExamples: ["Delete today's plan.", "Replace today's plan."],
            aliases: ["plan.approved.read", "plan.current.read"],
            metadataMutation: metadata => metadata with
            {
                Surfaces =
                [
                    ApplicationCapabilitySurface.Automation,
                    ApplicationCapabilitySurface.Model,
                    ApplicationCapabilitySurface.UserInterface
                ]
            });
        var other = ApplicationCapabilityDiscoveryFixtures.Descriptor(
            "progress.summary.read",
            "progress");

        var first = new ApplicationCapabilityDiscoveryIndexBuilder();
        first.TryAdd(firstDescriptor, ApplicationCapabilityDiscoveryFixtures.Schemas());
        first.TryAdd(other, ApplicationCapabilityDiscoveryFixtures.Schemas());
        var second = new ApplicationCapabilityDiscoveryIndexBuilder();
        second.TryAdd(other, ApplicationCapabilityDiscoveryFixtures.Schemas());
        second.TryAdd(reorderedDescriptor, ApplicationCapabilityDiscoveryFixtures.Schemas());

        first.ComputeDescriptorSetHash().Should().Be(second.ComputeDescriptorSetHash());
    }

    [Fact]
    public void Freeze_rebuilds_generation_from_the_internal_canonical_hash()
    {
        var builder = BuilderFor(Input(ApplicationCapabilityDiscoveryFixtures.Descriptor()));
        var supplied = ApplicationCapabilityDiscoveryFixtures.Generation(
            builder.ComputeDescriptorSetHash());
        var staleSchema = new ApplicationCapabilityIndexGeneration(
            "application-capability-discovery/v1",
            supplied.DescriptorSetHash,
            supplied.EmbeddingProvider,
            supplied.EmbeddingModel,
            supplied.EmbeddingGeneration,
            supplied.EmbeddingDimensions,
            supplied.CreatedAtUtc,
            supplied.PromotionState);

        Invoking(() => builder.Freeze(staleSchema))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*schema version*");

        var index = builder.Freeze(supplied);

        index.Generation.Should().NotBeSameAs(supplied);
        index.Generation.DescriptorSetHash.Should().Be(supplied.DescriptorSetHash);
    }

    [Fact]
    public void Generation_identity_is_immutable_and_cannot_be_upgraded_in_place()
    {
        typeof(ApplicationCapabilityIndexGeneration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Should().OnlyContain(property => !property.CanWrite);
        typeof(ApplicationCapabilityIndexGeneration)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Should().BeEmpty();
    }

    [Fact]
    public void Document_and_result_graphs_have_no_runtime_or_content_data_fields()
    {
        var graphTypes = ReachablePublicTypes(
            typeof(ApplicationCapabilityDiscoveryDocument),
            typeof(ApplicationCapabilityDiscoveryResult));
        var forbiddenNames = new[]
        {
            "UserId",
            "LearnerId",
            "SessionId",
            "OperationId",
            "Prompt",
            "PromptText",
            "Query",
            "QueryText",
            "Content",
            "RequestPayload",
            "ResultPayload"
        };

        graphTypes.SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(property => property.Name)
            .Should().NotIntersectWith(forbiddenNames);
        typeof(ApplicationCapabilityDiscoveryDocument)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.PropertyType)
            .Should().NotContain(type =>
                type == typeof(Type)
                || typeof(Delegate).IsAssignableFrom(type));
        graphTypes.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Should().NotContain(name =>
                name.Contains("Execute", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Authorize", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Invoke", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Capability_and_vocabulary_discovery_cannot_share_document_types()
    {
        typeof(ApplicationCapabilityDiscoveryDocument).IsSealed.Should().BeTrue();
        typeof(ApplicationCapabilityDiscoveryDocument)
            .IsAssignableFrom(typeof(VocabularyDiscoveryDocument))
            .Should().BeFalse();

        var providerDocuments = typeof(ApplicationCapabilitySemanticScoringRequest)
            .GetProperty(nameof(ApplicationCapabilitySemanticScoringRequest.Documents))!
            .PropertyType;
        providerDocuments.GetGenericArguments().Should().Equal(
            typeof(ApplicationCapabilityDiscoveryDocument));
    }

    private static HashSet<Type> ReachablePublicTypes(params Type[] roots)
    {
        var pending = new Queue<Type>(roots);
        var visited = new HashSet<Type>();
        while (pending.TryDequeue(out var type))
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsArray)
            {
                type = type.GetElementType()!;
            }

            if (type.IsGenericType
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    pending.Enqueue(argument);
                }

                continue;
            }

            if (type.Assembly != typeof(ApplicationCapabilityDiscoveryDocument).Assembly
                || !visited.Add(type))
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                pending.Enqueue(property.PropertyType);
            }
        }

        return visited;
    }

    private sealed class VocabularyDiscoveryDocument;

    private static IReadOnlyList<DescriptorMutation> DescriptorMutations()
    {
        const string alternateDescriptorFingerprint =
            "sha256:8888888888888888888888888888888888888888888888888888888888888888";
        const string alternateQualificationFingerprint =
            "sha256:9999999999999999999999999999999999999999999999999999999999999999";

        DescriptorInput Common() => Input(ApplicationCapabilityDiscoveryFixtures.Descriptor());
        DescriptorInput Mutate(
            Func<ApplicationCapabilityMetadata, ApplicationCapabilityMetadata> mutation) =>
            Input(ApplicationCapabilityDiscoveryFixtures.Descriptor(metadataMutation: mutation));

        return
        [
            new("code", Common, () => Input(
                ApplicationCapabilityDiscoveryFixtures.Descriptor("plan.current.read"))),
            new("family", Common, () => Input(
                ApplicationCapabilityDiscoveryFixtures.Descriptor(family: "current-plan"))),
            new("major version", Common, () => Input(
                ApplicationCapabilityDiscoveryFixtures.Descriptor(majorVersion: 2))),
            new("selection description", Common, () => Input(
                ApplicationCapabilityDiscoveryFixtures.Descriptor(
                    description: "Read a different approved plan projection."))),
            new("positive examples", Common, () => Input(
                ApplicationCapabilityDiscoveryFixtures.Descriptor(
                    positiveExamples: ["Display my approved plan."]))),
            new("negative examples", Common, () => Input(
                ApplicationCapabilityDiscoveryFixtures.Descriptor(
                    negativeExamples: ["Delete my approved plan."]))),
            new("aliases", Common, () => Input(
                ApplicationCapabilityDiscoveryFixtures.Descriptor(
                    aliases: ["plan.current.read"]))),
            new("surfaces", Common, () => Mutate(metadata => metadata with
            {
                Surfaces = metadata.Surfaces.Concat(
                    [ApplicationCapabilitySurface.Automation]).ToArray()
            })),
            new("execution authority", Common, () => Mutate(metadata => metadata with
            {
                ExecutionAuthority = ApplicationExecutionAuthority.NativeLocal
            })),
            new("effect", Common, () => Mutate(metadata => metadata with
            {
                Effect = ApplicationEffectClass.Write
            })),
            new("sensitivity", Common, () => Mutate(metadata => metadata with
            {
                Sensitivity = ApplicationSensitivity.Public
            })),
            new("confirmation", Common, () => Mutate(metadata => metadata with
            {
                Confirmation = ApplicationConfirmationPolicy.Accept
            })),
            new("idempotency", Common, () => Mutate(metadata => metadata with
            {
                Idempotency = ApplicationIdempotencyPolicy.NaturallyIdempotent
            })),
            new("synchronization", Common, () => Mutate(metadata => metadata with
            {
                Synchronization = ApplicationSynchronizationPolicy.Cached
            })),
            new("continuation", Common, () => Mutate(metadata => metadata with
            {
                Continuation = ApplicationContinuationPolicy.ClarificationOnly
            })),
            new("policy state", Common, () => Mutate(metadata => metadata with
            {
                PolicyState = ApplicationCapabilityPolicyState.Incomplete
            })),
            new("Coach exposure policy", Common, () => Mutate(metadata => metadata with
            {
                CoachExposurePolicy = ApplicationCapabilityCoachExposurePolicy.Denied
            })),
            new("budget declaration", Common, () => Mutate(metadata => metadata with
            {
                Budgets = null
            })),
            new("maximum request bytes", Common, () => Mutate(metadata => metadata with
            {
                Budgets = metadata.Budgets! with
                {
                    MaxRequestBytes = metadata.Budgets.MaxRequestBytes + 1
                }
            })),
            new("maximum client result bytes", Common, () => Mutate(metadata => metadata with
            {
                Budgets = metadata.Budgets! with
                {
                    MaxClientResultBytes = metadata.Budgets.MaxClientResultBytes + 1
                }
            })),
            new("maximum Coach observation bytes", Common, () => Mutate(metadata => metadata with
            {
                Budgets = metadata.Budgets! with
                {
                    MaxCoachObservationBytes = metadata.Budgets.MaxCoachObservationBytes + 1
                }
            })),
            new("schema token cost", Common, () => Mutate(metadata => metadata with
            {
                Budgets = metadata.Budgets! with
                {
                    SchemaTokenCost = metadata.Budgets.SchemaTokenCost + 1
                }
            })),
            new("risk cost", Common, () => Mutate(metadata => metadata with
            {
                Budgets = metadata.Budgets! with
                {
                    RiskCost = metadata.Budgets.RiskCost + 1
                }
            })),
            new("exposure cost", Common, () => Mutate(metadata => metadata with
            {
                Budgets = metadata.Budgets! with
                {
                    ExposureCost = metadata.Budgets.ExposureCost + 1
                }
            })),
            new("maximum execution milliseconds", Common, () => Mutate(metadata => metadata with
            {
                Budgets = metadata.Budgets! with
                {
                    MaxExecutionMilliseconds = metadata.Budgets.MaxExecutionMilliseconds + 1
                }
            })),
            new("maximum continuation depth", Common, () => Mutate(metadata => metadata with
            {
                Budgets = metadata.Budgets! with
                {
                    MaxContinuationDepth = 1
                }
            })),
            new("rollout", Common, () => Mutate(metadata => metadata with
            {
                Rollout = ApplicationCapabilityRolloutState.Disabled
            })),
            new("availability", Common, () => Mutate(metadata => metadata with
            {
                Availability = ApplicationCapabilityAvailabilityState.Unavailable
            })),
            new("dependencies", Common, () => Mutate(metadata => metadata with
            {
                Dependencies = ApplicationCapabilityDependencyState.Unsatisfied
            })),
            new("release validation", Common, () => Mutate(metadata => metadata with
            {
                ReleaseValidationStatus = ApplicationCapabilityReleaseValidationStatus.Failed
            })),
            new("descriptor fingerprint", Common, () => Mutate(metadata => metadata with
            {
                DescriptorFingerprint = alternateDescriptorFingerprint
            })),
            new("qualification descriptor fingerprint", Common, () => Mutate(metadata => metadata with
            {
                QualificationCandidate = ApplicationCapabilityFixtures.CreateQualificationCandidate(
                    alternateDescriptorFingerprint)
            })),
            new("qualification report fingerprint", Common, () => Mutate(metadata => metadata with
            {
                QualificationCandidate = ApplicationCapabilityFixtures.CreateQualificationCandidate(
                    qualificationReportFingerprint: alternateQualificationFingerprint)
            })),
            new("qualification presence", Common, () => Mutate(metadata => metadata with
            {
                QualificationCandidate = null
            })),
            new("handler identity", Common, () => Mutate(metadata => metadata with
            {
                Handler = ApplicationCapabilityImplementationIdentity.ExactContract(
                    typeof(IAlternateHandler))
            })),
            new("handler presence", Common, () => Mutate(metadata => metadata with
            {
                Handler = null
            })),
            new("Coach projector identity", Common, () => Mutate(metadata => metadata with
            {
                CoachProjector = ApplicationCapabilityImplementationIdentity.ExactContract(
                    typeof(IAlternateCoachProjector))
            })),
            new("Coach projector presence", Common, () => Mutate(metadata => metadata with
            {
                CoachProjector = null
            })),
            new("qualification handler contract", Common, () => Mutate(metadata => metadata with
            {
                QualificationCandidate = ApplicationCapabilityFixtures.CreateQualificationCandidate(
                    handlerContractType: typeof(IAlternateHandler))
            })),
            new("qualification Coach projector contract", Common, () => Mutate(metadata => metadata with
            {
                QualificationCandidate = ApplicationCapabilityFixtures.CreateQualificationCandidate(
                    coachProjectorContractType: typeof(IAlternateCoachProjector))
            })),
            new("request contract type", () => Input(
                DescriptorWithContracts<
                    ApplicationCapabilityQueryRequest,
                    ApplicationStateVersion,
                    CoachObservationEnvelope>()), () => Input(
                DescriptorWithContracts<
                    ApplicationCapabilityCommandRequest,
                    ApplicationStateVersion,
                    CoachObservationEnvelope>())),
            new("client result contract type", () => Input(
                DescriptorWithContracts<
                    ApplicationCapabilityQueryRequest,
                    ApplicationStateVersion,
                    CoachObservationEnvelope>()), () => Input(
                DescriptorWithContracts<
                    ApplicationCapabilityQueryRequest,
                    ApplicationOperationDecisionReference,
                    CoachObservationEnvelope>())),
            new("Coach observation contract type", () => Input(
                DescriptorWithContracts<
                    ApplicationCapabilityQueryRequest,
                    ApplicationStateVersion,
                    CoachObservationEnvelope>()), () => Input(
                DescriptorWithContracts<
                    ApplicationCapabilityQueryRequest,
                    ApplicationStateVersion,
                    ApplicationLimitation>())),
            new("request schema fingerprint", Common, () => Input(
                ApplicationCapabilityDiscoveryFixtures.Descriptor(),
                new ApplicationCapabilitySchemaFingerprints(
                    "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    ApplicationCapabilityDiscoveryFixtures.ClientResultSchemaFingerprint,
                    ApplicationCapabilityDiscoveryFixtures.ObservationSchemaFingerprint))),
            new("client result schema fingerprint", Common, () => Input(
                ApplicationCapabilityDiscoveryFixtures.Descriptor(),
                new ApplicationCapabilitySchemaFingerprints(
                    ApplicationCapabilityDiscoveryFixtures.RequestSchemaFingerprint,
                    "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    ApplicationCapabilityDiscoveryFixtures.ObservationSchemaFingerprint))),
            new("Coach observation schema fingerprint", Common, () => Input(
                ApplicationCapabilityDiscoveryFixtures.Descriptor(),
                new ApplicationCapabilitySchemaFingerprints(
                    ApplicationCapabilityDiscoveryFixtures.RequestSchemaFingerprint,
                    ApplicationCapabilityDiscoveryFixtures.ClientResultSchemaFingerprint,
                    "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")))
        ];
    }

    private static ApplicationCapabilityDiscoveryIndexBuilder BuilderFor(
        DescriptorInput input,
        bool allowValidationRejection = false)
    {
        var builder = new ApplicationCapabilityDiscoveryIndexBuilder();
        try
        {
            builder.TryAdd(input.Descriptor, input.Schemas);
        }
        catch (ApplicationCapabilityValidationException) when (allowValidationRejection)
        {
        }

        return builder;
    }

    private static DescriptorInput Input(
        IApplicationCapabilityDescriptor descriptor,
        ApplicationCapabilitySchemaFingerprints? schemas = null) =>
        new(descriptor, schemas ?? ApplicationCapabilityDiscoveryFixtures.Schemas());

    private static IApplicationCapabilityDescriptor DescriptorWithContracts<
        TRequest,
        TClientResult,
        TCoachObservation>() =>
        new ApplicationCapabilityDefinition<TRequest, TClientResult, TCoachObservation>(
            ApplicationCapabilityFixtures.StaticCandidateMetadata());

    private sealed record DescriptorMutation(
        string Name,
        Func<DescriptorInput> Original,
        Func<DescriptorInput> Changed);

    private sealed record DescriptorInput(
        IApplicationCapabilityDescriptor Descriptor,
        ApplicationCapabilitySchemaFingerprints Schemas);

    private interface IAlternateHandler;

    private interface IAlternateCoachProjector;

    private static Action Invoking(Action action) => action;
}
