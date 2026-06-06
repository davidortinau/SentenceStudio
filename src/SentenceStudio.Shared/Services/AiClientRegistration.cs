using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

namespace SentenceStudio.Services;

/// <summary>
/// Centralizes construction of the OpenAI-SDK client and the tiered (fast/reasoning)
/// <see cref="IChatClient"/> registrations so every host wires the Foundry endpoint and
/// model deployments the same way.
/// </summary>
public static class AiClientRegistration
{
    /// <summary>
    /// Builds an <see cref="OpenAIClient"/> routed through the Polly-backed HttpClient, pointed at
    /// <c>AI:OpenAI:Endpoint</c> when set (Azure AI Foundry's OpenAI-compatible <c>/openai/v1</c>
    /// endpoint). When the endpoint is empty the SDK falls back to its default (api.openai.com).
    /// </summary>
    public static OpenAIClient CreateOpenAIClient(
        IConfiguration configuration, string apiKey, System.Net.Http.HttpClient httpClient)
    {
        var transport = new HttpClientPipelineTransport(httpClient);
        var options = new OpenAIClientOptions { Transport = transport };

        var endpoint = configuration["AI:OpenAI:Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            options.Endpoint = new Uri(endpoint);
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey), options);
    }

    /// <summary>Fast-tier deployment name. Falls back to legacy <c>ChatModel</c> for back-compat.</summary>
    public static string FastModel(IConfiguration configuration) =>
        configuration["AI:OpenAI:Models:Fast"]
        ?? configuration["AI:OpenAI:ChatModel"]
        ?? "gpt-5-mini";

    /// <summary>Reasoning-tier deployment name. Falls back to the fast model when unset.</summary>
    public static string ReasoningModel(IConfiguration configuration) =>
        configuration["AI:OpenAI:Models:Reasoning"]
        ?? FastModel(configuration);

    /// <summary>
    /// Resource root for the Azure AI Foundry / Azure OpenAI account, derived from
    /// <c>AI:OpenAI:Endpoint</c>. The config stores the OpenAI-compatible "/openai/v1" surface;
    /// <c>AzureOpenAIClient</c> wants the bare resource root, so the suffix is stripped.
    /// </summary>
    public static string AzureResourceEndpoint(IConfiguration configuration)
    {
        var endpoint = (configuration["AI:OpenAI:Endpoint"] ?? string.Empty).TrimEnd('/');
        const string suffix = "/openai/v1";
        if (endpoint.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            endpoint = endpoint[..^suffix.Length];
        }
        return endpoint;
    }

    /// <summary>
    /// Registers the default (fast) <see cref="IChatClient"/> plus keyed fast/reasoning clients,
    /// building the underlying <see cref="OpenAIClient"/> from the supplied factory. The factory keeps
    /// provider-specific types (e.g. AzureOpenAIClient + DefaultAzureCredential) out of this shared,
    /// mobile-targeted assembly — server hosts pass an Azure/Entra factory, MAUI passes a key factory.
    /// </summary>
    public static IServiceCollection AddTieredChatClients(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<IServiceProvider, OpenAIClient> clientFactory)
    {
        var fast = FastModel(configuration);
        var reasoning = ReasoningModel(configuration);

        IChatClient Build(IServiceProvider sp, string model) =>
            clientFactory(sp).GetChatClient(model).AsIChatClient();

        // Default (unkeyed) client stays the fast model for back-compat with callers that
        // resolve IChatClient directly (e.g. the API chat endpoint, ConversationAgentService).
        services.AddSingleton<IChatClient>(sp => Build(sp, fast));
        services.AddKeyedSingleton<IChatClient>(AiTier.Fast.ToKey(), (sp, _) => Build(sp, fast));
        services.AddKeyedSingleton<IChatClient>(AiTier.Reasoning.ToKey(), (sp, _) => Build(sp, reasoning));

        return services;
    }

    /// <summary>
    /// Key-based convenience overload (OpenAI-compatible endpoint). Used by the MAUI host, whose
    /// direct AI calls run against the OpenAI-compatible <c>/openai/v1</c> surface with an API key.
    /// </summary>
    public static IServiceCollection AddTieredChatClients(
        this IServiceCollection services, IConfiguration configuration, string apiKey)
        => services.AddTieredChatClients(configuration, sp =>
            CreateOpenAIClient(
                configuration,
                apiKey,
                sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("openai")));
}
