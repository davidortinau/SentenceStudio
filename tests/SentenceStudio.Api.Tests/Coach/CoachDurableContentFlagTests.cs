using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SentenceStudio.Api.Security.DataProtection;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The two configuration keys that decide whether the API demands a durable key ring.
/// </summary>
/// <remarks>
/// <para>
/// These are asserted by their literal spelling on purpose. The gate is the only reader of
/// <c>Coach:DurableHistory:Enabled</c> and <c>Coach:Memory:Enabled</c>, and a misspelling on
/// either side would not fail anything: the flag would silently read false, the API would start
/// in Production with an ephemeral key ring, and durable learner content would be written under a
/// key that disappears on the next restart. Nothing would look wrong until someone tried to read
/// their own history back.
/// </para>
/// <para>
/// So the tests below go through real configuration rather than passing booleans to the planner.
/// A rename that misses one of these keys fails here, and the AppHost forwards the same two names
/// so a deployment can actually set them.
/// </para>
/// </remarks>
public class CoachDurableContentFlagTests
{
    public const string DurableHistoryKey = "Coach:DurableHistory:Enabled";
    public const string MemoryKey = "Coach:Memory:Enabled";

    [Fact]
    public void NeitherFlagSet_MeansNoDurableContent()
    {
        Gate([]).IsDurableContentEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(DurableHistoryKey)]
    [InlineData(MemoryKey)]
    public void EitherFlagAlone_IsEnoughToRequireADurableRing(string key)
    {
        // Deliberately not "both". Memory without history, or history without memory, still means
        // learner text is on disk, and either one alone has to pull the same requirement.
        Gate(new Dictionary<string, string?> { [key] = "true" })
            .IsDurableContentEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData(DurableHistoryKey)]
    [InlineData(MemoryKey)]
    public void InProduction_AFlagWithNoKeyRingConfiguredFailsStartup(string key)
    {
        var act = () => Configure(
            production: true,
            settings: new Dictionary<string, string?> { [key] = "true" });

        act.Should().Throw<CoachDataProtectionConfigurationException>(
            "starting is worse than not starting: the API would accept content it cannot read back");
    }

    [Theory]
    [InlineData(DurableHistoryKey)]
    [InlineData(MemoryKey)]
    public void InProduction_AFlagWithStorageButNoKeyVaultKeyFailsStartup(string key)
    {
        var act = () => Configure(
            production: true,
            settings: new Dictionary<string, string?>
            {
                [key] = "true",
                ["ConnectionStrings:coach-keyring"] = "Endpoint=https://acct.blob.core.windows.net/"
            });

        // Durable storage alone leaves the keys themselves unwrapped at rest.
        act.Should().Throw<CoachDataProtectionConfigurationException>();
    }

    [Fact]
    public void InProduction_NoFlagsMeansNoKeyRingRequirement()
    {
        var act = () => Configure(production: true, settings: []);

        act.Should().NotThrow(
            "a deployment that persists no learner content has nothing to keep decryptable");
    }

    [Fact]
    public void TheFailureNamesTheConfigurationKeyAndNeverAValue()
    {
        const string secretish = "https://contoso.vault.azure.net/keys/coach/abc123";

        var error = Record.Exception(() => Configure(
            production: true,
            settings: new Dictionary<string, string?>
            {
                [DurableHistoryKey] = "true",
                ["ConnectionStrings:coach-keyring"] = $"AccountKey={secretish}"
            }))!;

        error.Message.Should().NotContain(secretish,
            "a startup failure is one of the easiest ways for a connection string to reach a log");
        error.Message.Should().Contain("KeyVaultKeyIdentifier",
            "an operator needs to know which key to set");
    }

    [Theory]
    [InlineData(DurableHistoryKey)]
    [InlineData(MemoryKey)]
    public void OutsideProduction_AFlagWithNoKeyRingStillStarts(string key)
    {
        var act = () => Configure(
            production: false,
            settings: new Dictionary<string, string?> { [key] = "true" });

        act.Should().NotThrow(
            "a developer without Azure credentials must still be able to run the app");
    }

    private static ICoachDurableContentGate Gate(Dictionary<string, string?> settings) =>
        CoachDurableContentOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

    private static void Configure(bool production, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        new ServiceCollection().AddCoachDataProtection(
            configuration,
            new StubEnvironment(production ? Environments.Production : Environments.Development));
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
