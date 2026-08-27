using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Services;

namespace SentenceStudio.Api.Coach.Agents;

/// <summary>
/// Builds the <see cref="ChatOptions"/> both coach arms send. One definition, so the baseline
/// and the harness cannot be measured against each other with different limits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why reasoning effort is set here.</b> On a GPT-5 reasoning model, hidden reasoning tokens
/// are billed and counted as output. Microsoft Learn is explicit: <c>max_completion_tokens</c>
/// "cover[s] reasoning tokens, visible output tokens, and formatting tokens", and running out
/// "can occur before the model produces any visible output. You pay for input and reasoning
/// tokens but receive no answer."
/// (<c>https://learn.microsoft.com/azure/foundry/openai/how-to/reasoning</c>)
/// </para>
/// <para>
/// That is exactly what a live session hit: a first, trivial constraint turn answered fine, and
/// the tool-using suggestion turn came back <c>FinishReason=length, TextLength=0</c>. The cap
/// was read as a visible-output budget when it is a total-generation budget, and the model
/// spent all of it thinking.
/// </para>
/// <para>
/// A coach turn is bounded classification and extraction against a closed schema, so it wants
/// the least reasoning the model offers. GPT-5 reasoning models accept a <c>minimal</c>
/// effort setting, which is the default here. One documented consequence: parallel tool calls
/// are unavailable at <c>minimal</c>, so the coach's read-only tools are called in
/// sequence — more round trips, same answer, and each still bounded by
/// <see cref="CoachOptions.MaxIterationsPerRequest"/>.
/// </para>
/// <para>
/// The effort value is configurable so an operator can move to <c>low</c> without a redeploy if
/// the trajectory evaluation shows the model under-using its tools at <c>minimal</c>.
/// </para>
/// </remarks>
public static class CoachChatOptionsFactory
{
    /// <summary>The effort the coach asks for when configuration does not say otherwise.</summary>
    public const string DefaultReasoningEffort = "minimal";

    public static ChatOptions Create(CoachOptions options, IReadOnlyList<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tools);

        // Reuses the application's single mapping from an effort name to the OpenAI
        // reasoning-effort level, so the coach cannot accept a vocabulary the rest of the
        // app rejects. Instructions are always supplied, so this never returns null.
        var chatOptions = AiChatOptionsFactory.Create(
            CoachInstructions.Instructions,
            options.ReasoningEffort)!;

        // A hard ceiling on everything the model generates for one call: reasoning tokens,
        // visible output, and formatting. It is never removed — only sized so a bounded turn
        // can finish inside it.
        chatOptions.MaxOutputTokens = options.MaxOutputTokens;
        chatOptions.Tools = [.. tools];

        // Temperature is deliberately not set. Some models reject any explicit value:
        // gpt-5-mini answers HTTP 400 "temperature does not support 0 with this model. Only
        // the default (1) value is supported." Omitting the property lets each provider apply
        // its own default, which is the portable choice. Determinism comes from the closed
        // turn-intent schema and the application reducer, not from a sampling knob.

        return chatOptions;
    }
}
