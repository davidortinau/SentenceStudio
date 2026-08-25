using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Telemetry;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Coach.Agents;

/// <summary>
/// The single turn body both coach arms use.
/// </summary>
/// <remarks>
/// <para>
/// The baseline arm and the harness arm differ in one thing only: which <see cref="AIAgent"/>
/// they build. Everything after that — session resume, structured output, timeout and cancel
/// mapping, tool-failure mapping, usage accounting, and session serialization — runs here, so
/// an A/B comparison measures the agent pipeline and not two hand-written code paths that
/// drifted apart.
/// </para>
/// <para>
/// The runner never writes application data and never logs model or learner text.
/// </para>
/// </remarks>
public static class CoachAgentTurnRunner
{
    /// <summary>
    /// The serializer both arms use for structured output and for <c>AgentSession</c> state,
    /// so a session written by one arm stays readable by the same arm after a restart.
    /// </summary>
    public static JsonSerializerOptions IntentSerializerOptions { get; } = new(AIJsonUtilities.DefaultOptions);

    /// <summary>Runs exactly one turn against an already-built agent.</summary>
    public static async Task<CoachAgentTurnResult> RunAsync(
        AIAgent agent,
        CoachAgentTurnRequest request,
        CoachOptions options,
        CoachImplementation implementation,
        CoachTelemetry telemetry,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(logger);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.RequestTimeout);

        using var activity = telemetry.StartRun(implementation);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var (session, deserializationFailed) = await ResumeOrCreateSessionAsync(agent, request.AgentSessionJson, timeoutSource.Token)
                .ConfigureAwait(false);

            // A malformed AgentSession with no prior messages means the model would run
            // blind — no memory, no ledger context. Return early so the caller can rebuild
            // context from the conversation ledger and retry exactly once.
            if (deserializationFailed && request.PriorMessages.Count == 0)
            {
                telemetry.RecordSessionRestoration("deserialization_fallback");
                logger.LogWarning(
                    "[Coach] Session {SessionId}: AgentSession deserialization failed and no prior messages available. Signalling RequiresRebuild.",
                    request.SessionId);
                return new CoachAgentTurnResult
                {
                    Outcome = CoachAgentOutcome.Completed,
                    RequiresRebuild = true
                };
            }

            if (deserializationFailed)
            {
                telemetry.RecordSessionRestoration("deserialization_fallback");
            }

            var response = await agent
                .RunAsync<CoachTurnIntent>(
                    CoachInstructions.BuildTurnMessage(request),
                    session,
                    IntentSerializerOptions,
                    options: null,
                    cancellationToken: timeoutSource.Token)
                .ConfigureAwait(false);

            var usage = ToUsage(response.Usage);
            var serialized = await SerializeSessionAsync(agent, session, timeoutSource.Token).ConfigureAwait(false);

            if (!TryReadIntent(response, out var intent))
            {
                // Never log the model's text: it can quote the learner, and it can quote a
                // word that is due for review. These shape facts are enough to tell a
                // truncated answer from a fenced one from an empty one, which is what the
                // first live occurrence of this failure had no way to distinguish.
                logger.LogWarning(
                    "[Coach] Session {SessionId}: the model answer did not match the turn-intent schema. " +
                    "FinishReason={FinishReason}, TextLength={TextLength}, ContainsJsonObject={ContainsJsonObject}.",
                    request.SessionId,
                    response.FinishReason?.Value ?? "none",
                    response.Text?.Length ?? 0,
                    TryExtractJsonObject(response.Text) is not null);

                // A response that stopped at the cap is a budget problem, not a schema one,
                // and on a reasoning model it arrives with no visible output at all because
                // hidden reasoning tokens count against the same cap.
                var hitOutputLimit = response.FinishReason == ChatFinishReason.Length;

                return new CoachAgentTurnResult
                {
                    Outcome = hitOutputLimit
                        ? CoachAgentOutcome.OutputLimitReached
                        : CoachAgentOutcome.InvalidOutput,
                    AgentSessionJson = serialized,
                    Usage = usage,
                    FailureReason = hitOutputLimit
                        ? "The answer stopped at the output token limit."
                        : "The answer did not match the turn intent schema."
                };
            }

