using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Api.Coach.Tools;

/// <summary>
/// Eagerly resolves <see cref="ICoachToolRegistry"/> at host startup so an invalid registry
/// stops the process before it serves any request — not on the first Sam turn.
/// </summary>
/// <remarks>
/// The registry singleton factory already calls <see cref="Validation.CoachOutputContract.ValidateRegistry"/>
/// and throws on envelope drift, but that factory is lazy: it only runs when something resolves
/// the singleton. This hosted service forces the resolution at startup, converting the lazy DI
/// failure into a hard startup failure that blocks deployment.
/// </remarks>
public sealed class CoachToolRegistryStartupValidator : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CoachToolRegistryStartupValidator> _logger;

    public CoachToolRegistryStartupValidator(
        IServiceProvider services,
        ILogger<CoachToolRegistryStartupValidator> logger)
    {
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolve the singleton — the DI factory runs construct → freeze → validate.
        // If the registry is invalid, the exception propagates and stops the host.
        var registry = _services.GetRequiredService<ICoachToolRegistry>();

        // Capability matrix: resolving the manifest builds it from the now-frozen registry, and
        // validating it here turns an illegal declaration into a startup failure rather than
        // something a learner discovers mid-turn. Validate returns the population it examined so
        // a validator that silently swept nothing cannot pass for one that swept everything.
        var manifest = _services.GetRequiredService<Capabilities.ICoachCapabilityManifest>();
        var examined = Capabilities.CoachCapabilityMatrixValidator.Validate(manifest);

        // Read-capability metadata: the table Sam draws on when it states what a read covers,
        // orders and bounds. A row that has drifted from its tool becomes a confident false
        // statement to a learner, so it stops the host here rather than surfacing mid-turn. Like
        // the matrix validator, it returns the population it swept so a validator that examined
        // nothing cannot pass for one that examined everything.
        var metadataTable =
            _services.GetService<Capabilities.ICoachReadCapabilityMetadataSource>()?.All
            ?? Capabilities.CoachReadCapabilityMetadataTable.All;
        var readsDescribed =
            Capabilities.CoachReadCapabilityMetadataValidator.Validate(registry, metadataTable);

        _logger.LogInformation(
            "Coach tool registry validated at startup: {Total} registered, {Enabled} enabled, "
            + "{Capabilities} capabilities matrix-validated, {Reads} reads metadata-validated",
            registry.All.Count,
            registry.Enabled.Count,
            examined,
            readsDescribed);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
