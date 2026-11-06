#pragma warning disable MEAI001 // IRealtimeClient is experimental in Microsoft.Extensions.AI.
#pragma warning disable OPENAI002 // OpenAI.Realtime is experimental in the OpenAI SDK.

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OaiRealtimeClient = OpenAI.Realtime.RealtimeClient;
using OaiRealtimeClientOptions = OpenAI.Realtime.RealtimeClientOptions;

namespace SentenceStudio.Services.Realtime;

/// <summary>
/// Model names and the client for the Conversation activity's realtime voice mode.
/// </summary>
/// <remarks>
/// The API mints a short-lived client secret for one session (see the API's RealtimeEndpoints);
/// the device then opens the realtime websocket itself with that secret. The device never holds a
/// key or an Entra identity.
/// </remarks>
public static class RealtimeVoiceClients
{
    /// <summary>Realtime deployment name. Defaults to <c>gpt-realtime</c>.</summary>
    public static string RealtimeModel(IConfiguration configuration) =>
        configuration["AI:OpenAI:Models:Realtime"] ?? "gpt-realtime";

    /// <summary>Input transcription deployment name. Defaults to <c>gpt-4o-transcribe</c>.</summary>
    public static string TranscriptionModel(IConfiguration configuration) =>
        configuration["AI:OpenAI:Models:Transcribe"] ?? "gpt-4o-transcribe";

    /// <summary>
    /// Creates a realtime client for one session with <paramref name="credential"/>, wrapped in the
    /// same middleware as the chat clients: function invocation (the vocabulary tool), OpenTelemetry
    /// without sensitive data, and logging. Dispose it when the session ends.
    /// </summary>
    /// <remarks>
    /// The <see cref="OaiRealtimeClient"/> is constructed directly with an <see cref="ApiKeyCredential"/>.
    /// <c>OpenAIClient.GetRealtimeClient()</c> (OpenAI 2.12) builds the realtime client from the HTTP
    /// pipeline only, so the websocket handshake carries no Authorization header and fails with 401.
    /// </remarks>
    public static IRealtimeClient Create(RealtimeCredential credential, IServiceProvider? services = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.Value))
            throw new ArgumentException("A realtime credential needs a value.", nameof(credential));

        var options = new OaiRealtimeClientOptions();
        if (credential.Endpoint is { } endpoint)
            options.Endpoint = endpoint;
        var realtime = new OaiRealtimeClient(new ApiKeyCredential(credential.Value), options);
        return new OpenAIRealtimeClient(realtime, credential.Model ?? "gpt-realtime")
            .AsBuilder()
            .UseFunctionInvocation()
            .UseOpenTelemetry(configure: o => o.EnableSensitiveData = false)
            .UseLogging()
            .Build(services);
    }
}

/// <summary>
/// The credential for one realtime session: a bearer value (here, an ephemeral client secret) and,
/// optionally, the endpoint and model it is valid for.
/// </summary>
public sealed record RealtimeCredential(string Value, Uri? Endpoint = null, string? Model = null);
