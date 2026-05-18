using Microsoft.Extensions.Logging;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Plans;

/// <summary>
/// API-head fallback for <see cref="IDeterministicPlanGenerator"/>. Returns
/// an empty <see cref="PlanSkeleton"/> so the HTTP <c>POST
/// /api/v1/plans/today/generate</c> route can persist a parent
/// <c>DailyPlan</c> row + empty completion list and the Flutter client can
/// integrate against the contract.
/// </summary>
/// <remarks>
/// <para>
/// The full deterministic builder still lives in
/// <c>SentenceStudio.Services.PlanGeneration.DeterministicPlanBuilder</c>,
/// but it currently consumes single-user repositories
/// (<see cref="SentenceStudio.Data.UserProfileRepository.GetAsync()"/>
/// and friends) that rely on <c>IPreferences</c>. The API doesn't register
/// <c>IPreferences</c> nor several of those repos. Wiring real generation
/// on the server is Phase B of the daily-plan refactor (plan.md §7) and
/// requires:
/// </para>
/// <list type="number">
///   <item><description>Adding <c>GetAsync(string userProfileId)</c>
///   overloads on the affected repos.</description></item>
///   <item><description>Registering
///   <c>LearningResourceRepository</c>, <c>SkillProfileRepository</c>,
///   and the rest on the API host.</description></item>
///   <item><description>Replacing the <c>UserProfileRepository</c> active-
///   profile lookup with <c>IUserScopeProvider.UserProfileId</c>.</description></item>
/// </list>
/// <para>
/// Until then, the in-process MAUI Blazor path (which has all of the above
/// already wired via <c>AppLib.CoreServiceExtensions</c>) continues to use
/// the real <see cref="DeterministicPlanGenerator"/>. The Flutter team can
/// integrate against the HTTP surface today; the LLM/AUTO branch is
/// already plumbed.
/// </para>
/// </remarks>
public sealed class StubDeterministicPlanGenerator : IDeterministicPlanGenerator
{
    private readonly ILogger<StubDeterministicPlanGenerator> _logger;

    public StubDeterministicPlanGenerator(ILogger<StubDeterministicPlanGenerator> logger)
    {
        _logger = logger;
    }

    public Task<PlanSkeleton?> GenerateAsync(string? userProfileId = null, CancellationToken ct = default)
    {
        _logger.LogWarning(
            "StubDeterministicPlanGenerator: returning an empty skeleton for user '{UserProfileId}'. "
            + "Real server-side generation is pending the Phase B repo refactor.",
            userProfileId);

        var skeleton = new PlanSkeleton
        {
            Activities = new List<PlannedActivity>(),
            TotalMinutes = 0,
            ResourceSelectionReason = "Server-side plan generation is not yet wired on this host.",
        };
        return Task.FromResult<PlanSkeleton?>(skeleton);
    }
}
