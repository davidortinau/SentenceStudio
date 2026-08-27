using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Agents;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using CoachToolNames = SentenceStudio.Api.Coach.Tools.CoachToolNames;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Builds a real harness agent over the restricted provider and runs a turn through it.
/// </summary>
/// <remarks>
/// <para>
/// The descriptor-level contract test proves the provider exposes only what it should. It cannot
/// prove the harness still works with that much taken away — the agent could reach for a service
/// during construction or mid-turn and get a null, and the first sign of it would be a
/// <see cref="NullReferenceException"/> in production rather than a compile error here.
/// </para>
/// <para>
/// So this exercises the real <c>CoachAgentFactory</c> path end to end. The chat client is
/// scripted, so a passing run touches no network and needs no credential.
/// </para>
/// </remarks>
public class CoachHarnessLiveTurnTests
{
    [Fact]
    public void TheFactoryBuildsAHarnessAgentFromTheRestrictedProvider()
    {
        var factory = NewFactory(out _);

        factory.TryCreateHarnessAgent(Tools()).Should().NotBeNull(
            "the restricted provider must still satisfy everything harness construction resolves");
    }

    [Fact]
    public void TheAgentIsBuiltAgainstTheRestrictedProviderAndNotTheRoot()
    {
        var factory = NewFactory(out var root);

        factory.TryCreateHarnessAgent(Tools());

        var restricted = factory.RestrictedHarnessServices;
        restricted.Should().NotBeNull();
        restricted.Should().NotBeSameAs(root);
        restricted!.GetService(typeof(IServiceProvider)).Should().BeSameAs(restricted);
    }

    [Fact]
    public void EveryServiceTheHarnessNeedsResolves()
    {
        var factory = NewFactory(out _);
        factory.TryCreateHarnessAgent(Tools());
        var restricted = factory.RestrictedHarnessServices!;

        foreach (var required in CoachHarnessServices.AllowedServiceTypes)
        {
            restricted.GetService(required).Should().NotBeNull(
                "{0} is a declared harness dependency", required.Name);
        }
    }

    [Fact]
    public void TheApplicationServicesAreReachableFromTheRootButNotFromTheHarness()
    {
        var factory = NewFactory(out var root);
        factory.TryCreateHarnessAgent(Tools());
        var restricted = factory.RestrictedHarnessServices!;

        // Asserting against the root as well is what makes this meaningful: it shows the service
        // genuinely exists in the application and is being withheld, rather than being absent
        // from the test setup and trivially unresolvable in both.
        root.GetService(typeof(CoachDbContext)).Should().NotBeNull("the app really does have it");
        restricted.GetService(typeof(CoachDbContext)).Should().BeNull("and the agent loop does not");

        root.GetService(typeof(IHttpClientFactory)).Should().NotBeNull();
        restricted.GetService(typeof(IHttpClientFactory)).Should().BeNull();
    }

    [Fact]
    public async Task AHarnessTurnCompletesWithoutReachingForAnythingItWasDenied()
    {
        const string json = """
            {
              "Kind": "NoChange",
              "AcceptanceState": "NotApplicable",
              "CoachMessage": "Today's Plan is unchanged.",
              "EvidenceReferences": []
            }
            """;

        var factory = NewFactory(out _, new ScriptedChatClient(json));
        var agent = factory.TryCreateHarnessAgent(Tools());

        agent.Should().NotBeNull();

        // Running the turn is the point: a service the harness resolves lazily would surface here
        // and nowhere earlier.
        var response = await agent!.RunAsync("keep today the same");

        response.Should().NotBeNull();
        response.Text.Should().NotBeNullOrWhiteSpace();
    }

    private static IReadOnlyList<AIFunction> Tools() =>
        CoachToolNames.All.Select(name => AIFunctionFactory.Create(() => "{}", name)).ToList();

    private static CoachAgentFactory NewFactory(out IServiceProvider root, IChatClient? chatClient = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(chatClient ?? new ScriptedChatClient("""{"Kind":"NoChange","CoachMessage":"ok"}"""));
        services.AddLogging();
        services.AddHttpClient();

        // A stand-in for the application services the old code path exposed to the agent loop by
        // handing over the root provider.
        // Registered but never opened — resolvability is the whole point of the assertion.
        services.AddDbContext<CoachDbContext>(options => options.UseSqlite("Data Source=:memory:"));

        root = services.BuildServiceProvider();

        return new CoachAgentFactory(
            root,
            new TestOptionsMonitor<CoachOptions>(new CoachOptions { Enabled = true }),
            NullLoggerFactory.Instance);
    }
}
