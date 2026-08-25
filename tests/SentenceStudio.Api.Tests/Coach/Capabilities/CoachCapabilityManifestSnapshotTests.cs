using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.Api.Tests.Coach.Capabilities;

/// <summary>
/// The §5.2 contract itself: exactly the planned nine, none of the fields that were derived before
/// the plan was readable, plus the read metadata, the theme declaration and the handshake bounds.
/// </summary>
public class CoachCapabilityManifestSnapshotTests
{
    /// <summary>Plan §5.2, verbatim. Order as listed in the plan block.</summary>
    private static readonly string[] PlannedNine =
    [
        "EffectClass", "Surface", "MaxAvailability", "RequiredStage",
        "Reversal", "Confirmation", "ReceiptKind", "Scope", "DeclaredStepCount"
    ];

    /// <summary>
    /// Fields that were derived before the plan was readable and must not ship.
    /// </summary>
    private static readonly string[] Invented =
    [
        "Stage", "Effect", "IsReversible", "RequiresLearnerConfirmation",
        "RequiredClientCapability", "MinimumHandshakeVersion", "DeclaredAgainst"
    ];

    // ---------------------------------------------------------------- the contract

    [Fact]
    public void The_registration_carries_every_planned_field()
    {
        var actual = typeof(CoachToolRegistration).GetProperties().Select(p => p.Name).ToList();

        PlannedNine.Should().HaveCount(9);
        foreach (var planned in PlannedNine)
        {
            actual.Should().Contain(planned, $"§5.2 names '{planned}'");
        }
    }

    [Fact]
    public void The_registration_carries_none_of_the_invented_fields()
    {
        var actual = typeof(CoachToolRegistration).GetProperties().Select(p => p.Name).ToList();

        Invented.Should().HaveCount(7, "seven fields were derived before the plan was readable");
        foreach (var invented in Invented)
        {
            actual.Should().NotContain(invented, $"'{invented}' is not a §5.2 field and must not ship");
        }
    }

    [Fact]
    public void The_registration_keeps_RiskClass_unchanged_beside_the_nine()
    {
        // §5.2: "RiskClass  Read | WriteSoft | WriteHard   existing, unchanged".
        var riskClass = typeof(CoachToolRegistration).GetProperty(nameof(CoachToolRegistration.RiskClass));

        riskClass.Should().NotBeNull();
        riskClass!.PropertyType.Should().Be(typeof(CoachToolRiskClass));
        Enum.GetValues<CoachToolRiskClass>().Should().Equal(
            CoachToolRiskClass.Read, CoachToolRiskClass.WriteSoft, CoachToolRiskClass.WriteHard);
    }

    [Fact]
    public void No_handshake_authorization_field_sits_on_the_registration()
    {
        // §5.5 grants the handshake authority over reversible presentation state alone, so a
        // per-registration handshake field would imply any capability could be client-unlocked.
        var names = typeof(CoachToolRegistration).GetProperties().Select(p => p.Name).ToList();

        names.Should().NotContain(n => n.Contains("Handshake", StringComparison.Ordinal));
        names.Should().NotContain(n => n.Contains("ClientCapability", StringComparison.Ordinal));
    }

    [Fact]
    public void The_descriptor_carries_the_planned_nine_and_the_read_metadata_names()
    {
        var actual = typeof(CoachCapabilityDescriptor).GetProperties().Select(p => p.Name).ToList();

        foreach (var planned in PlannedNine)
        {
            actual.Should().Contain(planned);
        }

        actual.Should().Contain(nameof(CoachCapabilityDescriptor.ReadMetadata));

        // §5.2 line 160 names the six read fields; they live on the metadata record.
        var metadata = typeof(CoachReadCapabilityMetadata).GetProperties().Select(p => p.Name).ToList();
        foreach (var name in new[]
                 {
                     "Coverage", "SupportedOrders", "SupportedFilters",
                     "DateSupport", "RangeSupport", "MaxPageSize"
                 })
        {
            metadata.Should().Contain(name, $"§5.2 line 160 names '{name}'");
        }
    }

    // ---------------------------------------------------------------- read metadata is not a rival manifest

