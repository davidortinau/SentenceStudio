using FluentAssertions;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Capabilities;

/// <summary>
/// Plan §5.3 — <c>min(MaxAvailability, stage-permitted, handshake-permitted)</c>, swept exhaustively.
/// </summary>
public class CoachDerivedAvailabilityTests
{
    private static readonly CoachCapabilityAvailability[] Ceilings = Enum.GetValues<CoachCapabilityAvailability>();
    private static readonly CoachCapabilityStage[] Stages = Enum.GetValues<CoachCapabilityStage>();

    private static readonly CoachClientCapabilityHandshake?[] Handshakes =
    [
        null,
        CapabilityFixtures.Handshake(),
        CapabilityFixtures.Handshake(1, CoachClientCapabilityCode.ThemeMetadata)
    ];

    [Fact]
    public void The_cartesian_sweep_covers_the_whole_product_and_never_exceeds_any_ceiling()
    {
        var rows = CapabilityFixtures.OneLegalRowPerEffectClass();
        var expected = Ceilings.Length * Stages.Length * Handshakes.Length * rows.Count;
        var seen = 0;

        foreach (var declared in Ceilings)
        foreach (var currentStage in Stages)
        foreach (var handshake in Handshakes)
        foreach (var row in rows)
        {
            var descriptor = row with { MaxAvailability = declared, IsToolBacked = true };
            var resolved = new CoachCapabilityResolver(CapabilityFixtures.ManifestWith(descriptor))
                .Resolve(descriptor.Name, currentStage, handshake);

            CoachCapabilityAvailabilityRank.Of(resolved).Should().BeLessThanOrEqualTo(
                CoachCapabilityAvailabilityRank.Of(declared),
                "the declared ceiling is one of the three minimums");

            if (currentStage < descriptor.RequiredStage)
            {
                // §5.3 rule 1.
                resolved.Should().NotBe(CoachCapabilityAvailability.Present);
            }

            if (descriptor.Surface == CoachCapabilitySurface.Client
                && (handshake is null || !handshake.Codes.Contains(descriptor.ClientCapabilityCode)))
            {
                // §5.3 rule 2.
                resolved.Should().NotBe(CoachCapabilityAvailability.Present);
            }

            seen++;
        }

        seen.Should().Be(expected).And.BeGreaterThan(0);
        expected.Should().Be(5 * 6 * 3 * 6, "5 ceilings x 6 stages x 3 handshakes x 6 effect-class rows");
    }

    [Fact]
    public void Availability_is_never_stored_on_the_registration_or_the_descriptor()
    {
        // §5.3: "Availability is not a stored field."
        typeof(SentenceStudio.Api.Coach.Tools.CoachToolRegistration)
            .GetProperties().Select(p => p.Name).Should().NotContain("Availability");
        typeof(CoachCapabilityDescriptor)
            .GetProperties().Select(p => p.Name).Should().NotContain("Availability");
    }

    [Fact]
    public void The_planned_member_order_is_preserved_and_the_rank_supplies_the_ordering()
    {
        // §5.2 lists availability most-capable-first, so Present is the zero member and a naive
        // ordinal min would invert the rule. Both facts are pinned.
        Enum.GetValues<CoachCapabilityAvailability>().Should().Equal(
            CoachCapabilityAvailability.Present,
            CoachCapabilityAvailability.PresentOnAnotherSurface,
            CoachCapabilityAvailability.AbsentByDesign,
            CoachCapabilityAvailability.AbsentUnimplemented,
            CoachCapabilityAvailability.Unknown);

        ((int)CoachCapabilityAvailability.Present).Should().Be(0);

        CoachCapabilityAvailabilityRank.Min(
                CoachCapabilityAvailability.Present, CoachCapabilityAvailability.Unknown)
            .Should().Be(CoachCapabilityAvailability.Unknown, "an ordinal min would have said Present");
    }

    [Fact]
    public void The_stage_ladder_is_the_planned_one_in_the_planned_order()
    {
        // Plan §16 line 484: Off -> Read -> Presentation -> Launch -> Semantic -> External.
        Enum.GetValues<CoachCapabilityStage>().Should().Equal(
            CoachCapabilityStage.Off,
            CoachCapabilityStage.Read,
            CoachCapabilityStage.Presentation,
            CoachCapabilityStage.Launch,
            CoachCapabilityStage.Semantic,
            CoachCapabilityStage.External);
    }

    // ---------------------------------------------------------------- the three rules, individually

    [Fact]
    public void Rule1_a_capability_above_the_promoted_stage_never_resolves_to_present()
    {
        var descriptor = CapabilityFixtures.LegalLearnerData();

        Resolve(descriptor, CoachCapabilityStage.Read, null)
            .Should().Be(CoachCapabilityAvailability.AbsentUnimplemented);

        Resolve(descriptor, CoachCapabilityStage.Semantic, null)
            .Should().Be(CoachCapabilityAvailability.Present, "the stage now permits it");
    }

