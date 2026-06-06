namespace SentenceStudio.Services;

/// <summary>
/// Selects which deployed model a request should run on. Quality-vs-speed tiers map to
/// distinct Foundry deployments (see <c>AI:OpenAI:Models</c> config).
/// </summary>
public enum AiTier
{
    /// <summary>Fast generation tier (e.g. gpt-5-mini) — cloze, story, translation, shadowing, etc.</summary>
    Fast = 0,

    /// <summary>Reasoning tier (e.g. gpt-5) — grading, feedback, grammar correction, vision.</summary>
    Reasoning = 1
}

public static class AiTierExtensions
{
    public const string FastKey = "ai-fast";
    public const string ReasoningKey = "ai-reasoning";

    /// <summary>DI service key for the keyed <c>IChatClient</c> backing this tier.</summary>
    public static string ToKey(this AiTier tier) => tier switch
    {
        AiTier.Reasoning => ReasoningKey,
        _ => FastKey
    };
}
