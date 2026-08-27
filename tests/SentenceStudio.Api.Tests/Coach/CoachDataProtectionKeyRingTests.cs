using SentenceStudio.Api.Security.DataProtection;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The Data Protection key ring decision. Protected coach rows are only readable while the key
/// that wrapped them still exists, so where those keys live is a data-durability question, not a
/// configuration detail.
/// </summary>
/// <remarks>
/// These tests target <see cref="CoachKeyRingPlanner.Resolve"/>, which is pure. Every production
/// failure branch is reachable without an Azure account, a credential, or a network call.
/// </remarks>
public class CoachDataProtectionKeyRingTests
{
    private const string EmulatorConnectionString =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Zm9vYmFy;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;";

    private const string KeyIdentifier = "https://contoso.vault.azure.net/keys/coach/abc123";

    [Fact]
    public void Resolve_WithoutAConnectionString_FallsBackToTheHostRingOutsideProduction()
    {
        var plan = CoachKeyRingPlanner.Resolve(
            new CoachDataProtectionOptions(), connectionString: null,
            durableContentEnabled: false, isProduction: false);

        plan.Mode.Should().Be(CoachKeyRingMode.HostDefault);
        plan.IsDurable.Should().BeFalse();
    }

    [Fact]
    public void Resolve_WithAnEmulatorConnectionString_PersistsToBlobStorage()
    {
        var plan = CoachKeyRingPlanner.Resolve(
            new CoachDataProtectionOptions(), EmulatorConnectionString,
            durableContentEnabled: true, isProduction: false);

        plan.Mode.Should().Be(CoachKeyRingMode.AzureBlobConnectionString);
        plan.IsDurable.Should().BeTrue(
            "a local restart must not orphan the rows the previous run protected");
        plan.ContainerName.Should().Be("coach-dataprotection");
        plan.BlobName.Should().Be("keys.xml");
    }

    [Fact]
    public void Resolve_StripsTheAspireContainerSegmentFromTheConnectionString()
    {
        // Aspire appends its own container hint; the Azure SDK does not understand it and throws
        // on an unknown segment.
        var plan = CoachKeyRingPlanner.Resolve(
            new CoachDataProtectionOptions(),
            EmulatorConnectionString + "ContainerName=coach-dataprotection;",
            durableContentEnabled: true, isProduction: false);

        plan.Mode.Should().Be(CoachKeyRingMode.AzureBlobConnectionString);
        plan.ConnectionString.Should().NotContain("ContainerName=");
    }

    [Fact]
    public void Resolve_WithAnEndpointConnectionString_UsesTheUriAndManagedIdentity()
    {
        var plan = CoachKeyRingPlanner.Resolve(
            new CoachDataProtectionOptions { KeyVaultKeyIdentifier = KeyIdentifier },
            "Endpoint=https://contoso.blob.core.windows.net/",
            durableContentEnabled: true, isProduction: true);

        plan.Mode.Should().Be(CoachKeyRingMode.AzureBlobUri);
        plan.ContainerUri.Should().NotBeNull();
        plan.ContainerUri!.ToString().Should().EndWith("/coach-dataprotection");
    }

    [Fact]
    public void Resolve_UsesAStableApplicationNameByDefault()
    {
        var plan = CoachKeyRingPlanner.Resolve(
            new CoachDataProtectionOptions(), EmulatorConnectionString,
            durableContentEnabled: false, isProduction: false);

        plan.ApplicationName.Should().Be("SentenceStudio.Api.v1",
            "the framework default derives the name from the content root, so it changes between " +
            "images and directories and silently isolates the ring from its own payloads");
    }

    [Fact]
    public void Resolve_InProductionWithDurableContent_RequiresADurableRing()
    {
        var resolve = () => CoachKeyRingPlanner.Resolve(
            new CoachDataProtectionOptions { KeyVaultKeyIdentifier = KeyIdentifier },
            connectionString: null,
            durableContentEnabled: true, isProduction: true);

        resolve.Should().Throw<CoachDataProtectionConfigurationException>(
            "an ephemeral ring in production makes every row written before a restart permanently " +
            "unreadable, and failing at startup is the only point where that is still recoverable");
    }

    [Fact]
    public void Resolve_InProductionWithDurableContent_RequiresKeyVaultWrapping()
    {
        var resolve = () => CoachKeyRingPlanner.Resolve(
            new CoachDataProtectionOptions(), EmulatorConnectionString,
            durableContentEnabled: true, isProduction: true);

        resolve.Should().Throw<CoachDataProtectionConfigurationException>(
            "unwrapped keys in blob storage mean storage access alone is enough to read every " +
            "learner conversation");
    }

    [Fact]
    public void Resolve_InProductionWithoutDurableContent_DoesNotDemandAKeyRing()
    {
        var plan = CoachKeyRingPlanner.Resolve(
            new CoachDataProtectionOptions(), connectionString: null,
            durableContentEnabled: false, isProduction: true);

        plan.Mode.Should().Be(CoachKeyRingMode.HostDefault,
            "the requirement is tied to durable content, so a deployment that stores none still boots");
    }

    [Theory]
    [InlineData("http://contoso.vault.azure.net/keys/coach/abc123")]
    [InlineData("not-a-uri")]
    public void Resolve_RejectsAKeyIdentifierThatIsNotHttps(string identifier)
    {
        var resolve = () => CoachKeyRingPlanner.Resolve(
            new CoachDataProtectionOptions { KeyVaultKeyIdentifier = identifier },
            EmulatorConnectionString,
            durableContentEnabled: true, isProduction: true);

        resolve.Should().Throw<CoachDataProtectionConfigurationException>();
    }

    [Fact]
    public void ConfigurationErrors_NameTheKeyAndNeverTheValue()
    {
        var error = Record.Exception(() => CoachKeyRingPlanner.Resolve(
            new CoachDataProtectionOptions(), EmulatorConnectionString,
            durableContentEnabled: true, isProduction: true))!;

        error.Message.Should().Contain("KeyVaultKeyIdentifier", "an operator needs to know which key to set");
        error.Message.Should().NotContain("AccountKey",
            "a startup exception is written to the console and to every log sink attached to it");
        error.Message.Should().NotContain("devstoreaccount1");
    }

    [Fact]
    public void Describe_LeaksNoSecretMaterial()
    {
        var plan = CoachKeyRingPlanner.Resolve(
            new CoachDataProtectionOptions { KeyVaultKeyIdentifier = KeyIdentifier },
            EmulatorConnectionString,
            durableContentEnabled: true, isProduction: true);

        var description = plan.Describe();

        description.Should().NotContain("AccountKey");
        description.Should().NotContain("Zm9vYmFy");
        description.Should().NotContain(KeyIdentifier,
            "a key identifier in the logs tells an attacker exactly which vault object to go after");
        description.Should().Contain("coach-dataprotection", "an operator still has to be able to verify the ring");
    }

    [Fact]
    public void KeyVaultProtection_IsReportedSeparatelyFromDurability()
    {
        var durableUnwrapped = CoachKeyRingPlanner.Resolve(
            new CoachDataProtectionOptions(), EmulatorConnectionString,
            durableContentEnabled: false, isProduction: false);

        durableUnwrapped.IsDurable.Should().BeTrue();
        durableUnwrapped.IsKeyVaultProtected.Should().BeFalse(
            "local development persists keys without a vault, and the two properties must not be conflated");
    }
}
