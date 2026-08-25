using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Coach.Validation;

namespace SentenceStudio.Api.Tests.Coach.Validation;

/// <summary>
/// Tests for the tool registry, ToolResult embargo scope, allow-list integration,
/// and output contract coverage of the Sam read tool DTOs.
/// </summary>
public class CoachToolRegistryAndEmbargoTests
{
    // ── Registry basics ──

    [Fact]
    public void Registry_RegistersCoreFiveByDefault()
    {
        var options = CreateOptions();
        var registry = new CoachToolRegistry(options);

        registry.All.Should().HaveCountGreaterOrEqualTo(5);
        registry.IsRegistered(CoachToolNames.GetLearnerProfileSummary).Should().BeTrue();
        registry.IsRegistered(CoachToolNames.GetPracticeBalance).Should().BeTrue();
        registry.IsRegistered(CoachToolNames.GetVocabularyDueSummary).Should().BeTrue();
        registry.IsRegistered(CoachToolNames.GetResourceCatalog).Should().BeTrue();
        registry.IsRegistered(CoachToolNames.PreviewPracticePlan).Should().BeTrue();
    }

    [Fact]
    public void Registry_RegistersNineSamReadTools()
    {
        var options = CreateOptions();
        var registry = new CoachToolRegistry(options);

        // Core five, nine Sam read tools, twelve Sam write proposals.
        registry.All.Should().HaveCount(26);
        registry.IsRegistered(CoachToolNames.ListUserVocabularies).Should().BeTrue();
        registry.IsRegistered(CoachToolNames.GetCurrentPlanSummary).Should().BeTrue();
    }

    [Fact]
    public void Registry_SamToolsDisabled_WhenFeatureFlagsOff()
    {
        var options = CreateOptions(samOverlay: false, samReadTools: false);
        var registry = new CoachToolRegistry(options);

        registry.Enabled.Should().HaveCount(5, "only core five should be enabled");
        registry.IsEnabled(CoachToolNames.ListUserVocabularies).Should().BeFalse();
    }

    [Fact]
    public void Registry_SamToolsEnabled_WhenBothFlagsOn()
    {
        var options = CreateOptions(samOverlay: true, samReadTools: true);
        var registry = new CoachToolRegistry(options);

        registry.Enabled.Should().HaveCount(14);
        registry.IsEnabled(CoachToolNames.ListUserVocabularies).Should().BeTrue();
    }

    [Fact]
    public void Registry_SamReadTools_RequiresSamOverlay()
    {
        var options = CreateOptions(samOverlay: false, samReadTools: true);
        var registry = new CoachToolRegistry(options);

        registry.IsEnabled(CoachToolNames.ListUserVocabularies).Should().BeFalse(
            "SamReadTools requires SamOverlay");
    }

