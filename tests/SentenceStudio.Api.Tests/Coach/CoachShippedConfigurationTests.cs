using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Security.DataProtection;
using SentenceStudio.AppHost;
using Xunit;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Guards the configuration files that actually ship, rather than options objects built in a test.
/// The flag dependency rules are covered by <see cref="CoachOptionsTests"/>; what is checked here is
/// that the files on disk obey them, that local development gets the Sam read tools, and that no
/// production-facing file turns them on.
/// </summary>
public class CoachShippedConfigurationTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "src", "SentenceStudio.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the tests run from inside the repository");
        return dir!.FullName;
    }

    private static string SettingsPath(params string[] parts) =>
        Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray());

    private static CoachOptions Load(string path)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build();

        var options = new CoachOptions();
        configuration.GetSection("Coach").Bind(options);
        return options;
    }

    private static IConfiguration Values(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(entry => entry.Key, entry => entry.Value))
            .Build();

    private static IConfiguration AsApiConfiguration(CoachApiEnvironmentResult result) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(result.EnvironmentVariables.ToDictionary(
                entry => entry.Key.Replace("__", ":", StringComparison.Ordinal),
                entry => (string?)entry.Value))
            .Build();

    [Fact]
    public void Publish_manifest_without_explicit_coach_environment_stays_off()
    {
        var developmentAndUserSecrets = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:Enabled"] = "true",
                ["Coach:AllowedUserProfileIds:0"] = CoachOptions.DevAllSentinel,
                ["Coach:DurableHistory:Enabled"] = "true",
                ["Coach:SamOverlay:Enabled"] = "true",
                ["Coach:SamReadTools:Enabled"] = "true"
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coach:AllowedUserProfileIds:1"] = "user-secret-pilot",
                ["Coach:AgentConfigVersion"] = "user-secret-version"
            })
            .Build();

        var selected = CoachConfigurationReader.ForExecutionMode(
            developmentAndUserSecrets,
            isPublishMode: true,
            environmentConfiguration: Values());
        var result = CoachConfigurationReader.ReadApiEnvironment(selected);

        result.EnvironmentVariables.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["Coach__Enabled"] = "false",
            ["Coach__Implementation"] = "baseline"
        });
        result.EnvironmentVariables.Values.Should().NotContain(CoachOptions.DevAllSentinel);
        result.EnvironmentVariables.Values.Should().NotContain("user-secret-pilot");
        result.EnvironmentVariables.Values.Should().NotContain("user-secret-version");
        result.DuplicateAllowlistSourceIndices.Should().BeEmpty();
    }

    [Fact]
    public void Publish_manifest_emits_only_explicit_approved_coach_environment()
    {
        var builderConfiguration = Values(
            ("Coach:Enabled", "true"),
            ("Coach:AllowedUserProfileIds:0", CoachOptions.DevAllSentinel));
        var deploymentEnvironment = Values(
            ("Coach:Enabled", " true "),
            ("Coach:Implementation", " BASELINE "),
            ("Coach:AllowedUserProfileIds:0", " pilot-captain "),
            ("Coach:AllowedUserProfileIds:2", "pilot-jayne"),
            ("Coach:AgentConfigVersion", "prod-3"),
            ("Coach:MaxOutputTokens", "12000"),
            ("Coach:ReasoningEffort", "low"),
            ("Coach:MaxRunsPerDay", "20"),
            ("Coach:MaxRunsPerWeek", "100"),
            ("Coach:DataProtection:KeyVaultKeyIdentifier", "https://vault.example/keys/coach"),
            ("Coach:DataProtection:ManagedIdentityClientId", "11111111-2222-3333-4444-555555555555"),
            ("Coach:DurableHistory:Enabled", "true"),
            ("Coach:Memory:Enabled", "true"),
            ("Coach:SamOverlay:Enabled", "true"),
            ("Coach:SamReadTools:Enabled", "true"),
            ("Coach:SamWriteTools:Enabled", "true"),
            ("Coach:Opportunities:Enabled", "true"),
            ("Coach:Opportunities:RetentionDays", "30"),
            ("Coach:Reports:Enabled", "true"),
            ("Coach:Reports:RetentionDays", "60"));

        var selected = CoachConfigurationReader.ForExecutionMode(
            builderConfiguration,
            isPublishMode: true,
            deploymentEnvironment);
        var result = CoachConfigurationReader.ReadApiEnvironment(selected);

        result.EnvironmentVariables.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["Coach__Enabled"] = "true",
            ["Coach__Implementation"] = "baseline",
            ["Coach__AllowedUserProfileIds__0"] = "pilot-captain",
            ["Coach__AllowedUserProfileIds__1"] = "pilot-jayne",
            ["Coach__AgentConfigVersion"] = "prod-3",
            ["Coach__MaxOutputTokens"] = "12000",
            ["Coach__ReasoningEffort"] = "low",
            ["Coach__MaxRunsPerDay"] = "20",
            ["Coach__MaxRunsPerWeek"] = "100",
            ["Coach__DataProtection__KeyVaultKeyIdentifier"] = "https://vault.example/keys/coach",
            ["Coach__DataProtection__ManagedIdentityClientId"] = "11111111-2222-3333-4444-555555555555",
            ["Coach__DurableHistory__Enabled"] = "true",
            ["Coach__Memory__Enabled"] = "true",
            ["Coach__SamOverlay__Enabled"] = "true",
            ["Coach__SamReadTools__Enabled"] = "true",
            ["Coach__SamWriteTools__Enabled"] = "true",
            ["Coach__Opportunities__Enabled"] = "true",
            ["Coach__Opportunities__RetentionDays"] = "30",
            ["Coach__Reports__Enabled"] = "true",
            ["Coach__Reports__RetentionDays"] = "60"
        });
        result.EnvironmentVariables.Values.Should().NotContain(CoachOptions.DevAllSentinel);
        result.DuplicateAllowlistSourceIndices.Should().BeEmpty();

        var apiConfiguration = AsApiConfiguration(result);
        var options = new CoachOptions();
        apiConfiguration.GetSection(CoachOptions.SectionName).Bind(options);
        new CoachOptionsValidator().Validate(Options.DefaultName, options).Failed.Should().BeFalse();
        options.AllowedUserProfileIds.Should().Equal("pilot-captain", "pilot-jayne");

        var dataProtection = new CoachDataProtectionOptions();
        apiConfiguration.GetSection(CoachDataProtectionOptions.SectionName).Bind(dataProtection);
        dataProtection.KeyVaultKeyIdentifier.Should().Be("https://vault.example/keys/coach");
        dataProtection.ManagedIdentityClientId.Should().Be("11111111-2222-3333-4444-555555555555");

        var keyRingPlan = CoachKeyRingPlanner.Resolve(
            dataProtection,
            "Endpoint=https://storage.example/;ContainerName=coach-dataprotection",
            durableContentEnabled: true,
            isProduction: true);
        keyRingPlan.IsKeyVaultProtected.Should().BeTrue();
        keyRingPlan.ManagedIdentityClientId.Should().Be("11111111-2222-3333-4444-555555555555");
    }

    [Theory]
    [InlineData("Coach:Enabled", "yes")]
    [InlineData("Coach:Implementation", "experimental")]
    public void Publish_manifest_rejects_invalid_required_values(string key, string value)
    {
        var selected = CoachConfigurationReader.ForExecutionMode(
            Values(),
            isPublishMode: true,
            environmentConfiguration: Values((key, value)));

        var act = () => CoachConfigurationReader.ReadApiEnvironment(selected);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Publish_manifest_leaves_optional_value_validation_to_the_api()
    {
        var selected = CoachConfigurationReader.ForExecutionMode(
            Values(),
            isPublishMode: true,
            environmentConfiguration: Values(
                ("Coach:MaxRunsPerDay", "201"),
                ("Coach:MaxRunsPerWeek", "201")));
        var result = CoachConfigurationReader.ReadApiEnvironment(selected);

        result.EnvironmentVariables["Coach__MaxRunsPerDay"].Should().Be("201",
            "the AppHost must not sanitize an operator value and bypass the API's validation");

        var options = new CoachOptions();
        AsApiConfiguration(result).GetSection(CoachOptions.SectionName).Bind(options);
        var validation = new CoachOptionsValidator().Validate(Options.DefaultName, options);

        validation.Failed.Should().BeTrue();
        validation.FailureMessage.Should().Contain("MaxRunsPerDay");
    }

    [Fact]
    public void Local_run_mode_retains_the_complete_builder_configuration()
    {
        var developmentConfiguration = Values(
            ("Coach:Enabled", "true"),
            ("Coach:AllowedUserProfileIds:0", CoachOptions.DevAllSentinel),
            ("Coach:DurableHistory:Enabled", "true"),
            ("Coach:SamOverlay:Enabled", "true"),
            ("Coach:SamReadTools:Enabled", "true"));
        var deploymentEnvironment = Values(
            ("Coach:Enabled", "false"),
            ("Coach:AllowedUserProfileIds:0", "deployment-pilot"));

        var selected = CoachConfigurationReader.ForExecutionMode(
            developmentConfiguration,
            isPublishMode: false,
            deploymentEnvironment);
        var result = CoachConfigurationReader.ReadApiEnvironment(selected);

        selected.Should().BeSameAs(developmentConfiguration);
        result.EnvironmentVariables["Coach__Enabled"].Should().Be("true");
        result.EnvironmentVariables["Coach__AllowedUserProfileIds__0"]
            .Should().Be(CoachOptions.DevAllSentinel);
        result.EnvironmentVariables["Coach__DurableHistory__Enabled"].Should().Be("true");
        result.EnvironmentVariables["Coach__SamOverlay__Enabled"].Should().Be("true");
        result.EnvironmentVariables["Coach__SamReadTools__Enabled"].Should().Be("true");
        result.EnvironmentVariables.Values.Should().NotContain("deployment-pilot");
    }

    [Fact]
    public void Local_development_enables_the_sam_read_tools()
    {
        var options = Load(SettingsPath("src", "SentenceStudio.AppHost", "appsettings.Development.json"));

        options.IsSamReadToolsEnabled.Should().BeTrue(
            "phase two is meant to be exercisable locally without hand-editing configuration");
    }

    [Fact]
    public void The_development_flag_chain_is_complete_and_valid()
    {
        var options = Load(SettingsPath("src", "SentenceStudio.AppHost", "appsettings.Development.json"));

        // SamReadTools depends on SamOverlay, which depends on DurableHistory. Enabling the leaf
        // without its prerequisites fails startup, so the local file has to carry the whole chain.
        options.IsDurableHistoryEnabled.Should().BeTrue();
        options.IsSamOverlayEnabled.Should().BeTrue();
        options.IsSamReadToolsEnabled.Should().BeTrue();

        // The development file is a *development* file: it names the __dev_all__ cohort sentinel,
        // which CoachOptionsValidator permits only in Development. Validating it under any other
        // environment is expected to fail, and the companion assertion below fixes that.
        var result = new CoachOptionsValidator(new DevelopmentEnvironment())
            .Validate(Options.DefaultName, options);

        result.Failed.Should().BeFalse(
            "the shipped development file must pass the same validator that runs at startup, " +
            $"but it reported: {result.FailureMessage}");

        new CoachOptionsValidator().Validate(Options.DefaultName, options).Failed.Should().BeTrue(
            "this file is local-only and must not validate on a host that is not Development");
    }

    /// <summary>The environment the AppHost development file is written for.</summary>
    private sealed class DevelopmentEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Microsoft.Extensions.Hosting.Environments.Development;
        public string ApplicationName { get; set; } = "SentenceStudio.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public void Local_development_does_not_enable_the_sam_write_tools()
    {
        var options = Load(SettingsPath("src", "SentenceStudio.AppHost", "appsettings.Development.json"));

        options.IsSamWriteToolsEnabled.Should().BeFalse(
            "phase two is reads only; writes are a later phase with their own review");
    }

    [Theory]
    [InlineData("src", "SentenceStudio.Api", "appsettings.Production.json")]
    [InlineData("src", "SentenceStudio.Api", "appsettings.Development.json")]
    public void No_api_settings_file_turns_the_sam_read_tools_on(params string[] parts)
    {
        var options = Load(SettingsPath(parts));

        options.IsSamReadToolsEnabled.Should().BeFalse();
        options.IsSamWriteToolsEnabled.Should().BeFalse();
    }

    [Fact]
    public void The_default_options_leave_every_sam_flag_off()
    {
        // Nothing configured at all is the production posture: the API reads its flags from the
        // environment, and an absent flag must never be read as an opt in.
        var options = new CoachOptions();

        options.IsSamOverlayEnabled.Should().BeFalse();
        options.IsSamReadToolsEnabled.Should().BeFalse();
        options.IsSamWriteToolsEnabled.Should().BeFalse();
    }

    [Fact]
    public void The_development_file_uses_the_nested_flag_spelling()
    {
        // A retired flat spelling such as "Coach:SamReadTools": "true" binds to nothing and fails
        // startup through CoachConfigurationKeyValidator. Reading the raw JSON catches a file that
        // looks enabled to a human but is invisible to the binder.
        var path = SettingsPath("src", "SentenceStudio.AppHost", "appsettings.Development.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var coach = document.RootElement.GetProperty("Coach");

        foreach (var flag in new[] { "DurableHistory", "SamOverlay", "SamReadTools" })
        {
            coach.TryGetProperty(flag, out var element).Should().BeTrue($"{flag} must be present");
            element.ValueKind.Should().Be(JsonValueKind.Object,
                $"{flag} must be the nested object form with an Enabled child");
            element.TryGetProperty("Enabled", out _).Should().BeTrue();
        }
    }
}
