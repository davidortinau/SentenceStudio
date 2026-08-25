using FluentAssertions;
using Microsoft.Extensions.Configuration;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Validation.Claims;
using Xunit;

namespace SentenceStudio.Api.Tests.Coach.Claims;

/// <summary>
/// <c>Coach:Grounding:Stage</c> — bound, validated, and never silently defaulted.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this prevents.</b> A stage that does not parse binds to
/// <see cref="CoachGroundingStage.Off"/> and the host starts. The operator sees a running service,
/// a green deployment, and a dashboard of zeros — which reads as "no honesty violations" and
/// actually means "nothing was ever scanned". That is strictly worse than never turning the ladder
/// on, because it is indistinguishable from success.
/// </para>
/// <para>
/// Two validators, because there are two ways to get it wrong. The raw-configuration reader catches
/// the string a human typed; the bound-options reader catches the value a caller set in code, where
/// there is no string left to inspect.
/// </para>
/// </remarks>
public sealed class CoachGroundingStageConfigurationTests
{
    // ------------------------------------------------------------------ binding

    [Fact]
    public void The_default_is_Off()
    {
        new CoachOptions().Grounding.Stage.Should().Be(CoachGroundingStage.Off,
            "W6 ships the ladder; plan §10.2 promotes it in a separate, operator-owned step");
    }

    [Theory]
    [InlineData("Off", CoachGroundingStage.Off)]
    [InlineData("Observe", CoachGroundingStage.Observe)]
    [InlineData("Repair", CoachGroundingStage.Repair)]
    [InlineData("Enforce", CoachGroundingStage.Enforce)]
    [InlineData("observe", CoachGroundingStage.Observe)]
    public void The_exact_key_binds_every_rung(string configured, CoachGroundingStage expected)
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Coach:Grounding:Stage"] = configured
        });

        options.Grounding.Stage.Should().Be(expected);
    }

    [Fact]
    public void Every_rung_has_a_binding_case_above()
    {
        // Non-vacuity: a fifth rung added without a binding case fails here rather than shipping
        // unbound and defaulting to Off.
        Enum.GetNames<CoachGroundingStage>().Should().HaveCount(4);
    }

    // -------------------------------------------------------- raw-string refusal

    [Theory]
    [InlineData("Repare")]
    [InlineData("enforced")]
    [InlineData("On")]
    [InlineData("true")]
    public void A_value_that_is_not_a_rung_stops_startup(string configured)
    {
        var result = ValidateRaw(new Dictionary<string, string?>
        {
            ["Coach:Grounding:Stage"] = configured
        });

        result.Failed.Should().BeTrue(
            "an unrecognised value binds to Off, and a host that starts with grounding silently "
            + "disabled is the failure this validator exists for");
        result.FailureMessage.Should().Contain("Coach:Grounding:Stage");
        result.FailureMessage.Should().Contain(configured);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    public void An_ordinal_stops_startup_even_when_it_would_bind(string configured)
    {
        var result = ValidateRaw(new Dictionary<string, string?>
        {
            ["Coach:Grounding:Stage"] = configured
        });

        result.Failed.Should().BeTrue(
            "an ordinal keeps binding after a rung is inserted, and then it means a different "
            + "rung than the one that was reviewed");
        result.FailureMessage.Should().Contain("Name the stage");
    }

    [Theory]
    [InlineData("Observe,Repair")]
    [InlineData("Off,Enforce")]
    [InlineData("Observe, Repair")]
    public void A_comma_list_stops_startup_although_it_parses(string configured)
    {
        // The nastiest of the malformed values, and the reason this is refused before the parse
        // rather than by it. Enum.TryParse combines a comma list bitwise on any enum, flags or
        // not: 'Observe,Repair' is 1 | 2 = 3, which is Enforce. A typo would promote the ladder
        // two rungs past what was asked for, onto the only rung that refuses learner answers, and
        // Enum.IsDefined would agree it was legitimate.
        Enum.TryParse<CoachGroundingStage>("Observe,Repair", out var parsed).Should().BeTrue(
            "this documents the framework behaviour the guard exists for");
        parsed.Should().Be(CoachGroundingStage.Enforce);

        var result = ValidateRaw(new Dictionary<string, string?>
        {
            ["Coach:Grounding:Stage"] = configured
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("single rung");
    }

    [Fact]
    public void The_flat_spelling_stops_startup()
    {
        var result = ValidateRaw(new Dictionary<string, string?>
        {
            ["Coach:Grounding"] = "Observe"
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Coach:Grounding:Stage");
    }

    [Fact]
    public void An_absent_key_is_accepted_and_means_Off()
    {
        ValidateRaw(new Dictionary<string, string?>()).Succeeded.Should().BeTrue();
        Bind(new Dictionary<string, string?>()).Grounding.Stage.Should().Be(CoachGroundingStage.Off);
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("Observe")]
    [InlineData("Repair")]
    [InlineData("Enforce")]
    public void Every_named_rung_is_accepted(string configured)
    {
        ValidateRaw(new Dictionary<string, string?>
        {
            ["Coach:Grounding:Stage"] = configured
        }).Succeeded.Should().BeTrue();
    }

    // ------------------------------------------------------- bound-value refusal

    [Fact]
    public void A_stage_set_in_code_that_is_not_a_rung_stops_startup()
    {
        var options = Valid();
        options.Grounding.Stage = (CoachGroundingStage)9;

        var result = new CoachOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue(
            "an undefined stage sorts beside a real rung under the >= the engine uses, so it "
            + "behaves like whichever rung it happens to sort beside rather than failing closed");
        result.FailureMessage.Should().Contain("Grounding:Stage");
    }

    [Fact]
    public void A_null_grounding_section_stops_startup()
    {
        var options = Valid();
        options.Grounding = null!;

        new CoachOptionsValidator().Validate(null, options).Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData(CoachGroundingStage.Off)]
    [InlineData(CoachGroundingStage.Observe)]
    [InlineData(CoachGroundingStage.Repair)]
    [InlineData(CoachGroundingStage.Enforce)]
    public void Every_real_rung_passes_bound_validation(CoachGroundingStage stage)
    {
        var options = Valid();
        options.Grounding.Stage = stage;

        new CoachOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    // --------------------------------------------------------------- production

    [Fact]
    public void The_shipped_configuration_does_not_promote_the_stage()
    {
        // W6 may ship Off and only Off. Promotion is plan §10.2 step 5 and belongs to an operator
        // with the previous rung's metrics in hand, not to the workstream that wrote the code.
        var shipped = Directory
            .EnumerateFiles(RepositoryRoot(), "appsettings*.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
            .ToList();

        shipped.Should().NotBeEmpty("the scan must be reading real configuration files");

        foreach (var file in shipped)
        {
            File.ReadAllText(file).Should().NotContain("\"Grounding\"",
                $"{Path.GetFileName(file)} must not promote the grounding stage without authorisation");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static CoachOptions Bind(Dictionary<string, string?> values)
    {
        var options = new CoachOptions();
        new ConfigurationBuilder().AddInMemoryCollection(values).Build()
            .GetSection(CoachOptions.SectionName).Bind(options);
        return options;
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult ValidateRaw(
        Dictionary<string, string?> values) =>
        new CoachConfigurationKeyValidator(
                new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .Validate(null, new CoachOptions());

    /// <summary>Options that pass every other rule, so a failure names the stage and nothing else.</summary>
    private static CoachOptions Valid() => new() { Enabled = true };

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
        return Path.Combine(directory!.FullName, "src");
    }
}
