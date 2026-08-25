using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Runtime;
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