    [Fact]
    public void Read_metadata_and_the_frozen_registry_agree_in_both_directions()
    {
        var registry = CapabilityFixtures.FrozenRegistry();
        var reads = registry.All
            .Where(r => r.EffectClass == CoachCapabilityEffectClass.Read)
            .Select(r => r.Name)
            .ToList();

        reads.Should().NotBeEmpty();

        // No read without metadata, and no metadata without a read.
        CoachReadCapabilityMetadataTable.All.Keys.Should().BeEquivalentTo(reads);
        CoachReadCapabilityMetadataTable.All.Should().HaveCount(reads.Count).And.HaveCount(14);
    }

    [Fact]
    public void Every_read_metadata_row_cites_where_its_values_came_from()
    {
        // "Do not guess values." A row with no citation is a guess with better manners.
        CoachReadCapabilityMetadataTable.All.Should().NotBeEmpty();

        foreach (var (name, metadata) in CoachReadCapabilityMetadataTable.All)
        {
            metadata.Source.Should().NotBeNullOrWhiteSpace($"'{name}' must say where its values came from");
            metadata.Source.Should().Contain(".cs", "the citation names the file that emits the scope");
            metadata.SupportedOrders.Should().NotBeEmpty();
            metadata.SupportedFilters.Should().HaveFlag(CoachScopeFilters.OwnerScoped,
                "every read is at minimum owner-scoped");
        }
    }

    [Fact]
    public void A_non_read_capability_carries_no_read_metadata()
    {
        var manifest = CapabilityFixtures.ShippedManifest();
        var nonReads = manifest.All.Where(c => c.EffectClass != CoachCapabilityEffectClass.Read).ToList();

        nonReads.Should().NotBeEmpty();
        nonReads.Should().OnlyContain(c => c.ReadMetadata == null);
    }

    [Fact]
    public void Every_read_capability_in_the_manifest_carries_its_metadata()
    {
        var manifest = CapabilityFixtures.ShippedManifest();
        var reads = manifest.All.Where(c => c.EffectClass == CoachCapabilityEffectClass.Read).ToList();

        reads.Should().HaveCount(14);
        reads.Should().OnlyContain(c => c.ReadMetadata != null);
    }

    // ---------------------------------------------------------------- snapshot

    [Fact]
    public void The_manifest_matches_its_declared_shape()
    {
        var manifest = CapabilityFixtures.ShippedManifest();

        var reads = manifest.All.Where(c => c.EffectClass == CoachCapabilityEffectClass.Read).ToList();
        var learnerData = manifest.All.Where(c => c.EffectClass == CoachCapabilityEffectClass.LearnerData).ToList();
        var external = manifest.All.Where(c => c.EffectClass == CoachCapabilityEffectClass.ExternalEffect).ToList();
        var presentation = manifest.All.Where(c => c.EffectClass == CoachCapabilityEffectClass.PresentationState).ToList();

        reads.Should().HaveCount(14);
        learnerData.Should().HaveCount(11, "eleven propose_* tools write learner data");
        external.Should().HaveCount(1, "the YouTube import is the only external effect");
        presentation.Should().HaveCount(1, "the declared theme capability");

        (reads.Count + learnerData.Count + external.Count + presentation.Count)
            .Should().Be(manifest.All.Count, "every capability falls into one of the four classes in use");
    }

    [Fact]
    public void Lookup_answers_for_every_capability_the_manifest_lists()
    {
        var manifest = CapabilityFixtures.ShippedManifest();
        var found = 0;

        foreach (var capability in manifest.All)
        {
            manifest.Find(capability.Name).Should().BeSameAs(capability);
            found++;
        }

        found.Should().Be(manifest.All.Count).And.BeGreaterThan(0);
        manifest.Find("nothing_declares_this").Should().BeNull();
    }

    // ---------------------------------------------------------------- A7 — the theme declaration

