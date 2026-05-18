using SentenceStudio.Services.PlanGeneration;

namespace SentenceStudio.Services.Plans;

/// <summary>
/// Common abstraction for any plan generation strategy. Concrete generators
/// (<see cref="IDeterministicPlanGenerator"/>, <see cref="ILlmPlanGenerator"/>)
/// return the same <see cref="PlanSkeleton"/> shape so callers can swap them
/// per Strategy without branching on type.
/// </summary>
public interface IPlanGenerator
{
    /// <summary>
    /// Build a plan skeleton for the supplied user. The user's local date is
    /// resolved internally through <c>IPlanDateContext</c>. When
    /// <paramref name="userProfileId"/> is <c>null</c>, generators fall back
    /// to the active <c>IUserScopeProvider</c> identity (server scopes
    /// per-request, mobile uses the singleton active profile).
    /// </summary>
    /// <remarks>
    /// Phase A of the daily-plan server-contract refactor still defers
    /// per-user scoping to the underlying single-user repos. The parameter
    /// is preserved on the interface so callers (especially the new
    /// <c>IPlanService</c>) can forward the request principal's user id
    /// today and we can thread it through the repos in Phase B without an
    /// API break.
    /// </remarks>
    Task<PlanSkeleton?> GenerateAsync(string? userProfileId = null, CancellationToken ct = default);
}

/// <summary>
/// Deterministic plan generator. Always registered. Wraps
/// <see cref="DeterministicPlanBuilder"/> and never calls an LLM.
/// </summary>
public interface IDeterministicPlanGenerator : IPlanGenerator { }

/// <summary>
/// LLM-backed plan generator. Registered only when an
/// <see cref="Microsoft.Extensions.AI.IChatClient"/> is available in DI. A v1
/// implementation may delegate to the deterministic generator while real
/// LLM-enhanced narrative composition lands. Callers must NOT take a hard
/// dependency on this interface — resolve as optional and fall back to
/// <see cref="IDeterministicPlanGenerator"/> when absent.
/// </summary>
public interface ILlmPlanGenerator : IPlanGenerator { }
