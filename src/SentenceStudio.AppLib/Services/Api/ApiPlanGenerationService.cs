using SentenceStudio.Contracts.Plans;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services.PlanGeneration;
using SentenceStudio.Shared.Models.DailyPlanGeneration;

namespace SentenceStudio.Services.Api;

public sealed class ApiPlanGenerationService(IPlansApiClient plansApiClient, ILogger<ApiPlanGenerationService> logger) : ILlmPlanGenerationService
{
    private readonly IPlansApiClient _plansApiClient = plansApiClient;
    private readonly ILogger<ApiPlanGenerationService> _logger = logger;

    public async Task<DailyPlanResponse?> GeneratePlanAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _plansApiClient.GeneratePlanAsync(new GeneratePlanRequest(), ct);

            return new DailyPlanResponse
            {
                Rationale = response.Rationale,
                Activities = response.Activities.Select(activity => new PlanActivity
                {
                    ActivityType = activity.ActivityType,
                    EstimatedMinutes = activity.EstimatedMinutes,
                    Priority = activity.Priority,
                    ResourceId = activity.ResourceId,
                    SkillId = activity.SkillId,
                    VocabWordCount = activity.VocabWordCount
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate plan from API.");
            return null;
        }
    }
}
