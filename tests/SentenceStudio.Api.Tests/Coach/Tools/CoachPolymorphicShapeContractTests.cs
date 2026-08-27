using System.Text.Json.Serialization;
using FluentAssertions;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// Polymorphic tool-result and coach-shape types are refused at startup.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CoachEmbargoScanner"/> judges the members declared on the type it is handed. A type
/// carrying <c>[JsonDerivedType]</c> or <c>[JsonPolymorphic]</c> breaks that assumption: the
/// serializer can emit any registered derived shape, whose extra members were never walked and so
/// were never judged against the embargo. The shape the model actually receives would then be one
/// nobody reviewed, and adding a derived record later would widen the surface with no scan
/// failure to notice it.
/// </para>
/// <para>
/// These tests are deliberately non-vacuous: the polymorphic fixtures carry a member the embargo
/// would refuse outright (<c>UserProfileId</c>) on the <em>derived</em> type only, so a scanner
/// that quietly followed the base type and stopped would report nothing at all.
/// </para>
/// </remarks>
public class CoachPolymorphicShapeContractTests
{
    private static CoachOptions AllReadTools() => new()
    {
        DurableHistory = new CoachFeatureSwitch { Enabled = true },
        SamOverlay = new CoachFeatureSwitch { Enabled = true },
        SamReadTools = new CoachFeatureSwitch { Enabled = true },
        SamWriteTools = new CoachFeatureSwitch { Enabled = false }
    };

    [Fact]
    public void The_scanner_reports_nothing_about_the_derived_members_if_polymorphism_is_ignored()
    {
        // The premise, stated as a test so the rest is not an argument about reflection: the
        // walker never visits DerivedLeak, so its refused member is invisible. This is the hole.
        var derivedOnly = new CoachEmbargoScanner()
            .ScanTypes([typeof(DerivedLeakingResult)], CoachEmbargoScope.ModelVisible);

        derivedOnly.IsValid.Should().BeFalse("the derived type does carry a refused member");
        derivedOnly.Violations.Should().Contain(v => v.Code == "member_name");
    }

    [Fact]
    public void A_JsonDerivedType_base_is_refused()
    {
        var result = new CoachEmbargoScanner()
            .ScanTypes([typeof(PolymorphicResultBase)], CoachEmbargoScope.ModelVisible);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v =>
            v.Code == "polymorphic_type"
            && v.Message.Contains(nameof(PolymorphicResultBase))
            && v.Message.Contains(nameof(JsonDerivedTypeAttribute)));
    }

    [Fact]
    public void A_JsonPolymorphic_base_is_refused_even_without_declared_subtypes()
    {
        // [JsonPolymorphic] alone turns the feature on; the derived set can arrive from a custom
        // type-info resolver, which reflection here would never see.
        var result = new CoachEmbargoScanner()
            .ScanTypes([typeof(ResolverPolymorphicResult)], CoachEmbargoScope.ModelVisible);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v =>
            v.Code == "polymorphic_type" && v.Message.Contains(nameof(JsonPolymorphicAttribute)));
    }

    [Fact]
    public void A_nested_polymorphic_member_is_refused()
    {
        // The refusal has to hold anywhere in the graph, not only at the registered root.
        var result = new CoachEmbargoScanner()
            .ScanTypes([typeof(EnvelopeWithPolymorphicMember)], CoachEmbargoScope.ModelVisible);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == "polymorphic_type");
    }

    [Fact]
    public void A_polymorphic_member_inside_a_collection_is_refused()
    {
        var result = new CoachEmbargoScanner()
            .ScanTypes([typeof(EnvelopeWithPolymorphicList)], CoachEmbargoScope.ModelVisible);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == "polymorphic_type");
    }

    [Fact]
    public void A_polymorphic_tool_result_stops_startup()
    {
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Register(new CoachToolRegistration
        {
            Name = "get_polymorphic_shape",
            Description = "A tool whose envelope can serialize as more than one shape.",
            RiskClass = CoachToolRiskClass.Read,
            ResultType = typeof(PolymorphicResultBase),
            EmbargoScope = CoachEmbargoScope.ToolResult
        });
        registry.Freeze();

        var act = () => CoachOutputContract.ValidateRegistry(registry);

        act.Should().Throw<CoachContractViolationException>();
    }

    [Fact]
    public void Benign_serialization_attributes_are_not_refused()
    {
        // The rule must be about polymorphism and nothing else. [JsonPropertyName], [JsonIgnore],
        // [JsonConverter], and [Description] all leave the emitted member set equal to the
        // declared member set, which is exactly what the scanner walks.
        var result = new CoachEmbargoScanner()
            .ScanTypes([typeof(BenignlyAnnotatedResult)], CoachEmbargoScope.ModelVisible);

        result.IsValid.Should().BeTrue(
            "annotations that do not change the emitted shape are none of the contract's business: {0}",
            string.Join("; ", result.Violations.Select(v => v.Message)));
    }

    [Fact]
    public void The_shipped_registry_carries_no_polymorphic_envelope()
    {
        // Non-vacuous only because the fixtures above prove the rule fires. This is the assertion
        // that the production surface is actually clean under it.
        var registry = new CoachToolRegistry(AllReadTools());
        registry.Freeze();

        var result = CoachOutputContract.ScanRegistry(registry);

        result.Violations.Should().NotContain(v => v.Code == "polymorphic_type");
    }

    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    [JsonDerivedType(typeof(DerivedLeakingResult), "leak")]
    private sealed class PolymorphicResultBase
    {
        public string Summary { get; init; } = string.Empty;
    }

    /// <summary>
    /// The member the embargo refuses lives here, on the derived type only, so a scan that walks
    /// the base alone finds nothing.
    /// </summary>
    private sealed class DerivedLeakingResult
    {
        public string Summary { get; init; } = string.Empty;

        public string UserProfileId { get; init; } = string.Empty;
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
    private sealed class ResolverPolymorphicResult
    {
        public string Summary { get; init; } = string.Empty;
    }

    private sealed class EnvelopeWithPolymorphicMember
    {
        public PolymorphicResultBase? Detail { get; init; }
    }

    private sealed class EnvelopeWithPolymorphicList
    {
        public IReadOnlyList<PolymorphicResultBase> Details { get; init; } = [];
    }

    private sealed class BenignlyAnnotatedResult
    {
        [JsonPropertyName("summary_text")]
        public string Summary { get; init; } = string.Empty;

        [JsonIgnore]
        public string Internal { get; init; } = string.Empty;

        [System.ComponentModel.Description("How many items matched.")]
        public int MatchCount { get; init; }
    }
}
