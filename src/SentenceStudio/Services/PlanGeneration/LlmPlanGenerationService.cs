using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models.DailyPlanGeneration;

namespace SentenceStudio.Services.PlanGeneration;

public interface ILlmPlanGenerationService
{
    Task<DailyPlanResponse?> GeneratePlanAsync(CancellationToken ct = default);
}

public class LlmPlanGenerationService : ILlmPlanGenerationService
{
    private readonly IChatClient _chatClient;
    private readonly DeterministicPlanBuilder _deterministicBuilder;
    private readonly ILogger<LlmPlanGenerationService> _logger;

    private static readonly string PromptTemplate;

    static LlmPlanGenerationService()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "SentenceStudio.Resources.Prompts.DailyPlanGeneration.scriban";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException($"Could not find embedded resource: {resourceName}");

        using var reader = new StreamReader(stream);
        PromptTemplate = reader.ReadToEnd();
    }

    public LlmPlanGenerationService(
        IChatClient chatClient,
        DeterministicPlanBuilder deterministicBuilder,
        ILogger<LlmPlanGenerationService> logger)
    {
        _chatClient = chatClient;
        _deterministicBuilder = deterministicBuilder;
        _logger = logger;
    }

    public async Task<DailyPlanResponse?> GeneratePlanAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("🎯 Starting plan generation with deterministic builder");

            // Step 1: Generate deterministic plan (90% of work - fast, reliable, pedagogically sound)
            var planSkeleton = await _deterministicBuilder.BuildPlanAsync(ct);

            if (planSkeleton == null)
            {
                _logger.LogWarning("❌ Deterministic plan builder returned null");
                return null;
            }

            _logger.LogInformation("✅ Deterministic plan built: {ActivityCount} activities, {Minutes}min total",
                planSkeleton.Activities.Count, planSkeleton.TotalMinutes);
            _logger.LogInformation("📚 Primary resource: {ResourceTitle} (ID: {ResourceId})",
                planSkeleton.PrimaryResource.Title, planSkeleton.PrimaryResource.Id);

            if (planSkeleton.VocabularyReview != null)
            {
                _logger.LogInformation("📝 Vocab review: {WordCount} words (~{Minutes}min)",
                    planSkeleton.VocabularyReview.WordCount, planSkeleton.VocabularyReview.EstimatedMinutes);
            }

            // Step 2: Convert to DailyPlanResponse format
            var response = new DailyPlanResponse
            {
                Activities = planSkeleton.Activities.Select(a => new PlanActivity
                {
                    ActivityType = a.ActivityType,
                    EstimatedMinutes = a.EstimatedMinutes,
                    Priority = a.Priority,
                    ResourceId = a.ResourceId,
                    SkillId = a.SkillId,
                    VocabWordCount = a.ActivityType == "VocabularyReview" ? planSkeleton.VocabularyReview?.WordCount : null
                }).ToList(),
                Rationale = BuildRationale(planSkeleton)
            };

            _logger.LogInformation("✅ Plan ready with {ActivityCount} activities", response.Activities.Count);
            _logger.LogInformation("💡 Rationale: {Rationale}", response.Rationale);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Plan generation failed");
            return null;
        }
    }

    private string BuildRationale(PlanSkeleton plan)
    {
        var parts = new List<string>();

        // Resource selection rationale
        parts.Add($"Selected \"{plan.PrimaryResource.Title}\" ({plan.PrimaryResource.SelectionReason})");

        // Vocabulary rationale if applicable
        if (plan.VocabularyReview != null)
        {
            parts.Add($"Reviewing {plan.VocabularyReview.WordCount} vocabulary words for spaced repetition practice");
        }

        // Activity sequence rationale
        var activityTypes = string.Join(" → ", plan.Activities.Select(a => a.ActivityType));
        parts.Add($"Following pedagogical sequence: {activityTypes}");

        return string.Join(". ", parts) + ".";
    }
}
