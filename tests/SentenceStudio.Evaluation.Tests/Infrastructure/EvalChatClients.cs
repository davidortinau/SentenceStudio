using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using SentenceStudio.Services;

namespace SentenceStudio.Evaluation.Tests.Infrastructure;

/// <summary>
/// Builds chat clients the same way the API and WebApp hosts do (AiClientRegistration.AddTieredChatClients
/// with an AzureOpenAIClient factory): AzureOpenAIClient(resource root, Entra credential)
/// .GetChatClient(deployment).AsIChatClient(). Two deliberate differences, both for spend control:
/// SDK retries are disabled (the hosts keep Polly retries) and the credential is the Azure CLI login.
/// </summary>
public static class EvalChatClients
{
    public static IChatClient CreateAzure(
        Uri resourceEndpoint,
        string deployment,
        TokenCredential? credential = null,
        HttpClient? transport = null)
    {
        var options = new AzureOpenAIClientOptions
        {
            // No automatic retry loops: a failed call fails the scenario and is rerun by hand
            // (cached calls are free on rerun).
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            NetworkTimeout = TimeSpan.FromMinutes(5),
        };

        if (transport is not null)
        {
            options.Transport = new HttpClientPipelineTransport(transport);
        }

        var client = new AzureOpenAIClient(resourceEndpoint, credential ?? new AzureCliCredential(), options);
        return client.GetChatClient(deployment).AsIChatClient();
    }
}

/// <summary>
/// Sits directly above the network client and below every cache, so it sees only live (billed)
/// calls. Before sending it reserves the call's worst-case cost against the ledger and refuses the
/// call if the budget cap would be exceeded; afterwards it records actual token usage.
/// </summary>
public sealed class LiveCallMeterChatClient(
    IChatClient innerClient,
    LiveCallLedger ledger,
    string deployment,
    string role,
    string executionName) : DelegatingChatClient(innerClient)
{
    /// <summary>The scenario the current async flow is evaluating (for the ledger only).</summary>
    public static readonly AsyncLocal<string?> CurrentScenario = new();

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        if (options?.MaxOutputTokens is not int maxOutputTokens)
        {
            throw new InvalidOperationException(
                $"Live {role} call to '{deployment}' has no MaxOutputTokens. Every live call must be capped so worst-case spend is bounded.");
        }

        var inputUpperBound = EstimateInputTokensUpperBound(messageList, options);
        var worstCase = RetailPricing.Cost(deployment, inputUpperBound, maxOutputTokens);
        var scenario = CurrentScenario.Value ?? "(none)";
        ledger.Reserve(worstCase, $"{role} {deployment} {scenario}");

        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        ChatResponse? response = null;
        Exception? failure = null;
        try
        {
            response = await base.GetResponseAsync(messageList, options, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            var usage = response?.Usage;
            long input = usage?.InputTokenCount ?? 0;
            long output = usage?.OutputTokenCount ?? 0;
            decimal cost;
            if (response is not null)
            {
                cost = RetailPricing.Cost(deployment, input, output);
            }
            else
            {
                // A request the service rejected (4xx) is not billed. Anything else (timeout,
                // dropped connection) may have been, so it is booked at its worst case.
                var rejected = failure is ClientResultException { Status: >= 400 and < 500 };
                cost = rejected ? 0m : worstCase;
            }

            ledger.Complete(worstCase, new LiveCallRecord(
                StartedUtc: started,
                ExecutionName: executionName,
                Scenario: scenario,
                Role: role,
                Deployment: deployment,
                InputTokens: input,
                OutputTokens: output,
                ReasoningTokens: usage?.ReasoningTokenCount ?? 0,
                CachedInputTokens: usage?.CachedInputTokenCount ?? 0,
                LatencySeconds: Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
                EstimatedCostUsd: cost,
                WorstCaseCostUsd: worstCase,
                FinishReason: response?.FinishReason?.Value,
                Error: failure is null ? null : $"{failure.GetType().Name}: {Truncate(failure.Message, 300)}"));
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The live eval meter only supports non-streaming calls, so every call is metered.");

    /// <summary>
    /// Deliberately pessimistic: one token per character of every message plus the response-format
    /// schema plus a fixed overhead. Real counts are far lower for English (about 4 characters per token).
    /// </summary>
    internal static long EstimateInputTokensUpperBound(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        long chars = messages.Sum(m => (long)(m.Text?.Length ?? 0));
        if (options?.ResponseFormat is ChatResponseFormatJson { Schema: { } schema })
        {
            chars += schema.GetRawText().Length;
        }

        chars += options?.Instructions?.Length ?? 0;
        return chars + 500;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "...";
}

/// <summary>
/// Adds an output-token cap to target calls. The app's free-text import path sends no ChatOptions
/// at all (AiChatOptionsFactory.Create() returns null when there is no instruction or effort), so this
/// is the only option the eval changes on the model-under-test request. It sits below the response
/// cache, so the cache key still reflects exactly what the app sent.
/// </summary>
public sealed class OutputTokenCapChatClient(IChatClient innerClient, int maxOutputTokens) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options?.Clone() ?? new ChatOptions();
        effective.MaxOutputTokens ??= maxOutputTokens;
        return base.GetResponseAsync(messages, effective, cancellationToken);
    }
}

/// <summary>
/// Adapts the built-in quality evaluators' fixed ChatOptions to a GPT-5-family reasoning judge.
/// The evaluators send Temperature=0, TopP=1, Seed, presence/frequency penalties and
/// MaxOutputTokens=800, tuned for GPT-4o. Reasoning deployments reject the sampling parameters, and
/// 800 tokens would be consumed by hidden reasoning before the tagged answer is written. The adapter
/// removes those sampling parameters, raises the output cap, and sets reasoning effort through the
/// app's own AiChatOptionsFactory convention. The evaluator prompts themselves are not modified.
/// </summary>
public sealed class ReasoningJudgeChatClient(IChatClient innerClient, int maxOutputTokens, string reasoningEffort)
    : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options?.Clone() ?? new ChatOptions();
        effective.Temperature = null;
        effective.TopP = null;
        effective.TopK = null;
        effective.Seed = null;
        effective.PresencePenalty = null;
        effective.FrequencyPenalty = null;
        effective.MaxOutputTokens = maxOutputTokens;

        var effortOptions = AiChatOptionsFactory.Create(reasoningEffort: reasoningEffort)
            ?? throw new InvalidOperationException($"Unsupported judge reasoning effort '{reasoningEffort}'.");
        effective.RawRepresentationFactory = effortOptions.RawRepresentationFactory;

        return base.GetResponseAsync(messages, effective, cancellationToken);
    }
}

/// <summary>One request/response pair observed at the top of the target pipeline.</summary>
public sealed record CapturedCall(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options, ChatResponse Response);

/// <summary>
/// Records exactly what the app sent and received (cache hits included), so the eval can show the
/// real rendered prompt and the raw typed-JSON output in the report and the capture files.
/// </summary>
public sealed class CapturingChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    private readonly List<CapturedCall> _calls = new();

    public IReadOnlyList<CapturedCall> Calls => _calls;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        var response = await base.GetResponseAsync(messageList, options, cancellationToken).ConfigureAwait(false);
        _calls.Add(new CapturedCall(messageList, options, response));
        return response;
    }
}