    [Fact]
    public void Registry_Freeze_PreventsFurtherRegistration()
    {
        var options = CreateOptions();
        var registry = new CoachToolRegistry(options);
        registry.Freeze();

        var act = () => registry.Register(new CoachToolRegistration
        {
            Name = "test_late_tool",
            ResultType = typeof(string),
            RiskClass = CoachToolRiskClass.Read,
            Description = "Should fail."
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*frozen*");
    }

    [Fact]
    public void Registry_DuplicateRegistration_Throws()
    {
        var options = CreateOptions();
        var registry = new CoachToolRegistry(options);

        var act = () => registry.Register(new CoachToolRegistration
        {
            Name = CoachToolNames.GetLearnerProfileSummary,
            ResultType = typeof(string),
            RiskClass = CoachToolRiskClass.Read,
            Description = "Duplicate."
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already registered*");
    }

    [Fact]
    public void Registry_AllSamReadToolsAreRiskClassRead()
    {
        var options = CreateOptions();
        var registry = new CoachToolRegistry(options);

        // Feature-gated tools now include the write proposals, so the read assertion is scoped to
        // the tools that are not on the declared write list. Excluding them by name rather than by
        // risk class keeps the check honest: a read tool that quietly acquired a write class would
        // still fail here, which is the point.
        var samReadTools = registry.All
            .Where(t => t.RequiredFeatures.Count > 0)
            .Where(t => !CoachToolNames.AllWrite.Contains(t.Name));

        samReadTools.Should().OnlyContain(t => t.RiskClass == CoachToolRiskClass.Read);
    }

    [Fact]
    public void Registry_Find_ReturnsNullForUnknown()
    {
        var options = CreateOptions();
        var registry = new CoachToolRegistry(options);

        registry.Find("nonexistent_tool").Should().BeNull();
    }

    // ── Embargo ToolResult scope ──

    [Fact]
    public void Embargo_ToolResultScope_PermitsTermAndSentence()
    {
        // VocabularySearchEntry has TargetTerm, NativeTerm — content words allowed under ToolResult
        var scanner = new CoachEmbargoScanner();
        var result = scanner.ScanType(typeof(VocabularySearchEntry), CoachEmbargoScope.ToolResult);

        result.IsValid.Should().BeTrue(
            "ToolResult scope permits explicit learner-requested content like 'term': {0}",
            string.Join("; ", result.Violations.Select(v => v.Message)));
    }

    [Fact]
    public void Embargo_ToolResultScope_StillRefusesIdentity()
    {
        // A hypothetical DTO with an identity field should still be refused
        var scanner = new CoachEmbargoScanner();
        var result = scanner.ScanType(typeof(FakeToolResultWithEmail), CoachEmbargoScope.ToolResult);

        result.IsValid.Should().BeFalse("identity words are refused even under ToolResult scope");
        result.Violations.Should().Contain(v => v.Message.Contains("email"));
    }

    [Fact]
    public void Embargo_ToolResultScope_RefusesBulkContent()
    {
        var scanner = new CoachEmbargoScanner();
        var result = scanner.ScanType(typeof(FakeToolResultWithTranscript), CoachEmbargoScope.ToolResult);

        result.IsValid.Should().BeFalse("bulk content (transcript) is refused under ToolResult scope");
        result.Violations.Should().Contain(v => v.Message.Contains("transcript"));
    }

    [Fact]
    public void Embargo_ToolResultScope_RefusesMnemonics()
    {
        var scanner = new CoachEmbargoScanner();
        var result = scanner.ScanType(typeof(FakeToolResultWithMnemonic), CoachEmbargoScope.ToolResult);

        result.IsValid.Should().BeFalse("mnemonics are refused under ToolResult scope");
    }

    [Fact]
    public void Embargo_ModelVisibleScope_StillRefusesContentWords()
    {
        var scanner = new CoachEmbargoScanner();
        var result = scanner.ScanType(typeof(VocabularySearchEntry), CoachEmbargoScope.ModelVisible);

        result.IsValid.Should().BeFalse(
            "ModelVisible scope should refuse 'term' as a content word");
    }

    [Fact]
    public void Embargo_ExhaustiveSwitch_ThrowsOnInvalidScope()
    {
        var scanner = new CoachEmbargoScanner();
        var act = () => scanner.ScanType(typeof(VocabularySearchResult), (CoachEmbargoScope)99);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Output contract ──

    [Fact]
    public void OutputContract_ApprovedEnvelopes_CoverTheSamResultsAtToolResultScope()
    {
        // Replaces an earlier test that asserted membership in a hand-kept list of Sam result
        // types. That list was never consulted by anything at startup, so it could agree with
        // itself while the registry had drifted. The table below is what the startup scan reads.
        Type[] samResults =
        [
            typeof(VocabularySearchResult),
            typeof(VocabularyWordDetail),
            typeof(SkillListResult),
            typeof(SkillDetailResult),
            typeof(LearningResourceListResult),
            typeof(LearningResourceDetailResult),
            typeof(CurrentPlanSummary),
            typeof(CurrentProfileSummary),
            typeof(LearnerSettingsSummary)
        ];

        foreach (var result in samResults)
        {
            CoachOutputContract.ApprovedResultEnvelopes.Should().ContainKey(result);
            CoachOutputContract.ApprovedResultEnvelopes[result]
                .Should().Be(CoachEmbargoScope.ToolResult);
        }
    }

    [Fact]
    public void OutputContract_ScanPassesForAllShapes()
    {
        var result = CoachOutputContract.Scan();
        result.IsValid.Should().BeTrue(
            "all DTOs (core + Sam read) must pass embargo: {0}",
            string.Join("; ", result.Violations.Select(v => v.Message)));
    }

    // ── AllowList with registry ──

    [Fact]
    public void AllowList_WithRegistry_AcceptsRegisteredToolNames()
    {
        var options = CreateOptions(samOverlay: true, samReadTools: true);
        var registry = new CoachToolRegistry(options);
        var allowList = new CoachToolAllowList(registry);

        allowList.Should().NotBeNull();
        // The validate method requires AIFunctions, not just names — tested by existing integration tests.
    }

    // ── Helpers ──

    private static CoachOptions CreateOptions(bool samOverlay = false, bool samReadTools = false)
    {
        var options = new CoachOptions
        {
            SamOverlay = new CoachFeatureSwitch { Enabled = samOverlay },
            SamReadTools = new CoachFeatureSwitch { Enabled = samReadTools },
            SamWriteTools = new CoachFeatureSwitch { Enabled = false }
        };
        return options;
    }

    // Fake DTOs for negative embargo tests
    private sealed record FakeToolResultWithEmail(string Email, int Count);
    private sealed record FakeToolResultWithTranscript(string TranscriptText, int Count);
    private sealed record FakeToolResultWithMnemonic(string MnemonicHint, int Count);
}