    [Fact]
    public void Rule1_a_client_surface_above_the_stage_resolves_to_present_on_another_surface()
    {
        // "or to PresentOnAnotherSurface when the app ships the operation on a screen".
        var descriptor = CapabilityFixtures.LegalPresentationState();

        Resolve(descriptor, CoachCapabilityStage.Read,
                CapabilityFixtures.Handshake(1, CoachClientCapabilityCode.ThemeMetadata))
            .Should().Be(CoachCapabilityAvailability.PresentOnAnotherSurface);
    }

    [Fact]
    public void Rule2_a_client_capability_the_handshake_does_not_advertise_is_present_on_another_surface()
    {
        var descriptor = CapabilityFixtures.LegalPresentationState();

        Resolve(descriptor, CoachCapabilityStage.Presentation, null)
            .Should().Be(CoachCapabilityAvailability.PresentOnAnotherSurface);

        Resolve(descriptor, CoachCapabilityStage.Presentation, CapabilityFixtures.Handshake())
            .Should().Be(CoachCapabilityAvailability.PresentOnAnotherSurface, "the list is empty");

        Resolve(descriptor, CoachCapabilityStage.Presentation,
                CapabilityFixtures.Handshake(1, CoachClientCapabilityCode.ThemeMetadata))
            .Should().Be(CoachCapabilityAvailability.Present);
    }

    [Fact]
    public void An_unknown_code_is_ignored_and_the_turn_still_renders()
    {
        // §5.5: "An unknown code is ignored, and the turn still renders."
        var descriptor = CapabilityFixtures.LegalPresentationState();

        Resolve(descriptor, CoachCapabilityStage.Presentation,
                CapabilityFixtures.Handshake(1, CoachClientCapabilityCode.Unknown))
            .Should().Be(CoachCapabilityAvailability.PresentOnAnotherSurface);
    }

    [Fact]
    public void An_unknown_capability_is_absent_rather_than_an_error()
    {
        new CoachCapabilityResolver(CapabilityFixtures.ShippedManifest())
            .Resolve("no_such_capability", CoachCapabilityStage.External, null)
            .Should().Be(CoachCapabilityAvailability.AbsentUnimplemented);
    }

    // ---------------------------------------------------------------- §5.5 — the handshake's limit

    [Theory]
    [InlineData(CoachCapabilityEffectClass.LearnerData)]
    [InlineData(CoachCapabilityEffectClass.CompositeReversiblePair)]
    [InlineData(CoachCapabilityEffectClass.ExternalEffect)]
    [InlineData(CoachCapabilityEffectClass.ActivityLaunch)]
    [InlineData(CoachCapabilityEffectClass.Read)]
    public void The_handshake_authorizes_reversible_presentation_state_only(CoachCapabilityEffectClass effectClass)
    {
        // A client-surface capability of any other class, with the most generous handshake that
        // could be sent, still never reaches Present.
        var descriptor = CapabilityFixtures.LegalPresentationState() with
        {
            EffectClass = effectClass,
            RequiredStage = CoachCapabilityStage.Off
        };

        var generous = CapabilityFixtures.Handshake(int.MaxValue, Enum.GetValues<CoachClientCapabilityCode>());

        Resolve(descriptor, CoachCapabilityStage.External, generous)
            .Should().Be(CoachCapabilityAvailability.PresentOnAnotherSurface);
    }

    [Fact]
    public void Only_the_presentation_state_client_row_is_handshake_authorizable_and_the_set_is_swept()
    {
        var authorizable = Enum.GetValues<CoachCapabilityEffectClass>()
            .Where(e => (CapabilityFixtures.LegalPresentationState() with { EffectClass = e }).IsHandshakeAuthorizable)
            .ToList();

        Enum.GetValues<CoachCapabilityEffectClass>().Should().HaveCount(6);
        authorizable.Should().ContainSingle().Which.Should().Be(CoachCapabilityEffectClass.PresentationState);
    }

    [Fact]
    public void A_malformed_handshake_is_treated_as_absent_rather_than_failing_the_turn()
    {
        var malformed = new CoachClientCapabilityHandshake
        {
            Version = 0,
            Codes = [CoachClientCapabilityCode.ThemeMetadata]
        };

        malformed.IsUsable.Should().BeFalse();

        Resolve(CapabilityFixtures.LegalPresentationState(), CoachCapabilityStage.Presentation, malformed)
            .Should().Be(CoachCapabilityAvailability.PresentOnAnotherSurface);
    }

    private static CoachCapabilityAvailability Resolve(
        CoachCapabilityDescriptor descriptor,
        CoachCapabilityStage stage,
        CoachClientCapabilityHandshake? handshake) =>
        new CoachCapabilityResolver(CapabilityFixtures.ManifestWith(descriptor))
            .Resolve(descriptor.Name, stage, handshake);
}
