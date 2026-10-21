#pragma warning disable MEAI001 // IRealtimeClient is experimental in Microsoft.Extensions.AI.
#pragma warning disable OPENAI002 // OpenAI.Realtime is experimental in the OpenAI SDK.

using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OaiRealtimeClient = OpenAI.Realtime.RealtimeClient;
using OaiRealtimeClientOptions = OpenAI.Realtime.RealtimeClientOptions;

namespace SentenceStudio.Services.Realtime;

/// <summary>
/// Registers the realtime voice client used by the Conversation activity's voice mode.
/// </summary>
/// <remarks>
/// The client is an <see cref="IRealtimeClient"/> wrapped in the same kind of middleware pipeline
/// as the chat clients: function invocation, OpenTelemetry, and logging. Hosts supply only the
/// credential, so keys and tokens stay on the server (web) or in the host's existing key store (MAUI).
/// </remarks>
public static class RealtimeVoiceRegistration
{
    /// <summary>Realtime deployment name. Defaults to <c>gpt-realtime</c>.</summary>
    public static string RealtimeModel(IConfiguration configuration) =>
        configuration["AI:OpenAI:Models:Realtime"] ?? "gpt-realtime";

    /// <summary>Input transcription deployment name. Defaults to <c>gpt-4o-transcribe</c>.</summary>
    public static string TranscriptionModel(IConfiguration configuration) =>
        configuration["AI:OpenAI:Models:Transcribe"] ?? "gpt-4o-transcribe";

    /// <summary>
    /// Adds a singleton <see cref="IRealtimeClient"/>. <paramref name="credentialProvider"/> returns a
    /// bearer value for each new session: an Entra access token on servers, or an API key.
    /// </summary>
    public static IServiceCollection AddRealtimeVoice(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<IServiceProvider, CancellationToken, ValueTask<string>> credentialProvider)
    {
        ArgumentNullException.ThrowIfNull(credentialProvider);

        services.AddSingleton<IRealtimeClient>(sp =>
        {
            var endpoint = configuration["AI:OpenAI:Endpoint"];
            var inner = new PerSessionCredentialRealtimeClient(
                string.IsNullOrWhiteSpace(endpoint) ? null : new Uri(endpoint),
                RealtimeModel(configuration),
                ct => credentialProvider(sp, ct));

            return inner.AsBuilder()
                .UseFunctionInvocation()
                .UseOpenTelemetry(configure: o => o.EnableSensitiveData = false)
                .UseLogging()
                .Build(sp);
        });

        return services;
    }

    /// <summary>
    /// Creates an <see cref="OpenAIRealtimeClient"/> per session with a fresh credential.
    /// </summary>
    /// <remarks>
    /// Entra access tokens expire, and a realtime session is a long-lived websocket, so the
    /// credential is resolved when each session starts rather than once at startup.
    /// <para>
    /// The <see cref="OaiRealtimeClient"/> is constructed directly with an <see cref="ApiKeyCredential"/>.
    /// <c>OpenAIClient.GetRealtimeClient()</c> (OpenAI 2.12) builds the realtime client from the HTTP
    /// pipeline only, so the websocket handshake carries no Authorization header and fails with 401.
    /// </para>
    /// </remarks>
    private sealed class PerSessionCredentialRealtimeClient(
        Uri? endpoint,
        string model,
        Func<CancellationToken, ValueTask<string>> credentialProvider) : IRealtimeClient
    {
        private readonly ChatClientMetadata _metadata = new("openai", endpoint, model);

        public async Task<IRealtimeClientSession> CreateSessionAsync(
            RealtimeSessionOptions? options = null, CancellationToken cancellationToken = default)
        {
            var credential = await credentialProvider(cancellationToken).ConfigureAwait(false);
            var clientOptions = new OaiRealtimeClientOptions();
            if (endpoint is not null)
            {
                clientOptions.Endpoint = endpoint;
            }

            var realtime = new OaiRealtimeClient(new ApiKeyCredential(credential), clientOptions);
            using var client = new OpenAIRealtimeClient(realtime, model);
            return await client.CreateSessionAsync(options, cancellationToken).ConfigureAwait(false);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is not null ? null :
            serviceType == typeof(ChatClientMetadata) ? _metadata :
            serviceType.IsInstanceOfType(this) ? this :
            null;

        public void Dispose()
        {
        }
    }
}
