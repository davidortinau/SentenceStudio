using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SentenceStudio.Services.PlanGeneration;

namespace SentenceStudio.Services.Plans;

/// <summary>
/// Placeholder <see cref="ILlmPlanGenerator"/> for v1. Registered only when
/// an <see cref="IChatClient"/> is available. Today it delegates to the
/// deterministic builder — there is no real LLM call yet. When the real
/// LLM-augmented narrative composition lands, replace the body of
/// <see cref="GenerateAsync"/>; callers do not change.
/// </summary>
public sealed class LlmPlanGenerator : ILlmPlanGenerator
{
    private readonly IDeterministicPlanGenerator _deterministic;
    private readonly IChatClient _chatClient;
    private readonly ILogger<LlmPlanGenerator> _logger;

    public LlmPlanGenerator(
        IDeterministicPlanGenerator deterministic,
        IChatClient chatClient,
        ILogger<LlmPlanGenerator> logger)
    {
        _deterministic = deterministic;
        _chatClient = chatClient;
        _logger = logger;
    }

    public async Task<PlanSkeleton?> GenerateAsync(string? userProfileId = null, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "LlmPlanGenerator: v1 implementation defers to deterministic generator. Real LLM-augmented narrative is a v2 deliverable.");

        // Keep the IChatClient field referenced so removing it accidentally
        // surfaces a compile error rather than a silent regression.
        _ = _chatClient;

        return await _deterministic.GenerateAsync(userProfileId, ct).ConfigureAwait(false);
    }
}