    [Fact]
    public void The_theme_capability_is_declared_absent_until_its_workstream_and_stage_ship()
    {
        var theme = CoachCapabilityDeclarations.ThemeMetadata;

        theme.MaxAvailability.Should().Be(CoachCapabilityAvailability.AbsentUnimplemented);
        theme.Surface.Should().Be(CoachCapabilitySurface.Client);
        theme.RequiredStage.Should().Be(CoachCapabilityStage.Presentation);
        theme.EffectClass.Should().Be(CoachCapabilityEffectClass.PresentationState);
        theme.IsToolBacked.Should().BeFalse();

        // The §5.4 PresentationState row, exactly.
        theme.Reversal.Should().Be(CoachCapabilityReversal.ClientRevert);
        theme.Confirmation.Should().Be(CoachCapabilityConfirmation.Gesture);
        theme.ReceiptKind.Should().Be(CoachCapabilityReceiptKind.Client);
        theme.Scope.Should().NotBe(CoachCapabilityScope.Account);
        theme.DeclaredStepCount.Should().Be(1);
    }

    [Fact]
    public void The_theme_capability_resolves_to_absent_across_the_whole_stage_and_handshake_product()
    {
        var resolver = new CoachCapabilityResolver(CapabilityFixtures.ShippedManifest());
        var handshakes = new CoachClientCapabilityHandshake?[]
        {
            null,
            CapabilityFixtures.Handshake(),
            CapabilityFixtures.Handshake(int.MaxValue, Enum.GetValues<CoachClientCapabilityCode>())
        };

        var cases = 0;
        foreach (var stage in Enum.GetValues<CoachCapabilityStage>())
        foreach (var handshake in handshakes)
        {
            resolver.Resolve(CoachCapabilityDeclarations.ThemeMetadataCapabilityName, stage, handshake)
                .Should().Be(CoachCapabilityAvailability.AbsentUnimplemented,
                    "the declared ceiling caps it however far the stage is promoted");
            cases++;
        }

        cases.Should().Be(Enum.GetValues<CoachCapabilityStage>().Length * handshakes.Length)
            .And.Be(18).And.BeGreaterThan(0);
    }

    [Fact]
    public void The_theme_declaration_restates_nothing_from_the_frozen_catalogue()
    {
        foreach (var descriptor in ThemeCatalog.All)
        {
            CoachCapabilityDeclarations.ThemeMetadata.Name.Should().NotContain(descriptor.Id);
        }
    }

    // ---------------------------------------------------------------- the handshake

    [Fact]
    public void The_handshake_rides_the_turn_request_and_is_optional()
    {
        var property = typeof(CoachTurnRequest).GetProperty(nameof(CoachTurnRequest.ClientCapabilities));

        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(typeof(CoachClientCapabilityHandshake));

        new CoachTurnRequest { InputKind = CoachTurnInputKind.Text, Text = "hello" }
            .ClientCapabilities.Should().BeNull();
    }

    [Fact]
    public void The_handshake_is_content_free_and_carries_only_a_version_and_closed_codes()
    {
        foreach (var member in typeof(CoachClientCapabilityHandshake)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var type = member.PropertyType;
            (type == typeof(int)
             || type == typeof(IReadOnlyList<CoachClientCapabilityCode>)
             || type == typeof(bool)).Should().BeTrue(
                $"'{member.Name}' is a {type.Name}; §5.5 says the list is content-free");
        }
    }

    [Fact]
    public void The_handshake_is_not_part_of_any_persisted_coach_shape()
    {
        var holders = typeof(CoachTurnRequest).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("SentenceStudio.Contracts.Coach", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(CoachClientCapabilityHandshake))
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        holders.Should().ContainSingle()
            .Which.Should().Be($"{nameof(CoachTurnRequest)}.{nameof(CoachTurnRequest.ClientCapabilities)}");
    }

    [Fact]
    public void The_handshake_round_trips_with_unknown_codes_tolerated()
    {
        var parsed = JsonSerializer.Deserialize<CoachClientCapabilityHandshake>(
            """{"version":1,"codes":["ThemeMetadata","SomethingFromTheFuture"]}""",
            SentenceStudio.Contracts.Wire.WireJson.Client);

        parsed.Should().NotBeNull();
        parsed!.Codes.Should().Contain(CoachClientCapabilityCode.ThemeMetadata);
        parsed.Codes.Should().Contain(CoachClientCapabilityCode.Unknown);
    }
}
