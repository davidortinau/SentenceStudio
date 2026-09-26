using FluentAssertions;
using SentenceStudio.Application.Capabilities;
using SentenceStudio.Contracts.AppOperation;

namespace SentenceStudio.UnitTests.ApplicationCapabilities;

public sealed class ApplicationCapabilityCatalogTests
{
    [Fact]
    public void Duplicate_code_and_major_version_is_rejected()
    {
        var builder = new ApplicationCapabilityCatalogBuilder()
            .Add(ApplicationCapabilityFixtures.StaticCandidateDefinition("plan.today.read"));

        Invoking(() => builder.Add(
                ApplicationCapabilityFixtures.StaticCandidateDefinition("plan.today.read")))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*plan.today.read@1*collides*");
    }

    [Fact]
    public void Same_code_with_distinct_major_versions_is_allowed()
    {
        var first = ApplicationCapabilityFixtures.StaticCandidateDefinition("plan.today.read");
        var second = Define(ApplicationCapabilityFixtures.StaticCandidateMetadata("plan.today.read") with
        {
            MajorVersion = 2
        });

        var catalog = new ApplicationCapabilityCatalogBuilder()
            .Add(first)
            .Add(second)
            .Freeze();

        catalog.All.Should().HaveCount(2);
        catalog.TryGet("plan.today.read", 1, out var versionOne).Should().BeTrue();
        catalog.TryGet("plan.today.read", 2, out var versionTwo).Should().BeTrue();
        versionOne!.MajorVersion.Should().Be(1);
        versionTwo!.MajorVersion.Should().Be(2);
    }

    [Fact]
    public void Alias_to_primary_name_collision_is_rejected_in_both_registration_orders()
    {
        var aliased = Define(ApplicationCapabilityFixtures.StaticCandidateMetadata("plan.today.read") with
        {
            Aliases = ["plan.current.read"]
        });
        var primaryCollision =
            ApplicationCapabilityFixtures.StaticCandidateDefinition("plan.current.read");

        Invoking(() => new ApplicationCapabilityCatalogBuilder().Add(aliased).Add(primaryCollision))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*plan.current.read@1*collides*");
        Invoking(() => new ApplicationCapabilityCatalogBuilder().Add(primaryCollision).Add(aliased))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*plan.current.read@1*collides*");
    }

    [Fact]
    public void Alias_lookup_returns_the_canonical_descriptor()
    {
        var definition = Define(ApplicationCapabilityFixtures.StaticCandidateMetadata() with
        {
            Aliases = ["plan.current.read"]
        });

        var catalog = new ApplicationCapabilityCatalogBuilder().Add(definition).Freeze();

        catalog.TryGet("plan.current.read", 1, out var descriptor).Should().BeTrue();
        descriptor!.Code.Should().Be("plan.today.read");
    }

    [Fact]
    public void Invalid_definition_is_rejected_before_registration()
    {
        var invalid = Define(
            ApplicationCapabilityFixtures.StaticCandidateMetadata() with
            {
                MajorVersion = -1
            });
        var builder = new ApplicationCapabilityCatalogBuilder();

        Invoking(() => builder.Add(invalid))
            .Should().Throw<ApplicationCapabilityValidationException>();
        builder.IsFrozen.Should().BeFalse();
    }

    [Fact]
    public void Registration_and_refreeze_are_rejected_after_freeze()
    {
        var builder = new ApplicationCapabilityCatalogBuilder()
            .Add(ApplicationCapabilityFixtures.StaticCandidateDefinition());

        _ = builder.Freeze();

        builder.IsFrozen.Should().BeTrue();
        Invoking(() => builder.Add(
                ApplicationCapabilityFixtures.StaticCandidateDefinition(
                    "progress.summary.read")))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*frozen*");
        Invoking(() => builder.Freeze())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*frozen*");
    }

    [Fact]
    public void Frozen_catalog_has_literal_deterministic_order_and_no_mutable_collection()
    {
        var catalog = new ApplicationCapabilityCatalogBuilder()
            .Add(ApplicationCapabilityFixtures.StaticCandidateDefinition(
                "vocabulary.catalog.read"))
            .Add(ApplicationCapabilityFixtures.StaticCandidateDefinition("plan.today.read"))
            .Add(ApplicationCapabilityFixtures.StaticCandidateDefinition(
                "progress.summary.read"))
            .Freeze();

        catalog.All.Select(item => item.Code).Should().Equal(
            "plan.today.read",
            "progress.summary.read",
            "vocabulary.catalog.read");
        Invoking(() => ((IList<IApplicationCapabilityDescriptor>)catalog.All)
                .Add(ApplicationCapabilityFixtures.StaticCandidateDefinition(
                    "skill.catalog.read")))
            .Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Frozen_catalog_retains_more_than_eight_complete_definitions()
    {
        var builder = new ApplicationCapabilityCatalogBuilder();
        foreach (var code in new[]
                 {
                     "content-import.preview",
                     "learner.profile.read",
                     "plan.today.read",
                     "progress.activity-log.read",
                     "progress.summary.read",
                     "resource.catalog.read",
                     "skill.catalog.read",
                     "speech.voice.catalog.read",
                     "vocabulary.catalog.read",
                     "vocabulary.progress.read"
                 })
        {
            builder.Add(ApplicationCapabilityFixtures.StaticCandidateDefinition(code));
        }

        var catalog = builder.Freeze();

        catalog.All.Should().HaveCount(10);
        catalog.All.Select(item => item.Code).Should().Equal(
            "content-import.preview",
            "learner.profile.read",
            "plan.today.read",
            "progress.activity-log.read",
            "progress.summary.read",
            "resource.catalog.read",
            "skill.catalog.read",
            "speech.voice.catalog.read",
            "vocabulary.catalog.read",
            "vocabulary.progress.read");
        catalog.All.Should().OnlyContain(item =>
            item.CoachExposurePolicy == ApplicationCapabilityCoachExposurePolicy.Candidate
            && item.ReleaseValidationStatus == ApplicationCapabilityReleaseValidationStatus.Passed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing.capability")]
    public void Missing_or_unknown_lookup_fails_without_a_descriptor(string code)
    {
        var catalog = new ApplicationCapabilityCatalogBuilder()
            .Add(ApplicationCapabilityFixtures.StaticCandidateDefinition())
            .Freeze();

        catalog.TryGet(code, 1, out var descriptor).Should().BeFalse();
        descriptor.Should().BeNull();
    }

    private static ApplicationCapabilityDefinition<
        ApplicationCapabilityQueryRequest,
        ApplicationStateVersion,
        CoachObservationEnvelope> Define(ApplicationCapabilityMetadata metadata) => new(metadata);

    private static Action Invoking(Action action) => action;
}
