using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Coach.Capabilities;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Api.Tests.Coach.Capabilities;

/// <summary>
/// AC-F7, both halves: an illegal capability stops the host, and a staged one never resolves to
/// <see cref="CoachCapabilityAvailability.Present"/>.
/// </summary>
/// <remarks>
/// Asserting only the first half would leave the more likely failure untested. An illegal row is
/// loud; a staged row that quietly resolves as available is silent, and it is the one that would
/// offer a learner a control nothing implements.
/// </remarks>
public class CoachCapabilityStartupValidationTests
{
    // ---------------------------------------------------------------- half one: startup fails

    [Fact]
    public async Task An_illegal_capability_row_stops_the_host_at_startup()
    {
        // A LearnerData row with a Client receipt: §5.4 requires Ledger.
        var illegal = CapabilityFixtures.LegalLearnerData("illegal_at_startup") with
        {
            ReceiptKind = CoachCapabilityReceiptKind.Client
        };

        var act = () => BuildHost(illegal).StartAsync();

        await act.Should().ThrowAsync<CoachCapabilityMatrixException>();
    }

    [Fact]
    public async Task The_shipped_declarations_start_the_host_cleanly()
    {
        // The passing half. Without it, the failing test above could be failing for any reason.
        var host = BuildHost();

        var act = () => host.StartAsync();

        await act.Should().NotThrowAsync();
        await host.StopAsync();
    }

    [Fact]
    public void The_startup_validator_reports_the_population_it_examined()
    {
        // The census the hosted service logs. A validator that swept nothing must not be able to
        // report success, so Validate returns the count rather than void.
        var manifest = CapabilityFixtures.ShippedManifest();

        CoachCapabilityMatrixValidator.Validate(manifest)
            .Should().Be(manifest.All.Count)
            .And.BeGreaterThan(0);
    }

    // ---------------------------------------------------------------- half two: staged never Present

    [Theory]
    [InlineData(CoachCapabilityStage.Off, CoachCapabilityAvailability.AbsentUnimplemented)]
    [InlineData(CoachCapabilityStage.Read, CoachCapabilityAvailability.AbsentUnimplemented)]
    [InlineData(CoachCapabilityStage.Launch, CoachCapabilityAvailability.AbsentUnimplemented)]
    public void A_capability_above_the_promoted_stage_never_resolves_to_present(
        CoachCapabilityStage stage,
        CoachCapabilityAvailability expected)
    {
        var descriptor = CapabilityFixtures.LegalLearnerData($"staged_{stage}") with
        {
            RequiredStage = CoachCapabilityStage.Semantic,
            // The ceiling is as generous as §5.3 allows for a planned row, so the answer can only
            // be coming from the stage.
            MaxAvailability = CoachCapabilityAvailability.PresentOnAnotherSurface
        };

        var resolver = new CoachCapabilityResolver(CapabilityFixtures.ManifestWith(descriptor));

        resolver.Resolve(descriptor.Name, stage, null)
            .Should().Be(expected)
            .And.NotBe(CoachCapabilityAvailability.Present);
    }

    [Fact]
    public void No_capability_above_the_promoted_stage_resolves_to_present_anywhere_in_the_product()
    {
        var rows = CapabilityFixtures.OneLegalRowPerEffectClass();
        var handshakes = new CoachClientCapabilityHandshake?[]
        {
            null,
            CapabilityFixtures.Handshake(int.MaxValue, Enum.GetValues<CoachClientCapabilityCode>())
        };

        var cases = 0;
        foreach (var row in rows)
        foreach (var stage in Enum.GetValues<CoachCapabilityStage>())
        foreach (var handshake in handshakes)
        {
            if (stage >= row.RequiredStage)
            {
                continue;
            }

            new CoachCapabilityResolver(CapabilityFixtures.ManifestWith(row))
                .Resolve(row.Name, stage, handshake)
                .Should().NotBe(CoachCapabilityAvailability.Present);

            cases++;
        }

        cases.Should().BeGreaterThan(0, "the sweep must actually have found rows above the stage");
    }

    private static IHost BuildHost(params CoachCapabilityDescriptor[] extraDeclarations) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IOptions<CoachOptions>>(
                    Options.Create(CapabilityFixtures.AllToolsEnabled()));
                services.AddSingleton<ICoachToolRegistry>(sp =>
                    CoachToolServiceCollectionExtensions.BuildValidatedRegistry(
                        sp.GetRequiredService<IOptions<CoachOptions>>().Value));
                services.AddSingleton<ICoachCapabilityManifest>(sp =>
                    new CoachCapabilityManifest(
                        sp.GetRequiredService<ICoachToolRegistry>(),
                        [.. CoachCapabilityDeclarations.All, .. extraDeclarations]));
                services.AddHostedService<CoachToolRegistryStartupValidator>();
            })
            .Build();
}
