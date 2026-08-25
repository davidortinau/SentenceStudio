using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Security.DataProtection;
using SentenceStudio.Api.Tests.Infrastructure;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// One spelling of the durable-history switch, read the same way by everything that reads it.
/// </summary>
/// <remarks>
/// <para>
/// This class exists because of a real defect. The runtime bound a flat <c>Coach:DurableHistory</c>
/// boolean while the Data Protection guard read the nested <c>Coach:DurableHistory:Enabled</c>.
/// Both spellings looked identical in a deployment manifest and neither side ever complained, so a
/// host configured with the flat key turned the encrypted ledger on while the guard concluded no
/// durable content existed and permitted an ephemeral key ring. Learner history was written under a
/// key that did not survive the next restart, and nothing surfaced until someone tried to read
/// their own conversation back.
/// </para>
/// <para>
/// The fix is structural rather than a rename: the switch is a nested object, so only the canonical
/// key binds, and the guard projects the same bound <see cref="CoachOptions"/> the runtime uses
/// instead of issuing its own configuration reads. The tests below pin both halves — that the
/// canonical key moves runtime and gate together, and that the retired flat key cannot quietly
/// half-enable anything.
/// </para>
/// </remarks>
public class CoachCanonicalFeatureKeyTests
{
    private const string CanonicalHistoryKey = "Coach:DurableHistory:Enabled";
    private const string CanonicalMemoryKey = "Coach:Memory:Enabled";
    private const string RetiredFlatHistoryKey = "Coach:DurableHistory";
    private const string RetiredFlatMemoryKey = "Coach:Memory";

    [Fact]
    public void TheCanonicalKey_EnablesTheRuntimeAndTheKeyRingGateTogether()
    {
        var configuration = Build(new Dictionary<string, string?> { [CanonicalHistoryKey] = "true" });

        var runtime = BindCoachOptions(configuration);
        var gate = CoachDurableContentOptions.FromConfiguration(configuration);

        runtime.IsDurableHistoryEnabled.Should().BeTrue();
        gate.DurableHistoryEnabled.Should().BeTrue();
        gate.IsDurableContentEnabled.Should().BeTrue(
            "the ledger and the key ring requirement must switch on in the same breath");
    }

    [Fact]
    public void TheCanonicalMemoryKey_EnablesTheRuntimeAndTheKeyRingGateTogether()
    {
        var configuration = Build(new Dictionary<string, string?> { [CanonicalMemoryKey] = "true" });

        BindCoachOptions(configuration).IsMemoryEnabled.Should().BeTrue();
        CoachDurableContentOptions.FromConfiguration(configuration).MemoryEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData(RetiredFlatHistoryKey)]
    [InlineData(RetiredFlatMemoryKey)]
    public void TheRetiredFlatKeyAlone_EnablesNothing(string flatKey)
    {
        var configuration = Build(new Dictionary<string, string?> { [flatKey] = "true" });

        var runtime = BindCoachOptions(configuration);
        var gate = CoachDurableContentOptions.FromConfiguration(configuration);

        runtime.IsDurableHistoryEnabled.Should().BeFalse();
        runtime.IsMemoryEnabled.Should().BeFalse();
        gate.IsDurableContentEnabled.Should().BeFalse(
            "the flat spelling must not half-enable a feature behind the guard's back");
    }

