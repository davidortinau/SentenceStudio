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
    private readonly IUserScopeProvider? _scope;
    private readonly ILogger<DeterministicPlanGenerator> _logger;

    public DeterministicPlanGenerator(
        DeterministicPlanBuilder builder,
        ILogger<DeterministicPlanGenerator> logger,
        IUserScopeProvider? scope = null)
    {
        _builder = builder;
        _logger = logger;
        _scope = scope;
    }

    public Task<PlanSkeleton?> GenerateAsync(string? userProfileId = null, CancellationToken ct = default)
    {
        // Phase B: userProfileId is threaded all the way down through
        // DeterministicPlanBuilder so the HTTP API (multi-user) and the in-
        // process MAUI path (single-user with IPreferences fallback) share
        // one user-scoped pipeline.
        //
        // Fail-soft: when an IUserScopeProvider is in DI (API head registers
        // HttpUserScopeProvider, AppLib registers DeviceUserScopeProvider)
        // and no explicit userProfileId was supplied, resolve from the
        // request principal / device session. If the provider can't resolve
        // (e.g. AppLib cold start before SetActiveUser), fall through to
        // the builder's legacy IPreferences-based fallback so we don't
        // regress the mobile pre-onboarding path. On the API host the
        // scope provider always resolves (PlanService is the only caller
        // and only routes authenticated requests), so the "fall through"
        // branch is unreachable in practice from HTTP.
        if (string.IsNullOrEmpty(userProfileId) && _scope is not null)
        {
            if (_scope.TryGetUserProfileId(out var resolved))
            {
                userProfileId = resolved;
                _logger.LogDebug(
                    "DeterministicPlanGenerator: resolved userProfileId='{UserProfileId}' from IUserScopeProvider.",
                    resolved);
            }
        }

        return _builder.BuildPlanAsync(userProfileId, ct)!;
    }
}
