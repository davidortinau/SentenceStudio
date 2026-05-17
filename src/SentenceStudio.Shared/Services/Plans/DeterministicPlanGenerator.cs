using Microsoft.Extensions.Logging;
using SentenceStudio.Services.PlanGeneration;

namespace SentenceStudio.Services.Plans;

/// <summary>
/// Default <see cref="IDeterministicPlanGenerator"/> — a thin adapter over
/// <see cref="DeterministicPlanBuilder"/>. Exists so the new
/// <c>IPlanService</c> (server) and future thin clients can resolve a plan
/// generator by interface without taking a direct dependency on the
/// long-lived <see cref="DeterministicPlanBuilder"/> class, and so we can
/// register an LLM-augmented variant side-by-side later without touching
/// callers.
/// </summary>
public sealed class DeterministicPlanGenerator : IDeterministicPlanGenerator
{
    private readonly DeterministicPlanBuilder _builder;
    private readonly ILogger<DeterministicPlanGenerator> _logger;

    public DeterministicPlanGenerator(
        DeterministicPlanBuilder builder,
        ILogger<DeterministicPlanGenerator> logger)
    {
        _builder = builder;
        _logger = logger;
    }

    public Task<PlanSkeleton?> GenerateAsync(string? userProfileId = null, CancellationToken ct = default)
    {
        // userProfileId is reserved for Phase B; today the builder still
        // routes through the single-user repos. See IPlanGenerator XML doc.
        if (!string.IsNullOrEmpty(userProfileId))
        {
            _logger.LogDebug(
                "DeterministicPlanGenerator: userProfileId='{UserProfileId}' is reserved for Phase B repo scoping; ignored for now.",
                userProfileId);
        }

        return _builder.BuildPlanAsync(ct)!;
    }
}