    [Theory]
    [InlineData(RetiredFlatHistoryKey)]
    [InlineData(RetiredFlatMemoryKey)]
    public void TheRetiredFlatKeyAlone_FailsStartupValidation(string flatKey)
    {
        // Not enabling is necessary but not sufficient. An operator who set the flat key believes
        // the feature is on; starting quietly with it off is its own incident.
        var act = () => ValidateStartup(new Dictionary<string, string?> { [flatKey] = "true" });

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void TheValidationFailure_NamesTheKeyToChange()
    {
        var error = Record.Exception(() =>
            ValidateStartup(new Dictionary<string, string?> { [RetiredFlatHistoryKey] = "true" }))!;

        error.Message.Should().Contain(RetiredFlatHistoryKey);
        error.Message.Should().Contain(CanonicalHistoryKey,
            "the message has to carry the fix, not just the complaint");
        error.Message.Should().Contain("Coach__DurableHistory__Enabled",
            "most deployments set this as an environment variable");
    }

    [Fact]
    public void BothSpellingsAtOnce_StillFailsRatherThanPickingAWinner()
    {
        var settings = new Dictionary<string, string?>
        {
            [RetiredFlatHistoryKey] = "true",
            [CanonicalHistoryKey] = "true"
        };

        var act = () => ValidateStartup(settings);

        act.Should().Throw<OptionsValidationException>(
            "silently preferring one spelling is how the original mismatch survived review");
    }

    [Fact]
    public void ARealMemorySection_DoesNotTripTheFlatKeyCheck()
    {
        // Coach:Memory is a legitimate section with children. Only a *value* at that path is the
        // retired flat spelling, and the validator has to tell those apart.
        var settings = new Dictionary<string, string?>
        {
            [CanonicalMemoryKey] = "true",
            ["Coach:Memory:MaxContextFacts"] = "3"
        };

        var act = () => ValidateStartup(settings);

        act.Should().NotThrow();
    }

    [Fact]
    public void NoFlagsAtAll_PassesValidation()
    {
        var act = () => ValidateStartup([]);

        act.Should().NotThrow();
    }

    [Fact]
    public void InProduction_TheCanonicalKeyWithNoDurableKeyConfigFailsStartup()
    {
        var act = () => new ServiceCollection().AddCoachDataProtection(
            Build(new Dictionary<string, string?> { [CanonicalHistoryKey] = "true" }),
            new StubEnvironment(Environments.Production));

        act.Should().Throw<CoachDataProtectionConfigurationException>(
            "the canonical key is now the one that arms the guard, so it must arm it in Production");
    }

    [Fact]
    public void InProduction_TheRetiredFlatKeyDoesNotArmTheGuard()
    {
        // Documents the shape of the old defect from the other side: the flat key no longer
        // reaches the runtime either, so there is no configuration that writes durable content
        // while the guard sleeps. The host still refuses to boot, via validation, in
        // TheRetiredFlatKeyAlone_FailsStartupValidation.
        var act = () => new ServiceCollection().AddCoachDataProtection(
            Build(new Dictionary<string, string?> { [RetiredFlatHistoryKey] = "true" }),
            new StubEnvironment(Environments.Production));

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheAvailabilityFlagReflectsTheCanonicalOption(bool enabled)
    {
        await using var factory = new CoachApiFactory
        {
            CoachEnabled = true,
            CohortUserProfileId = CohortUser,
            DurableHistory = enabled
        };

        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CoachOptions>>();

        options.CurrentValue.IsDurableHistoryEnabled.Should().Be(enabled,
            "the factory sets the canonical key, so the real host must read it");

        var gate = scope.ServiceProvider.GetRequiredService<ICoachDurableContentGate>();
        gate.IsDurableContentEnabled.Should().Be(enabled,
            "the guard and the runtime resolve the same effective switch");
    }

    [Fact]
    public async Task TheAvailabilityPayloadCarriesNoConfigurationKeys()
    {
        await using var factory = new CoachApiFactory
        {
            CoachEnabled = true,
            CohortUserProfileId = CohortUser,
            DurableHistory = true
        };

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                TestJwtGenerator.GenerateToken(userProfileId: CohortUser));

        var payload = await client.GetStringAsync("/api/v1/coach/availability");

        foreach (var forbidden in new[]
                 {
                     "Coach:", "Coach__", "DurableHistory:", "Memory:", "Enabled",
                     "DataProtection", "KeyVault", "ConnectionString"
                 })
        {
            payload.Should().NotContain(forbidden,
                "an availability response tells a client what it may do, never how the server is wired");
        }
    }

    private const string CohortUser = "canonical-key-learner";

    private static IConfiguration Build(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static CoachOptions BindCoachOptions(IConfiguration configuration)
    {
        var options = new CoachOptions();
        configuration.GetSection(CoachOptions.SectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// Runs the same <c>ValidateOnStart</c> pass the host runs, so a passing test here means a
    /// booting host would have thrown.
    /// </summary>
    private static void ValidateStartup(Dictionary<string, string?> settings)
    {
        var configuration = Build(settings);

        var services = new ServiceCollection();
        services.AddCoachRuntime(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "SentenceStudio.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
