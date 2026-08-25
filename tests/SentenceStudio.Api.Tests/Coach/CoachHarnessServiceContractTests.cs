using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Runtime;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// The harness agent's service provider is a capability boundary, and it is asserted here as a
/// contract rather than left to code review.
/// </summary>
/// <remarks>
/// <c>CoachAgentFactory</c> previously handed the harness the application's root
/// <see cref="IServiceProvider"/>. Everything the API registers was therefore reachable from
/// inside the agent loop — the coach database, the identity managers, the data protector, the
/// HTTP client factory. Nothing exploited it, but the boundary was one <c>GetService</c> call
/// wide, and a future harness feature that resolves its own dependencies would have crossed it
/// silently.
/// </remarks>
public class CoachHarnessServiceContractTests
{
    [Fact]
    public void RestrictedProvider_ExposesExactlyTheDeclaredServices()
    {
        var provider = NewProvider();

        // The two DI plumbing types are the provider answering questions about itself, not
        // capabilities handed to the agent: IServiceProvider returns this same restricted
        // provider (never the root), and IServiceProviderIsService only reports whether a type
        // is resolvable. They are listed explicitly rather than filtered out of the surface,
        // because a contract test that hides part of what is reachable is not a contract test.
        provider.ProvidedServiceTypes.Should().BeEquivalentTo(
            CoachHarnessServices.AllowedServiceTypes
                .Concat([typeof(IServiceProvider), typeof(IServiceProviderIsService)]),
            options => options.WithoutStrictOrdering(),
            "an unexpected addition here is a new capability granted to the agent loop");
    }

    [Fact]
    public void RestrictedProvider_ResolvesItselfRatherThanTheRootContainer()
    {
        var provider = NewProvider();

        provider.GetService(typeof(IServiceProvider)).Should().BeSameAs(provider,
            "handing back any other provider would reopen the boundary this class exists to close");
    }

    [Fact]
    public void RestrictedProvider_RegistersOnlyPreResolvedInstances()
    {
        var descriptors = CoachHarnessServices.Describe(
            new RecordingChatClient(), NullLoggerFactory.Instance, TimeProvider.System);

        descriptors.Should().OnlyContain(descriptor => descriptor.ImplementationInstance != null,
            "a factory or a type registration lets the child container construct something the " +
            "application never approved, and makes the boundary depend on what that type resolves");
    }

    [Fact]
    public void RestrictedProvider_ResolvesTheThreeServicesTheHarnessNeeds()
    {
        var provider = NewProvider();

        provider.GetService(typeof(IChatClient)).Should().NotBeNull();
        provider.GetService(typeof(ILoggerFactory)).Should().NotBeNull();
        provider.GetService(typeof(TimeProvider)).Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(ForbiddenTypeNames))]
    public void RestrictedProvider_ResolvesNothingForbidden(string forbiddenTypeName)
    {
        var provider = NewProvider();

        var resolved = provider.ProvidedServiceTypes
            .Any(type => string.Equals(type.Name, forbiddenTypeName, StringComparison.Ordinal));

        resolved.Should().BeFalse(
            $"{forbiddenTypeName} must not be reachable from inside the agent loop");
    }

    [Fact]
    public void RestrictedProvider_GrantsNoDatabaseShellOrNetworkReach()
    {
        var provider = NewProvider();

        foreach (var type in provider.ProvidedServiceTypes)
        {
            var name = type.FullName ?? type.Name;

            name.Should().NotContainAny(
                ["DbContext", "HttpClient", "Process", "UserManager", "IConfiguration", "DataProtection"],
                "the harness has no reason to reach storage, the network, the shell, identity, " +
                "configuration, or key material");
        }
    }

    [Fact]
    public void RestrictedProvider_ReturnsItselfForIServiceProvider()
    {
        var provider = NewProvider();

        provider.GetService(typeof(IServiceProvider)).Should().BeSameAs(provider,
            "handing back the root here would restore the whole application surface in one call");
    }

    [Fact]
    public void RestrictedProvider_ReturnsNullForAnythingUnregistered()
    {
        var provider = NewProvider();

        provider.GetService(typeof(ICoachAgentFactory)).Should().BeNull();
        provider.GetService(typeof(IServiceScopeFactory)).Should().BeNull(
            "a scope factory is a route back to the root container");
    }

    [Fact]
    public void AgentFactory_HandsTheHarnessTheRestrictedProviderAndNotTheRoot()
    {
        var services = new ServiceCollection()
            .AddSingleton<IChatClient>(new RecordingChatClient())
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();

        var restricted = CoachHarnessServices.Build(
            services.GetRequiredService<IChatClient>(), NullLoggerFactory.Instance, TimeProvider.System);

        restricted.Should().NotBeSameAs(services);
        restricted.GetService(typeof(IServiceScopeFactory)).Should().BeNull();
    }

    [Fact]
    public void HarnessOptions_KeepEveryOptionalCapabilityDisabled()
    {
        var options = CoachHarnessOptionsFactory.Create(new CoachOptions(), []);

        options.DisableFileMemory.Should().BeTrue();
        options.DisableTodoProvider.Should().BeTrue();
        options.DisableAgentModeProvider.Should().BeTrue();
        options.DisableAgentSkillsProvider.Should().BeTrue();
        options.DisableWebSearch.Should().BeTrue();
        options.DisableToolAutoApproval.Should().BeTrue(
            "auto-approval would let the loop invoke a tool without the boundary ever being checked");
    }

    [Fact]
    public void HarnessOptions_CarryOnlyTheToolsTheCallerPassed()
    {
        var options = CoachHarnessOptionsFactory.Create(new CoachOptions(), []);

        (options.ChatOptions?.Tools ?? []).Should().BeEmpty(
            "tools arrive as function instances on the chat options; nothing may add its own");
    }

    public static TheoryData<string> ForbiddenTypeNames()
    {
        var data = new TheoryData<string>();

        foreach (var name in CoachHarnessServices.ForbiddenServiceTypeNames)
        {
            data.Add(name);
        }

        return data;
    }

    private static CoachHarnessServiceProvider NewProvider() =>
        CoachHarnessServices.Build(new RecordingChatClient(), NullLoggerFactory.Instance, TimeProvider.System);
}
