using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace SentenceStudio.Services;

public static class AiChatOptionsFactory
{
    public static ChatOptions? Create(string? instructions = null, string? reasoningEffort = null)
    {
        var hasInstructions = !string.IsNullOrWhiteSpace(instructions);
        var hasReasoningEffort = !string.IsNullOrWhiteSpace(reasoningEffort);

        if (!hasInstructions && !hasReasoningEffort)
        {
            return null;
        }

        var options = new ChatOptions();
        if (hasInstructions)
        {
            options.Instructions = instructions;
        }

        var effort = ParseReasoningEffort(reasoningEffort);
        if (effort != null)
        {
#pragma warning disable OPENAI001
            options.RawRepresentationFactory = _ => new ChatCompletionOptions
            {
                ReasoningEffortLevel = effort
            };
#pragma warning restore OPENAI001
        }

        return options;
    }

    public static bool IsSupportedReasoningEffort(string? reasoningEffort)
        => string.IsNullOrWhiteSpace(reasoningEffort) || ParseReasoningEffort(reasoningEffort) != null;

#pragma warning disable OPENAI001
    private static ChatReasoningEffortLevel? ParseReasoningEffort(string? reasoningEffort)
        => reasoningEffort?.Trim().ToLowerInvariant() switch
        {
            "minimal" => ChatReasoningEffortLevel.Minimal,
            "low" => ChatReasoningEffortLevel.Low,
            "medium" => ChatReasoningEffortLevel.Medium,
            "high" => ChatReasoningEffortLevel.High,
            _ => (ChatReasoningEffortLevel?)null
        };
#pragma warning restore OPENAI001
}
