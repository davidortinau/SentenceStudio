using SentenceStudio.Contracts.Plans;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Tools.SamTools;

/// <summary>
/// Today's plan, read once per request for the plan tools. <see cref="IPlanService.GetTodayAsync"/>
/// also loads the completions and works out the streak, so one turn that calls
/// <c>get_current_plan_summary</c> and <c>get_plan_vocabulary</c> pays for that once.
/// </summary>
/// <remarks>Scoped: a request reads one learner's plan, and a new request reads it fresh.</remarks>
public sealed class CoachTodayPlanReader(IPlanService plans)
{
    private Task<TodaysPlanDto?>? _today;

    public Task<TodaysPlanDto?> GetTodayAsync(CancellationToken ct = default) =>
        _today ??= plans.GetTodayAsync(ct);
}
