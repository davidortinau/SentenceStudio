using Azure.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;
using ServiceDefaults = Microsoft.Extensions.Hosting.Extensions;

namespace SentenceStudio.UnitTests.Diagnostics;

/// <summary>Opt-in Entra authentication for the Azure Monitor exporters (#168).</summary>
public class AzureMonitorCredentialTests
{
    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => default;
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => default;
    }

    private static IConfiguration Config(string? mode) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["AzureMonitor:Authentication"] = mode })
        .Build();

    [Fact]
    public void Unset_keeps_the_instrumentation_key_and_never_builds_a_credential()
    {
        var built = false;

        ServiceDefaults.ResolveAzureMonitorCredential(Config(null), () => { built = true; return new FakeCredential(); })
            .Should().BeNull();
        built.Should().BeFalse();
    }

    [Theory]
    [InlineData("ManagedIdentity")]
    [InlineData("managedidentity")]
    public void ManagedIdentity_uses_the_host_credential(string mode)
    {
        var credential = new FakeCredential();

        ServiceDefaults.ResolveAzureMonitorCredential(Config(mode), () => credential).Should().BeSameAs(credential);
    }

    [Fact]
    public void ManagedIdentity_without_a_host_credential_fails_at_startup()
    {
        var act = () => ServiceDefaults.ResolveAzureMonitorCredential(Config("ManagedIdentity"), null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*supplies no credential*");
    }

    [Fact]
    public void An_unknown_mode_fails_at_startup_instead_of_falling_back()
    {
        var act = () => ServiceDefaults.ResolveAzureMonitorCredential(Config("Key"), () => new FakeCredential());

        act.Should().Throw<InvalidOperationException>().WithMessage("*not supported*");
    }
}