            return new CoachAgentTurnResult
            {
                Outcome = CoachAgentOutcome.Completed,
                Intent = intent,
                AgentSessionJson = serialized,
                Usage = usage
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CoachAgentTurnResult.Failure(CoachAgentOutcome.Cancelled, "The run was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return CoachAgentTurnResult.Failure(CoachAgentOutcome.Timeout, "The run exceeded the request timeout.");
        }
        catch (CoachToolException ex)
        {
            telemetry.RecordToolCall(ex.ToolName, success: false, stopwatch.Elapsed);
            logger.LogWarning(
                "[Coach] Session {SessionId}: tool {ToolName} failed with {FailureCode}.",
                request.SessionId, ex.ToolName, ex.Code);
            return CoachAgentTurnResult.Failure(CoachAgentOutcome.Failed, $"A read failed ({ex.Code}).");
        }
        catch (Exception ex)
        {
            // The exception object is deliberately not passed to the logger. A provider failure
            // routinely carries the prompt, the learner text, or the model output in its message,
            // in an inner exception, or in Data, and LogError(ex, ...) writes all of it through
            // Exception.ToString(). Only the sanitizer's allow-listed shape facts are logged.
            // See CoachExceptionSanitizer.
            var facts = CoachExceptionSanitizer.Describe(ex);
            logger.LogError(
                "[Coach] Session {SessionId}: the agent run failed. " +
                "Category={FailureCategory} ProviderStatus={ProviderStatus} " +
                "ProviderCode={ProviderErrorCode} InnerDepth={InnerDepth}",
                request.SessionId,
                facts.Category,
                facts.ProviderStatus,
                facts.ProviderErrorCode,
                facts.InnerDepth);

            return CoachAgentTurnResult.Failure(CoachAgentOutcome.Failed, "The agent run failed.");
        }
    }

    /// <summary>
    /// Reads the typed intent from an agent response, tolerating the shapes a model wraps
    /// valid JSON in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The response schema is derived from <c>CoachTurnIntent</c>, but it is a request rather
    /// than a constraint: the deployed model is not run in strict structured-output mode, so a
    /// harder turn — one that called tools first, and has their results in context — can come
    /// back as a fenced <c>```json</c> block or as a sentence followed by the object. The
    /// object inside is correct; only the wrapper is not, and refusing the whole turn over a
    /// wrapper costs the learner a run and a suggestion.
    /// </para>
    /// <para>
    /// This widens what the coach will <b>read</b>, never what it will act on. The recovered
    /// object is deserialized with the same options and then goes through exactly the same
    /// intent validator, answer-leak validator, and application gates as a first-try parse.
    /// Anything that is not a single well-formed object is still refused, and a refused turn
    /// still writes nothing.
    /// </para>
    /// </remarks>
    public static bool TryReadIntent(AgentResponse<CoachTurnIntent> response, out CoachTurnIntent intent)
    {
        ArgumentNullException.ThrowIfNull(response);

        try
        {
            var result = response.Result;
            if (result is not null)
            {
                intent = result;
                return true;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            // Fall through to the recovery attempt below.
        }

        var json = TryExtractJsonObject(response.Text);
        if (json is not null)
        {
            try
            {
                var recovered = JsonSerializer.Deserialize<CoachTurnIntent>(json, IntentSerializerOptions);
                if (recovered is not null)
                {
                    intent = recovered;
                    return true;
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // Not recoverable. The caller refuses the turn.
            }
        }

        intent = null!;
        return false;
    }

    /// <summary>
    /// Returns the first balanced JSON object in <paramref name="text"/>, or null when there
    /// is not exactly one to find. String contents and escapes are respected so a brace inside
    /// a message value cannot end the object early.
    /// </summary>
    public static string? TryExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text[start..(i + 1)];
                    }

                    break;
            }
        }

        // Unbalanced: almost always a response truncated by the output-token cap.
        return null;
    }

    private static async Task<(AgentSession Session, bool DeserializationFailed)> ResumeOrCreateSessionAsync(
        AIAgent agent,
        string? agentSessionJson,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(agentSessionJson))
        {
            try
            {
                using var document = JsonDocument.Parse(agentSessionJson);
                var session = await agent
                    .DeserializeSessionAsync(document.RootElement.Clone(), IntentSerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                return (session, DeserializationFailed: false);
            }
            catch (JsonException)
            {
                // A payload this agent cannot read signals the caller to rebuild from the
                // ledger rather than silently losing context.
            }
        }

        var fresh = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        return (fresh, DeserializationFailed: !string.IsNullOrWhiteSpace(agentSessionJson));
    }

    private static async Task<string?> SerializeSessionAsync(
        AIAgent agent,
        AgentSession session,
        CancellationToken cancellationToken)
    {
        var element = await agent
            .SerializeSessionAsync(session, IntentSerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        return element.ValueKind == JsonValueKind.Undefined ? null : element.GetRawText();
    }

    private static CoachRunUsage ToUsage(UsageDetails? usage) => usage is null
        ? CoachRunUsage.None
        : new CoachRunUsage(usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0, 0m);
}
