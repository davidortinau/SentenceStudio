using FluentAssertions;
using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// Proves the allow-list lets approved proposal tools through and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The allow-list refuses any tool whose name reads like a change action. That rule is what kept
/// the read-only surface honest, and it now has one exception. An exception in a security check
/// is worth more tests than the rule it qualifies, because the failure is silent: a tool that
/// slips through does not error, it works.
/// </para>
/// <para>
/// Two conditions gate the exception — the <c>propose_</c> prefix and a write risk class in the
/// registry — so each is tested with the other absent.
/// </para>
/// </remarks>
public class CoachWriteAllowListTests
{
    private static CoachOptions Enabled() => new()
    {
        SamOverlay = new CoachFeatureSwitch { Enabled = true },
        SamReadTools = new CoachFeatureSwitch { Enabled = true },
        SamWriteTools = new CoachFeatureSwitch { Enabled = true }
    };

    private static AIFunction Stub(string name) =>
        AIFunctionFactory.Create(() => "ok", name, "A stand-in for schema-free allow-list checks.");

    /// <summary>
    /// The removal tools are the interesting case: their names contain "remove" and "delete",
    /// which the marker rule rejects outright.
    /// </summary>
    [Theory]
    [InlineData(CoachToolNames.ProposeVocabularyRemoval)]
    [InlineData(CoachToolNames.ProposeSkillArchive)]
    [InlineData(CoachToolNames.ProposeResourceRemoval)]
    [InlineData(CoachToolNames.ProposeVocabularyEntry)]
    [InlineData(CoachToolNames.ProposePreferenceChange)]
    public void An_approved_proposal_tool_is_not_refused_as_a_write(string toolName)
    {
        var registry = CoachToolServiceCollectionExtensions.BuildValidatedRegistry(Enabled());
        var allowList = new CoachToolAllowList(registry);

        var violations = allowList.Validate([Stub(toolName)]);

        violations.Violations
            .Should().NotContain(v => v.Code == "write_tool", $"{toolName} goes through the ledger");
    }

    /// <summary>
    /// The prefix on its own proves nothing. An unregistered name cannot claim the exemption,
    /// which is what stops a renamed tool from opting itself out of the marker rule.
    /// </summary>
    [Fact]
    public void An_unregistered_propose_name_is_still_refused_as_a_write()
    {
        var registry = CoachToolServiceCollectionExtensions.BuildValidatedRegistry(Enabled());
        var allowList = new CoachToolAllowList(registry);

        var violations = allowList.Validate([Stub("propose_delete_everything")]);

        violations.Violations.Should().Contain(v => v.Code == "write_tool");
    }

    /// <summary>
    /// A write-sounding name without the prefix is refused, exactly as before.
    /// </summary>
    [Theory]
    [InlineData("remove_vocabulary")]
    [InlineData("update_profile")]
    [InlineData("delete_resource")]
    [InlineData("save_skill")]
    public void A_bare_write_name_is_still_refused(string toolName)
    {
        var registry = CoachToolServiceCollectionExtensions.BuildValidatedRegistry(Enabled());
        var allowList = new CoachToolAllowList(registry);

        var violations = allowList.Validate([Stub(toolName)]);

        violations.Violations.Should().Contain(v => v.Code == "write_tool");
    }

    /// <summary>
    /// Without a registry there is no way to read a risk class, so the exemption cannot apply.
    /// </summary>
    /// <remarks>
    /// This is the fail-closed case. An allow-list built with no registry is the fallback
    /// configuration, and the fallback must be the strict one. The refusal arrives as
    /// <c>unknown_tool</c> rather than <c>write_tool</c> — with no registry there is no enabled
    /// set to be on, so the name is rejected before the marker rule is reached. Either code is a
    /// refusal; what matters is that the tool does not pass.
    /// </remarks>
    [Fact]
    public void A_registry_free_allow_list_grants_no_exemption()
    {
        var allowList = new CoachToolAllowList();

        var violations = allowList.Validate([Stub(CoachToolNames.ProposeVocabularyRemoval)]);

        violations.IsValid.Should().BeFalse();
        violations.Violations.Should().Contain(v => v.Code == "unknown_tool" || v.Code == "write_tool");
    }

    /// <summary>
    /// When write tools are off, their names are not on the enabled list, so they are refused as
    /// unknown before the marker rule is even reached.
    /// </summary>
    [Fact]
    public void A_write_tool_is_unknown_when_the_feature_is_off()
    {
        var registry = CoachToolServiceCollectionExtensions.BuildValidatedRegistry(new CoachOptions());
        var allowList = new CoachToolAllowList(registry);

        var violations = allowList.Validate([Stub(CoachToolNames.ProposeVocabularyEntry)]);

        violations.Violations.Should().Contain(v => v.Code == "unknown_tool");
    }
}
